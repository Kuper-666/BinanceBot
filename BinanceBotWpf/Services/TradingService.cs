using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using BinanceBotWpf.Models;
using BinanceBotWpf.ViewModels;

namespace BinanceBotWpf.Services
{
    public class TradingService
    {
        private readonly BinanceClient _client;
        private readonly WalletManager _wallet;
        private readonly EarnManager _earn;
        private readonly BalanceRebalancer _rebalancer;
        private readonly PositionManager _positionManager;
        private readonly MlModelManager _mlManager;
        private readonly DataLogger _dataLogger;
        private readonly BalanceManager _balanceManager;
        private MainWindowViewModel _ui;
        private bool _isRunning;

        private readonly StrategyEngine _strategy = new ();
        private List<string> _activePairs = new ();
        private readonly object _pairsLock = new object ();
        private TelegramNotifier _telegram;
        private DateTime _lastRetrainTime = DateTime.MinValue;
        private readonly TimeSpan _minRetrainInterval = TimeSpan.FromHours (1);
        private DateTime _lastBalanceLog = DateTime.MinValue;
        private decimal _lastLoggedBalance = -1;
        private DateTime _lastReportDate = DateTime.MinValue;
        private readonly Dictionary<string, (List<BinanceKline> Klines, DateTime Expiry)> _klinesCache = new ();
        private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds (10);

        public TradingService(BinanceClient client, WalletManager wallet, EarnManager earn, BalanceRebalancer rebalancer = null,
                              decimal minUsdcBalance = 5.50m, string telegramBotToken = "", string telegramChatId = "")
        {
            _client = client;
            _wallet = wallet;
            _earn = earn;
            _rebalancer = rebalancer ?? new BalanceRebalancer (new object (), 0.1m);
            string dataDir = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Data");
            string logsDir = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Logs");
            _positionManager = new PositionManager (Path.Combine (dataDir, "open_positions.json"), null);
            _mlManager = new MlModelManager (Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "trading_model.zip"), null);
            _dataLogger = new DataLogger (logsDir, null);
            _balanceManager = new BalanceManager (client, earn, _rebalancer, null);
        }

        public void SetLogger(Action<string> logger)
        {
            _wallet.OnLogGenerated += logger;
            _earn.OnLogGenerated += logger;
            _rebalancer.OnLogGenerated += logger;
            _client.OnLogGenerated += logger;
        }

        public async Task StartTradingAsync(MainWindowViewModel vm)
        {
            if (_isRunning) return;
            _ui = vm;
            _isRunning = true;

            SetLogger (vm.AddLog);
            await InitAsync ();
            _ = Task.Run (BalanceLoop);
            _ = Task.Run (UpdatePairsLoop);
            _ = Task.Run (EarnLoop);
            _ = Task.Run (DustLoop);
            _ = Task.Run (AutoRetrainLoop);
            await TradingLoop ();
        }

        private async Task InitAsync()
        {
            await _wallet.UpdateBalance ();
            await UpdatePairs ();
            await LoadPositions ();
        }

        private async Task LoadPositions()
        {
            await _positionManager.LoadAsync (_client, GetCurrentPrice, p => _ui.StopLossPercent, p => _ui.TakeProfitPercent);
            _ui?.UpdatePositionsStatus (_positionManager.Count, 3, _positionManager.GetSymbols ());
        }

        private async Task<decimal> GetCurrentPrice(string sym)
        {
            var k = await _client.GetKlinesAsync (sym, "5m", 1);
            return k?.Last ().Close ?? 0;
        }

        private async Task UpdatePairs()
        {
            try
            {
                var newPairs = await _client.GetTopVolumePairsAsync ("USDC", 20);
                newPairs = newPairs.Where (p => !p.Contains ("USD1") && !p.Contains ("UUSDC") && !p.Contains ("LD")).ToList ();
                if (newPairs.Count > 0)
                {
                    lock (_pairsLock) { _activePairs = newPairs; }
                    _ui?.AddLog ($"📊 Список пар обновлён: {string.Join (", ", _activePairs.Take (5))}...");
                }
            }
            catch (Exception ex) { _ui?.AddLog ($"❌ Ошибка обновления пар: {ex.Message}"); }
        }

