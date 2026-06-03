using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using BinanceBotWpf.Models;
using BinanceBotWpf.ViewModels;
using System.Globalization;

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
        private DateTime _lastRebalanceAttempt = DateTime.MinValue;
        private readonly TimeSpan _rebalanceCooldown = TimeSpan.FromMinutes (2);
        private DateTime _lastLowBalanceLog = DateTime.MinValue;
        private readonly HashSet<string> _blacklistedSymbols = new () { "FDUSDUSDC" };
        private DateTime _lastOrdersFetch = DateTime.MinValue;
        private readonly TimeSpan _ordersFetchInterval = TimeSpan.FromHours (4);
        private readonly string _telegramBotToken;
        private readonly string _telegramChatId;
        private AirdropNotifier _airdropNotifier;

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

            string tgToken = _telegramBotToken;
            string tgChatId = _telegramChatId;

            if (string.IsNullOrEmpty (tgToken) || string.IsNullOrEmpty (tgChatId))
            {
                string configPath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "config.txt");
                if (File.Exists (configPath))
                {
                    var lines = File.ReadAllLines (configPath);
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace (line) || !line.Contains ("=")) continue;
                        var parts = line.Split ('=', 2);
                        string key = parts[0].Trim ().ToLower ();
                        string value = parts[1].Trim ();
                        if (key == "telegrambottoken") tgToken = value;
                        if (key == "telegramchatid") tgChatId = value;
                    }
                }
            }

            logger ($"🔍 Telegram: token='{tgToken?.Substring (0, Math.Min (10, tgToken?.Length ?? 0))}...', chatId='{tgChatId}'");

            if (!string.IsNullOrEmpty (tgToken) && !string.IsNullOrEmpty (tgChatId))
            {
                try
                {
                    _telegram = new TelegramNotifier (tgToken, tgChatId);
                    _telegram.StartListening (HandleTelegramCommand);
                    logger ("✅ Telegram уведомления включены");
                    logger ("📡 Команды Telegram активированы (/help для списка)");
                }
                catch (Exception ex)
                {
                    logger ($"❌ Ошибка инициализации Telegram: {ex.Message}");
                }
            }
            else
            {
                logger ("⚠️ Telegram не настроен. Уведомления отключены.");
            }

            _airdropNotifier = new AirdropNotifier (_telegram, logger);
            logger ("✅ Запущен модуль уведомлений об аирдропах.");
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
            _ = Task.Run (OrderHistoryCollectorLoop);
            await TradingLoop ();
        }

        private async Task InitAsync()
        {
            await _wallet.UpdateBalance ();
            _ui?.AddLog ("✅ Фильтрация по разрешённым символам отключена (чёрный список)");
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
                var newPairs = await _client.GetTopVolumePairsAsync ("USDC", 30);
                newPairs = newPairs
                    .Where (p => !p.Contains ("USD1") && !p.Contains ("UUSDC") && !p.Contains ("LD"))
                    .Where (p => !_blacklistedSymbols.Contains (p))
                    .ToList ();
                if (newPairs.Count > 0)
                {
                    lock (_pairsLock) { _activePairs = newPairs; }
                    _ui?.AddLog ($"📊 Список пар обновлён: {string.Join (", ", _activePairs.Take (5))}...");
                }
                else
                {
                    _ui?.AddLog ("⚠️ Не найдено активных пар (чёрный список/фильтр)");
                }
            }
            catch (Exception ex) { _ui?.AddLog ($"❌ Ошибка обновления пар: {ex.Message}"); }
        }

        // ==================== СБОР ИСТОРИИ ОРДЕРОВ И ОБУЧЕНИЕ ====================
        private async Task OrderHistoryCollectorLoop()
        {
            while (_isRunning)
            {
                await Task.Delay (_ordersFetchInterval);
                if (!_isRunning) break;
                await FetchAndRetrainFromOrderHistoryAsync ();
            }
        }

        private async Task FetchAndRetrainFromOrderHistoryAsync()
        {
            try
            {
                if (DateTime.UtcNow - _lastOrdersFetch < _ordersFetchInterval) return;
                _ui?.AddLog ("📥 Сбор истории ордеров для переобучения ML...");

                List<string> pairsToFetch;
                lock (_pairsLock) { pairsToFetch = new List<string> (_activePairs); }
                if (pairsToFetch.Count == 0)
                {
                    _ui?.AddLog ("⚠️ Нет активных пар для загрузки истории ордеров");
                    return;
                }

                var allClosedTrades = new List<(DateTime CloseTime, string Symbol, decimal EntryPrice, decimal ExitPrice, decimal Quantity, bool IsProfitable)> ();
                foreach (var sym in pairsToFetch)
                {
                    var orders = await _client.GetAllOrdersAsync (sym, limit: 100);
                    if (orders == null || orders.Count == 0) continue;

                    var buys = orders.Where (o => o["side"].ToString () == "BUY" && o["status"].ToString () == "FILLED")
                                     .OrderBy (o => (long)o["time"]).ToList ();
                    var sells = orders.Where (o => o["side"].ToString () == "SELL" && o["status"].ToString () == "FILLED")
                                      .OrderBy (o => (long)o["time"]).ToList ();

                    int buyIdx = 0, sellIdx = 0;
                    while (buyIdx < buys.Count && sellIdx < sells.Count)
                    {
                        var buy = buys[buyIdx];
                        var sell = sells[sellIdx];
                        long buyTime = (long)buy["time"];
                        long sellTime = (long)sell["time"];
                        if (sellTime < buyTime) { sellIdx++; continue; }

                        decimal buyQty = decimal.Parse (buy["executedQty"].ToString (), CultureInfo.InvariantCulture);
                        decimal sellQty = decimal.Parse (sell["executedQty"].ToString (), CultureInfo.InvariantCulture);
                        decimal qty = Math.Min (buyQty, sellQty);
                        if (qty > 0)
                        {
                            decimal entryPrice = decimal.Parse (buy["price"].ToString (), CultureInfo.InvariantCulture);
                            decimal exitPrice = decimal.Parse (sell["price"].ToString (), CultureInfo.InvariantCulture);
                            bool profitable = exitPrice > entryPrice;
                            allClosedTrades.Add ((DateTimeOffset.FromUnixTimeMilliseconds (sellTime).DateTime, sym, entryPrice, exitPrice, qty, profitable));
                        }
                        if (buyQty <= sellQty) buyIdx++;
                        if (sellQty <= buyQty) sellIdx++;
                    }
                }

                _ui?.AddLog ($"📊 Найдено {allClosedTrades.Count} закрытых позиций в истории ордеров");
                if (allClosedTrades.Count < 30)
                {
                    _ui?.AddLog ($"⚠️ Недостаточно сделок ({allClosedTrades.Count}) для обучения, требуется 30.");
                    return;
                }

                var features = new List<(decimal FastSma, decimal SlowSma, decimal Rsi, decimal Volume, decimal Volatility, bool IsProfitable)> ();
                foreach (var trade in allClosedTrades)
                {
                    try
                    {
                        var klines = await _client.GetKlinesAsync (trade.Symbol, "5m", 50);
                        if (klines == null || klines.Count < Math.Max (_ui.FastSma, _ui.SlowSma) + 2) continue;
                        var closes = klines.Select (k => k.Close).ToList ();
                        decimal fastSma = CalculateSma (closes, _ui.FastSma);
                        decimal slowSma = CalculateSma (closes, _ui.SlowSma);
                        decimal rsi = CalculateRsi (closes);
                        decimal volatility = CalculateVolatility (closes, 20);
                        decimal volume = klines.Last ().Volume;
                        features.Add ((fastSma, slowSma, rsi, volume, volatility, trade.IsProfitable));
                    }
                    catch (Exception ex) { _ui?.AddLog ($"Ошибка обработки {trade.Symbol}: {ex.Message}"); }
                }

                if (features.Count < 20)
                {
                    _ui?.AddLog ($"⚠️ Недостаточно признаков для обучения ({features.Count})");
                    return;
                }

                await _mlManager.RetrainFromFeaturesAsync (features, _ui.AddLog);
                _lastOrdersFetch = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _ui?.AddLog ($"❌ Ошибка сбора истории ордеров: {ex.Message}");
            }
        }

        // ==================== АНАЛИЗ, ПОКУПКА, ПРОДАЖА ====================
        private async Task<List<(string Symbol, TradeAction Action, decimal Price, decimal Rsi,
            decimal FastSma, decimal SlowSma, decimal Volatility, decimal Volume)>> AnalyzePairsAsync(List<string> pairs)
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
            if (currentSpotBalance < 5)
                return currentSpotBalance;

            decimal totalBalance = _wallet.GetTotalBalance ("USDC");
            decimal riskAmount = totalBalance * _ui.MaxRiskPercent;
            decimal spend = Math.Max (10m, riskAmount);
            if (spend > currentSpotBalance) spend = currentSpotBalance;
            if (spend < 10) return currentSpotBalance;

            if (!_mlManager.IsProfitable (sig.FastSma, sig.SlowSma, sig.Rsi, sig.Volume, sig.Volatility))
            {
                _ui?.AddLog ($"⏸️ ML отклонила покупку {sig.Symbol}");
                return currentSpotBalance;
            }

            decimal rawQty = spend / sig.Price;
            decimal stepSize = await _client.GetStepSizeAsync (sig.Symbol);
            decimal qty = Math.Floor (rawQty / stepSize) * stepSize;
            qty = Math.Round (qty, 8);
            if (qty <= 0) return currentSpotBalance;

            decimal required = qty * sig.Price;
            if (required > currentSpotBalance) return currentSpotBalance;

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
                decimal newBalance = currentSpotBalance - required;
                _ui?.AddLog ($"✅ КУПЛЕНО: {qty} {sig.Symbol} по {sig.Price:F4} | Остаток USDC на споте: {newBalance:F2}");
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
                return newBalance;
            }
            else
            {
                if (_client.LastOrderError?.Contains ("This symbol is not permitted") == true)
                {
                    _ui?.AddLog ($"⚠️ Символ {sig.Symbol} не разрешён для торговли, исключаю из списка");
                    lock (_pairsLock) { _activePairs.Remove (sig.Symbol); }
                    _blacklistedSymbols.Add (sig.Symbol);
                }
                else
                {
                    _ui?.AddLog ($"❌ Ошибка ордера BUY {sig.Symbol}: {_client.LastOrderError}");
                }
                return currentSpotBalance;
            }
        }

        private async Task ExecuteSell((string Symbol, TradeAction Action, decimal Price, decimal Rsi,
            decimal FastSma, decimal SlowSma, decimal Volatility, decimal Volume) sig)
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
            else
            {
                _ui?.AddLog ($"❌ Не удалось продать {sig.Symbol}: {_client.LastOrderError}");
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
            await _mlManager.RetrainFromFeaturesAsync (new List<(decimal, decimal, decimal, decimal, decimal, bool)> (), _ui.AddLog);
            _lastRetrainTime = DateTime.UtcNow;
        }

        private decimal CalculateSma(List<decimal> data, int period) => data.Skip (data.Count - period).Average ();
        private decimal CalculateRsi(List<decimal> closes) => TechnicalAnalysis.RSI (closes, 14).LastOrDefault () ?? 50;
        private decimal CalculateVolatility(List<decimal> data, int period)
        {
            if (data.Count < period) return 0;
            var last = data.TakeLast (period).ToList ();
            decimal avg = last.Average ();
            decimal sumSq = last.Select (x => ( x - avg ) * ( x - avg )).Sum ();
            return (decimal)Math.Sqrt ((double)( sumSq / period ));
        }

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
                await FetchAndRetrainFromOrderHistoryAsync ();
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

                    decimal spotBalance = await _client.GetAccountBalanceAsync ("USDC");
                    decimal totalBalance = _wallet.GetTotalBalance ("USDC");
                    _ui?.UpdateWalletDisplay (totalBalance.ToString ("F2"));
                    if (DateTime.UtcNow - _lastBalanceLog > TimeSpan.FromSeconds (30))
                    {
                        _ui?.AddLog ($"💰 Баланс USDC: спот={spotBalance:F2}, всего={totalBalance:F2}");
                        _lastBalanceLog = DateTime.UtcNow;
                    }

                    if (spotBalance < 10)
                    {
                        if (DateTime.UtcNow - _lastRebalanceAttempt < _rebalanceCooldown)
                        {
                            if (DateTime.UtcNow - _lastLowBalanceLog > TimeSpan.FromSeconds (30))
                            {
                                _ui?.AddLog ($"⚠️ Низкий спот USDC ({spotBalance:F2}), ребаланс недавно выполнялся, пропускаем.");
                                _lastLowBalanceLog = DateTime.UtcNow;
                            }
                        }
                        else
                        {
                            _ui?.AddLog ($"🔄 Спот USDC низкий ({spotBalance:F2}), запускаю ребаланс...");
                            _lastRebalanceAttempt = DateTime.UtcNow;
                            await _rebalancer.AutoConvertAssetsToUsdcAsync (_client, _isRunning);
                            spotBalance = await _client.GetAccountBalanceAsync ("USDC");
                            totalBalance = _wallet.GetTotalBalance ("USDC");
                            _ui?.AddLog ($"💰 После ребаланса: спот={spotBalance:F2}, всего={totalBalance:F2}");
                            if (spotBalance < 10)
                                _ui?.AddLog ($"⚠️ Ребаланс не дал достаточного USDC, продолжаем с {spotBalance:F2}");
                        }
                    }

                    List<string> pairs;
                    lock (_pairsLock) { pairs = new List<string> (_activePairs); }
                    if (pairs.Count == 0) { await Task.Delay (5000); continue; }

                    var signals = await AnalyzePairsAsync (pairs);

                    foreach (var sig in signals)
                    {
                        bool hasPos = _positionManager.TryGet (sig.Symbol, out _);
                        if (sig.Action == TradeAction.Buy && !hasPos && _positionManager.Count < 3)
                        {
                            if (spotBalance < 5) continue;
                            spotBalance = await ExecuteBuy (sig, spotBalance);
                        }
                        else if (sig.Action == TradeAction.Sell && hasPos)
                        {
                            await ExecuteSell (sig);
                            spotBalance = await _client.GetAccountBalanceAsync ("USDC");
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

        private async Task HandleTelegramCommand(string command, string chatId)
        {
            string cmd = command.Trim ();
            // Преобразование текста reply-кнопок в системные команды
            switch (cmd)
            {
                case "📊 Статус":
                    cmd = "/status";
                    break;
                case "💼 Баланс":
                    cmd = "/balance";
                    break;
                case "🧠 Переобучить ML":
                    cmd = "/retrain";
                    break;
                case "📁 Экспорт":
                    cmd = "/export";
                    break;
                case "▶️ Запуск":
                    cmd = "/start";
                    break;
                case "⏹️ Стоп":
                    cmd = "/stop";
                    break;
                case "📈 График PnL":
                    cmd = "/pnl";
                    break;
                case "❓ Помощь":
                    cmd = "/help";
                    break;
            }

            switch (cmd)
            {
                case "/status":
                    await _telegram.SendMessageAsync (GetStatusText (), chatId);
                    break;
                case "/balance":
                    decimal bal = _wallet.GetTotalBalance ("USDC");
                    await _telegram.SendMessageAsync ($"💰 Баланс USDC: {bal:F2} (спот + Earn)", chatId);
                    break;
                case "/stop":
                    if (_isRunning)
                    {
                        StopTrading ();
                        await _telegram.SendMessageAsync ("⏹️ Торговля остановлена.", chatId);
                    }
                    else
                        await _telegram.SendMessageAsync ("Бот уже остановлен.", chatId);
                    break;
                case "/start":
                    if (!_isRunning && _ui != null)
                    {
                        await _telegram.SendMessageAsync ("🔄 Перезапуск бота...", chatId);
                        _isRunning = true;
                        _ = Task.Run (TradingLoop);
                        await _telegram.SendMessageAsync ("✅ Бот запущен.", chatId);
                    }
                    else
                        await _telegram.SendMessageAsync ("Бот уже запущен.", chatId);
                    break;
                case "/export":
                    _ui?.ExportData ();
                    await _telegram.SendMessageAsync ("📁 Данные экспортированы в папку Export.", chatId);
                    break;
                case "/retrain":
                    await _telegram.SendMessageAsync ("🔄 Запускаю переобучение ML модели...", chatId);
                    _ = Task.Run (FetchAndRetrainFromOrderHistoryAsync);
                    break;
                case "/pnl":
                    await _telegram.SendMessageAsync (
                        $"📈 Общий PnL: {( _ui?.TotalPnL ?? 0 ):F2} USDC\n" +
                        $"🎯 Win Rate: {( _ui?.WinRate ?? 0 ):F1}%\n" +
                        $"📊 Всего сделок: {( _ui?.TotalTrades ?? 0 )} (✅{_ui?.WinningTrades ?? 0} / ❌{_ui?.LosingTrades ?? 0})",
                        chatId);
                    break;
                case "/help":
                    string help = "🤖 *Команды и кнопки Telegram бота:*\n\n" +
                                  "• /status – состояние бота\n" +
                                  "• /balance – баланс USDC\n" +
                                  "• /stop – остановить торговлю\n" +
                                  "• /start – запустить торговлю\n" +
                                  "• /export – экспорт логов\n" +
                                  "• /retrain – переобучить ML\n" +
                                  "• /pnl – сводная статистика PnL\n" +
                                  "• /help – эта справка\n\n" +
                                  "📱 Используйте кнопки внизу экрана для быстрого доступа";
                    await _telegram.SendMessageAsync (help, chatId);
                    break;
                default:
                    await _telegram.SendMessageAsync ("Неизвестная команда. /help", chatId);
                    break;
            }
        }

        private string GetStatusText()
        {
            string status = _isRunning ? "🟢 Активен" : "🔴 Остановлен";
            decimal balance = _wallet.GetTotalBalance ("USDC");
            int positions = _positionManager.Count;
            return $"🤖 Статус: {status}\n💰 USDC: {balance:F2}\n📊 Открыто позиций: {positions}";
        }
    }
}