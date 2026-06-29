using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BinanceBotWpf.Models;
using BinanceBotWpf.Risk;
using BinanceBotWpf.ViewModels;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Основной сервис торговли (упрощённая версия после рефакторинга)
    /// </summary>
    public class TradingService : ITradingService
    {
        private static readonly HttpClient _sharedHttpClient = new () { Timeout = TimeSpan.FromSeconds (30) };

        // Основные компоненты (интерфейсы — все зависимости через DI)
        private readonly BinanceClient _client;
        private readonly IWalletManager _wallet;
        private readonly IEarnManager _earn;
        private readonly IBalanceRebalancer _rebalancer;
        private readonly IPositionManager _positionManager;
        private IMlModelManager _mlManager;
        private readonly ITradingStrategy _strategy;
        private readonly ISignalFilter _signalFilter;
        private readonly IPositionProtector _positionProtector;
        private WebSocketPriceManager _webSocketManager;
        private UpdateChecker _updateChecker;
        private GridBot _gridBot;
        private readonly IVolumeBreakoutStrategy _volumeBreakout;
        private readonly IDcaStrategy _dcaStrategy;
        private readonly INewsProvider _newsProvider;
        private readonly IMacroCalendarProvider _macroCalendar;
        private TradingSettings _tradingSettings;
        private readonly IBackupService _backupService;
        private readonly IAiRiskEngine _aiRiskEngine;
        private readonly IDashboardWebSocketServer _dashboardServer;
        private WhaleMonitor _whaleMonitor;
        private ISimpleEarnStrategy _earnStrategy;
        private readonly IFearGreedIndexProvider _fearGreedProvider;
        private readonly IPriceAlertManager _priceAlertManager;
        private readonly IRiskManager _riskManager;
        private readonly BotConfig _config;
        private readonly StatePersistence _statePersistence;
        private TelegramCommandHandler _telegramHandler;
        private DashboardCommandHandler _dashboardHandler;

        private MainWindowViewModel _ui;
        private bool _isRunning;
        private TelegramNotifier _telegram;
        private CancellationTokenSource _shutdownCts;

        // Списки и кэш
        private List<string> _activePairs = new ();
        private readonly object _pairsLock = new ();
        private readonly Dictionary<string, DateTime> _lastBuyTime = new ();
        private readonly List<DateTime> _recentTradeTimes = new ();
        private readonly List<string> _recentErrors = new ();
        private readonly Dictionary<string, (List<BinanceKline> Klines, DateTime Expiry)> _klinesCache = new ();
        private readonly object _klinesCacheLock = new ();

        // Настройки
        private readonly string _telegramBotToken;
        private readonly string _telegramChatId;
        private readonly TimeSpan _ordersFetchInterval = TimeSpan.FromHours (4);
        private DateTime _lastOrdersFetch = DateTime.MinValue;

        // Флаги циклов
        private bool _balanceLoopEnabled = true;
        private bool _tradingLoopEnabled = true;
        private readonly List<Dictionary<string, object>> _equityHistory = new ();
        private const int MaxEquityHistory = 200;
        private readonly Dictionary<string, Dictionary<string, object>> _lastAnalysis = new ();
        private readonly List<string> _recentLogs = new ();
        private const int MaxRecentLogs = 100;
        private decimal _sessionStartBalance;
        private bool _sessionStartBalanceCaptured;
        private readonly List<Dictionary<string, object>> _pnlHistory = new ();
        private const int MaxPnlHistory = 200;

        // TradingService.cs, конструктор — все зависимости через DI:
        public TradingService (
            IBinanceClient client,
            IWalletManager wallet,
            IEarnManager earn,
            IBalanceRebalancer rebalancer,
            IPositionManager positionManager,
            IMlModelManager mlManager,
            ITradingStrategy strategy,
            ISignalFilter signalFilter,
            IPositionProtector positionProtector,
            IVolumeBreakoutStrategy volumeBreakout,
            IDcaStrategy dcaStrategy,
            INewsProvider newsProvider,
            IMacroCalendarProvider macroCalendar,
            TradingSettings tradingSettings,
            IBackupService backupService,
            IAiRiskEngine aiRiskEngine,
            IDashboardWebSocketServer dashboardServer,
            IFearGreedIndexProvider fearGreedProvider,
            IPriceAlertManager priceAlertManager,
            IRiskManager riskManager,
            WebSocketPriceManager webSocketManager,
            ISimpleEarnStrategy earnStrategy,
            BotConfig config)
        {
            _client = (BinanceClient)client;
            _wallet = wallet;
            _earn = earn;
            _rebalancer = rebalancer;
            _positionManager = positionManager;
            _mlManager = mlManager;
            _strategy = strategy;
            _signalFilter = signalFilter;
            _positionProtector = positionProtector;
            _volumeBreakout = volumeBreakout;
            _dcaStrategy = dcaStrategy;
            _newsProvider = newsProvider;
            _macroCalendar = macroCalendar;
            _tradingSettings = tradingSettings;
            _backupService = backupService;
            _aiRiskEngine = aiRiskEngine;
            _dashboardServer = dashboardServer;
            _fearGreedProvider = fearGreedProvider;
            _priceAlertManager = priceAlertManager;
            _riskManager = riskManager;
            _webSocketManager = webSocketManager;
            _earnStrategy = earnStrategy;
            _config = config;
            _telegramBotToken = config?.TelegramBotToken ?? "";
            _telegramChatId = config?.TelegramChatId ?? "";

            // State persistence
            string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            _statePersistence = new StatePersistence(dataDir);
        }

        private bool _loggerSet = false;

        public async Task SetLoggerAsync(Action<string> logger)
        {
            if (_loggerSet) return;
            _loggerSet = true;

            // Оборачиваем логгер для сбора логов для дашборда
            Action<string> wrappedLogger = msg =>
            {
                logger (msg);
                lock (_recentLogs)
                {
                    _recentLogs.Add ($"[{DateTime.UtcNow:HH:mm:ss}] {msg}");
                    if (_recentLogs.Count > MaxRecentLogs)
                        _recentLogs.RemoveAt (0);
                }
            };

            ServiceLogger.Instance.SetRootLogger (wrappedLogger);

            _wallet.OnLogGenerated += wrappedLogger;
            _earn.OnLogGenerated += wrappedLogger;
            _rebalancer.OnLogGenerated += wrappedLogger;
            _client.OnLogGenerated += wrappedLogger;

            _strategy.SetMlManager((MlModelManager)_mlManager);

            // ═══════ Золотая архитектура: 3 эшелона ИИ ═══════
            try
            {
                var cfg = _config;
                bool adaptiveEnabled = cfg?.AdaptiveAgentEnabled ?? true;
                bool validatorEnabled = cfg?.SignalValidatorEnabled ?? true;
                bool newsEnabled = cfg?.NewsSentinelEnabled ?? true;

                decimal slMult = cfg?.AdaptiveSlMultiplier ?? 0.4m;
                decimal periodMult = cfg?.AdaptivePeriodMultiplier ?? 0.3m;
                decimal volThresh = cfg?.ValidatorVolumeThreshold ?? 8.0m;
                decimal atrThresh = cfg?.ValidatorAtrThreshold ?? 0.15m;
                int rsiLow = cfg?.ValidatorRsiLow ?? 20;
                int rsiHigh = cfg?.ValidatorRsiHigh ?? 80;

                var adaptiveAgent = new AdaptiveAgent (wrappedLogger, slMult, periodMult);
                _strategy.SetAdaptiveAgent (adaptiveAgent, adaptiveEnabled);
                _ui?.AddLog ($"🔧 Эшелон 1 (AdaptiveAgent): {(adaptiveEnabled ? "включён" : "выключен")} SL×{slMult} Period×{periodMult}");

                var signalValidator = new SignalValidator (wrappedLogger, volThresh, atrThresh, rsiLow, rsiHigh);
                _strategy.SetSignalValidator (signalValidator, validatorEnabled);
                _ui?.AddLog ($"🔍 Эшелон 2 (SignalValidator): {(validatorEnabled ? "включён" : "выключен")} Vol>{volThresh} ATR>{atrThresh} RSI {rsiLow}/{rsiHigh}");

                var newsSentinel = new NewsSentinel (wrappedLogger);
                _strategy.SetNewsSentinel (newsSentinel, newsEnabled);
                _ui?.AddLog ($"📰 Эшелон 3 (NewsSentinel): {(newsEnabled ? "включён" : "выключен")}");
            }
            catch (Exception ex)
            {
                _ui?.AddLog ($"⚠️ Ошибка инициализации ИИ-агентов: {ex.Message}");
            }

            // Инициализация Telegram
            string tgToken = _telegramBotToken;
            string tgChatId = _telegramChatId;

            if (string.IsNullOrEmpty (tgToken) || string.IsNullOrEmpty (tgChatId))
            {
                try
                {
                    var fallbackConfig = _config;
                    if (fallbackConfig != null)
                    {
                        if (string.IsNullOrEmpty (tgToken)) tgToken = fallbackConfig.TelegramBotToken;
                        if (string.IsNullOrEmpty (tgChatId)) tgChatId = fallbackConfig.TelegramChatId;
                    }
                }
                catch (Exception ex)
                {
                    wrappedLogger ($"⚠️ Не удалось прочитать config.json для Telegram: {ex.Message}");
                }
            }

            if (!string.IsNullOrEmpty (tgToken) && !string.IsNullOrEmpty (tgChatId))
            {
                try
                {
                    if (_telegram == null)
                    {
                        _telegram = new TelegramNotifier (tgToken, tgChatId);
                        _telegram.OnStatusChanged += (isEnabled, msg) =>
                        {
                            wrappedLogger (isEnabled ? $"✅ Telegram: {msg}" : $"❌ Telegram: {msg}");
                            _ui?.RefreshTelegramStatus ();
                        };
                        _telegramHandler = new TelegramCommandHandler (
                            _telegram, _client, (WalletManager)_wallet, (PositionManager)_positionManager, _tradingSettings,
                            (MlModelManager)_mlManager, _webSocketManager, (BackupService)_backupService, (FearGreedIndexProvider)_fearGreedProvider,
                            (PriceAlertManager)_priceAlertManager, _ui, _recentErrors, _activePairs,
                            () => _isRunning,
                            () => { StopTrading (); return Task.CompletedTask; },
                            async (sym) => await StartAutoGridAsync (sym),
                            () => StopGridAsync (),
                            () => GetStatusText (),
                            () => GetPerformanceStats (),
                            msg => _ui?.AddLog (msg));
                        _telegram.StartListening ((cmd, chatId) => _telegramHandler.HandleAsync (cmd, chatId));
                        wrappedLogger ("⏳ Подключение к Telegram...");
                    }
                }
                catch (Exception ex)
                {
                    wrappedLogger ($"❌ Ошибка инициализации Telegram: {ex.Message}");
                }
            }
            else
            {
                wrappedLogger ("⚠️ Telegram не настроен. Уведомления отключены.");
            }

            // Инициализация UpdateChecker для проверки обновлений на GitHub
            Func<string, System.Threading.Tasks.Task> notifyTelegram = async (msg) =>
            {
                if (_telegram != null && !string.IsNullOrEmpty (tgToken) && !string.IsNullOrEmpty (tgChatId))
                    await _telegram.SendMessageAsync (msg);
            };

            try
            {
                var httpClient = SharedHttpClient.Instance;
                
                _updateChecker = new UpdateChecker (httpClient, logger, notifyTelegram);
                _updateChecker.OnNewVersionAvailable += (version, url) =>
                {
                    _ui?.AddLog ($"🎉 Новая версия {version} доступна!");
                };
                
                wrappedLogger ("✅ Проверка обновлений инициализирована");
            }
            catch (Exception ex)
            {
                wrappedLogger ($"⚠️ Ошибка инициализации UpdateChecker: {ex.Message}");
            }

            // FearGreed и PriceAlert инициализированы через DI — привязываем только обработчики
            _priceAlertManager.OnAlertTriggered += alert =>
            {
                _ui?.AddLog ($"🔔 {alert.Symbol} {alert.Direction} {alert.TargetPrice} triggered!");
            };

            // Запуск Dashboard WebSocket сервера сразу при старте приложения
            try
            {
                System.Diagnostics.Debug.WriteLine ("[Dashboard] Creating handler...");
                _dashboardHandler = new DashboardCommandHandler (
                    _ui, () => _isRunning,
                    () => { StopTrading (); return Task.CompletedTask; },
                    async (vm) => await StartTradingAsync (vm),
                    () => RunBacktestAndBroadcast (),
                    () => RunOptimizationAndBroadcast (),
                    () => TestTelegramAsync ());
                _dashboardServer.OnCommand = (action, data) => _dashboardHandler.HandleAsync (action, data);
                System.Diagnostics.Debug.WriteLine ("[Dashboard] Starting server on port 8765...");
                await _dashboardServer.StartAsync (8765);
                System.Diagnostics.Debug.WriteLine ("[Dashboard] Server started OK");
                _ui?.AddLog ("📡 Dashboard WebSocket доступен на http://localhost:8765");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine ($"[Dashboard] FAILED: {ex}");
                _ui?.AddLog ($"⚠️ Dashboard WS не запущен: {ex.Message}");
            }
        }

        public async Task StartTradingAsync(MainWindowViewModel vm)
        {
            if (_isRunning) return;
            _ui = vm;
            _isRunning = true;
            _shutdownCts = new CancellationTokenSource ();
            if (_ui != null) _ui.IsRunning = true;

            await SetLoggerAsync (vm.AddLog);
            await InitAsync ();

            // Sync trailing stop from settings to protector
            if (_ui != null && _positionProtector is PositionProtector prot)
            {
                prot.TrailingStopPercent = _ui.TrailingStopPercent;
            }

            // Restore trading state from file
            _statePersistence.Register(GetCurrentState, RestoreState);
            _statePersistence.Restore();
            _statePersistence.StartAutoSave();

            _ = Task.Run (() => RunLoopWithRestart (BalanceLoop, "BalanceLoop"));
            _ = Task.Run (() => RunLoopWithRestart (TradingLoop, "TradingLoop"));
            _ = Task.Run (() => RunLoopWithRestart (AutoOptimizeLoop, "AutoOptimizeLoop"));
            _ = Task.Run (() => RunLoopWithRestart (PeriodicUpdateCheckLoop, "PeriodicUpdateCheck"));
            _ = Task.Run (() => RunLoopWithRestart (DailyReportLoop, "DailyReport"));
            _ = Task.Run (() => RunLoopWithRestart (WhaleLoop, "WhaleLoop"));
            _ = Task.Run (() => RunLoopWithRestart (EarnOptimizeLoop, "EarnOptimize"));
            _ = Task.Run (() => RunLoopWithRestart (FearGreedLoop, "FearGreed"));
        }

        private async Task RunLoopWithRestart (Func<Task> loop, string name)
        {
            while (_isRunning)
            {
                try
                {
                    await loop ();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _ui?.AddLog ($"❌ {name} упал: {ex.Message}. Перезапуск через 10 сек...");
                    try { await Task.Delay (10000, _shutdownCts?.Token ?? CancellationToken.None); } catch { }
                }
            }
        }

        public void StopTrading()
        {
            _isRunning = false;

            // Save state before stopping
            try { _statePersistence?.Save (); } catch { }

            _shutdownCts?.Cancel ();
            if (_ui != null) _ui.IsRunning = false;

            try { _webSocketManager?.Dispose (); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"⚠️ Ошибка остановки WebSocket: {ex.Message}"); }
            finally { _webSocketManager = null; }

            try { _gridBot?.Dispose (); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"⚠️ Ошибка остановки GridBot: {ex.Message}"); }
            finally { _gridBot = null; }

            try { _whaleMonitor?.Dispose (); }
            catch { }

            // Неdisposing DI singletons — контейнер управляет их жизненным циклом

            try { _dashboardServer?.Stop (); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"⚠️ Ошибка остановки DashboardServer: {ex.Message}"); }

            try { _shutdownCts?.Dispose (); } catch { }
            _shutdownCts = null;
        }

        public decimal GetCurrentPriceForSymbol(string symbol) => GetCurrentPrice (symbol);

        /// <summary>
        /// Capture current trading state for persistence.
        /// </summary>
        private TradingState GetCurrentState ()
        {
            var trades = _ui?.TradesHistory?.ToList () ?? new List<TradeLog> ();
            return new TradingState
            {
                SessionStartBalance = _sessionStartBalance,
                SessionStartBalanceCaptured = _sessionStartBalanceCaptured,
                LastBuyTime = new Dictionary<string, DateTime> (_lastBuyTime),
                RecentTradeTimes = new List<DateTime> (_recentTradeTimes),
                TradesHistory = trades,
                TotalPnL = _ui?.TotalPnL ?? 0,
                WinRate = _ui?.WinRate ?? 0,
                TotalTrades = _ui?.TotalTrades ?? 0,
                WinningTrades = _ui?.WinningTrades ?? 0,
                LosingTrades = _ui?.LosingTrades ?? 0,
                BestPnL = _ui?.BestPnL ?? 0,
                WorstPnL = _ui?.WorstPnL ?? 0,
                PeakBalance = _ui?.PeakBalance ?? 0,
                MaxDrawdown = _ui?.MaxDrawdown ?? 0,
                TotalProfitSum = _ui?.TotalProfitSum ?? 0,
                TotalLossSum = _ui?.TotalLossSum ?? 0,
                EquityHistory = new List<Dictionary<string, object>> (_equityHistory),
                PnlHistory = new List<Dictionary<string, object>> (_pnlHistory),
            };
        }

        /// <summary>
        /// Restore trading state from saved snapshot.
        /// </summary>
        private void RestoreState (TradingState state)
        {
            if (state == null) return;

            _sessionStartBalance = state.SessionStartBalance;
            _sessionStartBalanceCaptured = state.SessionStartBalanceCaptured;

            _lastBuyTime.Clear ();
            foreach (var kvp in state.LastBuyTime)
            {
                // Only restore cooldowns that haven't expired
                if (DateTime.UtcNow - kvp.Value < TimeSpan.FromMinutes (15))
                    _lastBuyTime[kvp.Key] = kvp.Value;
            }

            _recentTradeTimes.Clear ();
            foreach (DateTime t in state.RecentTradeTimes)
            {
                if (DateTime.UtcNow - t < TimeSpan.FromHours (1))
                    _recentTradeTimes.Add (t);
            }

            // Restore trade history and stats
            if (_ui != null && state.TradesHistory.Count > 0)
            {
                foreach (var trade in state.TradesHistory)
                {
                    _ui.AddTradeToHistory (trade);
                }
            }

            _equityHistory.Clear ();
            _equityHistory.AddRange (state.EquityHistory);

            _pnlHistory.Clear ();
            _pnlHistory.AddRange (state.PnlHistory);

            _ui?.AddLog ($"📂 Состояние восстановлено: {state.TradesHistory.Count} сделок, баланс сессии ${state.SessionStartBalance:F2}");
        }

        private async Task RunBacktestAndBroadcast ()
        {
            if (_dashboardServer == null || !_dashboardServer.IsRunning) return;

            _ui?.AddLog ("📊 Бэктест: загрузка исторических данных...");

            string[] candidates;
            lock (_pairsLock) { candidates = _activePairs.Count > 0 ? _activePairs.ToArray () : new[] { "BTCUSDC", "ETHUSDC", "DOGEUSDC" }; }

            List<BinanceKline> klines = null;
            string pair = null;
            foreach (string candidate in candidates)
            {
                klines = await GetKlinesCachedAsync (candidate, "1h", 1000);
                if (klines != null && klines.Count >= 100)
                {
                    pair = candidate;
                    break;
                }
                _ui?.AddLog ($"⚠️ Бэктест: {candidate} — мало данных, пробуем следующую...");
            }

            if (klines == null || klines.Count < 100 || pair == null)
            {
                _ui?.AddLog ("❌ Бэктест: недостаточно данных ни для одной пары");
                return;
            }

            var engine = new BacktestEngine (msg => _ui?.AddLog (msg));
            decimal balance = _wallet?.GetTotalBalance ("USDC") ?? 1000m;
            if (balance <= 0) balance = 1000m;

            var result = engine.Run (
                klines,
                _ui?.FastSma ?? 12,
                _ui?.SlowSma ?? 26,
                _ui?.RsiPeriod ?? 14,
                _ui?.StopLossPercent ?? 0.02m,
                _ui?.TakeProfitPercent ?? 0.06m,
                balance);

            if (result == null)
            {
                _ui?.AddLog ("❌ Бэктест: не удалось выполнить");
                return;
            }

            decimal[] eqArray = result.EquityCurve?.ToArray () ?? Array.Empty<decimal> ();
            int step = Math.Max (1, eqArray.Length / 30);
            var equityCurve = new List<Dictionary<string, object>> ();
            for (int i = 0; i < eqArray.Length; i += step)
            {
                equityCurve.Add (new Dictionary<string, object>
                {
                    ["date"] = i.ToString (),
                    ["value"] = Math.Round (eqArray[i], 2),
                });
            }
            if (eqArray.Length > 0)
            {
                equityCurve.Add (new Dictionary<string, object>
                {
                    ["date"] = ( eqArray.Length - 1 ).ToString (),
                    ["value"] = Math.Round (eqArray[^1], 2),
                });
            }

            decimal avgWin = result.WinningTrades > 0
                ? Math.Round (result.TotalReturn / result.WinningTrades, 2)
                : 0;
            decimal avgLoss = result.LosingTrades > 0
                ? -Math.Round (Math.Abs (result.TotalReturn / result.LosingTrades), 2)
                : 0;

            _dashboardServer.BroadcastBacktest (new Dictionary<string, object>
            {
                ["startDate"] = klines.First ().OpenTime.ToString ("yyyy-MM-dd"),
                ["endDate"] = klines.Last ().OpenTime.ToString ("yyyy-MM-dd"),
                ["initialBalance"] = balance,
                ["finalBalance"] = Math.Round (balance * ( 1 + result.TotalReturn / 100m ), 2),
                ["totalReturn"] = Math.Round (result.TotalReturn, 1),
                ["maxDrawdown"] = Math.Round (result.MaxDrawdown, 1),
                ["sharpeRatio"] = Math.Round (result.SharpeRatio, 2),
                ["winRate"] = Math.Round (result.WinRate, 1),
                ["totalTrades"] = result.TotalTrades,
                ["profitFactor"] = Math.Round (result.ProfitFactor, 2),
                ["avgWin"] = avgWin,
                ["avgLoss"] = avgLoss,
                ["equity"] = equityCurve,
                ["monthlyReturns"] = new List<object> (),
                ["strategyParams"] = new Dictionary<string, string>
                {
                    ["fastSma"] = (_ui?.FastSma ?? 12).ToString (),
                    ["slowSma"] = (_ui?.SlowSma ?? 26).ToString (),
                    ["rsiPeriod"] = (_ui?.RsiPeriod ?? 14).ToString (),
                    ["stopLoss"] = $"ATR × 1.5",
                    ["takeProfit"] = $"SL × 3",
                    ["adaptiveSl"] = "0.4x",
                },
            });

            _ui?.AddLog ($"✅ Бэктест завершён: доходность {result.TotalReturn:F1}%, винрейт {result.WinRate:F1}%, сделок {result.TotalTrades}");
        }

        private async Task RunOptimizationAndBroadcast ()
        {
            await RunBacktestAndBroadcast ();
        }

        /// <summary>
        /// Запуск сеточного бота
        /// </summary>
        public async Task StartGridAsync(string symbol, decimal gridRangePercent, int gridLevels, decimal investmentPercent)
        {
            if (_gridBot != null && _gridBot.IsRunning)
            {
                _ui?.AddLog ("⚠️ GridBot уже запущен. Остановите перед перезапуском.");
                return;
            }

            decimal currentPrice = GetCurrentPrice (symbol);
            if (currentPrice <= 0)
            {
                _ui?.AddLog ($"❌ Не удалось получить цену для {symbol}");
                return;
            }

            decimal balance = _wallet.GetTotalBalance ("USDC");
            decimal investmentUsdc = balance * investmentPercent;

            _gridBot = new GridBot (_client, (PositionManager)_positionManager, msg => _ui?.AddLog (msg));
            _gridBot.OnTrade += async trade =>
            {
                _ui?.AddTradeToHistory (trade);
                string emoji = trade.PnL >= 0 ? "🟢" : "🔴";
                await SendTradeNotification ($"{emoji} <b>GRID SELL</b>\n📊 {trade.Symbol}\n💵 Вход: {trade.EntryPrice:F4} → Выход: {trade.ExitPrice:F4}\n📈 PnL: {trade.PnL:+F2;-F2} USDC ({trade.PnLPercent:+F2;-F2}%)");
            };
            await _gridBot.StartAsync (symbol, currentPrice, gridRangePercent, gridLevels, investmentUsdc);
        }

        /// <summary>
        /// Авто-запуск сетки с параметрами от ИИ
        /// </summary>
        public async Task StartAutoGridAsync(string symbol)
        {
            if (_gridBot != null && _gridBot.IsRunning)
            {
                _ui?.AddLog ("⚠️ GridBot уже запущен.");
                return;
            }

            decimal currentPrice = GetCurrentPrice (symbol);
            if (currentPrice <= 0)
            {
                _ui?.AddLog ($"❌ Не удалось получить цену для {symbol}");
                return;
            }

            decimal balance = _wallet.GetTotalBalance ("USDC");

            // Получаем индикаторы для ИИ
            var klines = await _client.GetKlinesAsync (symbol, "1h", 100);
            if (klines == null || klines.Count < 30) return;

            var closes = klines.Select (k => k.Close).ToList ();
            var volumes = klines.Select (k => k.Volume).ToList ();
            var highs = klines.Select (k => k.High).ToList ();
            var lows = klines.Select (k => k.Low).ToList ();

            decimal fastSma = closes.TakeLast (9).Average ();
            decimal slowSma = closes.TakeLast (21).Average ();
            var rsiValues = TechnicalAnalysis.RSI (closes, 14);
            decimal rsi = rsiValues.LastOrDefault () ?? 50;
            decimal avgVolume = volumes.TakeLast (20).Average ();
            decimal volumeRatio = avgVolume > 0 ? volumes.Last () / avgVolume : 1;
            var macd = TechnicalAnalysis.MACD (closes, 12, 26, 9);
            decimal macdHist = macd.Histogram.LastOrDefault () ?? 0;
            var bb = TechnicalAnalysis.BollingerBands (closes, 20, 2);
            decimal bbWidth = (bb.Upper.LastOrDefault () ?? currentPrice) - (bb.Lower.LastOrDefault () ?? currentPrice);
            bbWidth = currentPrice > 0 ? bbWidth / currentPrice : 0.05m;
            decimal obv = TechnicalAnalysis.OBV (klines).LastOrDefault ();

            // ИИ рассчитывает параметры сетки
            var aiRisk = await _aiRiskEngine.CalculateRiskAsync (
                symbol, balance, currentPrice, fastSma, slowSma, rsi, volumeRatio, macdHist, bbWidth, obv);

            var grid = aiRisk.Grid;
            decimal investmentUsdc = balance * grid.InvestmentPercent;

            _ui?.AddLog ($"🤖 ИИ-автосетка: {symbol} | Баланс: {balance:F2} USDC");
            _ui?.AddLog ($"   Диапазон: ±{grid.RangePercent:P0} | Уровней: {grid.Levels} | Инвестиции: {grid.InvestmentPercent:P0} ({investmentUsdc:F2} USDC)");

            _gridBot = new GridBot (_client, (PositionManager)_positionManager, msg => _ui?.AddLog (msg));
            _gridBot.OnTrade += async trade =>
            {
                _ui?.AddTradeToHistory (trade);
                string emoji = trade.PnL >= 0 ? "🟢" : "🔴";
                await SendTradeNotification ($"{emoji} <b>GRID SELL</b>\n📊 {trade.Symbol}\n💵 Вход: {trade.EntryPrice:F4} → Выход: {trade.ExitPrice:F4}\n📈 PnL: {trade.PnL:+F2;-F2} USDC ({trade.PnLPercent:+F2;-F2}%)");
            };
            await _gridBot.StartAsync (symbol, currentPrice, grid.RangePercent, grid.Levels, investmentUsdc, grid.UseDynamicStep);
        }

        /// <summary>
        /// Остановка сеточного бота
        /// </summary>
        public async Task StopGridAsync()
        {
            if (_gridBot != null && _gridBot.IsRunning)
            {
                await _gridBot.StopAsync ();
                _gridBot.Dispose ();
                _gridBot = null;
            }
        }

        private async Task InitAsync()
        {
            // TradingSettings загружены через DI, не нужно загружать заново

            // Инициализация WebSocket менеджера
            bool useFutures = _ui?.FuturesEnabled == true || _tradingSettings?.FuturesEnabled == true;
            if (_webSocketManager != null)
            {
                _webSocketManager.Dispose ();
            }
            _webSocketManager = new WebSocketPriceManager (msg => _ui?.AddLog (msg), useFutures);
            _ui?.AddLog ($"🔌 WebSocket эндпоинт: {(useFutures ? "фьючерсы (fstream.binance.com)" : "спот (stream.binance.com)")}");

            await _wallet.UpdateBalance ();
            await UpdatePairs ();
            await LoadPositions ();
            _ui?.AddLog (_client.IsTestnet ? "⚠️ ТЕСТОВАЯ СЕТЬ" : "✅ РЕАЛЬНАЯ СЕТЬ");

            // Загружаем настройки фьючерсов из BotConfig
            try
            {
                var cfg = _config;
                if (cfg != null)
                {
                    _tradingSettings.FuturesLeverage = cfg.FuturesLeverage;
                    _ui?.AddLog ($"⚙️ Фьючерсы: плечо {cfg.FuturesLeverage}x, макс. риск {cfg.FuturesMaxRiskPercent:P0}");
                }
            }
            catch { }

            // Инициализация фьючерсов (если включены)
            if (_ui?.FuturesEnabled == true)
            {
                try
                {
                    // Читаем ключи напрямую из BotConfig
                    string futuresKey = "";
                    string futuresSecret = "";

                    try
                    {
                        var cfg = _config;
                        if (cfg != null)
                        {
                            futuresKey = cfg.FuturesApiKey ?? "";
                            futuresSecret = cfg.FuturesApiSecret ?? "";
                            // Фоллбэк на спот-ключи если фьючерсные не заданы
                            if (string.IsNullOrEmpty (futuresKey))
                                futuresKey = cfg.ApiKey ?? "";
                            if (string.IsNullOrEmpty (futuresSecret))
                                futuresSecret = cfg.ApiSecret ?? "";
                        }
                    }
                    catch { }

                    if (string.IsNullOrEmpty (futuresKey) || string.IsNullOrEmpty (futuresSecret))
                    {
                        _ui?.AddLog ("⚠️ Фьючерсные API ключи не настроены. Укажите futuresApiKey/futuresApiSecret в config.json");
                    }
                    else
                    {
                        var futuresClient = new BinanceFuturesClient (futuresKey, futuresSecret);
                        await futuresClient.SyncTimeAsync ();
                        await futuresClient.SetMarginTypeAsync ("BTCUSDT", "ISOLATED");
                        await futuresClient.SetPositionModeAsync (true); // Hedge Mode

                        int leverage = _tradingSettings?.FuturesLeverage ?? 5;
                        await futuresClient.SetLeverageAsync ("BTCUSDT", leverage);
                        _ui?.AddLog ($"✅ Фьючерсы: Isolated Margin, Hedge Mode, плечо {leverage}x");
                    }
                }
                catch (Exception ex)
                {
                    _ui?.AddLog ($"⚠️ Ошибка инициализации фьючерсов: {ex.Message}");
                }
            }
            
            // Проверка обновлений версии на GitHub (не блокирует инициализацию)
            if (_updateChecker != null)
            {
                _ = _updateChecker.CheckForUpdatesAsync ();
            }

            // Авто-запуск сетки с параметрами от ИИ (если включена)
            if (_tradingSettings?.GridBotEnabled == true)
            {
                string gridSymbol = _tradingSettings.GridSymbol ?? "DOGEUSDC";
                _ui?.AddLog ($"🤖 Автозапуск ИИ-сетки для {gridSymbol}...");
                _ = Task.Run (async () =>
                {
                    for (int attempt = 0; attempt < 10; attempt++)
                    {
                        await Task.Delay (3000, _shutdownCts?.Token ?? CancellationToken.None);
                        decimal price = GetCurrentPrice (gridSymbol);
                        if (price <= 0)
                        {
                            try { price = await _client.GetPriceAsync (gridSymbol); } catch { }
                        }
                        if (price > 0)
                        {
                            _webSocketManager?.UpdatePrice (gridSymbol, price);
                            _ui?.AddLog ($"💰 Цена {gridSymbol}: {price:F6} — запуск сетки");
                            await StartAutoGridAsync (gridSymbol);
                            return;
                        }
                        _ui?.AddLog ($"⏳ Ожидание цены {gridSymbol}... (попытка {attempt + 1}/10)");
                    }
                    _ui?.AddLog ($"❌ Не удалось получить цену {gridSymbol} за 30 сек. Запустите сетку вручную.");
                });
            }

            // Dashboard уже запущен в SetLogger — пропускаем
        }

        // Тир-лист пар по балансу (приоритет: чем выше — тем раньше в списке)
        private static readonly string[] _tierLow = new[] { "DOGE", "XRP", "ADA", "SOL" };
        private static readonly string[] _tierMid = new[] { "ETH", "BNB", "LINK", "NEAR", "SUI" };
        private static readonly string[] _tierHigh = new[] { "BTC", "PEPE" };

        private static string[] GetWhitelistForBalance (decimal balance)
        {
            var result = new List<string> ();
            result.AddRange (_tierLow);
            if (balance >= 100) result.AddRange (_tierMid);
            if (balance >= 1000) result.AddRange (_tierHigh);
            return result.ToArray ();
        }

        private static int GetMaxPositionsForBalance (decimal balance)
        {
            if (balance >= 1000) return 10;
            if (balance >= 100) return 5;
            return 2;
        }

        private async Task UpdatePairs()
        {
            try
            {
                if (_webSocketManager == null)
                {
                    _ui?.AddLog ("⚠️ WebSocket менеджер не инициализирован, пропускаю обновление пар");
                    return;
                }

                decimal balance = _wallet?.GetTotalBalance ("USDC") ?? 0;
                string quoteCurrency = _ui?.QuoteCurrency ?? "USDC";
                string quote = quoteCurrency == "USDT" ? "USDT" : "USDC";

                // Динамический белый список по балансу
                string[] whitelist = GetWhitelistForBalance (balance);
                int maxPositions = GetMaxPositionsForBalance (balance);

                if (_ui != null) _ui.MaxConcurrentTrades = maxPositions;

                var allMinNotionals = await _client.GetAllMinNotionalsAsync ();
                var allPairs = new List<string> ();

                foreach (string asset in whitelist)
                {
                    string pair = asset + quote;
                    if (allMinNotionals.TryGetValue (pair, out decimal minNot))
                    {
                        if (balance >= minNot * 2m)
                            allPairs.Add (pair);
                    }
                    else
                    {
                        allPairs.Add (pair);
                    }
                }

                if (allPairs.Count > 0)
                {
                    lock (_pairsLock) { _activePairs = allPairs; }
                    var subscribedSymbols = _webSocketManager.GetSubscribedSymbols ();

                    if (subscribedSymbols == null || subscribedSymbols.Length == 0)
                    {
                        await _webSocketManager.SubscribeToSymbolsAsync (allPairs.ToArray ());
                    }
                    else
                    {
                        var newSymbols = allPairs.Except (subscribedSymbols).ToArray ();
                        if (newSymbols.Any ())
                            await _webSocketManager.SubscribeToSymbolsAsync (newSymbols);
                    }

                    // Логируем minNotional для отладки
                    string minInfo = string.Join (", ", allPairs.Take (5).Select (p =>
                    {
                        allMinNotionals.TryGetValue (p, out decimal mn);
                        return $"{p}={mn:F0}";
                    }));
                    _ui?.AddLog ($"📊 {allPairs.Count} пар (баланс: {balance:F0} USDC) | minNotional: {minInfo}");
                }
            }
            catch (Exception ex) 
            { 
                _ui?.AddLog ($"❌ Ошибка обновления пар: {ex.Message}");
                if (ex.InnerException != null)
                    _ui?.AddLog ($"   Детали: {ex.InnerException.Message}");
            }
        }

        /// <summary>
        /// Загружает пары и индикаторы для отображения в таблице до старта торговли.
        /// Вызывается при запуске приложения, чтобы таблица не была пустой.
        /// </summary>
        public async Task LoadPairsForDisplayAsync (MainWindowViewModel ui)
        {
            try
            {
                decimal balance = _wallet?.GetTotalBalance ("USDC") ?? 0;
                if (balance <= 0)
                {
                    try { balance = await _client.GetAccountBalanceAsync ("USDC"); } catch { }
                }

                Dictionary<string, decimal> allMinNotionals;
                try
                {
                    allMinNotionals = await _client.GetAllMinNotionalsAsync ();
                }
                catch (Exception ex)
                {
                    allMinNotionals = new Dictionary<string, decimal> ();
                    ui.AddLog ($"⚠️ minNotional ошибка: {ex.Message}");
                }

                string quoteCurrency = ui?.QuoteCurrency ?? "USDC";
                string quote = quoteCurrency == "USDT" ? "USDT" : "USDC";

                // Динамический белый список по балансу
                string[] whitelist = GetWhitelistForBalance (balance);
                int maxPositions = GetMaxPositionsForBalance (balance);

                if (ui != null) ui.MaxConcurrentTrades = maxPositions;

                var filteredPairs = new List<string> ();
                foreach (string asset in whitelist)
                {
                    string pair = asset + quote;
                    if (allMinNotionals.TryGetValue (pair, out decimal minNot))
                    {
                        if (balance >= minNot * 2m)
                            filteredPairs.Add (pair);
                    }
                    else
                    {
                        filteredPairs.Add (pair);
                    }
                }

                if (filteredPairs.Count == 0)
                {
                    ui.AddLog ("❌ Совсем нет пар для отображения");
                    return;
                }

                ui.AddLog ($"📊 Загружено {filteredPairs.Count} пар для баланса {balance:F2} USDC (макс. {maxPositions} позиций)");

                foreach (var sym in filteredPairs)
                {
                    try
                    {
                        var klines = await _client.GetKlinesAsync (sym, "1h", 50);
                        if (klines == null || klines.Count < 30) continue;

                        var closes = klines.Select (k => k.Close).ToList ();
                        decimal price = closes.Last ();
                        decimal fastSma = closes.Skip (closes.Count - 9).Average ();
                        decimal slowSma = closes.Skip (closes.Count - 21).Average ();

                        decimal rsi = TechnicalAnalysis.RSI (closes, 14).LastOrDefault () ?? 50;
                        var macd = TechnicalAnalysis.MACD (closes, 12, 26, 9);
                        decimal macdHist = macd.Histogram.LastOrDefault () ?? 0;

                        var signal = new StrategyEngine ().AnalyzePairWithWallet (sym, closes, 9, 21, price);

                        ui.UpdateMarketTable (sym, price.ToString ("F4"), false, signal.Action, fastSma, slowSma, null, null, rsi, macdHist, MarketSessionService.GetSessionLabel ());
                        await Task.Delay (150, _shutdownCts?.Token ?? CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        ui.AddLog ($"⚠️ {sym}: {ex.Message}");
                    }
                }

                ui.AddLog ($"✅ Таблица обновлена: {filteredPairs.Count} пар");
            }
            catch (Exception ex)
            {
                ui.AddLog ($"❌ Ошибка загрузки пар: {ex.Message}");
            }
        }

        private async Task LoadPositions()
        {
            await _positionManager.LoadAsync (_client, sym => Task.FromResult (GetCurrentPrice (sym)),
                p => _ui?.StopLossPercent ?? 0.02m, p => _ui?.TakeProfitPercent ?? 0.04m);
            _ui?.UpdatePositionsStatus (_positionManager.Count, _ui?.MaxConcurrentTrades ?? 3, _positionManager.GetSymbols ());
        }

        private decimal GetCurrentPrice(string sym) => _webSocketManager?.GetCurrentPrice (sym) ?? 0;

        private async Task BalanceLoop()
        {
            while (_isRunning)
            {
                try
                {
                    if (!_balanceLoopEnabled) { await Task.Delay (5000, _shutdownCts?.Token ?? CancellationToken.None); continue; }
                    await Task.Delay (60000, _shutdownCts?.Token ?? CancellationToken.None);
                    if (!_isRunning) break;

                    await _wallet.UpdateBalance ();
                    decimal bal = _wallet.GetTotalBalance ("USDC");
                    _ui?.UpdateWalletDisplay (bal.ToString ("F2"));
                    _ui?.UpdateDrawdown (bal);
                    _ui?.AddBalancePoint (DateTime.Now, bal);

                    // Проверка и создание бэкапа (раз в сутки)
                    await _backupService.CheckAndBackupAsync ();

                    // Ребаланс при низком балансе
                    decimal spotBalance = await _client.GetAccountBalanceAsync ("USDC");
                    if (spotBalance < 10)
                    {
                        var openSymbols = new HashSet<string> (_positionManager.GetSymbols ());
                        await _rebalancer.AutoConvertAssetsToUsdcAsync (_client, _isRunning, openSymbols);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _ui?.AddLog ($"❌ BalanceLoop ошибка: {ex.Message}");
                    try { await Task.Delay (5000, _shutdownCts?.Token ?? CancellationToken.None); } catch { }
                }
            }
        }

        /// <summary>
        /// Возвращает клиент Binance для доступа к API
        /// </summary>
        public BinanceClient GetBinanceClient() => _client;

        private async Task<List<BinanceKline>> GetKlinesCachedAsync (string symbol, string interval, int limit)
        {
            string cacheKey = $"{symbol}_{interval}_{limit}";
            lock (_klinesCacheLock)
            {
                if (_klinesCache.TryGetValue (cacheKey, out var cached) && DateTime.UtcNow < cached.Expiry)
                {
                    return cached.Klines;
                }
            }
            var klines = await _client.GetKlinesAsync (symbol, interval, limit);
            if (klines != null)
            {
                lock (_klinesCacheLock)
                {
                    _klinesCache[cacheKey] = (klines, DateTime.UtcNow + TimeSpan.FromSeconds (30));
                }
            }
            return klines;
        }

        private async Task TradingLoop()
        {
            while (_isRunning)
            {
                try
                {
                    if (!_tradingLoopEnabled) { await Task.Delay (5000, _shutdownCts?.Token ?? CancellationToken.None); continue; }

                    // 0. Проверка новостей и макро-событий
                    if (_tradingSettings?.AvoidNewsTime == true
                        && (_newsProvider?.HasRealApi == true || _macroCalendar?.HasRealApi == true))
                    {
                        bool newsNear = await _newsProvider.IsEventNearAsync (30);
                        bool macroNear = await _macroCalendar.IsHighImpactEventNearAsync (60);
                        if (newsNear || macroNear)
                        {
                            _ui?.AddLog ("📰 Торговля приостановлена: обнаружены значимые события");
                            await Task.Delay (300000, _shutdownCts?.Token ?? CancellationToken.None);
                            continue;
                        }
                    }

                    // 1. Защита позиций
                    var toClose = await _positionProtector.CheckAndProtectAsync (GetCurrentPrice);
                    foreach (var sym in toClose)
                    {
                        await ExecuteSell (sym);
                    }

                    // 2. Проверка баланса
                    decimal spotBalance = await _client.GetAccountBalanceAsync ("USDC");
                    if (spotBalance < 10)
                    {
                        await Task.Delay (15000, _shutdownCts?.Token ?? CancellationToken.None);
                        continue;
                    }

                    // Обновляем RiskManager с текущим балансом
                    _riskManager.BalanceUSDC = spotBalance;

                    // 3. Получение списка пар
                    List<string> pairs;
                    lock (_pairsLock) { pairs = new List<string> (_activePairs); }
                    if (pairs.Count == 0) { await Task.Delay (5000, _shutdownCts?.Token ?? CancellationToken.None); continue; }

                    // Читаем интервал из BotConfig один раз на итерацию, не на каждую пару
                    string candleInterval = "1h";
                    string entryInterval = "5m";
                    try
                    {
                        var cfg = _config;
                        if (cfg != null && !string.IsNullOrEmpty (cfg.CandleInterval))
                            candleInterval = cfg.CandleInterval;
                    }
                    catch { }

                    // Мульти-таймфрейм: читаем из TradingSettings
                    if (_ui != null)
                    {
                        candleInterval = _ui.MainTimeframe ?? "1h";
                        entryInterval = _ui.EntryTimeframe ?? "5m";
                    }

                    // 4. Анализ пар
                    foreach (var sym in pairs)
                    {
                        var klines = await GetKlinesCachedAsync (sym, candleInterval, 100);
                        if (klines == null || klines.Count < 30) continue;

                        if (_ui != null)
                        {
                            _strategy.FastSmaPeriod = _ui.FastSma;
                            _strategy.SlowSmaPeriod = _ui.SlowSma;
                            _strategy.RsiPeriod = _ui.RsiPeriod;
                            _strategy.MainTimeframe = _ui.MainTimeframe ?? "1h";
                            _strategy.EntryTimeframe = _ui.EntryTimeframe ?? "5m";
                        }

                        var analysis = await _strategy.AnalyzeAsync (sym, klines);
                        bool hasPosition = _positionManager.TryGet (sym, out _);

                        // Мульти-таймфрейм: подтверждение на мелком TF
                        bool confirmed = true;
                        if (analysis.Action == TradeAction.Buy || analysis.Action == TradeAction.Sell)
                        {
                            var entryKlines = await GetKlinesCachedAsync (sym, entryInterval, 30);
                            confirmed = _strategy.CheckEntryConfirmation (entryKlines, analysis.Action);
                            if (!confirmed)
                            {
                                _ui?.AddLog ($"⏳ {sym}: {analysis.Action} на {candleInterval}, ожидаем подтверждение на {entryInterval}");
                            }
                        }

                        // Обновление UI
                        if (analysis.Indicators.ContainsKey ("price"))
                        {
                            _ui.UpdateMarketTable (sym, analysis.Indicators["price"].ToString ("F4"),
                                hasPosition, analysis.Action,
                                analysis.Indicators.ContainsKey ("fastSma") ? analysis.Indicators["fastSma"] : 0,
                                analysis.Indicators.ContainsKey ("slowSma") ? analysis.Indicators["slowSma"] : 0,
                                null, null,
                                analysis.Indicators.ContainsKey ("rsi") ? analysis.Indicators["rsi"] : 50,
                                analysis.Indicators.ContainsKey ("macdHist") ? analysis.Indicators["macdHist"] : 0,
                                MarketSessionService.GetSessionLabel ());
                        }

                        // Cache analysis for dashboard
                        var cached = new Dictionary<string, object> ();
                        foreach (var kvp in analysis.Indicators)
                        {
                            cached[kvp.Key] = kvp.Value;
                        }
                        cached["signal"] = analysis.Action.ToString ().ToLower ();
                        cached["action"] = analysis.Action.ToString ();
                        _lastAnalysis[sym] = cached;

                        // 5. Исполнение сигналов (только с подтверждением + новостной фильтр)
                        bool traded = false;

                        // Диагностика: почему STRONG Buy не исполняется
                        if (analysis.Action == TradeAction.Buy && !hasPosition && !confirmed)
                        {
                            _ui?.AddLog ($"🔍 {sym}: STRONG Buy ЗАБЛОКИРОВАН — multi-TF подтверждение НЕ пройдено (5m)");
                        }

                        if (analysis.Action == TradeAction.Buy && !hasPosition && !confirmed)
                        {
                            // Уже залогировано выше
                        }
                        else if (analysis.Action == TradeAction.Buy && !hasPosition && _positionManager.Count < (_ui?.MaxConcurrentTrades ?? 3))
                        {
                            // Проверка лимитов RiskManager — оценка реального размера сделки
                            decimal estimatedOrderValue = spotBalance * 0.10m;
                            var riskCheck = _riskManager.CanOpenPosition (_positionManager.Count, estimatedOrderValue);
                            if (!riskCheck.Allowed)
                            {
                                _ui?.AddLog ($"🛡️ {sym}: покупка заблокирована — {riskCheck.Reason}");
                            }
                            else if (!_strategy.CheckNewsBeforePosition (sym))
                            {
                                _ui?.AddLog ($"🚫 {sym}: позиция заблокирована высокорисковыми новостями");
                            }
                            else if (_fearGreedProvider != null && _fearGreedProvider.IsExtremeGreed ())
                            {
                                var fgCached = await _fearGreedProvider.GetCurrentAsync ();
                                _ui?.AddLog ($"⚠️ {sym}: BUY при Extreme Greed (FG={fgCached?.Value}), повышенный риск!");
                                await ExecuteBuy (sym, analysis.Indicators, spotBalance);
                                traded = true;
                            }
                            else
                            {
                                await ExecuteBuy (sym, analysis.Indicators, spotBalance);
                                traded = true;
                            }
                        }
                        else if (analysis.Action == TradeAction.Sell && hasPosition && confirmed)
                        {
                            await ExecuteSell (sym);
                            traded = true;
                        }

                        // 6. Volume Breakout стратегия (если включена)
                        if (_ui?.VolumeBreakoutEnabled == true && !hasPosition && _positionManager.Count < (_ui?.MaxConcurrentTrades ?? 3))
                        {
                            if (_volumeBreakout.CheckVolumeBreakout (klines))
                            {
                                _ui?.AddLog ($"🚀 {sym}: Volume Breakout сигнал!");
                                await ExecuteBuy (sym, analysis.Indicators, spotBalance);
                                traded = true;
                            }
                        }

                        // 7. DCA стратегия (если включена)
                        if (_ui?.DcaEnabled == true && !hasPosition)
                        {
                            if (_dcaStrategy.ShouldBuy (sym, klines, spotBalance))
                            {
                                decimal buyAmount = _dcaStrategy.CalculateBuyAmount (spotBalance);
                                _ui?.AddLog ($"📊 {sym}: DCA покупка на {buyAmount:F2} USDC");
                                var indicators = new Dictionary<string, decimal> (analysis.Indicators);
                                indicators["dcaBuyAmount"] = buyAmount;
                                await ExecuteBuy (sym, indicators, spotBalance);
                                traded = true;
                            }
                        }

                        // Однократное обновление баланса за итерацию по паре
                        if (traded)
                        {
                            spotBalance = await _client.GetAccountBalanceAsync ("USDC");
                        }
                    }

                    // Push data to dashboard
                    if (_dashboardServer?.IsRunning == true)
                    {
                        try
                        {
                            List<string> activePairList;
                            lock (_pairsLock) { activePairList = new List<string> (_activePairs); }

                            var sessionLabel = MarketSessionService.GetSessionLabel ();
                            var pricesData = activePairList.Select (sym =>
                            {
                                var pairData = new Dictionary<string, object>
                                {
                                    ["pair"] = sym,
                                    ["price"] = _webSocketManager?.GetCurrentPrice (sym) ?? 0m,
                                    ["hasPosition"] = _positionManager.TryGet (sym, out _),
                                    ["session"] = sessionLabel
                                };
                                if (_lastAnalysis.TryGetValue (sym, out var indicators))
                                {
                                    foreach (var kvp in indicators)
                                    {
                                        pairData[kvp.Key] = kvp.Value;
                                    }
                                }
                                return pairData;
                            }).ToList ();
                            _dashboardServer.BroadcastPrices (pricesData);

                            var positionsData = _positionManager.Positions.Select (kvp =>
                            {
                                var pos = kvp.Value;
                                decimal currentPrice = _webSocketManager?.GetCurrentPrice (kvp.Key) ?? pos.EntryPrice;
                                decimal pnl = currentPrice > 0 ? (currentPrice - pos.EntryPrice) * pos.Quantity : 0;
                                decimal pnlPercent = pos.EntryPrice > 0 ? (currentPrice / pos.EntryPrice - 1) * 100 : 0;
                                decimal slPercent = pos.EntryPrice > 0 ? (1 - pos.StopLossPrice / pos.EntryPrice) * 100 : 0;
                                decimal tpPercent = pos.EntryPrice > 0 ? (pos.TakeProfitPrice / pos.EntryPrice - 1) * 100 : 0;
                                return new Dictionary<string, object>
                                {
                                    ["pair"] = kvp.Key,
                                    ["side"] = "LONG",
                                    ["entry"] = pos.EntryPrice,
                                    ["current"] = currentPrice,
                                    ["qty"] = pos.Quantity,
                                    ["sl"] = pos.StopLossPrice,
                                    ["tp"] = pos.TakeProfitPrice,
                                    ["pnl"] = Math.Round (pnl, 2),
                                    ["pnlPercent"] = Math.Round (pnlPercent, 2),
                                    ["slPercent"] = Math.Round (slPercent, 2),
                                    ["tpPercent"] = Math.Round (tpPercent, 2),
                                    ["margin"] = Math.Round (pos.EntryPrice * pos.Quantity, 2),
                                    ["leverage"] = 1,
                                    ["openTime"] = pos.OpenTime.ToString ("HH:mm"),
                                    ["duration"] = (DateTime.UtcNow - pos.OpenTime).TotalMinutes >= 60
                                        ? $"{(int)(DateTime.UtcNow - pos.OpenTime).TotalHours}h {(DateTime.UtcNow - pos.OpenTime).Minutes}m"
                                        : $"{(int)(DateTime.UtcNow - pos.OpenTime).TotalMinutes}m"
                                };
                            }).ToList ();
                            _dashboardServer.BroadcastPositions (positionsData);

                            _dashboardServer.BroadcastEchelons (new Dictionary<string, object>
                            {
                                ["adaptive"] = _tradingSettings?.AdaptiveAgentEnabled ?? true,
                                ["validator"] = _tradingSettings?.SignalValidatorEnabled ?? true,
                                ["newsSentinel"] = _tradingSettings?.NewsSentinelEnabled ?? true
                            });

                            // Equity curve live
                            _equityHistory.Add (new Dictionary<string, object>
                            {
                                ["time"] = DateTime.UtcNow.ToString ("MM-dd HH:mm"),
                                ["value"] = spotBalance
                            });
                            if (_equityHistory.Count > MaxEquityHistory)
                            {
                                _equityHistory.RemoveAt (0);
                            }
                            _dashboardServer.BroadcastEquity (new List<Dictionary<string, object>> (_equityHistory));

                            // PnL curve: разница от начального баланса сессии
                            if (!_sessionStartBalanceCaptured && spotBalance > 0)
                            {
                                _sessionStartBalance = spotBalance;
                                _sessionStartBalanceCaptured = true;
                            }
                            if (_sessionStartBalanceCaptured && _sessionStartBalance > 0)
                            {
                                decimal sessionPnl = spotBalance - _sessionStartBalance;
                                decimal sessionPnlPercent = _sessionStartBalance > 0 ? sessionPnl / _sessionStartBalance * 100 : 0;
                                _pnlHistory.Add (new Dictionary<string, object>
                                {
                                    ["time"] = DateTime.UtcNow.ToString ("MM-dd HH:mm"),
                                    ["pnl"] = Math.Round (sessionPnl, 2),
                                    ["pnlPercent"] = Math.Round (sessionPnlPercent, 2),
                                    ["balance"] = spotBalance,
                                    ["startBalance"] = _sessionStartBalance
                                });
                                if (_pnlHistory.Count > MaxPnlHistory)
                                    _pnlHistory.RemoveAt (0);
                                _dashboardServer.BroadcastPnl (new List<Dictionary<string, object>> (_pnlHistory));
                            }

                            // Stats — real balance (Spot + Simple Earn), PnL, win rate, positions
                            decimal totalPnL = _ui?.TotalPnL ?? 0;
                            decimal winRate = _ui?.WinRate ?? 0;
                            int totalTrades = _ui?.TotalTrades ?? 0;
                            int openPosCount = _positionManager.Count;
                            int maxPos = _ui?.MaxPositions ?? _tradingSettings?.MaxConcurrentTrades ?? 3;
                            decimal realBalance = _wallet?.GetTotalBalance ("USDC") ?? spotBalance;
                            decimal ddStr = 0;
                            if (decimal.TryParse ((_ui?.MaxDrawdownDisplay ?? "0").Replace ("%", "").Replace (",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal ddParsed))
                            {
                                ddStr = Math.Abs (ddParsed);
                            }

                            FearGreedData fearGreedData = _fearGreedProvider != null
                                ? await _fearGreedProvider.GetCurrentAsync ()
                                : null;

                            _dashboardServer.BroadcastStats (new Dictionary<string, object>
                            {
                                ["balance"] = realBalance,
                                ["pnl"] = totalPnL,
                                ["pnlPercent"] = realBalance > 0 ? Math.Round (totalPnL / realBalance * 100, 1) : 0,
                                ["winRate"] = winRate,
                                ["maxDrawdown"] = ddStr,
                                ["totalTrades"] = totalTrades,
                                ["openPositions"] = openPosCount,
                                ["maxPositions"] = maxPos,
                                ["leverage"] = _tradingSettings?.FuturesLeverage ?? 5,
                                ["winningTrades"] = _ui?.WinningTrades ?? 0,
                                ["losingTrades"] = _ui?.LosingTrades ?? 0,
                                ["bestPnL"] = _ui?.BestPnL ?? 0,
                                ["worstPnL"] = _ui?.WorstPnL ?? 0,
                                ["fearGreedValue"] = fearGreedData?.Value ?? 50,
                                ["fearGreedClassification"] = fearGreedData?.Classification ?? "Neutral",
                                ["dcaEnabled"] = _tradingSettings?.DcaEnabled ?? false,
                                ["futuresEnabled"] = _tradingSettings?.FuturesEnabled ?? false,
                                ["gridBotRunning"] = _gridBot?.IsRunning ?? false,
                                ["telegramStatus"] = _telegram?.IsEnabled == true ? "connected" : "disconnected",
                                ["session"] = MarketSessionService.GetSessionLabel (),
                                ["sessionFilterEnabled"] = _tradingSettings?.SessionFilterEnabled ?? false,
                                ["tradeOnlyEuUs"] = _tradingSettings?.TradeOnlyEuUs ?? false,
                                ["fastSma"] = _ui?.FastSma ?? 12,
                                ["slowSma"] = _ui?.SlowSma ?? 26,
                                ["rsiPeriod"] = _ui?.RsiPeriod ?? 14,
                                ["stopLossPercent"] = (double)(_ui?.StopLossPercent ?? 0.005m),
                                ["takeProfitPercent"] = (double)(_ui?.TakeProfitPercent ?? 0.008m),
                                ["riskPerTradePercent"] = (double)(_tradingSettings?.RiskPerTradePercent ?? 0.01m),
                                ["adaptiveAgentEnabled"] = _tradingSettings?.AdaptiveAgentEnabled ?? true,
                                ["signalValidatorEnabled"] = _tradingSettings?.SignalValidatorEnabled ?? true,
                                ["newsSentinelEnabled"] = _tradingSettings?.NewsSentinelEnabled ?? true,
                                ["gridBotEnabled"] = _tradingSettings?.GridBotEnabled ?? false,
                                ["gridSymbol"] = _tradingSettings?.GridSymbol ?? "DOGEUSDC",
                                ["gridRangePercent"] = (double)(_tradingSettings?.GridRangePercent ?? 0.10m) * 100,
                                ["gridLevels"] = _tradingSettings?.GridLevels ?? 10,
                                ["gridInvestmentPercent"] = (double)(_tradingSettings?.TotalInvestmentPercent ?? 0.80m) * 100,
                                ["trailingStopPercent"] = (double)(_ui?.TrailingStopPercent ?? 0.01m) * 100,
                            });

                            // Trades — last 50 from history
                            var tradesHistory = _ui?.TradesHistory;
                            if (tradesHistory != null && tradesHistory.Count > 0)
                            {
                                var recentTrades = tradesHistory.TakeLast (Math.Min (50, tradesHistory.Count))
                                    .Reverse ()
                                    .Select (t => new Dictionary<string, object>
                                    {
                                        ["time"] = t.CloseTime.ToString ("HH:mm"),
                                        ["pair"] = t.Symbol,
                                        ["action"] = t.IsLong ? "BUY" : "SELL",
                                        ["entry"] = t.EntryPrice,
                                        ["exit"] = t.ExitPrice,
                                        ["pnl"] = t.PnLPercent,
                                        ["duration"] = t.Duration.TotalMinutes >= 60
                                            ? $"{(int)t.Duration.TotalHours}h {t.Duration.Minutes}m"
                                            : $"{(int)t.Duration.TotalMinutes}m",
                                        ["reason"] = t.Reason
                                    }).ToList ();
                                _dashboardServer.BroadcastTrades (recentTrades);
                            }

                            // Logs — last 50
                            lock (_recentLogs)
                            {
                                if (_recentLogs.Count > 0)
                                {
                                    _dashboardServer.BroadcastLogs (string.Join ("\n", _recentLogs.TakeLast (50)));
                                }
                            }

                            // Grid Bot data
                            if (_gridBot != null && _gridBot.IsRunning)
                            {
                                var filledOrders = _gridBot.FilledOrders;
                                var allLevels = new List<Dictionary<string, object>> ();

                                if (_gridBot.BuyLevels != null)
                                {
                                    foreach (decimal level in _gridBot.BuyLevels)
                                    {
                                        bool isActive = _gridBot.ActiveOrderIds.ContainsKey (level);
                                        var filled = filledOrders.FirstOrDefault (o => ( decimal )o["price"] == level);
                                        string status = filled != null ? "filled" : ( isActive ? "open" : "open" );
                                        var order = new Dictionary<string, object>
                                        {
                                            ["level"] = level,
                                            ["side"] = "BUY",
                                            ["price"] = level,
                                            ["status"] = status,
                                        };
                                        if (filled != null)
                                        {
                                            order["qty"] = filled["qty"];
                                            order["filledAt"] = filled["filledAt"];
                                        }
                                        allLevels.Add (order);
                                    }
                                }
                                if (_gridBot.SellLevels != null)
                                {
                                    foreach (decimal level in _gridBot.SellLevels)
                                    {
                                        bool isActive = _gridBot.ActiveOrderIds.ContainsKey (level);
                                        var filled = filledOrders.FirstOrDefault (o => ( decimal )o["price"] == level);
                                        string status = filled != null ? "filled" : ( isActive ? "open" : "open" );
                                        var order = new Dictionary<string, object>
                                        {
                                            ["level"] = level,
                                            ["side"] = "SELL",
                                            ["price"] = level,
                                            ["status"] = status,
                                        };
                                        if (filled != null)
                                        {
                                            order["qty"] = filled["qty"];
                                            order["filledAt"] = filled["filledAt"];
                                        }
                                        allLevels.Add (order);
                                    }
                                }

                                decimal currentPrice = _webSocketManager?.GetCurrentPrice (_gridBot.Symbol) ?? 0m;
                                decimal unrealizedPnl = 0;
                                if (currentPrice > 0 && _positionManager.TryGet (_gridBot.Symbol, out var gridPos))
                                {
                                    unrealizedPnl = ( currentPrice - gridPos.EntryPrice ) * gridPos.Quantity;
                                }

                                _dashboardServer.BroadcastGridBot (new Dictionary<string, object>
                                {
                                    ["enabled"] = true,
                                    ["running"] = true,
                                    ["pair"] = _gridBot.Symbol,
                                    ["centerPrice"] = _gridBot.CenterPrice,
                                    ["levels"] = _gridBot.BuyLevels?.Length ?? 0,
                                    ["rangeLow"] = _gridBot.BuyLevels?.Length > 0 ? _gridBot.BuyLevels[^1] : 0m,
                                    ["rangeHigh"] = _gridBot.SellLevels?.Length > 0 ? _gridBot.SellLevels[^1] : 0m,
                                    ["investment"] = _gridBot.TotalInvestment,
                                    ["investmentPercent"] = _tradingSettings?.TotalInvestmentPercent != 0
                                        ? Math.Round ((_tradingSettings?.TotalInvestmentPercent ?? 0.20m) * 100)
                                        : 20,
                                    ["orders"] = allLevels,
                                    ["totalOrders"] = allLevels.Count,
                                    ["filledOrders"] = filledOrders.Count,
                                    ["realizedPnl"] = Math.Round (_gridBot.RealizedPnl, 2),
                                    ["unrealizedPnl"] = Math.Round (unrealizedPnl, 2),
                                });
                            }
                        }
                        catch { }
                    }

                    // Sync trailing stop from UI settings
                    if (_ui != null && _positionProtector is PositionProtector prot2)
                    {
                        prot2.TrailingStopPercent = _ui.TrailingStopPercent;
                    }

                    await Task.Delay (30000, _shutdownCts?.Token ?? CancellationToken.None);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    string msg = ex.Message;
                    _ui?.AddLog ($"❌ TradingLoop: {msg}");

                    // P1: Graceful shutdown на критических ошибках API.
                    // -2015 = Invalid API-key/IP (keys скомпрометированы, IP заблокирован)
                    // -2010 = Order would trigger immediately (ликвидация, TP/SL конфликт)
                    if (msg.Contains ("-2015") || msg.Contains ("UNAUTHORIZED") || msg.Contains ("Invalid API-key"))
                    {
                        _ui?.AddLog ("🚨 КРИТИЧНО: API ключи невалидны. Остановка торговли.");
                        _ui?.AddLog ("   Проверьте API Key, Secret и IP Whitelist в настройках Binance.");
                        StopTrading ();
                        return;
                    }
                    if (msg.Contains ("-2010") || msg.Contains ("Order would trigger"))
                    {
                        _ui?.AddLog ("🚨 КРИТИЧНО: Ордер вызовет немедленное исполнение (ликвидация?). Остановка.");
                        StopTrading ();
                        return;
                    }

                    await Task.Delay (10000, _shutdownCts?.Token ?? CancellationToken.None);
                }
            }
        }

        private async Task ExecuteBuy(string symbol, Dictionary<string, decimal> indicators, decimal currentBalance)
        {
            if (!indicators.ContainsKey ("price")) return;

            decimal price = indicators["price"];
            decimal rsi = indicators.ContainsKey ("rsi") ? indicators["rsi"] : 50;
            decimal fastSma = indicators.ContainsKey ("fastSma") ? indicators["fastSma"] : 0;
            decimal slowSma = indicators.ContainsKey ("slowSma") ? indicators["slowSma"] : 0;
            decimal macdHist = indicators.ContainsKey ("macdHist") ? indicators["macdHist"] : 0;
            decimal bbWidth = indicators.ContainsKey ("bbWidth") ? indicators["bbWidth"] : 0.05m;
            decimal volumeRatio = indicators.ContainsKey ("volumeRatio") ? indicators["volumeRatio"] : 1.0m;
            decimal obv = indicators.ContainsKey ("obv") ? indicators["obv"] : 0;

            // === ИИ-движок рассчитывает динамические параметры риска ===
            var aiRisk = await _aiRiskEngine.CalculateRiskAsync (
                symbol, currentBalance, price, fastSma, slowSma, rsi, volumeRatio, macdHist, bbWidth, obv);

            _ui?.AddLog ($"📊 {symbol}: Risk={aiRisk.RiskPerTradePercent:P1} TP={aiRisk.TakeProfitPercent:P2} SL={aiRisk.StopLossPercent:P2} R/R={aiRisk.RiskRewardRatio:F1}");

            decimal riskPerTrade = aiRisk.RiskPerTradePercent;
            decimal riskRewardRatio = aiRisk.RiskRewardRatio;
            decimal riskAmount = RiskCalculator.CalculateRiskAmount (currentBalance, riskPerTrade);

            decimal stepSize = await _client.GetStepSizeAsync (symbol);
            decimal minQty = 0m;
            var (qty, qtyResult) = RiskCalculator.CalculatePositionQuantity (riskAmount, price, stepSize, minQty, currentBalance);

            // P0: Защита от ордеров ниже MIN_NOTIONAL (динамический из exchangeInfo).
            decimal symbolMinNotional = await _client.GetMinNotionalAsync (symbol);
            decimal notional = qty * price;
            if (notional < symbolMinNotional)
            {
                decimal minQtyForNotional = Math.Ceiling (symbolMinNotional / price / stepSize) * stepSize;
                if (minQtyForNotional * price <= currentBalance)
                {
                    qty = minQtyForNotional;
                    notional = qty * price;
                    qtyResult = RiskCalculator.QuantityResult.Ok;
                    _ui?.AddLog ($"🔧 {symbol}: поднято до {qty} ({notional:F2} USDC) для MIN_NOTIONAL ({symbolMinNotional} USDC)");
                }
                else
                {
                    _ui?.AddLog ($"⏸ {symbol}: BUY пропущен — {currentBalance:F2} USDC < MIN_NOTIONAL ({symbolMinNotional} USDC)");
                    return;
                }
            }

            switch (qtyResult)
            {
                case RiskCalculator.QuantityResult.InsufficientBalanceForMinNotional:
                    _ui?.AddLog ($"⏸ {symbol}: BUY проигнорирован — баланс {currentBalance:F2} USDC ниже минимального ордера");
                    return;
                case RiskCalculator.QuantityResult.ZeroQuantityAfterRounding:
                    _ui?.AddLog ($"⏸ {symbol}: BUY проигнорирован — нулевое количество после округления");
                    return;
                case RiskCalculator.QuantityResult.ExceedsAvailableBalance:
                    _ui?.AddLog ($"⏸ {symbol}: BUY проигнорирован — ордер превышает доступный баланс {currentBalance:F2} USDC");
                    return;
            }

            if (qty * price > riskAmount * 1.01m)
                _ui?.AddLog ($"ℹ️ {symbol}: риск {riskAmount:F2} USDC ниже минимального ордера, сумма поднята до {qty * price:F2} USDC");

            // Проверка минимальной прибыли: если TP < 0.4% — сделка неприбыльна даже с лимитными ордерами
            if (aiRisk.TakeProfitPercent < 0.004m)
            {
                _ui?.AddLog ($"⏸ {symbol}: BUY пропущен — TP {aiRisk.TakeProfitPercent:P2} < минимального 0.4%");
                return;
            }

            // Кулдаун на пару — 15 минут
            if (_lastBuyTime.TryGetValue (symbol, out var lastTime) && DateTime.UtcNow - lastTime < TimeSpan.FromMinutes (15))
            {
                _ui?.AddLog ($"⏸ {symbol}: BUY проигнорирован — кулдаун ({(TimeSpan.FromMinutes (15) - (DateTime.UtcNow - lastTime)).TotalSeconds:F0} сек)");
                return;
            }

            // Глобальный кулдаун: не более 3 сделок в час
            _recentTradeTimes.RemoveAll (t => DateTime.UtcNow - t > TimeSpan.FromHours (1));
            if (_recentTradeTimes.Count >= 3)
            {
                _ui?.AddLog ($"⏸ {symbol}: BUY пропущен — глобальный лимит 3 сделки/час");
                return;
            }

            _lastBuyTime[symbol] = DateTime.UtcNow;

            // === SL/TP от ИИ с адаптивным множителем ===
            decimal adaptiveSlMult = indicators.ContainsKey ("adaptiveSlMultiplier") ? indicators["adaptiveSlMultiplier"] : 1.0m;
            decimal slPrice = price * (1 - aiRisk.StopLossPercent * adaptiveSlMult);
            decimal tpPrice = price * (1 + aiRisk.TakeProfitPercent * adaptiveSlMult);
            decimal slPct = aiRisk.StopLossPercent * adaptiveSlMult;

            _ui?.AddLog ($"📐 {symbol}: Risk={riskAmount:F2} ({riskPerTrade:P2}), SL={slPrice:F4} (-{slPct:P2}), TP={tpPrice:F4} (+{aiRisk.TakeProfitPercent:P2}), R/R 1:{riskRewardRatio:F1}");

            // Исполнение ордера — лимитный на 0.2% ниже рынка (maker fee)
            decimal tickSize = await _client.GetTickSizeAsync (symbol);
            decimal limitPrice = price * 0.998m;
            if (tickSize > 0)
                limitPrice = Math.Floor (limitPrice / tickSize) * tickSize;

            _ui?.AddLog ($"💵 Покупка {qty} {symbol} | лимит {limitPrice:F4} ( рынок {price:F4}, -0.2%)");
            var order = await _client.PlaceLimitOrder (symbol, "BUY", qty, limitPrice);

            if (order != null)
            {
                var pos = new OpenPosition
                {
                    Symbol = symbol,
                    Quantity = qty,
                    EntryPrice = limitPrice,
                    OpenTime = DateTime.UtcNow,
                    StopLossPrice = slPrice,
                    TakeProfitPrice = tpPrice,
                    HighestPrice = limitPrice,
                    HighestPriceSinceOpen = limitPrice,
                    OcoOrderListId = 0
                };

                await _positionManager.AddOrUpdateAsync (symbol, pos);
                _recentTradeTimes.Add (DateTime.UtcNow);
                _ui?.AddLog ($"✅ Куплено {qty} {symbol} | SL={slPrice:F4} TP={tpPrice:F4} | R/R 1:{riskRewardRatio:F1}");
                _ui?.UpdatePositionsStatus (_positionManager.Count, _ui?.MaxConcurrentTrades ?? 3, _positionManager.GetSymbols ());

                // Telegram: buy notification
                await SendTradeNotification ($"🟢 <b>ПОКУПКА</b>\n" +
                    $"📊 {symbol}\n" +
                    $"💵 Цена: {limitPrice:F4}\n" +
                    $"📦 Количество: {qty}\n" +
                    $"🛡 SL: {slPrice:F4} (-{slPct:P2})\n" +
                    $"🎯 TP: {tpPrice:F4} (+{aiRisk.TakeProfitPercent:P2})\n" +
                    $"⚖️ Риск: {riskAmount:F2} USDC ({riskPerTrade:P2})\n" +
                    $"📐 R/R: 1:{riskRewardRatio:F1}");
            }
        }

        private async Task ExecuteSell(string symbol)
        {
            if (!_positionManager.TryGet (symbol, out var pos)) return;

            string asset = symbol.Replace ("USDC", "");
            decimal price = GetCurrentPrice (symbol);

            // Проверяем баланс на споте
            decimal spotBalance = await _client.GetAccountBalanceAsync (asset);

            // Если на споте мало — проверяем Earn и выкупаем
            if (spotBalance < pos.Quantity * 0.99m)
            {
                _ui?.AddLog ($"🔄 {symbol}: на споте {spotBalance:F8} {asset}, нужно {pos.Quantity:F8}. Проверяю Earn...");

                var earnPositions = await _client.GetFlexibleEarnBalanceAsync ();
                if (earnPositions != null)
                {
                    var earnPos = earnPositions.FirstOrDefault (p => p["asset"]?.ToString () == asset);
                    if (earnPos != null)
                    {
                        decimal earnAmount = decimal.Parse (earnPos["totalAmount"]?.ToString () ?? "0", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
                        _ui?.AddLog ($"   💰 В Earn: {earnAmount:F8} {asset}");

                        if (earnAmount > 0)
                        {
                            decimal needToRedeem = Math.Min (pos.Quantity - spotBalance, earnAmount);
                            _ui?.AddLog ($"   🔄 Выкупаю {needToRedeem:F8} {asset} из Earn...");
                            bool redeemed = await _client.RedeemFlexibleEarnWithWaitAsync (asset, needToRedeem);
                            if (redeemed)
                            {
                                await Task.Delay (2000, _shutdownCts?.Token ?? CancellationToken.None); // Ждём зачисления
                                spotBalance = await _client.GetAccountBalanceAsync (asset);
                                _ui?.AddLog ($"   ✅ После выкупа на споте: {spotBalance:F8} {asset}");
                            }
                            else
                            {
                                _ui?.AddLog ($"   ⚠️ Не удалось выкупить из Earn");
                            }
                        }
                    }
                }
            }

            decimal qtyToSell = Math.Min (pos.Quantity, spotBalance);
            if (qtyToSell <= 0.000001m)
            {
                _ui?.AddLog ($"⚠️ {symbol}: нет актива для продажи (ни на споте, ни в Earn)");
                await _positionManager.RemoveAsync (symbol);
                return;
            }

            // Округляем по stepSize биржи
            decimal stepSize = await _client.GetStepSizeAsync (symbol);
            if (stepSize > 0)
            {
                qtyToSell = Math.Floor (qtyToSell / stepSize) * stepSize;
                if (qtyToSell <= 0)
                {
                    _ui?.AddLog ($"⏸ {symbol}: количество {pos.Quantity} меньше шага лота {stepSize}");
                    return;
                }
            }

            // Отмена OCO ордера
            if (pos.OcoOrderListId != 0)
            {
                await _client.CancelOcoOrder (symbol, pos.OcoOrderListId);
            }

            // Продажа — лимитный на 0.2% выше рынка (maker fee)
            decimal tickSize = await _client.GetTickSizeAsync (symbol);
            decimal limitPrice = price * 1.002m;
            if (tickSize > 0)
                limitPrice = Math.Ceiling (limitPrice / tickSize) * tickSize;

            _ui?.AddLog ($"💵 Продажа {qtyToSell} {symbol} | лимит {limitPrice:F4} ( рынок {price:F4}, +0.2%)");
            var order = await _client.PlaceLimitOrder (symbol, "SELL", qtyToSell, limitPrice);
            if (order != null)
            {
                decimal pnl = ( limitPrice - pos.EntryPrice ) * qtyToSell;
                decimal pnlPct = ( limitPrice / pos.EntryPrice - 1 ) * 100;

                _ui?.AddLog ($"🔒 Закрыта {symbol}: PnL {pnl:F2} ({pnlPct:F2}%)");

                var trade = new TradeLog
                {
                    Symbol = symbol,
                    EntryPrice = pos.EntryPrice,
                    ExitPrice = limitPrice,
                    Quantity = qtyToSell,
                    PnL = pnl,
                    PnLPercent = pnlPct,
                    OpenTime = pos.OpenTime,
                    CloseTime = DateTime.UtcNow,
                    Reason = "Signal Sell",
                    Duration = DateTime.UtcNow - pos.OpenTime,
                    Action = "SELL_CLOSE"
                };

                _ui.AddTradeToHistory (trade);
                _riskManager.RecordTrade (pnl);
                await _positionManager.RemoveAsync (symbol);
                _ui?.UpdatePositionsStatus (_positionManager.Count, _ui?.MaxConcurrentTrades ?? 3, _positionManager.GetSymbols ());

                // Telegram: sell notification
                string emoji = pnl >= 0 ? "🟢" : "🔴";
                await SendTradeNotification ($"{emoji} <b>ПРОДАЖА</b>\n" +
                    $"📊 {symbol}\n" +
                    $"💵 Вход: {pos.EntryPrice:F4} → Выход: {limitPrice:F4}\n" +
                    $"📦 Количество: {qtyToSell}\n" +
                    $"📈 PnL: {pnl:+F2;-F2} USDC ({pnlPct:+F2;-F2}%)\n" +
                    $"⏱ Длительность: {(DateTime.UtcNow - pos.OpenTime):hh\\:mm}");
            }
        }

        private async Task SendTradeNotification (string message)
        {
            try
            {
                if (_telegram != null && _telegram.IsEnabled)
                {
                    await _telegram.SendMessageAsync (message);
                }
            }
            catch { }
        }

        private string GetStatusText()
        {
            string status = _isRunning ? "🟢 Активен" : "🔴 Остановлен";
            decimal balance = _wallet.GetTotalBalance ("USDC");
            int posCount = _positionManager.Count;
            decimal pnl = _ui?.TotalPnL ?? 0;
            decimal winRate = _ui?.WinRate ?? 0;
            int totalTrades = _ui?.TotalTrades ?? 0;

            string echelonStatus = "";
            if (_tradingSettings != null)
            {
                echelonStatus = $"\n🧠 Эшелоны: AD={(_tradingSettings.AdaptiveAgentEnabled ? "✅" : "❌")} " +
                    $"SV={(_tradingSettings.SignalValidatorEnabled ? "✅" : "❌")} " +
                    $"NS={(_tradingSettings.NewsSentinelEnabled ? "✅" : "❌")}";
            }

            string posDetails = "";
            if (posCount > 0)
            {
                foreach (var kvp in _positionManager.Positions)
                {
                    decimal currentPrice = _webSocketManager?.GetCurrentPrice (kvp.Key) ?? 0;
                    decimal profit = currentPrice > 0 ? ( currentPrice - kvp.Value.EntryPrice ) / kvp.Value.EntryPrice * 100 : 0;
                    posDetails += $"\n  • {kvp.Key}: Entry={kvp.Value.EntryPrice:F4}, PnL={profit:+F2;-F2}%";
                }
            }

            return $"🤖 *Статус:* {status}\n" +
                   $"💰 *USDC:* {balance:F2}\n" +
                   $"📊 *Позиций:* {posCount}{posDetails}\n" +
                   $"📈 *PnL:* {pnl:+F2;-F2} USDC\n" +
                   $"🎯 *Win Rate:* {winRate:F1}% ({totalTrades} сделок)" +
                   echelonStatus;
        }

        private string GetPerformanceStats()
        {
            var totalTrades = _ui?.TotalTrades ?? 0;
            if (totalTrades == 0) return "Нет сделок для статистики.";
            var wins = _ui?.WinningTrades ?? 0;
            var winRate = totalTrades > 0 ? wins * 100.0m / totalTrades : 0;
            return $"📊 Статистика торговли\n📈 Общий PnL: {_ui?.TotalPnL ?? 0:F2} USDC\n🎯 Win Rate: {winRate:F1}% ({wins}/{totalTrades})";
        }

        /// <summary>
        /// Автоматическая оптимизация параметров (вызывается раз в сутки)
        /// </summary>
        private async Task AutoOptimizeLoop()
        {
            int lastTradeCount = 0;
            while (_isRunning)
            {
                try
                {
                    await Task.Delay (TimeSpan.FromHours (24), _shutdownCts?.Token ?? CancellationToken.None);

                    if (!_isRunning) break;

                    int currentTradeCount = _ui?.TotalTrades ?? 0;
                    int newTrades = currentTradeCount - lastTradeCount;

                    if (newTrades < 10)
                    {
                        _ui?.AddLog ($"🧠 Оптимизация пропущена: только {newTrades} новых сделок (нужно минимум 10)");
                        lastTradeCount = currentTradeCount;
                        continue;
                    }

                    _ui?.AddLog ($"🧠 Запуск оптимизации ({newTrades} новых сделок)...");

                    decimal balance = _wallet?.GetTotalBalance ("USDC") ?? 0;
                    var optimizer = new StrategyOptimizer (_client, _ui, _ui.AddLog);
                    bool success = await optimizer.RunOptimizationAsync (balance);

                    if (success)
                    {
                        _ui?.AddLog ("✅ Оптимизация завершена");
                    }
                    else
                    {
                        _ui?.AddLog ("⚠️ Оптимизация не дала результатов");
                    }

                    lastTradeCount = currentTradeCount;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _ui?.AddLog ($"❌ Ошибка оптимизации: {ex.Message}");
                    try { await Task.Delay (60000, _shutdownCts?.Token ?? CancellationToken.None); } catch { }
                }
            }
        }

        /// <summary>
        /// Фоновая проверка обновлений каждые 30 минут
        /// </summary>
        private async Task PeriodicUpdateCheckLoop()
        {
            // Первая проверка через 5 минут после запуска
            await Task.Delay (TimeSpan.FromMinutes (5), _shutdownCts?.Token ?? CancellationToken.None);

            while (_isRunning)
            {
                try
                {
                    if (!_isRunning) break;

                    if (_updateChecker != null)
                    {
                        await _updateChecker.CheckForUpdatesAsync ();
                    }
                }
                catch { }

                // Ждём 30 минут до следующей проверки
                await Task.Delay (TimeSpan.FromMinutes (30), _shutdownCts?.Token ?? CancellationToken.None);
            }
        }

        public bool IsTelegramEnabled() => _telegram != null && _telegram.IsEnabled;

        private async Task DailyReportLoop()
        {
            while (_isRunning)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    var nextRun = now.Date.AddDays (1).AddHours (9);
                    var delay = nextRun - now;
                    if (delay.TotalMilliseconds > 0)
                        await Task.Delay (delay, _shutdownCts?.Token ?? CancellationToken.None);

                    if (!_isRunning || _telegram == null) continue;

                    decimal totalPnL = _ui?.TotalPnL ?? 0;
                    decimal winRate = _ui?.WinRate ?? 0;
                    int totalTrades = _ui?.TotalTrades ?? 0;
                    int winningTrades = _ui?.WinningTrades ?? 0;
                    int losingTrades = _ui?.LosingTrades ?? 0;

                    await _telegram.SendDailyReport (totalPnL, winRate, totalTrades, winningTrades, losingTrades);
                }
                catch { }
            }
        }

        public async Task<bool> TestTelegramAsync()
        {
            if (_telegram == null) return false;
            return await _telegram.TestConnectionAsync ();
        }


        private async Task WhaleLoop()
        {
            Action<string> log = (msg) => _ui?.AddLog (msg);
            // Стейблкоины не информативны для whale-мониторинга: крупные сделки там — норма,
            // а не сигнал. Фильтруем их, чтобы не засорять лог (например USDCUSDT).
            HashSet<string> stablecoinPairs = new HashSet<string> (StringComparer.OrdinalIgnoreCase)
            {
                "USDCUSDT", "USDTUSDC", "BUSDUSDT", "FDUSDUSDT", "TUSDUSDT", "USDCUSDC", "DAIUSDT"
            };

            bool started = false;
            while (_isRunning)
            {
                try
                {
                    if (started) { await Task.Delay (60000, _shutdownCts?.Token ?? CancellationToken.None); continue; }

                    List<string> pairs;
                    lock (_pairsLock) { pairs = new List<string> (_activePairs); }

                    // Исключаем стейблкоин-пары из мониторинга
                    pairs = pairs.Where (p => !stablecoinPairs.Contains (p)).ToList ();

                    if (pairs.Count > 0)
                    {
                        _whaleMonitor = new WhaleMonitor (log, 100000);
                        _whaleMonitor.OnWhaleDetected += whale =>
                        {
                            _ui?.AddLog ($"🐋 WHALE {whale.Side} {whale.Symbol}: ${whale.ValueUsdc:N0}");
                        };
                        await _whaleMonitor.StartAsync (pairs.ToArray ());
                        _ui?.AddLog ($"🐋 Whale monitor запущен для {pairs.Count} пар (порог: $100k, стейблкоины исключены)");
                        started = true;
                    }
                    await Task.Delay (10000, _shutdownCts?.Token ?? CancellationToken.None);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _ui?.AddLog ($"❌ Whale monitor ошибка: {ex.Message}");
                    try { await Task.Delay (30000, _shutdownCts?.Token ?? CancellationToken.None); } catch { }
                }
            }
        }

        private async Task EarnOptimizeLoop()
        {
            while (_isRunning)
            {
                try
                {
                    await Task.Delay (TimeSpan.FromHours (6), _shutdownCts?.Token ?? CancellationToken.None);
                    if (!_isRunning) break;
                    await _earnStrategy.OptimizeEarnAsync ();
                }
                catch { }
            }
        }

        private async Task FearGreedLoop()
        {
            while (_isRunning)
            {
                try
                {
                    if (_fearGreedProvider != null)
                    {
                        var data = await _fearGreedProvider.GetCurrentAsync ();
                        if (data != null && _ui != null)
                        {
                            _ui.FearGreedValue = data.Value;
                            _ui.FearGreedClassification = data.Classification;
                        }
                    }
                    await Task.Delay (TimeSpan.FromMinutes (15), _shutdownCts?.Token ?? CancellationToken.None);
                }
                catch (OperationCanceledException) { break; }
                catch { try { await Task.Delay (TimeSpan.FromMinutes (15), _shutdownCts?.Token ?? CancellationToken.None); } catch { } }
            }
        }

    }
}
