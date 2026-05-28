using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Collections.Concurrent;
using Microsoft.ML;
using Microsoft.ML.Data;
using BinanceBotWpf.Models;
using BinanceBotWpf.ViewModels;
using System.IO.Compression;

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
        private DateTime _lastPairsUpdate = DateTime.MinValue;
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

            // Сначала пробуем из переменных окружения
            var tgToken = Environment.GetEnvironmentVariable ("TELEGRAM_BOT_TOKEN");
            var tgChatId = Environment.GetEnvironmentVariable ("TELEGRAM_CHAT_ID");

            // Если не нашли – читаем из config.txt
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
            await Task.Run (TradingLoop);
        }

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
                _ui?.AddLog ("✅ ML модель загружена, будет использоваться для фильтрации сделок.");
            }
            catch (Exception ex)
            {
                _ui?.AddLog ($"❌ Ошибка загрузки ML модели: {ex.Message}");
            }
        }

        private bool IsMlPredictionProfitable(decimal fastSma, decimal slowSma, decimal rsi, decimal volume, decimal volatility)
        {
            if (!_mlModelLoaded) return true;
            try
            {
                var input = new ModelInput
                {
                    FastSma = (float)fastSma,
                    SlowSma = (float)slowSma,
                    Rsi = (float)rsi,
                    Volume = (float)volume,
                    Volatility = (float)volatility
                };
                var predEngine = _mlContext.Model.CreatePredictionEngine<ModelInput, ModelOutput> (_mlModel);
                var result = predEngine.Predict (input);
                return result.IsProfitable && result.Probability > 0.6f;
            }
            catch { return true; }
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
                    {
                        File.Delete (file);
                        _ui?.AddLog ($"🗑️ Удалён старый лог: {Path.GetFileName (file)}");
                    }
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
                foreach (var file in files)
                    File.Delete (file);

                _ui?.AddLog ($"📦 Логи заархивированы: {zipPath}");
            }
            catch (Exception ex)
            {
                _ui?.AddLog ($"❌ Ошибка архивации логов: {ex.Message}");
            }
        }

        private void InitCsv()
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

        private void LogTradeToCsv(TradeLog trade)
        {
            lock (_tradesCsvLock)
            {
                try
                {
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

        private async Task LoadSavedPositions()
        {
            if (!File.Exists (_positionsFilePath)) return;
            try
            {
                string json = await File.ReadAllTextAsync (_positionsFilePath);
                var saved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, OpenPosition>> (json);
                if (saved == null) return;
                foreach (var kv in saved)
                {
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
                    _ui?.AddLog ($"💰 Баланс USDC: {bal:F2}");
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
                if (newPairs.Count > 0)
                {
                    _activePairs = newPairs;
                    _ui?.AddLog ($"📊 Список пар обновлён: {string.Join (", ", _activePairs.Take (5))}...");
                    lock (_klinesCache)
                    {
                        var toRemove = _klinesCache.Keys.Except (_activePairs).ToList ();
                        foreach (var key in toRemove)
                            _klinesCache.Remove (key);
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
            lock (_klinesCache)
            {
                _klinesCache[symbol] = (klines, DateTime.UtcNow + _cacheDuration);
            }
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
                            _ui.UpdateMarketTable (sym, price.ToString ("F4"), 0, 0);
                            signalResults.Add ((sym, signal.Action, price, rsi, fastSma, slowSma, volatility, volume));

                            // Отладочный лог
                            _ui?.AddLog ($"🔍 Анализ {sym}: сигнал={signal.Action}, RSI={rsi:F1}, цена={price:F4}");
                        }
                        catch (Exception ex) { _ui?.AddLog ($"❌ Ошибка при анализе {sym}: {ex.Message}"); }
                    }).ToArray ();
                    await Task.WhenAll (tasks);

                    foreach (var res in signalResults)
                    {
                        if (!_isRunning) break;
                        bool hasPos = _positions.ContainsKey (res.Symbol);

                        // BUY сигнал (RSI фильтр временно отключён)
                        if (res.Action == TradeAction.Buy && !hasPos && _positions.Count < _maxConcurrentPositions) // && res.Rsi < _ui.RsiBuyThreshold)
                        {
                            _ui?.AddLog ($"🔔 ПОПЫТКА ПОКУПКИ {res.Symbol}: hasPos={hasPos}, positionsCount={_positions.Count}, maxPos={_maxConcurrentPositions}");

                            decimal spend = Math.Max (10m, balance * riskPercent);
                            if (spend > balance) spend = balance;

                            _ui?.AddLog ($"🔍 Проверка BUY для {res.Symbol}: spend={spend:F2}, balance={balance:F2}, riskPercent={riskPercent:P1}");

                            if (spend < 10)
                            {
                                _ui?.AddLog ($"⏸️ Сумма spend={spend:F2} меньше 10 USDC – покупка отклонена");
                                continue;
                            }

                            var features = new Dictionary<string, decimal>
                            {
                                ["FastSma"] = res.FastSma,
                                ["SlowSma"] = res.SlowSma,
                                ["Rsi"] = res.Rsi,
                                ["Volume"] = res.Volume,
                                ["Volatility"] = res.Volatility
                            };
                            LogFeaturesToCsv (res.Symbol, res.Price, features);

                            // ML проверка
                            bool mlResult = IsMlPredictionProfitable (res.FastSma, res.SlowSma, res.Rsi, res.Volume, res.Volatility);
                            _ui?.AddLog ($"🔍 ML проверка для {res.Symbol}: {mlResult}");
                            if (!mlResult)
                            {
                                _ui?.AddLog ($"⏸️ ML модель предсказала убыток для {res.Symbol} – покупка отклонена");
                                continue;
                            }

                            // Проверка баланса USDC
                            _ui?.AddLog ($"🔍 Проверка Earn/USDC баланса для {res.Symbol}, нужно {spend:F2} USDC");
                            bool usdcReady = await _earn.EnsureLiquidBalanceAsync ("USDC", spend, _client);
                            _ui?.AddLog ($"🔍 Результат проверки USDC: {usdcReady}");
                            if (!usdcReady)
                            {
                                _ui?.AddLog ($"⏸️ Недостаточно USDC для покупки {res.Symbol}");
                                continue;
                            }

                            decimal qty = Math.Round (spend / res.Price, 6);
                            _ui?.AddLog ($"🔍 Расчёт qty={qty}, spend={spend:F2}, price={res.Price:F4}");

                            if (qty <= 0)
                            {
                                _ui?.AddLog ($"⏸️ qty={qty} <= 0 – покупка отклонена");
                                continue;
                            }

                            _ui?.AddLog ($"🚀 Отправка ордера BUY {res.Symbol}: {qty} по {res.Price:F4}");
                            var order = await _client.PlaceOrder (res.Symbol, "BUY", "MARKET", qty);

                            if (order != null)
                            {
                                var pos = new OpenPosition
                                {
                                    Symbol = res.Symbol,
                                    Quantity = qty,
                                    EntryPrice = res.Price,
                                    OpenTime = DateTime.UtcNow,
                                    StopLossPrice = res.Price * ( 1 - _ui.StopLossPercent ),
                                    TakeProfitPrice = res.Price * ( 1 + _ui.TakeProfitPercent ),
                                    HighestPrice = res.Price
                                };
                                _positions[res.Symbol] = pos;
                                balance -= spend;
                                await SavePositions ();
                                _ui?.AddLog ($"✅ КУПЛЕНО: {qty} {res.Symbol} по {res.Price:F4} | SL: {pos.StopLossPrice:F4} | TP: {pos.TakeProfitPrice:F4} | RSI={res.Rsi:F1}");
                                _ui?.UpdateWalletDisplay (balance.ToString ("F2"));
                                _ui?.UpdatePositionsStatus (_positions.Count, _maxConcurrentPositions, _positions.Keys.ToList ());
                                var openLog = new TradeLog
                                {
                                    Symbol = res.Symbol,
                                    EntryPrice = res.Price,
                                    ExitPrice = res.Price,
                                    Quantity = qty,
                                    Action = "BUY_OPEN",
                                    CloseTime = DateTime.UtcNow,
                                    Reason = "SMA Buy signal"
                                };
                                LogTradeToCsv (openLog);
                                _ = _telegram?.SendTradeNotification (res.Symbol, "BUY", res.Price, qty, 0, "SMA Buy");
                            }
                            else
                            {
                                _ui?.AddLog ($"❌ Ошибка при отправке ордера BUY {res.Symbol}");
                            }
                        }
                        // SELL сигнал (RSI фильтр временно отключён)
                        else if (res.Action == TradeAction.Sell && hasPos) // && res.Rsi > _ui.RsiSellThreshold)
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

        private async Task ClosePosition(string sym, decimal price, string reason)
        {
            if (!_positions.ContainsKey (sym)) return;
            var pos = _positions[sym];
            string asset = sym.Replace ("USDC", "");
            await _earn.EnsureLiquidBalanceAsync (asset, pos.Quantity, _client);
            var order = await _client.PlaceOrder (sym, "SELL", "MARKET", pos.Quantity);
            if (order != null)
            {
                _positions.Remove (sym);
                await SavePositions ();
                decimal pnl = ( price - pos.EntryPrice ) * pos.Quantity;
                decimal pnlPct = ( price / pos.EntryPrice - 1 ) * 100;
                _ui?.AddLog ($"🔒 ЗАКРЫТА: {sym} по {price:F4} | PnL: {pnl:F2} ({pnlPct:F2}%) | {reason}");
                var trade = new TradeLog
                {
                    Symbol = sym,
                    EntryPrice = pos.EntryPrice,
                    ExitPrice = price,
                    Quantity = pos.Quantity,
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
                _ = _telegram?.SendTradeNotification (sym, "SELL", price, pos.Quantity, pnl, reason);
                decimal newBal = _wallet.GetTotalBalance ("USDC");
                _ui?.UpdateWalletDisplay (newBal.ToString ("F2"));
                _ui?.UpdatePositionsStatus (_positions.Count, _maxConcurrentPositions, _positions.Keys.ToList ());
            }
        }

        private async Task<decimal> GetCurrentPrice(string sym)
        {
            var k = await _client.GetKlinesAsync (sym, "5m", 1);
            return k?.Last ().Close ?? 0;
        }

        public void StopTrading() => _isRunning = false;

        private async Task HandleTelegramCommand(string command, string chatId)
        {
            var parts = command.Trim ().Split (' ');
            var cmd = parts[0].ToLower ();

            switch (cmd)
            {
                case "/status":
                    await _telegram.SendMessageAsync (GetStatusText (), chatId);
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
                case "/help":
                    string help = "Доступные команды:\n/status – состояние бота\n/stop – остановить торговлю\n/start – запустить торговлю\n/export – экспорт логов и признаков\n/help – эта справка";
                    await _telegram.SendMessageAsync (help, chatId);
                    break;
                default:
                    await _telegram.SendMessageAsync ("Неизвестная команда. /help для списка.", chatId);
                    break;
            }
        }

        private string GetStatusText()
        {
            string status = _isRunning ? "🟢 Активен" : "🔴 Остановлен";
            decimal balance = _wallet.GetTotalBalance ("USDC");
            int positions = _positions.Count;
            return $"🤖 <b>Статус бота:</b> {status}\n💰 Баланс USDC: {balance:F2}\n📊 Открытых позиций: {positions}";
        }
    }
}