        private async Task<List<(string Symbol, TradeAction Action, decimal Price, decimal Rsi, decimal FastSma, decimal SlowSma, decimal Volatility, decimal Volume)>> AnalyzePairsAsync(List<string> pairs)
        {
            var results = new ConcurrentBag<(string, TradeAction, decimal, decimal, decimal, decimal, decimal, decimal)> ();
            await Parallel.ForEachAsync (pairs, async (sym, ct) =>
            {
                try
                {
                    var klines = await GetKlinesCachedAsync (sym, "5m", 50);
                    if (klines?.Count < Math.Max (_ui.FastSma, _ui.SlowSma) + 2) return;
                    var closes = klines.Select (k => k.Close).ToList ();
                    decimal price = closes.Last ();
                    var signal = _strategy.AnalyzePairWithWallet (sym, closes, _ui.FastSma, _ui.SlowSma, price);
                    decimal rsi = CalculateRsi (closes);
                    decimal fastSma = CalculateSma (closes, _ui.FastSma);
                    decimal slowSma = CalculateSma (closes, _ui.SlowSma);
                    decimal volatility = CalculateVolatility (closes, 20);
                    decimal volume = klines.Last ().Volume;
                    _ui.UpdateMarketTable (sym, price.ToString ("F4"), _positionManager.TryGet (sym, out _), signal.Action, fastSma, slowSma);
                    results.Add ((sym, signal.Action, price, rsi, fastSma, slowSma, volatility, volume));
                }
                catch (Exception ex) { _ui?.AddLog ($"❌ Ошибка анализа {sym}: {ex.Message}"); }
            });
            return results.ToList ();
        }

        private async Task<decimal> ExecuteBuy((string Symbol, TradeAction Action, decimal Price, decimal Rsi,
     decimal FastSma, decimal SlowSma, decimal Volatility, decimal Volume) sig, decimal currentSpotBalance)
        {
            // 1. Проверяем, достаточно ли USDC на споте
            if (currentSpotBalance < 10)
            {
                _ui?.AddLog ($"⚠️ Недостаточно USDC на споте для {sig.Symbol}: {currentSpotBalance:F2} (нужно минимум 10)");
                return currentSpotBalance;
            }

            // 2. Расчёт суммы сделки (не более доступного спота)
            decimal totalBalance = _wallet.GetTotalBalance ("USDC");
            decimal riskAmount = totalBalance * _ui.MaxRiskPercent;
            decimal spend = Math.Max (10m, riskAmount);
            if (spend > currentSpotBalance) spend = currentSpotBalance;
            if (spend < 10)
            {
                _ui?.AddLog ($"⚠️ Недостаточно USDC для покупки {sig.Symbol} (нужно min 10, есть {currentSpotBalance:F2})");
                return currentSpotBalance;
            }

            // 3. ML проверка
            if (!_mlManager.IsProfitable (sig.FastSma, sig.SlowSma, sig.Rsi, sig.Volume, sig.Volatility))
            {
                _ui?.AddLog ($"⏸️ ML отклонила покупку {sig.Symbol}");
                return currentSpotBalance;
            }

            // 4. Расчёт количества с шагом лота
            decimal rawQty = spend / sig.Price;
            decimal stepSize = await _client.GetStepSizeAsync (sig.Symbol);
            decimal qty = Math.Floor (rawQty / stepSize) * stepSize;
            qty = Math.Round (qty, 8);
            if (qty <= 0) return currentSpotBalance;

            decimal required = qty * sig.Price;
            if (required > currentSpotBalance)
            {
                _ui?.AddLog ($"⚠️ Сумма покупки {required:F2} USDC превышает спот-баланс {currentSpotBalance:F2} для {sig.Symbol}");
                return currentSpotBalance;
            }

            _ui?.AddLog ($"💵 Попытка купить {qty} {sig.Symbol} по {sig.Price:F4}, сумма ~{required:F2} USDC (доступно {currentSpotBalance:F2})");

            var order = await _client.PlaceOrder (sig.Symbol, "BUY", "MARKET", qty);
            if (order != null)
            {
                var pos = new OpenPosition
                {
                    Symbol = sig.Symbol,
                    Quantity = qty,
                    EntryPrice = sig.Price,
                    OpenTime = DateTime.UtcNow,
                    StopLossPrice = sig.Price * ( 1 - _ui.StopLossPercent ),
                    TakeProfitPrice = sig.Price * ( 1 + _ui.TakeProfitPercent ),
                    HighestPrice = sig.Price
                };
                _positionManager.AddOrUpdate (sig.Symbol, pos);
                decimal newSpotBalance = currentSpotBalance - required;
                _ui?.AddLog ($"✅ КУПЛЕНО: {qty} {sig.Symbol} по {sig.Price:F4} | Остаток USDC на споте: {newSpotBalance:F2}");
                _ui?.UpdateWalletDisplay (_wallet.GetTotalBalance ("USDC").ToString ("F2"));
                _ui?.UpdatePositionsStatus (_positionManager.Count, 3, _positionManager.GetSymbols ());
                _dataLogger.LogTrade (new TradeLog
                {
                    Symbol = sig.Symbol,
                    EntryPrice = sig.Price,
                    ExitPrice = sig.Price,
                    Quantity = qty,
                    Action = "BUY_OPEN",
                    CloseTime = DateTime.UtcNow,
                    Reason = "SMA Buy"
                });
                return newSpotBalance;
            }
            else
            {
                _ui?.AddLog ($"❌ Ошибка ордера BUY {sig.Symbol}");
                return currentSpotBalance;
            }
        }

