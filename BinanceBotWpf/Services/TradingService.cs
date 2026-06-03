using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Globalization;
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
        private DateTime _lastRebalanceAttempt = DateTime.MinValue;
        private readonly TimeSpan _rebalanceCooldown = TimeSpan.FromMinutes (2);
        private DateTime _lastLowBalanceLog = DateTime.MinValue;
        private readonly HashSet<string> _blacklistedSymbols = new () { "FDUSDUSDC" };
        private DateTime _lastOrdersFetch = DateTime.MinValue;
        private readonly TimeSpan _ordersFetchInterval = TimeSpan.FromHours (4);
        private readonly string _telegramBotToken;
        private readonly string _telegramChatId;

        // Список последних ошибок для команды /errors
        private readonly List<string> _recentErrors = new ();
        private const int MaxErrors = 20;

        public TradingService(BinanceClient client, WalletManager wallet, EarnManager earn, BalanceRebalancer rebalancer = null,
                              decimal minUsdcBalance = 5.50m, string telegramBotToken = "", string telegramChatId = "")
        {
            _client = client;
            _wallet = wallet;
            _earn = earn;
            _rebalancer = rebalancer ?? new BalanceRebalancer (new object (), 0.1m);
            _telegramBotToken = telegramBotToken;
            _telegramChatId = telegramChatId;
            string dataDir = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Data");
            string logsDir = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Logs");
            _positionManager = new PositionManager (Path.Combine (dataDir, "open_positions.json"), null);
            _mlManager = new MlModelManager (Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "trading_model.zip"), null);
            _dataLogger = new DataLogger (logsDir, null);
            _balanceManager = new BalanceManager (client, earn, _rebalancer, null);
        }

        private async Task LogErrorToTelegram(string error, bool sendToTelegram = true)
        {
            _ui?.AddLog ($"❌ {error}");
            lock (_recentErrors)
            {
                _recentErrors.Insert (0, $"{DateTime.Now:HH:mm:ss} - {error}");
                if (_recentErrors.Count > MaxErrors)
                    _recentErrors.RemoveAt (_recentErrors.Count - 1);
            }
            if (sendToTelegram && _telegram != null)
                await _telegram.SendErrorNotification (error);
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

            logger ($"🔍 Telegram: token='{( tgToken?.Length > 10 ? tgToken.Substring (0, 10) : tgToken )}...', chatId='{tgChatId}'");

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
            try
            {
                await _positionManager.LoadAsync (_client, GetCurrentPrice, p => _ui.StopLossPercent, p => _ui.TakeProfitPercent);
                _ui?.UpdatePositionsStatus (_positionManager.Count, 3, _positionManager.GetSymbols ());
            }
            catch (Exception ex)
            {
                await LogErrorToTelegram ($"LoadPositions: {ex.Message}");
            }
        }

        private async Task<decimal> GetCurrentPrice(string sym)
        {
            try
            {
                var k = await _client.GetKlinesAsync (sym, "5m", 1);
                return k?.Last ().Close ?? 0;
            }
            catch (Exception ex)
            {
                await LogErrorToTelegram ($"GetCurrentPrice {sym}: {ex.Message}");
                return 0;
            }
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
            catch (Exception ex)
            {
                await LogErrorToTelegram ($"UpdatePairs: {ex.Message}");
            }
        }

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
                await LogErrorToTelegram ($"FetchAndRetrainFromOrderHistoryAsync: {ex.Message}");
            }
        }

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
                catch (Exception ex)
                {
                    await LogErrorToTelegram ($"AnalyzePairsAsync {sym}: {ex.Message}");
                }
            });
            return results.ToList ();
        }

        private decimal CalculateVolatility(List<decimal> data, int period)
        {
            if (data == null || data.Count < period || period <= 0) return 0.02m;
            var last = data.TakeLast (period).ToList ();
            decimal avg = last.Average ();
            if (avg == 0) return 0.02m;
            decimal sumSq = last.Select (x => ( x - avg ) * ( x - avg )).Sum ();
            decimal stdDev = (decimal)Math.Sqrt ((double)( sumSq / period ));
            decimal volatility = stdDev / avg;
            return Math.Min (0.30m, Math.Max (0.005m, volatility));
        }

        private async Task<decimal> ExecuteBuy((string Symbol, TradeAction Action, decimal Price, decimal Rsi,
            decimal FastSma, decimal SlowSma, decimal Volatility, decimal Volume) sig, decimal currentSpotBalance)
        {
            if (currentSpotBalance < 5)
                return currentSpotBalance;

            decimal totalBalance = _wallet.GetTotalBalance ("USDC");

            decimal volatility = sig.Volatility;
            volatility = Math.Min (0.30m, Math.Max (0.005m, volatility));
            decimal baseRisk = _ui.MaxRiskPercent;
            decimal riskMultiplier = Math.Max (0.2m, 1 - ( volatility - 0.02m ) * 10);
            decimal adjustedRisk = baseRisk * riskMultiplier;
            adjustedRisk = Math.Clamp (adjustedRisk, 0.05m, 0.25m);

            decimal spend = totalBalance * adjustedRisk;
            _ui?.AddLog ($"📊 Волатильность: {volatility:P2}, скорректированный риск: {adjustedRisk:P2}");

            if (spend > currentSpotBalance) spend = currentSpotBalance;
            if (spend < 5) return currentSpotBalance;

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
                await Task.Delay (1000);

                decimal stopPrice = sig.Price * ( 1 - _ui.StopLossPercent );
                decimal limitPrice = sig.Price * ( 1 + _ui.TakeProfitPercent );
                long ocoOrderListId = 0;

                var ocoOrder = await _client.PlaceOcoOrder (sig.Symbol, qty, stopPrice, limitPrice);
                if (ocoOrder != null)
                {
                    ocoOrderListId = (long)ocoOrder["orderListId"];
                    _ui?.AddLog ($"✅ OCO-ордер размещён (ID={ocoOrderListId}) | SL={stopPrice:F4}, TP={limitPrice:F4}");
                }
                else
                {
                    await Task.Delay (1000);
                    ocoOrder = await _client.PlaceOcoOrder (sig.Symbol, qty, stopPrice, limitPrice);
                    if (ocoOrder != null)
                    {
                        ocoOrderListId = (long)ocoOrder["orderListId"];
                        _ui?.AddLog ($"✅ OCO-ордер размещён со второй попытки (ID={ocoOrderListId}) | SL={stopPrice:F4}, TP={limitPrice:F4}");
                    }
                    else
                    {
                        _ui?.AddLog ($"⚠️ Не удалось разместить OCO-ордер для {sig.Symbol}: {_client.LastOrderError}. Защита локальная.");
                    }
                }

                var pos = new OpenPosition
                {
                    Symbol = sig.Symbol,
                    Quantity = qty,
                    EntryPrice = sig.Price,
                    OpenTime = DateTime.UtcNow,
                    StopLossPrice = stopPrice,
                    TakeProfitPrice = limitPrice,
                    HighestPrice = sig.Price,
                    OcoOrderListId = ocoOrderListId
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
                    await LogErrorToTelegram ($"ExecuteBuy {sig.Symbol}: {_client.LastOrderError}");
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
            decimal qtyToSell = pos.Quantity;

            if (spotBalance < qtyToSell - 0.000001m && spotBalance > 0)
            {
                decimal stepSize = await _client.GetStepSizeAsync (sig.Symbol);
                qtyToSell = Math.Floor (spotBalance / stepSize) * stepSize;
                if (qtyToSell <= 0)
                {
                    _ui?.AddLog ($"⚠️ Недостаточно {asset} для продажи {sig.Symbol}. Удаляю позицию.");
                    _positionManager.Remove (sig.Symbol);
                    _ui?.UpdatePositionsStatus (_positionManager.Count, 3, _positionManager.GetSymbols ());
                    return;
                }
                _ui?.AddLog ($"⚠️ Корректировка продажи {sig.Symbol}: продаю {qtyToSell} вместо {pos.Quantity} (доступно {spotBalance})");
            }

            if (qtyToSell <= 0)
            {
                _ui?.AddLog ($"⚠️ Нулевое количество для продажи {sig.Symbol}. Удаляю позицию.");
                _positionManager.Remove (sig.Symbol);
                _ui?.UpdatePositionsStatus (_positionManager.Count, 3, _positionManager.GetSymbols ());
                return;
            }

            if (pos.OcoOrderListId != 0)
            {
                bool cancelled = await _client.CancelOcoOrder (sig.Symbol, pos.OcoOrderListId);
                if (cancelled)
                    _ui?.AddLog ($"✅ Отменён OCO-ордер {pos.OcoOrderListId} для {sig.Symbol}");
                else
                    _ui?.AddLog ($"⚠️ Не удалось отменить OCO-ордер {pos.OcoOrderListId} (возможно, уже сработал)");
            }

            var order = await _client.PlaceOrder (sig.Symbol, "SELL", "MARKET", qtyToSell);
            if (order != null)
            {
                decimal pnl = ( sig.Price - pos.EntryPrice ) * qtyToSell;
                decimal pnlPct = ( sig.Price / pos.EntryPrice - 1 ) * 100;
                _ui?.AddLog ($"🔒 ЗАКРЫТА: {sig.Symbol} по {sig.Price:F4} | PnL: {pnl:F2} ({pnlPct:F2}%) | SMA Sell (продано {qtyToSell})");
                var trade = new TradeLog
                {
                    Symbol = sig.Symbol,
                    EntryPrice = pos.EntryPrice,
                    ExitPrice = sig.Price,
                    Quantity = qtyToSell,
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
                if (_client.LastOrderError?.Contains ("Lot size") == true ||
                    _client.LastOrderError?.Contains ("minimum notional") == true ||
                    _client.LastOrderError?.Contains ("quantity below") == true)
                {
                    _ui?.AddLog ($"⚠️ Не удалось продать {sig.Symbol}: остаток слишком мал. Отправляю в конвертацию пыли.");
                    await ConvertDustAssetAsync (asset);
                    _positionManager.Remove (sig.Symbol);
                    _ui?.UpdatePositionsStatus (_positionManager.Count, 3, _positionManager.GetSymbols ());
                }
                else
                {
                    await LogErrorToTelegram ($"ExecuteSell {sig.Symbol}: {_client.LastOrderError}");
                }
            }
        }

        private async Task ConvertDustAssetAsync(string asset)
        {
            try
            {
                var dustList = await _client.GetDustAssetsAsync ();
                if (dustList == null || dustList.Count == 0)
                {
                    _ui?.AddLog ($"⚠️ Нет доступных активов для конвертации пыли.");
                    return;
                }
                var dustItem = dustList.FirstOrDefault (d => d["asset"].ToString () == asset);
                if (dustItem == null)
                {
                    _ui?.AddLog ($"⚠️ Актив {asset} не найден в списке пыли.");
                    return;
                }
                string assetId = dustItem["assetId"].ToString ();
                bool success = await _client.ConvertDustToBnbAsync (new List<string> { assetId });
                if (success)
                    _ui?.AddLog ($"✅ {asset} успешно конвертирован в BNB.");
                else
                    _ui?.AddLog ($"⚠️ Не удалось конвертировать {asset} в BNB.");
            }
            catch (Exception ex)
            {
                await LogErrorToTelegram ($"ConvertDustAssetAsync {asset}: {ex.Message}");
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

                    await BackupSettingsAndModel ();
                }
            }
        }

        private async Task BackupSettingsAndModel()
        {
            try
            {
                string backupDir = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Backups");
                if (!Directory.Exists (backupDir)) Directory.CreateDirectory (backupDir);
                string dateStamp = DateTime.Now.ToString ("yyyyMMdd");
                string modelPath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "trading_model.zip");
                string settingsPath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Data", "strategy_settings.json");
                string modelBackup = Path.Combine (backupDir, $"trading_model_{dateStamp}.zip");
                string settingsBackup = Path.Combine (backupDir, $"strategy_settings_{dateStamp}.json");
                if (File.Exists (modelPath)) File.Copy (modelPath, modelBackup, true);
                if (File.Exists (settingsPath)) File.Copy (settingsPath, settingsBackup, true);
                var cutoff = DateTime.Now.AddDays (-7);
                foreach (var file in Directory.GetFiles (backupDir, "trading_model_*.zip"))
                {
                    if (File.GetCreationTime (file) < cutoff) File.Delete (file);
                }
                foreach (var file in Directory.GetFiles (backupDir, "strategy_settings_*.json"))
                {
                    if (File.GetCreationTime (file) < cutoff) File.Delete (file);
                }
            }
            catch (Exception ex)
            {
                await LogErrorToTelegram ($"BackupSettingsAndModel: {ex.Message}");
            }
        }

        private void RotateLogs()
        {
            try
            {
                string logsDir = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Logs");
                if (!Directory.Exists (logsDir)) return;
                var cutoff = DateTime.Now.AddDays (-30);
                foreach (var file in Directory.GetFiles (logsDir))
                {
                    var info = new FileInfo (file);
                    if (info.CreationTime < cutoff || info.LastWriteTime < cutoff)
                        File.Delete (file);
                }
            }
            catch (Exception ex) { _ui?.AddLog ($"Ошибка ротации логов: {ex.Message}"); }
        }

        private void ArchiveLogs()
        {
            try
            {
                string logsDir = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Logs");
                if (!Directory.Exists (logsDir)) return;
                var files = Directory.GetFiles (logsDir);
                if (files.Length == 0) return;

                string archiveDir = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Archives");
                if (!Directory.Exists (archiveDir)) Directory.CreateDirectory (archiveDir);
                string zipName = $"logs_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
                string zipPath = Path.Combine (archiveDir, zipName);
                ZipFile.CreateFromDirectory (logsDir, zipPath);
                foreach (var file in files) File.Delete (file);
                _ui?.AddLog ($"📦 Логи заархивированы: {zipPath}");
            }
            catch (Exception ex) { _ui?.AddLog ($"❌ Ошибка архивации: {ex.Message}"); }
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
                var ids = dust.Select (item => item["assetId"]?.ToString ()).Where (id => !string.IsNullOrEmpty (id)).ToList ();
                if (ids.Count == 0) continue;
                _ui?.AddLog ($"🧹 Конвертирую пыль ({ids.Count} активов)");
                await _client.ConvertDustToBnbAsync (ids);
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
                    await LogErrorToTelegram ($"TradingLoop: {ex.Message}");
                    await Task.Delay (10000);
                }
            }
        }

        private async Task HandleTelegramCommand(string command, string chatId)
        {
            string cmd = command.Trim ();
            _ui?.AddLog ($"📨 Получена команда: '{cmd}'");

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

                case "/update":
                    await _telegram.SendMessageAsync ("🔄 Проверяю обновления...", chatId);
                    var updater = new UpdateManager (msg => _ui?.AddLog (msg));
                    bool updated = await updater.CheckAndUpdateAsync (silent: false);
                    if (!updated)
                        await _telegram.SendMessageAsync ("✅ Обновлений не найдено или ошибка.", chatId);
                    break;

                case "/dust":
                    await _telegram.SendMessageAsync ("🧹 Запускаю конвертацию пыли в BNB...", chatId);
                    await _client.ConvertDustToBnbAsync (null);
                    await _telegram.SendMessageAsync ("✅ Конвертация пыли выполнена (или запущена).", chatId);
                    break;

                case "/errors":
                    string errors;
                    lock (_recentErrors)
                    {
                        errors = _recentErrors.Count == 0 ? "✅ Нет ошибок" : string.Join ("\n", _recentErrors);
                    }
                    await _telegram.SendMessageAsync ($"📋 <b>Последние ошибки ({_recentErrors.Count}):</b>\n{errors}", chatId);
                    break;

                case "/convert":
                    var parts = command.Split (' ');
                    if (parts.Length != 4)
                    {
                        await _telegram.SendMessageAsync ("❗ Использование: /convert FROM TO AMOUNT\nПример: /convert USDC USDT 10", chatId);
                        break;
                    }
                    string fromAsset = parts[1].ToUpper ();
                    string toAsset = parts[2].ToUpper ();
                    if (!decimal.TryParse (parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal convertAmount))
                    {
                        await _telegram.SendMessageAsync ("❌ Ошибка: сумма должна быть числом (точка как разделитель).", chatId);
                        break;
                    }
                    await _telegram.SendMessageAsync ($"🔄 Конвертирую {convertAmount} {fromAsset} → {toAsset}...", chatId);
                    bool convertSuccess = await _client.ConvertAssetAsync (fromAsset, toAsset, convertAmount);
                    if (convertSuccess)
                        await _telegram.SendMessageAsync ($"✅ Конвертация {convertAmount} {fromAsset} → {toAsset} успешно выполнена!", chatId);
                    else
                        await _telegram.SendMessageAsync ($"❌ Не удалось конвертировать {convertAmount} {fromAsset} → {toAsset}. Проверьте баланс и права API.", chatId);
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
                                  "• /update – проверить обновления\n" +
                                  "• /dust – конвертировать пыль в BNB\n" +
                                  "• /errors – показать последние ошибки\n" +
                                  "• /convert FROM TO AMOUNT – конвертировать активы\n" +
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

        public void StopTrading() => _isRunning = false;
    }
}