using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.ML;
using Microsoft.ML.Data;
using BinanceBotWpf.Models;
using BinanceBotWpf.ViewModels;
using System.IO.Compression;
using System.Globalization;
using CsvHelper;

namespace BinanceBotWpf.Services
{
    public class ModelInput
    {
        public float FastSma { get; set; }
        public float SlowSma { get; set; }
        public float Rsi { get; set; }
        public float Volume { get; set; }
        public float Volatility { get; set; }
    }

    public class ModelOutput
    {
        [ColumnName ("PredictedLabel")]
        public bool IsProfitable { get; set; }
        public float Probability { get; set; }
    }

    public class TradingService
    {
        private class OpenPosition
        {
            public string Symbol { get; set; }
            public decimal Quantity { get; set; }
            public decimal EntryPrice { get; set; }
            public DateTime OpenTime { get; set; }
            public decimal StopLossPrice { get; set; }
            public decimal TakeProfitPrice { get; set; }
            public decimal HighestPrice { get; set; }
        }

        private readonly BinanceClient _client;
        private readonly WalletManager _wallet;
        private readonly EarnManager _earn;
        private readonly BalanceRebalancer _rebalancer;
        private MainWindowViewModel _ui;
        private bool _isRunning;

        private readonly int _rsiPeriod = 14;
        private int _maxConcurrentPositions = 1;
        private readonly int _absoluteMaxPositions = 3;

        private Dictionary<string, OpenPosition> _positions = new ();
        private List<string> _activePairs = new ();
        private DateTime _lastBalanceLog = DateTime.MinValue;
        private decimal _lastLoggedBalance = -1;
        private DateTime _lastReportDate = DateTime.MinValue;

        private readonly Dictionary<string, (List<BinanceKline> Klines, DateTime Expiry)> _klinesCache = new ();
        private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds (10);

        private readonly object _tradesCsvLock = new ();
        private readonly object _featuresCsvLock = new ();
        private string _tradesCsvPath;
        private string _featuresCsvPath;
        private readonly StrategyEngine _strategy = new ();

        private MLContext _mlContext;
        private ITransformer _mlModel;
        private readonly string _modelPath;
        private bool _mlModelLoaded = false;

        private readonly string _positionsFilePath;
        private TelegramNotifier _telegram;

        private string _telegramBotToken;
        private string _telegramChatId;

        public TradingService(BinanceClient client, WalletManager wallet, EarnManager earn, BalanceRebalancer rebalancer = null,
                      decimal minUsdcBalance = 5.50m, string telegramBotToken = "", string telegramChatId = "")
        {
            _client = client;
            _wallet = wallet;
            _earn = earn;
            _rebalancer = rebalancer ?? new BalanceRebalancer (new object (), 0.1m);
            _positionsFilePath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Data", "open_positions.json");
            _modelPath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "trading_model.zip");
            _telegramBotToken = telegramBotToken;
            _telegramChatId = telegramChatId;
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

            if (!string.IsNullOrEmpty (tgToken) && !string.IsNullOrEmpty (tgChatId))
            {
                _telegram = new TelegramNotifier (tgToken, tgChatId);
                _telegram.StartListening (HandleTelegramCommand);
                logger ("✅ Telegram уведомления включены");
                logger ("📡 Команды Telegram активированы (/help для списка)");
            }
            if (_telegram != null)
            {
                _ = _telegram.SendWelcomeMessageAsync (tgChatId);
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

            InitCsv ();
            RotateLogs ();
            LoadMlModel ();
            await _wallet.UpdateBalance ();
            await UpdatePairs ();
            await LoadSavedPositions ();

            _ = Task.Run (BalanceLoop);
            _ = Task.Run (PairsLoop);
            _ = Task.Run (EarnLoop);
            _ = Task.Run (DustLoop);
            _ = Task.Run (AutoRetrainLoop);
            _ = Task.Run (async () => { await Task.Delay (TimeSpan.FromMinutes (1)); await BackupDataAsync (); });
            await Task.Run (TradingLoop);
        }

        // ==================== ИНИЦИАЛИЗАЦИЯ CSV ====================
        private void InitCsv()
        {
            try
            {
                string dir = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Logs");
                if (!Directory.Exists (dir)) Directory.CreateDirectory (dir);
                _tradesCsvPath = Path.Combine (dir, $"trades_{DateTime.Now:yyyyMMdd}.csv");
                if (!File.Exists (_tradesCsvPath))
                    File.WriteAllText (_tradesCsvPath, "DateTime,Symbol,Action,Price,Quantity,PnL,PnLPercent,EntryPrice,ExitPrice,Reason,Duration\n");
                _featuresCsvPath = Path.Combine (dir, "features.csv");
                if (!File.Exists (_featuresCsvPath))
                    File.WriteAllText (_featuresCsvPath, "Timestamp,Symbol,Price,FastSma,SlowSma,Rsi,Volume,Volatility\n");
            }
            catch (Exception ex) { _ui?.AddLog ($"❌ Ошибка InitCsv: {ex.Message}"); }
        }