        private async Task ExecuteSell((string Symbol, TradeAction Action, decimal Price, decimal Rsi, decimal FastSma, decimal SlowSma, decimal Volatility, decimal Volume) sig)
        {
            if (!_positionManager.TryGet (sig.Symbol, out var pos)) return;
            string asset = sig.Symbol.Replace ("USDC", "");
            decimal spotBalance = await _client.GetAccountBalanceAsync (asset);
            if (spotBalance < pos.Quantity - 0.000001m)
            {
                _ui?.AddLog ($"⚠️ Недостаточно {asset} на споте для продажи {sig.Symbol}. Удаляю позицию.");
                _positionManager.Remove (sig.Symbol);
                _ui?.UpdatePositionsStatus (_positionManager.Count, 3, _positionManager.GetSymbols ());
                return;
            }
            var order = await _client.PlaceOrder (sig.Symbol, "SELL", "MARKET", pos.Quantity);
            if (order != null)
            {
                decimal pnl = ( sig.Price - pos.EntryPrice ) * pos.Quantity;
                decimal pnlPct = ( sig.Price / pos.EntryPrice - 1 ) * 100;
                _ui?.AddLog ($"🔒 ЗАКРЫТА: {sig.Symbol} по {sig.Price:F4} | PnL: {pnl:F2} ({pnlPct:F2}%) | SMA Sell");
                var trade = new TradeLog
                {
                    Symbol = sig.Symbol,
                    EntryPrice = pos.EntryPrice,
                    ExitPrice = sig.Price,
                    Quantity = pos.Quantity,
                    PnL = pnl,
                    PnLPercent = pnlPct,
                    OpenTime = pos.OpenTime,
                    CloseTime = DateTime.UtcNow,
                    Reason = "SMA Sell",
                    Duration = DateTime.UtcNow - pos.OpenTime,
                    Action = "SELL_CLOSE"
                };
                _ui.AddTradeToHistory (trade);
                _dataLogger.LogTrade (trade);
                _positionManager.Remove (sig.Symbol);
                _ui?.UpdatePositionsStatus (_positionManager.Count, 3, _positionManager.GetSymbols ());
                _ = TryAutoRetrainAsync ();
            }
        }

        private async Task CheckProtections()
        {
            var toClose = new List<string> ();
            foreach (var sym in _positionManager.GetSymbols ())
            {
                if (!_positionManager.TryGet (sym, out var pos)) continue;
                decimal price = await GetCurrentPrice (sym);
                if (price <= 0) continue;
                if (price > pos.HighestPrice)
                {
                    pos.HighestPrice = price;
                    decimal newSl = pos.HighestPrice * ( 1 - _ui.TrailingStopPercent );
                    if (newSl > pos.StopLossPrice) pos.StopLossPrice = newSl;
                }
                if (price <= pos.StopLossPrice || price >= pos.TakeProfitPrice || DateTime.UtcNow - pos.OpenTime > TimeSpan.FromHours (2))
                    toClose.Add (sym);
            }
            foreach (var sym in toClose)
            {
                decimal price = await GetCurrentPrice (sym);
                await ExecuteSell ((sym, TradeAction.Sell, price, 0, 0, 0, 0, 0));
            }
        }

