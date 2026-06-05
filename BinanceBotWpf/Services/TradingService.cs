using System;
using System.IO;
using System.IO.Compression;
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
    /// <summary>
    /// Основной сервис торговли: управление циклами, анализ пар, покупка/продажа, ребаланс, Telegram.
    /// </summary>
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
        private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds (60);
        private DateTime _lastRebalanceAttempt = DateTime.MinValue;
        private readonly TimeSpan _rebalanceCooldown = TimeSpan.FromMinutes (2);
        private DateTime _lastLowBalanceLog = DateTime.MinValue;
        private readonly HashSet<string> _blacklistedSymbols = new () { "FDUSDUSDC" };
        private DateTime _lastOrdersFetch = DateTime.MinValue;
        private readonly TimeSpan _ordersFetchInterval = TimeSpan.FromHours (4);
        private readonly string _telegramBotToken;
        private readonly string _telegramChatId;
        private readonly List<string> _recentErrors = new ();
        private const int MaxErrors = 20;
        private decimal _totalProfitSum = 0;
        private decimal _totalLossSum = 0;

        private bool _balanceLoopEnabled = true;
        private bool _pairsLoopEnabled = true;
        private bool _earnLoopEnabled = true;
        private bool _dustLoopEnabled = true;
        private bool _autoRetrainLoopEnabled = true;
        private bool _orderHistoryLoopEnabled = true;
        private bool _tradingLoopEnabled = true;

        /// <summary>Конструктор сервиса торговли.</summary>
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
                if (_recentErrors.Count > MaxErrors) _recentErrors.RemoveAt (_recentErrors.Count - 1);
            }
            if (sendToTelegram && _telegram != null)
                await _telegram.SendErrorNotification (error);
        }

        /// <summary>Настройка логирования для всех менеджеров и инициализация Telegram.</summary>
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

        /// <summary>Запуск всех циклов бота.</summary>
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
            if (_client.IsTestnet)
                _ui?.AddLog ("⚠️ РАБОТА В ТЕСТОВОЙ СЕТИ BINANCE (testnet). Все операции не настоящие.");
            else
                _ui?.AddLog ("✅ Реальная сеть Binance (mainnet).");
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
                var newPairs = await _client.GetTopVolumePairsAsync ("USDC", 15);
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
                    _ui?.AddLog ("⚠️ Не найдено активных пар");
                }
            }
            catch (Exception ex) { _ui?.AddLog ($"❌ Ошибка обновления пар: {ex.Message}"); }
        }

        // ==================== ФОНОВЫЕ ЦИКЛЫ ====================
        private async Task BalanceLoop()
        {
            while (_isRunning)
            {
                if (!_balanceLoopEnabled) { await Task.Delay (5000); continue; }
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
                    RotateLogs ();
                    ArchiveLogs ();
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
                    if (File.GetCreationTime (file) < cutoff) File.Delete (file);
                foreach (var file in Directory.GetFiles (backupDir, "strategy_settings_*.json"))
                    if (File.GetCreationTime (file) < cutoff) File.Delete (file);
            }
            catch (Exception ex) { await LogErrorToTelegram ($"BackupSettingsAndModel: {ex.Message}"); }
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
                if (!_pairsLoopEnabled) { await Task.Delay (5000); continue; }
                await Task.Delay (30 * 60000);
                if (!_isRunning) break;
                await UpdatePairs ();
            }
        }

        private async Task EarnLoop()
        {
            while (_isRunning)
            {
                if (!_earnLoopEnabled) { await Task.Delay (5000); continue; }
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
                if (!_dustLoopEnabled) { await Task.Delay (5000); continue; }
                await Task.Delay (24 * 3600000);
                if (!_isRunning) break;
                var dust = await _client.GetDustAssetsAsync ();
                if (dust == null || dust.Count == 0) continue;
                var ids = dust.Select (item => item["assetId"]?.ToString ()).Where (id => !string.IsNullOrEmpty (id)).ToList ();
                if (ids.Count == 0) continue;
                _ui?.AddLog ($"🧹 Конвертирую пыль ({ids.Count} активов) в USDC...");
                await _client.ConvertDustToUsdcAsync (ids);
            }
        }

        private async Task AutoRetrainLoop()
        {
            while (_isRunning)
            {
                if (!_autoRetrainLoopEnabled) { await Task.Delay (5000); continue; }
                await Task.Delay (TimeSpan.FromHours (24));
                if (!_isRunning) break;
                await FetchAndRetrainFromOrderHistoryAsync ();
            }
        }

        private async Task OrderHistoryCollectorLoop()
        {
            while (_isRunning)
            {
                if (!_orderHistoryLoopEnabled) { await Task.Delay (5000); continue; }
                await Task.Delay (_ordersFetchInterval);
                if (!_isRunning) break;
                await FetchAndRetrainFromOrderHistoryAsync ();
            }
        }

        /// <summary>
        /// Сбор истории ордеров из API Binance и переобучение ML-модели на основе закрытых позиций.
        /// </summary>
        /// <summary>
        /// Сбор истории ордеров и переобучение ML-модели.
        /// </summary>
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

                int profitableCount = allClosedTrades.Count (t => t.IsProfitable);
                int unprofitableCount = allClosedTrades.Count (t => !t.IsProfitable);
                _ui?.AddLog ($"📊 Найдено {allClosedTrades.Count} закрытых позиций (прибыльных: {profitableCount}, убыточных: {unprofitableCount})");

                if (allClosedTrades.Count < 30)
                {
                    _ui?.AddLog ($"⚠️ Недостаточно сделок ({allClosedTrades.Count}) для обучения, требуется 30.");
                    return;
                }

                if (profitableCount == 0 || unprofitableCount == 0)
                {
                    _ui?.AddLog ($"⚠️ Нет одновременно прибыльных и убыточных сделок. Обучение отложено.");
                    return;
                }

                var features = new List<(decimal FastSma, decimal SlowSma, decimal Rsi, decimal VolumeRatio, decimal Atr, decimal MacdHistogram, decimal BbWidth, decimal Obv, bool IsProfitable)> ();
                foreach (var trade in allClosedTrades)
                {
                    try
                    {
                        var klines = await _client.GetKlinesAsync (trade.Symbol, "5m", 50);
                        if (klines == null || klines.Count < Math.Max (_ui.FastSma, _ui.SlowSma) + 2) continue;
                        var closes = klines.Select (k => k.Close).ToList ();
                        var volumes = klines.Select (k => k.Volume).ToList ();
                        decimal fastSmaVal = closes.Skip (closes.Count - _ui.FastSma).Average ();
                        decimal slowSmaVal = closes.Skip (closes.Count - _ui.SlowSma).Average ();
                        decimal rsi = TechnicalAnalysis.RSI (closes, 14).LastOrDefault () ?? 50;
                        decimal avgVolume = volumes.TakeLast (20).Average ();
                        decimal volumeRatio = volumes.Last () / avgVolume;
                        decimal atr = await _client.GetATRAsync (trade.Symbol, 14);
                        var macd = TechnicalAnalysis.MACD (closes, 12, 26, 9);
                        decimal macdHist = macd.Histogram.LastOrDefault () ?? 0;
                        var bb = TechnicalAnalysis.BollingerBands (closes, 20, 2);
                        decimal bbUpper = bb.Upper.LastOrDefault () ?? closes.Last ();
                        decimal bbLower = bb.Lower.LastOrDefault () ?? closes.Last ();
                        decimal bbMiddle = bb.Middle.LastOrDefault () ?? closes.Last ();
                        decimal bbWidth = ( bbUpper - bbLower ) / ( bbMiddle + 0.0001m );

                        // OBV (On-Balance Volume)
                        var obvValues = TechnicalAnalysis.OBV (klines);
                        decimal obvLast = obvValues.Last ();
                        decimal obvNormalized = (decimal)Math.Log10 (Math.Abs ((double)obvLast) + 1);

                        features.Add ((fastSmaVal, slowSmaVal, rsi, volumeRatio, atr, macdHist, bbWidth, obvNormalized, trade.IsProfitable));
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

        private decimal CalculateSma(List<decimal> data, int period) => data.Skip (data.Count - period).Average ();
        private decimal CalculateRsi(List<decimal> closes) => TechnicalAnalysis.RSI (closes, 14).LastOrDefault () ?? 50;
        private decimal CalculateVolatility(List<decimal> data, int period)
        {
            if (data == null || data.Count < period || period <= 0) return 0.02m;
            var last = data.TakeLast (period).ToList ();
            decimal avg = last.Average ();
            if (avg == 0 || avg > 1_000_000m) return 0.02m;
            decimal sumSq = last.Select (x => ( x - avg ) * ( x - avg )).Sum ();
            decimal stdDev = (decimal)Math.Sqrt ((double)( sumSq / period ));
            decimal volatility = stdDev / avg;
            if (volatility > 1.0m || volatility < 0.001m) return 0.02m;
            return Math.Min (0.30m, Math.Max (0.005m, volatility));
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

        // ==================== АНАЛИЗ СИГНАЛОВ ====================
        /// <summary>Анализирует все пары: рассчитывает SMA, RSI, MACD, Bollinger Bands, фильтрует по объёму и возвращает сигналы.</summary>
        private async Task<List<(string Symbol, TradeAction Action, decimal Price, decimal Rsi, decimal FastSma, decimal SlowSma, decimal Volatility, decimal Volume, decimal AvgVolume, decimal MacdHistogram, decimal BbWidth, decimal Obv)>> AnalyzePairsAsync(List<string> pairs)
        {
            var results = new ConcurrentBag<(string, TradeAction, decimal, decimal, decimal, decimal, decimal, decimal, decimal, decimal, decimal, decimal)> ();
            await Parallel.ForEachAsync (pairs, async (sym, ct) =>
            {
                try
                {
                    var klines = await GetKlinesCachedAsync (sym, "5m", 50);
                    if (klines?.Count < Math.Max (_ui.FastSma, _ui.SlowSma) + 2) return;
                    var closes = klines.Select (k => k.Close).ToList ();
                    var volumes = klines.Select (k => k.Volume).ToList ();
                    var obvValues = TechnicalAnalysis.OBV (klines);
                    decimal obvLast = obvValues.Last ();
                    decimal obvNormalized = (decimal)Math.Log10 (Math.Abs ((double)obvLast) + 1);
                    decimal price = closes.Last ();
                    decimal volume = volumes.Last ();
                    decimal avgVolume = volumes.TakeLast (20).Average ();
                    if (volume < avgVolume * 0.8m) return;

                    var signal = _strategy.AnalyzePairWithWallet (sym, closes, _ui.FastSma, _ui.SlowSma, price);
                    decimal rsi = CalculateRsi (closes);
                    decimal fastSma = CalculateSma (closes, _ui.FastSma);
                    decimal slowSma = CalculateSma (closes, _ui.SlowSma);
                    decimal volatility = CalculateVolatility (closes, 20);
                    var macd = TechnicalAnalysis.MACD (closes, 12, 26, 9);
                    decimal macdHist = macd.Histogram.LastOrDefault () ?? 0;
                    var bb = TechnicalAnalysis.BollingerBands (closes, 20, 2);
                    decimal bbUpper = bb.Upper.LastOrDefault () ?? price;
                    decimal bbLower = bb.Lower.LastOrDefault () ?? price;
                    decimal bbMiddle = bb.Middle.LastOrDefault () ?? price;
                    decimal bbWidth = ( bbUpper - bbLower ) / ( bbMiddle + 0.0001m );

                    if (signal.Action == TradeAction.Buy && ( price <= bbLower || macdHist > 0 ))
                        signal.Action = TradeAction.Buy;
                    else if (signal.Action == TradeAction.Sell && ( price >= bbUpper || macdHist < 0 ))
                        signal.Action = TradeAction.Sell;
                    else
                        signal.Action = TradeAction.Hold;

                    _ui.UpdateMarketTable (sym, price.ToString ("F4"), _positionManager.TryGet (sym, out _), signal.Action, fastSma, slowSma);
                    results.Add ((sym, signal.Action, price, rsi, fastSma, slowSma, volatility, volume, avgVolume, macdHist, bbWidth, obvNormalized));
                }
                catch (Exception ex) { await LogErrorToTelegram ($"AnalyzePairsAsync {sym}: {ex.Message}"); }
            });
            return results.ToList ();
        }

        // ==================== ПОКУПКА ====================
        /// <summary>Выполняет рыночную покупку на фиксированную сумму 10 USDC (упрощённая версия для накопления истории).</summary>
        private async Task<decimal> ExecuteBuy((string Symbol, TradeAction Action, decimal Price, decimal Rsi,
     decimal FastSma, decimal SlowSma, decimal Volatility, decimal Volume, decimal AvgVolume,
     decimal MacdHistogram, decimal BbWidth, decimal Obv) sig, decimal currentSpotBalance)
        {
            if (currentSpotBalance < 10) return currentSpotBalance;
            decimal spend = 10;
            decimal rawQty = spend / sig.Price;
            decimal stepSize = await _client.GetStepSizeAsync (sig.Symbol);
            decimal qty = Math.Floor (rawQty / stepSize) * stepSize;
            if (qty <= 0) return currentSpotBalance;
            decimal required = qty * sig.Price;
            if (required > currentSpotBalance) return currentSpotBalance;

            _ui?.AddLog ($"💵 Покупка {qty} {sig.Symbol} по {sig.Price}, сумма ~{required:F2} USDC");
            var order = await _client.PlaceOrder (sig.Symbol, "BUY", "MARKET", qty);
            if (order != null)
            {
                _ui?.AddLog ($"✅ КУПЛЕНО: {qty} {sig.Symbol} по {sig.Price}");
                var pos = new OpenPosition
                {
                    Symbol = sig.Symbol,
                    Quantity = qty,
                    EntryPrice = sig.Price,
                    OpenTime = DateTime.UtcNow,
                    StopLossPrice = sig.Price * ( 1 - _ui.StopLossPercent ),
                    TakeProfitPrice = sig.Price * ( 1 + _ui.TakeProfitPercent ),
                    HighestPrice = sig.Price,
                    OcoOrderListId = 0
                };
                _positionManager.AddOrUpdate (sig.Symbol, pos);
                _ui?.UpdatePositionsStatus (_positionManager.Count, 3, _positionManager.GetSymbols ());
                return currentSpotBalance - required;
            }
            return currentSpotBalance;
        }

        // ==================== ПРОДАЖА ====================
        /// <summary>Закрывает позицию рыночным ордером, отменяет OCO-ордер.</summary>
        private async Task ExecuteSell((string Symbol, TradeAction Action, decimal Price, decimal Rsi,
    decimal FastSma, decimal SlowSma, decimal Volatility, decimal Volume, decimal AvgVolume,
    decimal MacdHistogram, decimal BbWidth, decimal Obv) sig)
        {
            if (!_positionManager.TryGet (sig.Symbol, out var pos)) return;
            string asset = sig.Symbol.Replace ("USDC", "");
            decimal spotBalance = await _client.GetAccountBalanceAsync (asset);

            if (spotBalance < 0.000001m)
            {
                _ui?.AddLog ($"⚠️ Нет {asset} на споте для продажи {sig.Symbol}. Удаляю позицию.");
                _positionManager.Remove (sig.Symbol);
                _ui?.UpdatePositionsStatus (_positionManager.Count, 3, _positionManager.GetSymbols ());
                return;
            }

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
                _positionManager.Remove (sig.Symbol);
                _ui?.UpdatePositionsStatus (_positionManager.Count, 3, _positionManager.GetSymbols ());
                return;
            }

            if (pos.OcoOrderListId != 0)
            {
                bool cancelled = await _client.CancelOcoOrder (sig.Symbol, pos.OcoOrderListId);
                if (cancelled) _ui?.AddLog ($"✅ Отменён OCO-ордер {pos.OcoOrderListId}");
                else _ui?.AddLog ($"⚠️ Не удалось отменить OCO-ордер {pos.OcoOrderListId}");
            }

            var order = await _client.PlaceOrder (sig.Symbol, "SELL", "MARKET", qtyToSell);
            if (order != null)
            {
                decimal pnl = ( sig.Price - pos.EntryPrice ) * qtyToSell;
                decimal pnlPct = ( sig.Price / pos.EntryPrice - 1 ) * 100;
                _ui?.AddLog ($"🔒 ЗАКРЫТА: {sig.Symbol} по {sig.Price:F4} | PnL: {pnl:F2} ({pnlPct:F2}%)");
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
                if (_client.LastOrderError?.Contains ("Lot size") == true || _client.LastOrderError?.Contains ("minimum notional") == true)
                    await ConvertDustAssetAsync (asset);
                else
                    await LogErrorToTelegram ($"ExecuteSell {sig.Symbol}: {_client.LastOrderError}");
            }
        }

        private async Task ConvertDustAssetAsync(string asset)
        {
            try
            {
                var dustList = await _client.GetDustAssetsAsync ();
                var assetId = dustList?.FirstOrDefault (d => d["asset"].ToString () == asset)?["assetId"].ToString ();
                if (!string.IsNullOrEmpty (assetId))
                {
                    _ui?.AddLog ($"🔄 Добавляю {asset} в конвертацию пыли в BNB.");
                    bool success = await _client.ConvertDustToBnbAsync (new List<string> { assetId });
                    if (success) _ui?.AddLog ($"✅ {asset} конвертирован в BNB.");
                    else _ui?.AddLog ($"⚠️ Не удалось конвертировать {asset} в BNB.");
                }
                else
                {
                    _ui?.AddLog ($"⚠️ Актив {asset} не найден в списке допустимых для конвертации пыли.");
                }
            }
            catch (Exception ex) { await LogErrorToTelegram ($"ConvertDustAssetAsync {asset}: {ex.Message}"); }
        }

        // ==================== ЗАЩИТА ПОЗИЦИЙ ====================
        /// <summary>Проверяет стоп-лосс, тейк-профит, трейлинг-стоп, трейлинг-тейк-профит и частичное закрытие при +5%.</summary>
        private async Task CheckProtections()
        {
            var toClose = new List<string> ();
            foreach (var sym in _positionManager.GetSymbols ())
            {
                if (!_positionManager.TryGet (sym, out var pos)) continue;
                decimal price = await GetCurrentPrice (sym);
                if (price <= 0) continue;

                // Трейлинг-стоп
                if (price > pos.HighestPrice)
                {
                    pos.HighestPrice = price;
                    decimal newSl = pos.HighestPrice * ( 1 - _ui.TrailingStopPercent );
                    if (newSl > pos.StopLossPrice) pos.StopLossPrice = newSl;
                }

                // Трейлинг-тейк-профит
                if (price > pos.HighestPriceSinceOpen)
                {
                    pos.HighestPriceSinceOpen = price;
                    decimal profitPercent = ( price - pos.EntryPrice ) / pos.EntryPrice;
                    if (profitPercent > 0.02m)
                    {
                        decimal newTP = price * ( 1 + _ui.TakeProfitPercent );
                        if (newTP > pos.TakeProfitPrice)
                        {
                            pos.TakeProfitPrice = newTP;
                            _ui?.AddLog ($"📈 Трейлинг TP для {sym}: повышен до {newTP:F4}");
                            await UpdateOcoOrder (sym, pos);
                        }
                    }
                }

                // Частичный тейк-профит (при +5%)
                if (price >= pos.EntryPrice * 1.05m && pos.Quantity > 0)
                {
                    decimal stepSize = await _client.GetStepSizeAsync (sym);
                    decimal closeQty = Math.Floor (pos.Quantity / 2 / stepSize) * stepSize;
                    if (closeQty > 0.000001m)
                    {
                        var order = await _client.PlaceOrder (sym, "SELL", "MARKET", closeQty);
                        if (order != null)
                        {
                            _ui?.AddLog ($"🎯 Частичная фиксация: продано {closeQty} {sym} по {price:F4} (+5%)");
                            pos.Quantity -= closeQty;
                            if (pos.Quantity <= 0)
                            {
                                toClose.Add (sym);
                            }
                            else
                            {
                                pos.StopLossPrice = pos.EntryPrice;
                                _ui?.AddLog ($"🛡️ Стоп-лосс для {sym} перемещён в безубыток: {pos.StopLossPrice:F4}");
                                await UpdateOcoOrder (sym, pos);
                            }
                        }
                    }
                }

                // Полное закрытие по стопу, тейку или времени
                if (price <= pos.StopLossPrice || price >= pos.TakeProfitPrice || DateTime.UtcNow - pos.OpenTime > TimeSpan.FromHours (2))
                    toClose.Add (sym);
            }
            foreach (var sym in toClose)
            {
                decimal price = await GetCurrentPrice (sym);
                await ExecuteSell ((sym, TradeAction.Sell, price, 0, 0, 0, 0, 0, 0, 0, 0, 0));
            }
        }

        private async Task UpdateOcoOrder(string symbol, OpenPosition pos)
        {
            if (pos.OcoOrderListId != 0)
                await _client.CancelOcoOrder (symbol, pos.OcoOrderListId);
            var newOco = await _client.PlaceOcoOrder (symbol, pos.Quantity, pos.StopLossPrice, pos.TakeProfitPrice);
            if (newOco != null)
            {
                pos.OcoOrderListId = (long)newOco["orderListId"];
                _ui?.AddLog ($"🔄 OCO-ордер обновлён (ID={pos.OcoOrderListId}) | SL={pos.StopLossPrice:F4}, TP={pos.TakeProfitPrice:F4}");
            }
        }

        private async Task TryAutoRetrainAsync()
        {
            if (DateTime.UtcNow - _lastRetrainTime < _minRetrainInterval) return;
            _ui?.AddLog ("🔄 Автоматическое переобучение ML модели...");
            await FetchAndRetrainFromOrderHistoryAsync ();
            _lastRetrainTime = DateTime.UtcNow;
        }

        // ==================== ГЛАВНЫЙ ТОРГОВЫЙ ЦИКЛ ====================
        /// <summary>Основной цикл: защита, баланс, ребаланс, анализ, покупка/продажа.</summary>
        private async Task TradingLoop()
        {
            while (_isRunning)
            {
                try
                {
                    if (!_tradingLoopEnabled) { await Task.Delay (5000); continue; }

                    await CheckProtections ();

                    decimal spotBalance = await _client.GetAccountBalanceAsync ("USDC");
                    decimal totalBalance = _wallet.GetTotalBalance ("USDC");
                    _ui?.UpdateWalletDisplay (totalBalance.ToString ("F2"));
                    if (DateTime.UtcNow - _lastBalanceLog > TimeSpan.FromSeconds (60))
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
                            if (spotBalance < 10) continue;
                            spotBalance = await ExecuteBuy (sig, spotBalance);
                        }
                        else if (sig.Action == TradeAction.Sell && hasPos)
                        {
                            await ExecuteSell (sig);
                            spotBalance = await _client.GetAccountBalanceAsync ("USDC");
                        }
                    }

                    await Task.Delay (30000);
                }
                catch (Exception ex)
                {
                    await LogErrorToTelegram ($"TradingLoop: {ex.Message}");
                    await Task.Delay (10000);
                }
            }
        }

        // ==================== TELEGRAM КОМАНДЫ ====================
        private async Task HandleTelegramCommand(string command, string chatId)
        {
            string cmd = command.Trim ();
            // Преобразование reply-кнопок
            switch (cmd)
            {
                case "📊 Статус": cmd = "/status"; break;
                case "💼 Баланс": cmd = "/balance"; break;
                case "🧠 Переобучить ML": cmd = "/retrain"; break;
                case "📁 Экспорт": cmd = "/export"; break;
                case "▶️ Запуск": cmd = "/start"; break;
                case "⏹️ Стоп": cmd = "/stop"; break;
                case "📈 График PnL": cmd = "/chart"; break;
                case "❓ Помощь": cmd = "/help"; break;
            }

            switch (cmd)
            {
                case "/status":
                    await _telegram.SendMessageAsync (GetStatusText (), chatId);
                    break;
                case "/balance":
                    await _telegram.SendMessageAsync ($"💰 Баланс USDC: {_wallet.GetTotalBalance ("USDC"):F2} (спот + Earn)", chatId);
                    break;
                case "/stop":
                    if (_isRunning) { StopTrading (); await _telegram.SendMessageAsync ("⏹️ Торговля остановлена.", chatId); }
                    else await _telegram.SendMessageAsync ("Бот уже остановлен.", chatId);
                    break;
                case "/start":
                    if (!_isRunning && _ui != null)
                    {
                        await _telegram.SendMessageAsync ("🔄 Перезапуск бота...", chatId);
                        _isRunning = true;
                        _ = Task.Run (TradingLoop);
                        await _telegram.SendMessageAsync ("✅ Бот запущен.", chatId);
                    }
                    else await _telegram.SendMessageAsync ("Бот уже запущен.", chatId);
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
                    await _telegram.SendMessageAsync ($"📈 Общий PnL: {_ui?.TotalPnL ?? 0:F2} USDC\n🎯 Win Rate: {_ui?.WinRate ?? 0:F1}%", chatId);
                    break;
                case "/update":
                    await _telegram.SendMessageAsync ("🔄 Проверяю обновления...", chatId);
                    var updater = new UpdateManager (msg => _ui?.AddLog (msg));
                    bool updated = await updater.CheckAndUpdateAsync (silent: false);
                    if (!updated) await _telegram.SendMessageAsync ("✅ Обновлений не найдено.", chatId);
                    break;
                case "/dust":
                    await _telegram.SendMessageAsync ("🧹 Запускаю конвертацию пыли...", chatId);
                    await _client.ConvertDustToUsdcAsync (null);
                    await _telegram.SendMessageAsync ("✅ Конвертация пыли выполнена.", chatId);
                    break;
                case "/errors":
                    string errors;
                    lock (_recentErrors) { errors = _recentErrors.Count == 0 ? "✅ Нет ошибок" : string.Join ("\n", _recentErrors); }
                    await _telegram.SendMessageAsync ($"📋 <b>Последние ошибки:</b>\n{errors}", chatId);
                    break;
                case "/performance":
                    await _telegram.SendMessageAsync (GetPerformanceStats (), chatId);
                    break;
                case "/chart":
                    await SendPnlChartAsync (chatId);
                    break;
                case "/stop_all":
                    StopAllLoops ();
                    await _telegram.SendMessageAsync ("⏸️ Все циклы остановлены.", chatId);
                    break;
                case "/start_all":
                    StartAllLoops ();
                    await _telegram.SendMessageAsync ("▶️ Все циклы запущены.", chatId);
                    break;
                case "/help":
                    string help = "🤖 *Команды:*\n/status – состояние\n/balance – баланс\n/stop – стоп торговли\n/start – старт\n/export – экспорт\n/retrain – переобучить ML\n/pnl – статистика PnL\n/update – обновление\n/dust – конвертация пыли\n/errors – ошибки\n/performance – детальная статистика\n/stop_all – остановить все циклы\n/start_all – запустить все циклы\n/help – помощь";
                    await _telegram.SendMessageAsync (help, chatId);
                    break;
                default:
                    await _telegram.SendMessageAsync ("Неизвестная команда. /help", chatId);
                    break;
            }
        }

        // ==================== ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ====================
        private async Task SendPnlChartAsync(string chatId)
        {
            try
            {
                var model = new OxyPlot.PlotModel { Title = "Баланс USDC", Background = OxyPlot.OxyColors.White, TextColor = OxyPlot.OxyColors.Black };
                var series = new OxyPlot.Series.LineSeries { Color = OxyPlot.OxyColors.Green, MarkerType = OxyPlot.MarkerType.Circle, MarkerSize = 3 };
                var originalSeries = _ui.PlotModel.Series[0] as OxyPlot.Series.LineSeries;
                if (originalSeries != null)
                    foreach (var point in originalSeries.Points)
                        series.Points.Add (point);
                model.Series.Add (series);
                model.Axes.Add (new OxyPlot.Axes.DateTimeAxis { Position = OxyPlot.Axes.AxisPosition.Bottom, StringFormat = "HH:mm", Title = "Время" });
                model.Axes.Add (new OxyPlot.Axes.LinearAxis { Position = OxyPlot.Axes.AxisPosition.Left, Title = "USDC" });
                string tempFile = Path.GetTempFileName () + ".png";
                using (var stream = File.OpenWrite (tempFile))
                {
                    var exporter = new OxyPlot.Wpf.PngExporter { Width = 800, Height = 400 };
                    exporter.Export (model, stream);
                }
                using (var fs = File.OpenRead (tempFile))
                    await _telegram.SendPhotoAsync (chatId, fs, "📈 График баланса USDC");
                File.Delete (tempFile);
            }
            catch (Exception ex)
            {
                await _telegram.SendMessageAsync ($"❌ Ошибка создания графика: {ex.Message}", chatId);
            }
        }

        private string GetPerformanceStats()
        {
            var totalTrades = _ui.TotalTrades;
            if (totalTrades == 0) return "Нет сделок для статистики.";
            var wins = _ui.WinningTrades;
            var losses = _ui.LosingTrades;
            var totalPnL = _ui.TotalPnL;
            var winRate = wins * 100.0m / totalTrades;
            var avgWin = wins > 0 ? _totalProfitSum / wins : 0;
            var avgLoss = losses > 0 ? Math.Abs (_totalLossSum / losses) : 0;
            var profitFactor = avgLoss > 0 ? avgWin / avgLoss : 0;
            return $"📊 Статистика торговли\n📈 Общий PnL: {totalPnL:F2} USDC\n🎯 Win Rate: {winRate:F1}% ({wins}/{totalTrades})\n📉 Макс. просадка: {_ui.MaxDrawdownDisplay}\n💰 Средняя прибыль: {avgWin:F2}\n💸 Средний убыток: {avgLoss:F2}\n⚖️ Фактор прибыли: {profitFactor:F2}";
        }

        private string GetStatusText()
        {
            string status = _isRunning ? "🟢 Активен" : "🔴 Остановлен";
            decimal balance = _wallet.GetTotalBalance ("USDC");
            int positions = _positionManager.Count;
            return $"🤖 Статус: {status}\n💰 USDC: {balance:F2}\n📊 Открыто позиций: {positions}";
        }

        private void StopAllLoops()
        {
            _balanceLoopEnabled = false;
            _pairsLoopEnabled = false;
            _earnLoopEnabled = false;
            _dustLoopEnabled = false;
            _autoRetrainLoopEnabled = false;
            _orderHistoryLoopEnabled = false;
            _tradingLoopEnabled = false;
            _ui?.AddLog ("⏸️ Все циклы остановлены");
        }

        private void StartAllLoops()
        {
            _balanceLoopEnabled = true;
            _pairsLoopEnabled = true;
            _earnLoopEnabled = true;
            _dustLoopEnabled = true;
            _autoRetrainLoopEnabled = true;
            _orderHistoryLoopEnabled = true;
            _tradingLoopEnabled = true;
            _ui?.AddLog ("▶️ Все циклы запущены");
        }

        /// <summary>Остановка всех циклов и завершение работы.</summary>
        public void StopTrading() => _isRunning = false;
    }
}