        private void LogTradeToCsv(TradeLog trade)
        {
            lock (_tradesCsvLock)
            {
                try
                {
                    if (string.IsNullOrEmpty (_tradesCsvPath)) return;
                    string line = $"{trade.CloseTime:yyyy-MM-dd HH:mm:ss},{trade.Symbol},{trade.Action},{trade.ExitPrice:F4},{trade.Quantity:F6},{trade.PnL:F2},{trade.PnLPercent:F2},{trade.EntryPrice:F4},{trade.ExitPrice:F4},{trade.Reason},{trade.Duration}\n";
                    File.AppendAllText (_tradesCsvPath, line);
                }
                catch (Exception ex) { Debug.WriteLine ($"CSV trade error: {ex.Message}"); }
            }
        }

        private void LogFeaturesToCsv(string symbol, decimal price, Dictionary<string, decimal> features)
        {
            lock (_featuresCsvLock)
            {
                try
                {
                    string line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss},{symbol},{price:F4},{features["FastSma"]:F4},{features["SlowSma"]:F4},{features["Rsi"]:F2},{features["Volume"]:F4},{features["Volatility"]:F4}\n";
                    File.AppendAllText (_featuresCsvPath, line);
                }
                catch (Exception ex) { Debug.WriteLine ($"Features CSV error: {ex.Message}"); }
            }
        }

        // ==================== ЗАГРУЗКА МОДЕЛИ ====================
        private void LoadMlModel()
        {
            if (!File.Exists (_modelPath))
            {
                _ui?.AddLog ("⚠️ ML модель не найдена, фильтрация сделок отключена.");
                return;
            }
            try
            {
                _mlContext = new MLContext ();
                _mlModel = _mlContext.Model.Load (_modelPath, out _);
                _mlModelLoaded = true;
                _ui?.AddLog ("✅ ML модель загружена");
            }
            catch (Exception ex) { _ui?.AddLog ($"❌ Ошибка загрузки ML: {ex.Message}"); }
        }

        private bool IsMlPredictionProfitable(decimal fastSma, decimal slowSma, decimal rsi, decimal volume, decimal volatility)
        {
            if (!_mlModelLoaded) return true;
            try
            {
                var input = new ModelInput { FastSma = (float)fastSma, SlowSma = (float)slowSma, Rsi = (float)rsi, Volume = (float)volume, Volatility = (float)volatility };
                var predEngine = _mlContext.Model.CreatePredictionEngine<ModelInput, ModelOutput> (_mlModel);
                var result = predEngine.Predict (input);
                return result.IsProfitable && result.Probability > 0.6f;
            }
            catch { return true; }
        }

        // ==================== РОТАЦИЯ ЛОГОВ ====================
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