        private async Task TryAutoRetrainAsync()
        {
            if (DateTime.UtcNow - _lastRetrainTime < _minRetrainInterval) return;
            _ui?.AddLog ("🔄 Автоматическое переобучение ML модели...");
            await _mlManager.RetrainAsync (Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Logs"), _ui.AddLog);
            _lastRetrainTime = DateTime.UtcNow;
        }

        private decimal CalculateSma(List<decimal> data, int period) => data.Skip (data.Count - period).Average ();
        private decimal CalculateRsi(List<decimal> closes) => TechnicalAnalysis.RSI (closes, 14).LastOrDefault () ?? 50;
        private decimal CalculateVolatility(List<decimal> data, int period) => (decimal)Math.Sqrt ((double)data.TakeLast (period).Select (x => ( x - data.TakeLast (period).Average () )).Sum (x => x * x) / period);

        private async Task<List<BinanceKline>> GetKlinesCachedAsync(string symbol, string interval, int limit)
        {
            lock (_klinesCache)
            {
                if (_klinesCache.TryGetValue (symbol, out var cached) && DateTime.UtcNow < cached.Expiry)
                    return cached.Klines;
            }
            var klines = await _client.GetKlinesAsync (symbol, interval, limit);
            lock (_klinesCache) { _klinesCache[symbol] = (klines, DateTime.UtcNow + _cacheDuration); }
            return klines;
        }

        // ==================== ФОНОВЫЕ ЦИКЛЫ ====================
        private async Task BalanceLoop()
        {
            while (_isRunning)
            {
                await Task.Delay (60000);
                if (!_isRunning) break;
                await _wallet.UpdateBalance ();
                decimal bal = _wallet.GetTotalBalance ("USDC");
                if (Math.Abs (bal - _lastLoggedBalance) > 5 || DateTime.UtcNow.Minute == 0)
                {
                    _ui?.AddLog ($"💰 Баланс USDC: {bal:F2} (спот + Earn)");
                    _lastLoggedBalance = bal;
                }
                _ui?.UpdateWalletDisplay (bal.ToString ("F2"));
                _ui?.UpdateDrawdown (bal);
                _ui?.AddBalancePoint (DateTime.Now, bal);

                if (DateTime.UtcNow.Date != _lastReportDate)
                {
                    _lastReportDate = DateTime.UtcNow.Date;
                    // ежедневный отчёт в Telegram (опционально)
                    if (_telegram != null && _ui?.TotalTrades > 0)
                        await _telegram.SendDailyReport (_ui.TotalPnL, _ui.WinRate, _ui.TotalTrades, _ui.WinningTrades, _ui.LosingTrades);
                }
            }
        }

        private async Task UpdatePairsLoop()
        {
            while (_isRunning)
            {
                await Task.Delay (30 * 60000);
                if (!_isRunning) break;
                await UpdatePairs ();
            }
        }

        private async Task EarnLoop()
        {
            while (_isRunning)
            {
                await Task.Delay (60 * 60000);
                if (!_isRunning) break;
                decimal free = await _client.GetAccountBalanceAsync ("USDC");
                if (free <= 15) continue;
                decimal toSub = free - 15;
                if (toSub < 5) continue;
                _ui?.AddLog ($"💸 Размещаю {toSub:F2} USDC в Earn");
                var products = await _client.GetFlexibleProductsAsync ("USDC");
                var product = products?.FirstOrDefault (p => p["asset"]?.ToString () == "USDC");
                if (product != null)
                    await _client.SubscribeFlexibleEarnAsync (product["productId"].ToString (), toSub);
            }
        }

        private async Task DustLoop()
        {
            while (_isRunning)
            {
                await Task.Delay (24 * 3600000);
                if (!_isRunning) break;
                var dust = await _client.GetDustAssetsAsync ();
                if (dust == null || dust.Count == 0) continue;
                var ids = new List<string> ();
                foreach (var item in dust)
                {
                    string asset = item["asset"]?.ToString ();
                    if (asset != "USDC" && !asset.StartsWith ("LD") && !new[] { "RDNT", "NTRN" }.Contains (asset))
                        ids.Add (item["assetId"]?.ToString ());
                }
                if (ids.Count == 0) continue;
                _ui?.AddLog ($"🧹 Конвертирую пыль ({ids.Count} активов)");
                await _client.ConvertDustToBnbAsync (ids);
                await Task.Delay (5000);
                decimal bnb = await _client.GetAccountBalanceAsync ("BNB");
                if (bnb > 0.001m)
                {
                    var price = ( await _client.GetKlinesAsync ("BNBUSDC", "5m", 1) ).Last ().Close;
                    decimal step = await _client.GetStepSizeAsync ("BNBUSDC");
                    decimal qty = Math.Floor (bnb / step) * step;
                    if (qty > 0) await _client.PlaceOrder ("BNBUSDC", "SELL", "MARKET", qty);
                }
            }
        }

        private async Task AutoRetrainLoop()
        {
            while (_isRunning)
            {
                await Task.Delay (TimeSpan.FromHours (24));
                if (!_isRunning) break;
                await _mlManager.RetrainAsync (Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Logs"), _ui.AddLog);
                _lastRetrainTime = DateTime.UtcNow;
            }
        }

        private async Task TradingLoop()
        {
            while (_isRunning)
            {
                try
                {
                    await CheckProtections ();

                    // 1. Актуальный баланс USDC (спот)
                    decimal spotBalance = await _client.GetAccountBalanceAsync ("USDC");
                    decimal totalBalance = _wallet.GetTotalBalance ("USDC");
                    _ui?.UpdateWalletDisplay (totalBalance.ToString ("F2"));
                    _ui?.AddLog ($"💰 Баланс USDC: спот={spotBalance:F2}, всего={totalBalance:F2}");

                    // 2. Если спот-баланс ниже 10 – пробуем выкупить из Earn (но не блокируем)
                    if (spotBalance < 10)
                    {
                        _ui?.AddLog ($"🔄 Спот USDC низкий ({spotBalance:F2}), пробую выкупить до 10 USDC...");
                        bool redeemed = await _earn.EnsureLiquidBalanceAsync ("USDC", 10, _client);
                        if (redeemed)
                        {
                            spotBalance = await _client.GetAccountBalanceAsync ("USDC");
                            totalBalance = _wallet.GetTotalBalance ("USDC");
                            _ui?.AddLog ($"✅ После выкупа: спот={spotBalance:F2}, всего={totalBalance:F2}");
                        }
                        else
                        {
                            // Не удалось выкупить – запускаем ребаланс (продажа других активов)
                            _ui?.AddLog ($"⚠️ Не удалось выкупить USDC, запускаю ребаланс...");
                            await _rebalancer.AutoConvertAssetsToUsdcAsync (_client, _isRunning);
                            spotBalance = await _client.GetAccountBalanceAsync ("USDC");
                            totalBalance = _wallet.GetTotalBalance ("USDC");
                            _ui?.AddLog ($"💰 После ребаланса: спот={spotBalance:F2}, всего={totalBalance:F2}");
                        }
                    }

                    // 3. Список активных пар
                    List<string> pairs;
                    lock (_pairsLock) { pairs = new List<string> (_activePairs); }
                    if (pairs.Count == 0) { await Task.Delay (5000); continue; }

                    // 4. Анализ сигналов
                    var signals = await AnalyzePairsAsync (pairs);

                    // 5. Обработка сигналов
                    foreach (var sig in signals)
                    {
                        bool hasPos = _positionManager.TryGet (sig.Symbol, out _);
                        if (sig.Action == TradeAction.Buy && !hasPos && _positionManager.Count < 3)
                        {
                            if (spotBalance < 10)
                            {
                                _ui?.AddLog ($"⚠️ Недостаточно USDC на споте для {sig.Symbol}: {spotBalance:F2}");
                                continue;
                            }
                            spotBalance = await ExecuteBuy (sig, spotBalance);
                        }
                        else if (sig.Action == TradeAction.Sell && hasPos)
                        {
                            await ExecuteSell (sig);
                            spotBalance = await _client.GetAccountBalanceAsync ("USDC");
                            _ui?.AddLog ($"🔄 После продажи спот USDC: {spotBalance:F2}");
                        }
                    }

                    await Task.Delay (10000);
                }
                catch (Exception ex)
                {
                    _ui?.AddLog ($"❌ Ошибка TradingLoop: {ex.Message}");
                    await Task.Delay (10000);
                }
            }
        }

        public void StopTrading() => _isRunning = false;
    }
}