        // ==================== ВОССТАНОВЛЕНИЕ ПОЗИЦИЙ ====================
        private async Task LoadSavedPositions()
        {
            if (!File.Exists (_positionsFilePath)) return;
            try
            {
                string json = await File.ReadAllTextAsync (_positionsFilePath);
                var saved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, OpenPosition>> (json);
                if (saved == null) return;

                var toRemove = new List<string> ();
                foreach (var kv in saved)
                {
                    string asset = kv.Key.Replace ("USDC", "");
                    decimal spotBalance = await _client.GetAccountBalanceAsync (asset);
                    decimal earnBalance = 0;
                    var earnPositions = await _client.GetFlexibleEarnBalanceAsync ();
                    var earnPos = earnPositions?.FirstOrDefault (p => p["asset"]?.ToString () == asset);
                    if (earnPos != null)
                        earnBalance = decimal.Parse (earnPos["totalAmount"]?.ToString () ?? "0", CultureInfo.InvariantCulture);
                    decimal totalBalance = spotBalance + earnBalance;

                    decimal required = kv.Value.Quantity;
                    if (totalBalance < required - 0.000001m)
                    {
                        // Корректируем количество, если разница <1%
                        decimal diff = required - totalBalance;
                        if (diff / required < 0.01m && diff < 0.001m)
                        {
                            decimal stepSize = await _client.GetStepSizeAsync (kv.Key);
                            decimal adjusted = Math.Floor (totalBalance / stepSize) * stepSize;
                            if (adjusted > 0)
                            {
                                kv.Value.Quantity = adjusted;
                                _ui?.AddLog ($"🔄 Позиция {kv.Key}: скорректирована с {required} до {adjusted} (доступно {totalBalance})");
                            }
                            else toRemove.Add (kv.Key);
                        }
                        else toRemove.Add (kv.Key);
                        if (toRemove.Contains (kv.Key)) continue;
                    }

                    decimal currentPrice = await GetCurrentPrice (kv.Key);
                    if (currentPrice > 0)
                    {
                        kv.Value.StopLossPrice = currentPrice * ( 1 - _ui.StopLossPercent );
                        kv.Value.TakeProfitPrice = currentPrice * ( 1 + _ui.TakeProfitPercent );
                        kv.Value.HighestPrice = currentPrice;
                        _positions[kv.Key] = kv.Value;
                        _ui?.AddLog ($"🔄 Восстановлена позиция {kv.Key} ({kv.Value.Quantity} по {kv.Value.EntryPrice:F4})");
                    }
                }
                foreach (var sym in toRemove) saved.Remove (sym);
                if (toRemove.Count > 0) await SavePositions ();
                _ui?.UpdatePositionsStatus (_positions.Count, _maxConcurrentPositions, _positions.Keys.ToList ());
            }
            catch (Exception ex) { _ui?.AddLog ($"Ошибка загрузки позиций: {ex.Message}"); }
        }

        private async Task SavePositions()
        {
            try
            {
                string dir = Path.GetDirectoryName (_positionsFilePath);
                if (!Directory.Exists (dir)) Directory.CreateDirectory (dir);
                string json = System.Text.Json.JsonSerializer.Serialize (_positions, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync (_positionsFilePath, json);
            }
            catch (Exception ex) { Debug.WriteLine ($"Save positions error: {ex.Message}"); }
        }

        // ==================== БЭКАП ДАННЫХ ====================
        private async Task BackupDataAsync()
        {
            try
            {
                string backupDir = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Backups");
                if (!Directory.Exists (backupDir)) Directory.CreateDirectory (backupDir);

                string dateStamp = DateTime.Now.ToString ("yyyyMMdd_HHmmss");
                string backupZip = Path.Combine (backupDir, $"backup_{dateStamp}.zip");

                string tempDir = Path.Combine (Path.GetTempPath (), $"BinanceBotBackup_{dateStamp}");
                Directory.CreateDirectory (tempDir);

                string dataDir = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Data");
                if (Directory.Exists (dataDir)) CopyDirectory (dataDir, Path.Combine (tempDir, "Data"));

                string logsDir = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Logs");
                if (Directory.Exists (logsDir)) CopyDirectory (logsDir, Path.Combine (tempDir, "Logs"));

                string modelPath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "trading_model.zip");
                if (File.Exists (modelPath)) File.Copy (modelPath, Path.Combine (tempDir, "trading_model.zip"));

                string settingsPath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Data", "strategy_settings.json");
                if (File.Exists (settingsPath)) File.Copy (settingsPath, Path.Combine (tempDir, "strategy_settings.json"));

                ZipFile.CreateFromDirectory (tempDir, backupZip);
                Directory.Delete (tempDir, true);

                // удаляем бэкапы старше 7 дней
                foreach (var file in Directory.GetFiles (backupDir, "backup_*.zip"))
                {
                    if (File.GetCreationTime (file) < DateTime.Now.AddDays (-7))
                        File.Delete (file);
                }
                _ui?.AddLog ($"💾 Создан бэкап: {Path.GetFileName (backupZip)}");
            }
            catch (Exception ex)
            {
                _ui?.AddLog ($"❌ Ошибка бэкапа: {ex.Message}");
            }
        }

        private void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory (destDir);
            foreach (var file in Directory.GetFiles (sourceDir))
            {
                string destFile = Path.Combine (destDir, Path.GetFileName (file));
                File.Copy (file, destFile, true);
            }
            foreach (var subDir in Directory.GetDirectories (sourceDir))
            {
                string destSubDir = Path.Combine (destDir, Path.GetFileName (subDir));
                CopyDirectory (subDir, destSubDir);
            }
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
                UpdateMaxPositionsByBalance (bal);
                _ui?.AddBalancePoint (DateTime.Now, bal);

                if (DateTime.UtcNow.Date != _lastReportDate)
                {
                    _lastReportDate = DateTime.UtcNow.Date;
                    if (_telegram != null && _ui?.TotalTrades > 0)
                        _ = _telegram.SendDailyReport (_ui.TotalPnL, _ui.WinRate, _ui.TotalTrades, _ui.WinningTrades, _ui.LosingTrades);
                    RotateLogs ();
                    ArchiveLogs ();
                }

                // бэкап в 3 часа ночи
                if (DateTime.UtcNow.Hour == 3 && DateTime.UtcNow.Minute < 2)
                    await BackupDataAsync ();
            }
        }

        private void UpdateMaxPositionsByBalance(decimal balance)
        {
            if (balance < 30) _maxConcurrentPositions = 1;
            else if (balance < 100) _maxConcurrentPositions = 2;
            else _maxConcurrentPositions = _absoluteMaxPositions;
        }

        private async Task PairsLoop()
        {
            while (_isRunning)
            {
                await Task.Delay (30 * 60000);
                if (!_isRunning) break;
                await UpdatePairs ();
            }
        }

        private async Task UpdatePairs()
        {
            try
            {
                var newPairs = await _client.GetTopVolumePairsAsync ("USDC", 20);
                newPairs = newPairs.Where (p => !p.Contains ("USD1") && !p.Contains ("UUSDC") && !p.Contains ("LD")).ToList ();
                if (newPairs.Count > 0)
                {
                    _activePairs = newPairs;
                    _ui?.AddLog ($"📊 Список пар обновлён: {string.Join (", ", _activePairs.Take (5))}...");
                    _ui.RemoveMissingPairs (_activePairs);
                    lock (_klinesCache)
                    {
                        var toRemove = _klinesCache.Keys.Except (_activePairs).ToList ();
                        foreach (var key in toRemove) _klinesCache.Remove (key);
                    }
                }
            }
            catch (Exception ex) { _ui?.AddLog ($"❌ Ошибка обновления пар: {ex.Message}"); }
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

        private async Task EarnLoop()
        {
            while (_isRunning)
            {
                await Task.Delay (60 * 60000);
                if (!_isRunning) break;
                await ManageIdleUsdc ();
            }
        }

        private async Task ManageIdleUsdc()
        {
            decimal free = await _client.GetAccountBalanceAsync ("USDC");
            if (free <= 15) return;
            decimal toSub = free - 15;
            if (toSub < 5) return;
            _ui?.AddLog ($"💸 Размещаю {toSub:F2} USDC в Earn");
            var products = await _client.GetFlexibleProductsAsync ("USDC");
            var product = products?.FirstOrDefault (p => p["asset"]?.ToString () == "USDC");
            if (product != null)
                await _client.SubscribeFlexibleEarnAsync (product["productId"].ToString (), toSub);
        }

        private async Task DustLoop()
        {
            while (_isRunning)
            {
                await Task.Delay (24 * 3600000);
                if (!_isRunning) break;
                await CleanupDust ();
            }
        }

        private async Task CleanupDust()
        {
            var dust = await _client.GetDustAssetsAsync ();
            if (dust == null || dust.Count == 0) return;
            var ids = new List<string> ();
            foreach (var item in dust)
            {
                string asset = item["asset"]?.ToString ();
                if (asset != "USDC" && !asset.StartsWith ("LD") && !new[] { "RDNT", "NTRN" }.Contains (asset))
                    ids.Add (item["assetId"]?.ToString ());
            }
            if (ids.Count == 0) return;
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

        private decimal GetRsi(List<decimal> closes)
        {
            if (closes.Count < _rsiPeriod + 1) return 50;
            var rsi = TechnicalAnalysis.RSI (closes, _rsiPeriod);
            return rsi.LastOrDefault () ?? 50;
        }

        private decimal CalculateSma(List<decimal> data, int period)
        {
            if (data.Count < period) return 0;
            return data.Skip (data.Count - period).Average ();
        }

        private decimal CalculateVolatility(List<decimal> data, int period)
        {
            if (data.Count < period) return 0;
            var last = data.TakeLast (period).ToList ();
            decimal avg = last.Average ();
            decimal sumSq = last.Select (x => ( x - avg ) * ( x - avg )).Sum ();
            return (decimal)Math.Sqrt ((double)( sumSq / period ));
        }

        // ==================== ОСНОВНОЙ ТОРГОВЫЙ ЦИКЛ ====================
        private async Task TradingLoop()
        {
            while (_isRunning)
            {
                try
                {
                    await CheckProtections ();
                    decimal balance = _wallet.GetTotalBalance ("USDC");
                    if (balance < _ui.MinBalanceForTrading)
                    {
                        await _rebalancer.AutoConvertAssetsToUsdcAsync (_client, _isRunning);
                        await _wallet.UpdateBalance ();
                        balance = _wallet.GetTotalBalance ("USDC");
                        if (balance < _ui.MinBalanceForTrading) { await Task.Delay (30000); continue; }
                    }
                    if (_activePairs.Count == 0) { await Task.Delay (5000); continue; }
                    decimal riskPercent = Math.Min (_ui.MaxRiskPercent, 0.10m + ( balance - 20 ) / 180 * 0.15m);
                    _ui?.UpdateRiskDisplay (riskPercent);

                    var signalResults = new ConcurrentBag<(string Symbol, TradeAction Action, decimal Price, decimal Rsi,
                        decimal FastSma, decimal SlowSma, decimal Volatility, decimal Volume)> ();
                    var tasks = _activePairs.Select (async sym =>
                    {
                        try
                        {
                            var klines = await GetKlinesCachedAsync (sym, "5m", 50);
                            if (klines == null || klines.Count < Math.Max (_ui.FastSma, _ui.SlowSma) + 2) return;
                            var closes = klines.Select (k => k.Close).ToList ();
                            decimal price = closes.Last ();
                            var signal = _strategy.AnalyzePairWithWallet (sym, closes, _ui.FastSma, _ui.SlowSma, price);
                            decimal rsi = GetRsi (closes);
                            decimal fastSma = CalculateSma (closes, _ui.FastSma);
                            decimal slowSma = CalculateSma (closes, _ui.SlowSma);
                            decimal volatility = CalculateVolatility (closes, 20);
                            decimal volume = klines.Last ().Volume;
                            // ИЗМЕНЁННЫЙ ВЫЗОВ:
                            _ui.UpdateMarketTable (sym, price.ToString ("F4"), _positions.ContainsKey (sym), signal.Action, fastSma, slowSma);
                            signalResults.Add ((sym, signal.Action, price, rsi, fastSma, slowSma, volatility, volume));
                        }
                        catch (Exception ex) { _ui?.AddLog ($"❌ Ошибка при анализе {sym}: {ex.Message}"); }
                    }).ToArray ();
                    await Task.WhenAll (tasks);

                    foreach (var res in signalResults)
                    {
                        if (!_isRunning) break;
                        bool hasPos = _positions.ContainsKey (res.Symbol);

                        if (res.Action == TradeAction.Buy && !hasPos && _positions.Count < _maxConcurrentPositions)
                        {
                            // ... остальной код покупки без изменений ...
                        }
                        else if (res.Action == TradeAction.Sell && hasPos)
                        {
                            await ClosePosition (res.Symbol, res.Price, "SMA Sell");
                        }
                    }
                    await Task.Delay (10000);
                }
                catch (Exception ex)
                {
                    _ui?.AddLog ($"❌ Ошибка: {ex.Message}");
                    _ = _telegram?.SendErrorNotification (ex.Message);
                    await Task.Delay (10000);
                }
            }
            _isRunning = false;
        }

        private async Task CheckProtections()
        {
            var toClose = new List<string> ();
            foreach (var pos in _positions.Values)
            {
                decimal price = await GetCurrentPrice (pos.Symbol);
                if (price <= 0) continue;
                if (price > pos.HighestPrice)
                {
                    pos.HighestPrice = price;
                    decimal newSl = pos.HighestPrice * ( 1 - _ui.TrailingStopPercent );
                    if (newSl > pos.StopLossPrice) pos.StopLossPrice = newSl;
                }
                if (price <= pos.StopLossPrice) toClose.Add (pos.Symbol);
                else if (price >= pos.TakeProfitPrice) toClose.Add (pos.Symbol);
                else if (DateTime.UtcNow - pos.OpenTime > TimeSpan.FromHours (2)) toClose.Add (pos.Symbol);
            }
            foreach (var sym in toClose)
                await ClosePosition (sym, await GetCurrentPrice (sym), "Protection");
        }

        // ==================== ЗАКРЫТИЕ ПОЗИЦИИ ====================
        private async Task ClosePosition(string sym, decimal price, string reason)
        {
            if (!_positions.ContainsKey (sym)) return;
            var pos = _positions[sym];
            string asset = sym.Replace ("USDC", "");

            decimal spotBalance = await _client.GetAccountBalanceAsync (asset);
            decimal earnBalance = 0;
            var earnPositions = await _client.GetFlexibleEarnBalanceAsync ();
            var earnPos = earnPositions?.FirstOrDefault (p => p["asset"]?.ToString () == asset);
            if (earnPos != null)
                earnBalance = decimal.Parse (earnPos["totalAmount"]?.ToString () ?? "0", CultureInfo.InvariantCulture);
            decimal totalBalance = spotBalance + earnBalance;

            decimal qtyToSell = pos.Quantity;
            decimal stepSize = await _client.GetStepSizeAsync (sym);

            if (totalBalance < qtyToSell - 0.000001m)
            {
                decimal diff = qtyToSell - totalBalance;
                if (diff / qtyToSell < 0.02m)
                {
                    qtyToSell = Math.Floor (totalBalance / stepSize) * stepSize;
                    if (qtyToSell <= 0)
                    {
                        _ui?.AddLog ($"⚠️ Недостаточно {asset} для закрытия {sym}. Удаляю позицию.");
                        _positions.Remove (sym);
                        await SavePositions ();
                        _ui?.UpdatePositionsStatus (_positions.Count, _maxConcurrentPositions, _positions.Keys.ToList ());
                        return;
                    }
                    _ui?.AddLog ($"⚠️ Корректировка продажи {sym}: доступно {totalBalance}, продаю {qtyToSell} вместо {pos.Quantity}");
                }
                else
                {
                    _ui?.AddLog ($"⚠️ Недостаточно {asset} для закрытия {sym}. Удаляю позицию.");
                    _positions.Remove (sym);
                    await SavePositions ();
                    _ui?.UpdatePositionsStatus (_positions.Count, _maxConcurrentPositions, _positions.Keys.ToList ());
                    return;
                }
            }

            if (spotBalance < qtyToSell - 0.000001m)
            {
                decimal need = qtyToSell - spotBalance;
                bool redeemed = await _earn.EnsureLiquidBalanceAsync (asset, need, _client);
                if (!redeemed)
                {
                    if (spotBalance > 0)
                    {
                        qtyToSell = Math.Floor (spotBalance / stepSize) * stepSize;
                        if (qtyToSell <= 0)
                        {
                            _ui?.AddLog ($"⚠️ Не удалось выкупить {need} {asset} и нет спота. Удаляю.");
                            _positions.Remove (sym);
                            await SavePositions ();
                            _ui?.UpdatePositionsStatus (_positions.Count, _maxConcurrentPositions, _positions.Keys.ToList ());
                            return;
                        }
                        _ui?.AddLog ($"⚠️ Выкуп {need} не удался, продаю только {qtyToSell} из спота");
                    }
                    else
                    {
                        _ui?.AddLog ($"⚠️ Не удалось выкупить {need} {asset}. Удаляю.");
                        _positions.Remove (sym);
                        await SavePositions ();
                        _ui?.UpdatePositionsStatus (_positions.Count, _maxConcurrentPositions, _positions.Keys.ToList ());
                        return;
                    }
                }
                else
                    spotBalance = await _client.GetAccountBalanceAsync (asset);
            }

            if (qtyToSell > 0)
            {
                var order = await _client.PlaceOrder (sym, "SELL", "MARKET", qtyToSell);
                if (order != null)
                {
                    _positions.Remove (sym);
                    await SavePositions ();
                    decimal pnl = ( price - pos.EntryPrice ) * qtyToSell;
                    decimal pnlPct = ( price / pos.EntryPrice - 1 ) * 100;
                    _ui?.AddLog ($"🔒 ЗАКРЫТА: {sym} по {price:F4} | PnL: {pnl:F2} ({pnlPct:F2}%) | {reason} (продано {qtyToSell})");
                    var trade = new TradeLog
                    {
                        Symbol = sym,
                        EntryPrice = pos.EntryPrice,
                        ExitPrice = price,
                        Quantity = qtyToSell,
                        PnL = pnl,
                        PnLPercent = pnlPct,
                        OpenTime = pos.OpenTime,
                        CloseTime = DateTime.UtcNow,
                        Reason = reason,
                        Duration = DateTime.UtcNow - pos.OpenTime,
                        Action = "SELL_CLOSE"
                    };
                    _ui.AddTradeToHistory (trade);
                    LogTradeToCsv (trade);
                    _ = _telegram?.SendTradeNotification (sym, "SELL", price, qtyToSell, pnl, reason);
                    decimal newBal = _wallet.GetTotalBalance ("USDC");
                    _ui?.UpdateWalletDisplay (newBal.ToString ("F2"));
                    _ui?.UpdatePositionsStatus (_positions.Count, _maxConcurrentPositions, _positions.Keys.ToList ());
                }
                else
                {
                    _ui?.AddLog ($"❌ Не удалось продать {sym}. Удаляю.");
                    _positions.Remove (sym);
                    await SavePositions ();
                    _ui?.UpdatePositionsStatus (_positions.Count, _maxConcurrentPositions, _positions.Keys.ToList ());
                }
            }
            else
            {
                _ui?.AddLog ($"⚠️ Невозможно продать {sym}: qty=0. Удаляю.");
                _positions.Remove (sym);
                await SavePositions ();
                _ui?.UpdatePositionsStatus (_positions.Count, _maxConcurrentPositions, _positions.Keys.ToList ());
            }
        }

        private async Task<decimal> GetCurrentPrice(string sym)
        {
            var k = await _client.GetKlinesAsync (sym, "5m", 1);
            return k?.Last ().Close ?? 0;
        }

        public void StopTrading() => _isRunning = false;

        // ==================== АВТОМАТИЧЕСКОЕ ПЕРЕОБУЧЕНИЕ ====================
        private async Task AutoRetrainLoop()
        {
            while (_isRunning)
            {
                await Task.Delay (TimeSpan.FromHours (24));
                if (!_isRunning) break;
                await RetrainModelAsync ();
            }
        }

        private async Task RetrainModelAsync()
        {
            try
            {
                _ui?.AddLog ("🤖 Запуск переобучения ML модели...");
                string logsDir = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Logs");
                if (!Directory.Exists (logsDir))
                {
                    _ui?.AddLog ("❌ Папка Logs не найдена.");
                    return;
                }

                var trades = new List<(DateTime CloseTime, string Symbol, bool IsProfitable)> ();
                var tradeFiles = Directory.GetFiles (logsDir, "trades_*.csv");
                foreach (var file in tradeFiles)
                {
                    using var reader = new StreamReader (file);
                    using var csv = new CsvReader (reader, CultureInfo.InvariantCulture);
                    var records = csv.GetRecords<dynamic> ();
                    foreach (var r in records)
                    {
                        string action = r.Action;
                        if (action != "SELL_CLOSE") continue;
                        DateTime dt = DateTime.Parse (r.CloseTime);
                        string symbol = r.Symbol;
                        decimal pnl = decimal.Parse (r.PnL, CultureInfo.InvariantCulture);
                        trades.Add ((dt, symbol, pnl > 0));
                    }
                }

                string featuresPath = Path.Combine (logsDir, "features.csv");
                if (!File.Exists (featuresPath))
                {
                    _ui?.AddLog ("❌ Файл features.csv не найден.");
                    return;
                }

                var features = new List<(DateTime Timestamp, string Symbol, decimal FastSma, decimal SlowSma, decimal Rsi, decimal Volume, decimal Volatility)> ();
                using (var reader = new StreamReader (featuresPath))
                using (var csv = new CsvReader (reader, CultureInfo.InvariantCulture))
                {
                    var records = csv.GetRecords<dynamic> ();
                    foreach (var r in records)
                    {
                        DateTime ts = DateTime.Parse (r.Timestamp);
                        string symbol = r.Symbol;
                        decimal fast = decimal.Parse (r.FastSma, CultureInfo.InvariantCulture);
                        decimal slow = decimal.Parse (r.SlowSma, CultureInfo.InvariantCulture);
                        decimal rsi = decimal.Parse (r.Rsi, CultureInfo.InvariantCulture);
                        decimal vol = decimal.Parse (r.Volume, CultureInfo.InvariantCulture);
                        decimal volt = decimal.Parse (r.Volatility, CultureInfo.InvariantCulture);
                        features.Add ((ts, symbol, fast, slow, rsi, vol, volt));
                    }
                }

                if (trades.Count < 30)
                {
                    _ui?.AddLog ($"⚠️ Недостаточно сделок: {trades.Count} (нужно 30)");
                    return;
                }

                var merged = new List<(decimal FastSma, decimal SlowSma, decimal Rsi, decimal Volume, decimal Volatility, bool IsProfitable)> ();
                foreach (var t in trades)
                {
                    var closest = features
                        .Where (f => f.Symbol == t.Symbol && f.Timestamp <= t.CloseTime)
                        .OrderByDescending (f => f.Timestamp)
                        .FirstOrDefault ();
                    if (closest.Timestamp != DateTime.MinValue)
                    {
                        merged.Add ((closest.FastSma, closest.SlowSma, closest.Rsi, closest.Volume, closest.Volatility, t.IsProfitable));
                    }
                }

                if (merged.Count < 20)
                {
                    _ui?.AddLog ($"⚠️ Недостаточно объединённых записей: {merged.Count} (нужно 20)");
                    return;
                }

                var mlContext = new MLContext (seed: 42);
                var dataWithLabel = merged.Select ((m, idx) => new
                {
                    FastSma = (float)m.FastSma,
                    SlowSma = (float)m.SlowSma,
                    Rsi = (float)m.Rsi,
                    Volume = (float)m.Volume,
                    Volatility = (float)m.Volatility,
                    Label = m.IsProfitable
                }).ToList ();

                var dataView = mlContext.Data.LoadFromEnumerable (dataWithLabel);
                var split = mlContext.Data.TrainTestSplit (dataView, testFraction: 0.2);
                var trainData = split.TrainSet;
                var testData = split.TestSet;

                var pipeline = mlContext.Transforms.Concatenate ("Features",
                        nameof (ModelInput.FastSma),
                        nameof (ModelInput.SlowSma),
                        nameof (ModelInput.Rsi),
                        nameof (ModelInput.Volume),
                        nameof (ModelInput.Volatility))
                    .Append (mlContext.BinaryClassification.Trainers.FastTree (
                        numberOfTrees: 100,
                        numberOfLeaves: 20,
                        minimumExampleCountPerLeaf: 5));

                _ui?.AddLog ($"🔄 Обучение на {merged.Count} примерах...");
                var model = pipeline.Fit (trainData);

                var predictions = model.Transform (testData);
                var metrics = mlContext.BinaryClassification.Evaluate (predictions);
                _ui?.AddLog ($"📊 Точность: {metrics.Accuracy:P2}, AUC: {metrics.AreaUnderRocCurve:P2}, F1: {metrics.F1Score:P2}");

                string tempModelPath = _modelPath + ".tmp";
                mlContext.Model.Save (model, trainData.Schema, tempModelPath);
                if (File.Exists (_modelPath)) File.Delete (_modelPath);
                File.Move (tempModelPath, _modelPath);

                _mlModel = mlContext.Model.Load (_modelPath, out _);
                _mlModelLoaded = true;
                _ui?.AddLog ("✅ ML модель переобучена и загружена!");
            }
            catch (Exception ex)
            {
                _ui?.AddLog ($"❌ Ошибка переобучения: {ex.Message}");
            }
        }

        // ==================== СТАТИСТИКА ДЛЯ TELEGRAM ====================
        private string GetStatisticsText()
        {
            var stats = new StringBuilder ();
            stats.AppendLine ("📊 <b>СТАТИСТИКА БОТА</b>");
            stats.AppendLine ($"🟢 Статус: {( _isRunning ? "Активен" : "Остановлен" )}");
            stats.AppendLine ($"💰 Баланс USDC: {_wallet.GetTotalBalance ("USDC"):F2}");
            stats.AppendLine ($"📈 Общий PnL: {( _ui?.TotalPnL ?? 0 ):F2} USDC");
            stats.AppendLine ($"🎯 Win Rate: {( _ui?.WinRate ?? 0 ):F1}%");
            stats.AppendLine ($"📊 Сделок: {( _ui?.TotalTrades ?? 0 )} (✅{_ui?.WinningTrades ?? 0}/❌{_ui?.LosingTrades ?? 0})");
            stats.AppendLine ($"📉 Макс. просадка: {( _ui?.MaxDrawdownDisplay ?? "0%" )}");
            stats.AppendLine ($"🤖 ML модель: {( _mlModelLoaded ? "активна" : "не загружена" )}");
            stats.AppendLine ($"📂 Последний бэкап: {GetLastBackupDate ()}");
            stats.AppendLine ($"📊 Открытых позиций: {_positions.Count}/{_maxConcurrentPositions}");
            return stats.ToString ();
        }

        private string GetLastBackupDate()
        {
            string backupDir = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Backups");
            if (!Directory.Exists (backupDir)) return "нет";
            var lastBackup = Directory.GetFiles (backupDir, "backup_*.zip")
                .OrderByDescending (f => File.GetCreationTime (f))
                .FirstOrDefault ();
            return lastBackup == null ? "нет" : File.GetCreationTime (lastBackup).ToString ("dd.MM.yyyy HH:mm");
        }

        // ==================== ОБРАБОТКА ТЕЛЕГРАМ-КОМАНД ====================
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

            // Обработка команд
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
                    _ = Task.Run (RetrainModelAsync);
                    break;
                case "/pnl":
                    await _telegram.SendMessageAsync ($"📈 Общий PnL: {( _ui?.TotalPnL ?? 0 ):F2} USDC\n🎯 Win Rate: {( _ui?.WinRate ?? 0 ):F1}%", chatId);
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
            int positions = _positions.Count;
            return $"🤖 Статус: {status}\n💰 USDC: {balance:F2}\n📊 Открыто позиций: {positions}";
        }
    }
}