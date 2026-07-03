using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BinanceBotWpf.Models;
using BinanceBotWpf.Risk;
using BinanceBotWpf.Services.Strategies;
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
        private BinanceFuturesClient _futuresClient;
        private readonly IVolumeBreakoutStrategy _volumeBreakout;
        private readonly IDcaStrategy _dcaStrategy;
        private readonly INewsProvider _newsProvider;
        private readonly IMacroCalendarProvider _macroCalendar;
        private TradingSettings _tradingSettings;
        private readonly IBackupService _backupService;
        private readonly IAiRiskEngine _aiRiskEngine;
        private ISimpleEarnStrategy _earnStrategy;
        private readonly IFearGreedIndexProvider _fearGreedProvider;

        // Decomposed services
        private readonly PairManager _pairManager;
        private readonly OrderExecutor _orderExecutor;
        private readonly BackgroundLoopManager _backgroundLoopManager;
        private readonly IPriceAlertManager _priceAlertManager;
        private readonly IRiskManager _riskManager;
        private readonly BotConfig _config;
        private readonly StatePersistence _statePersistence;
        private TelegramCommandHandler _telegramHandler;
        private NewsFetcher _newsFetcher;
        private WebhookServer _webhookServer;
        private TradingViewWebhookService _tradingViewHandler;

        private MainWindowViewModel _ui;
        private volatile bool _isRunning;
        private TelegramNotifier _telegram;
        private CancellationTokenSource _shutdownCts;

        // Списки и кэш
        private readonly List<string> _recentErrors = new ();
        private readonly ConcurrentDictionary<string, (List<BinanceKline> Klines, DateTime Expiry)> _klinesCache = new ();

        // Настройки
        private readonly string _telegramBotToken;
        private readonly string _telegramChatId;
        private readonly TimeSpan _ordersFetchInterval = TimeSpan.FromHours (4);
        private DateTime _lastOrdersFetch = DateTime.MinValue;

        // Флаги циклов
        private bool _balanceLoopEnabled = true;
        private bool _tradingLoopEnabled = true;
        private CancellationTokenSource _protectorCts;
        private Task _protectorLoopTask;
        private int _consecutiveErrors;
        private DateTime _circuitBreakerUntil = DateTime.MinValue;
        private const int CircuitBreakerThreshold = 5;
        private readonly TimeSpan CircuitBreakerCooldown = TimeSpan.FromMinutes (5);

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
            _fearGreedProvider = fearGreedProvider;
            _priceAlertManager = priceAlertManager;
            _riskManager = riskManager;
            _webSocketManager = webSocketManager;
            _earnStrategy = earnStrategy;
            _config = config;
            _telegramBotToken = config?.TelegramBotToken ?? "";
            _telegramChatId = config?.TelegramChatId ?? "";

            // Decomposed services
            _pairManager = new PairManager (_client, _wallet);
            _orderExecutor = new OrderExecutor (
                _client, _aiRiskEngine, _positionManager, _riskManager,
                () => _shutdownCts?.Token ?? CancellationToken.None,
                msg => SendTradeNotification (msg));
            _orderExecutor.SetPriceProvider (sym => _webSocketManager?.GetCurrentPrice (sym) ?? 0);
            _backgroundLoopManager = new BackgroundLoopManager (_client, _wallet, _earnStrategy, _fearGreedProvider);

            // State persistence
            string dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            _statePersistence = new StatePersistence(dataDir);
        }

        private bool _loggerSet = false;

        public async Task SetLoggerAsync(Action<string> logger)
        {
            if (_loggerSet) return;
            _loggerSet = true;

            ServiceLogger.Instance.SetRootLogger (logger);

            _wallet.OnLogGenerated += logger;
            _earn.OnLogGenerated += logger;
            _rebalancer.OnLogGenerated += logger;
            _client.OnLogGenerated += logger;

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

                var adaptiveAgent = new AdaptiveAgent (logger, slMult, periodMult);
                _strategy.SetAdaptiveAgent (adaptiveAgent, adaptiveEnabled);
                _ui?.AddLog ($"🔧 Эшелон 1 (AdaptiveAgent): {(adaptiveEnabled ? "включён" : "выключен")} SL×{slMult} Period×{periodMult}");

                var signalValidator = new SignalValidator (logger, volThresh, atrThresh, rsiLow, rsiHigh);
                _strategy.SetSignalValidator (signalValidator, validatorEnabled);
                _ui?.AddLog ($"🔍 Эшелон 2 (SignalValidator): {(validatorEnabled ? "включён" : "выключен")} Vol>{volThresh} ATR>{atrThresh} RSI {rsiLow}/{rsiHigh}");

                var newsSentinel = new NewsSentinel (logger);
                _strategy.SetNewsSentinel (newsSentinel, newsEnabled);
                _ui?.AddLog ($"📰 Эшелон 3 (NewsSentinel): {(newsEnabled ? "включён" : "выключен")}");

                _newsFetcher = new NewsFetcher (SharedHttpClient.Instance, newsSentinel, logger);
                _newsFetcher.Start ();
                _ui?.AddLog ("📰 NewsFetcher: фоновый сбор новостей запущен");
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
                    logger ($"⚠️ Не удалось прочитать config.json для Telegram: {ex.Message}");
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
                            logger (isEnabled ? $"✅ Telegram: {msg}" : $"❌ Telegram: {msg}");
                            _ui?.RefreshTelegramStatus ();
                        };
                        _telegramHandler = new TelegramCommandHandler (
                            _telegram, _client, (WalletManager)_wallet, (PositionManager)_positionManager, _tradingSettings,
                            (MlModelManager)_mlManager, _webSocketManager, (BackupService)_backupService, (FearGreedIndexProvider)_fearGreedProvider,
                            (PriceAlertManager)_priceAlertManager, _ui, _recentErrors, _pairManager.GetActivePairs (),
                            () => _isRunning,
                            () => { StopTrading (); return Task.CompletedTask; },
                            async (sym) => await StartAutoGridAsync (sym),
                            () => StopGridAsync (),
                            () => GetStatusText (),
                            () => GetPerformanceStats (),
                            msg => _ui?.AddLog (msg));
                        _telegram.StartListening ((cmd, chatId) => _telegramHandler.HandleAsync (cmd, chatId));
                        logger ("⏳ Подключение к Telegram...");
                    }
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
                
                logger ("✅ Проверка обновлений инициализирована");
            }
            catch (Exception ex)
            {
                logger ($"⚠️ Ошибка инициализации UpdateChecker: {ex.Message}");
            }

            // FearGreed и PriceAlert инициализированы через DI — привязываем только обработчики
            _priceAlertManager.OnAlertTriggered += alert =>
            {
                _ui?.AddLog ($"🔔 {alert.Symbol} {alert.Direction} {alert.TargetPrice} сработал!");
            };
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

            // Configure services (after InitAsync creates _webSocketManager)
            _pairManager.SetViewModel (_ui);

            _orderExecutor.SetViewModel (_ui);
            _orderExecutor.BuyCooldownMinutes = _tradingSettings?.BuyCooldownMinutes ?? 15;
            _orderExecutor.MaxTradesPerHour = _tradingSettings?.MaxTradesPerHour ?? 3;
            _backgroundLoopManager.Configure (_ui, _isRunning, _shutdownCts, _tradingSettings, _updateChecker, _telegram, _pairManager);

            // Sync trailing stop from settings to protector
            if (_ui != null && _positionProtector is PositionProtector prot)
            {
                prot.TrailingStopPercent = _ui.TrailingStopPercent;
                prot.MaxHoldTime = TimeSpan.FromHours (_tradingSettings?.MaxHoldTimeHours ?? 24);
            }

            // Restore trading state from file
            _statePersistence.Register(GetCurrentState, RestoreState);
            _statePersistence.Restore();
            _statePersistence.StartAutoSave();

            _ = Task.Run (() => RunLoopWithRestart (BalanceLoop, "BalanceLoop"));
            _ = Task.Run (() => RunLoopWithRestart (TradingLoop, "TradingLoop"));
            _ = Task.Run (() => RunLoopWithRestart (_backgroundLoopManager.AutoOptimizeLoop, "AutoOptimizeLoop"));
            _ = Task.Run (() => RunLoopWithRestart (_backgroundLoopManager.PeriodicUpdateCheckLoop, "PeriodicUpdateCheck"));
            _ = Task.Run (() => RunLoopWithRestart (_backgroundLoopManager.DailyReportLoop, "DailyReport"));
            _ = Task.Run (() => RunLoopWithRestart (_backgroundLoopManager.WhaleLoop, "WhaleLoop"));
            _ = Task.Run (() => RunLoopWithRestart (_backgroundLoopManager.EarnOptimizeLoop, "EarnOptimize"));
            _ = Task.Run (() => RunLoopWithRestart (_backgroundLoopManager.FearGreedLoop, "FearGreed"));

            // PositionProtector runs independently — survives StopTrading()
            StartProtectorLoop ();
        }

        private void StartProtectorLoop ()
        {
            if (_protectorLoopTask != null && !_protectorLoopTask.IsCompleted) return;
            _protectorCts = new CancellationTokenSource ();
            _protectorLoopTask = Task.Run (async () =>
            {
                while (!_protectorCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (_isRunning)
                        {
                            var toClose = await _positionProtector.CheckAndProtectAsync (GetCurrentPrice);
                            foreach (var sym in toClose)
                            {
                                await _orderExecutor.ExecuteSellAsync (sym);
                            }
                        }

                        // Sync trailing stop from UI
                        if (_positionProtector is PositionProtector prot)
                        {
                            prot.TrailingStopPercent = _ui?.TrailingStopPercent ?? 0.02m;
                            prot.MaxHoldTime = TimeSpan.FromHours (_tradingSettings?.MaxHoldTimeHours ?? 24);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine ($"⚠️ ProtectorLoop error: {ex.Message}");
                    }
                    await Task.Delay (10000, _protectorCts.Token);
                }
            });
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
                    try { await Task.Delay (10000, _shutdownCts?.Token ?? CancellationToken.None); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        public void StopTrading()
        {
            _isRunning = false;

            // Save state before stopping
            try { _statePersistence?.Save (); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"State save error: {ex.Message}"); }

            _shutdownCts?.Cancel ();
            if (_ui != null) _ui.IsRunning = false;

            // Update decomposed services running state
            _backgroundLoopManager.UpdateRunningState (false, null);

            try { _webSocketManager?.Dispose (); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"⚠️ Ошибка остановки WebSocket: {ex.Message}"); }
            finally { _webSocketManager = null; }

            try { _gridBot?.Dispose (); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"⚠️ Ошибка остановки GridBot: {ex.Message}"); }
            finally { _gridBot = null; }

            try { _webhookServer?.Dispose (); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"⚠️ Ошибка остановки WebhookServer: {ex.Message}"); }
            finally { _webhookServer = null; _tradingViewHandler = null; }

            try { _newsFetcher?.Dispose (); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"⚠️ Ошибка остановки NewsFetcher: {ex.Message}"); }

            _backgroundLoopManager.DisposeWhaleMonitor ();

            try { _shutdownCts?.Dispose (); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"CTS dispose error: {ex.Message}"); }
            _shutdownCts = null;

            // Protector loop survives StopTrading — only killed on app exit
            try { _protectorCts?.Cancel (); _protectorCts?.Dispose (); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"Protector CTS dispose error: {ex.Message}"); }
        }

        public decimal GetCurrentPriceForSymbol(string symbol) => GetCurrentPrice (symbol);

        /// <summary>
        /// Capture current trading state for persistence.
        /// </summary>
        private TradingState GetCurrentState ()
        {
            List<TradeLog> trades = _ui?.TradesHistory?.ToList () ?? new List<TradeLog> ();
            Dictionary<string, DateTime> lastBuyTime = _orderExecutor.GetLastBuyTimes ();
            List<DateTime> recentTradeTimes = _orderExecutor.GetRecentTradeTimes ();
            {
                return new TradingState
                {
                    LastBuyTime = lastBuyTime,
                    RecentTradeTimes = recentTradeTimes,
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
                EquityHistory = _ui?.GetBalanceHistory () ?? new List<Dictionary<string, object>> (),
                };
            }
        }

        /// <summary>
        /// Restore trading state from saved snapshot.
        /// </summary>
        private void RestoreState (TradingState state)
        {
            if (state == null) return;

            _orderExecutor.RestoreCooldowns (state.LastBuyTime, state.RecentTradeTimes);

            // Restore trade history and stats
            if (_ui != null && state.TradesHistory.Count > 0)
            {
                foreach (var trade in state.TradesHistory)
                {
                    _ui.AddTradeToHistory (trade);
                }
            }

            _ui?.AddLog ($"📂 Состояние восстановлено: {state.TradesHistory.Count} сделок");

            if (state.EquityHistory.Count > 0)
            {
                _ui?.RestoreBalanceHistory (state.EquityHistory);
                _ui?.AddLog ($"📈 График баланса: восстановлено {state.EquityHistory.Count} точек");
            }
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

            if (_futuresClient == null)
            {
                _ui?.AddLog ("⚠️ Фьючерсные API ключи не настроены. Сетка требует фьючерсный аккаунт.");
                return;
            }

            // Проверяем баланс на фьючерсах и переводим со спота при необходимости
            decimal futuresBalance = await _futuresClient.GetAccountBalanceAsync ("USDC");
            _ui?.AddLog ($"💰 Фьючерсный баланс USDC: {futuresBalance:F2}");
            if (futuresBalance < investmentUsdc)
            {
                decimal toTransfer = investmentUsdc - futuresBalance + 1m; // +1 USDC запас на комиссии
                decimal spotBalance = await _client.GetAccountBalanceAsync ("USDC");
                if (spotBalance >= toTransfer)
                {
                    _ui?.AddLog ($"💸 Перевод {toTransfer:F2} USDC из спота в фьючерсы...");
                    var transferResult = await _futuresClient.TransferToFuturesAsync ("USDC", toTransfer);
                    if (transferResult != null)
                    {
                        _ui?.AddLog ($"✅ Перевод выполнен");
                        await Task.Delay (2000); // Ждём обновления баланса
                    }
                    else
                    {
                        _ui?.AddLog ("❌ Ошибка перевода. Проверьте баланс на фьючерсах.");
                        return;
                    }
                }
                else
                {
                    _ui?.AddLog ($"⚠️ Недостаточно USDC на споте ({spotBalance:F2}) для перевода ({toTransfer:F2})");
                    return;
                }
            }

            _gridBot = new GridBot (_futuresClient, (PositionManager)_positionManager, msg => _ui?.AddLog (msg));
            _gridBot.OnTrade += async trade =>
            {
                _ui?.AddTradeToHistory (trade);
                string emoji = trade.PnL >= 0 ? "🟢" : "🔴";
                await SendTradeNotification ($"{emoji} <b>СЕТКА ПРОДАЖА</b>\n📊 {trade.Symbol}\n💵 Вход: {trade.EntryPrice:F4} → Выход: {trade.ExitPrice:F4}\n📈 PnL: {trade.PnL:+F2;-F2} USDC ({trade.PnLPercent:+F2;-F2}%)");
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
            if (klines == null || klines.Count < 30)
            {
                _ui?.AddLog ($"❌ Сетка: недостаточно данных для {symbol} ({klines?.Count ?? 0}/30 свечей)");
                return;
            }

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

            if (grid.Levels <= 0 || investmentUsdc <= 0)
            {
                _ui?.AddLog ($"⛔ Сетка отключена: баланс {balance:F2} USDC недостаточен (мин. ~20 USDC)");
                return;
            }

            if (_futuresClient == null)
            {
                _ui?.AddLog ("⚠️ Фьючерсные API ключи не настроены. ИИ-сетка требует фьючерсный аккаунт.");
                return;
            }

            // Проверяем баланс на фьючерсах и переводим со спота при необходимости
            decimal futuresBalance = await _futuresClient.GetAccountBalanceAsync ("USDC");
            _ui?.AddLog ($"💰 Фьючерсный баланс USDC: {futuresBalance:F2}");
            if (futuresBalance < investmentUsdc)
            {
                decimal toTransfer = investmentUsdc - futuresBalance + 1m;
                decimal spotBalance = await _client.GetAccountBalanceAsync ("USDC");
                if (spotBalance >= toTransfer)
                {
                    _ui?.AddLog ($"💸 Перевод {toTransfer:F2} USDC из спота в фьючерсы...");
                    var transferResult = await _futuresClient.TransferToFuturesAsync ("USDC", toTransfer);
                    if (transferResult != null)
                    {
                        _ui?.AddLog ($"✅ Перевод выполнен");
                        await Task.Delay (2000);
                    }
                    else
                    {
                        _ui?.AddLog ("❌ Ошибка перевода. Проверьте баланс на фьючерсах.");
                        return;
                    }
                }
                else
                {
                    _ui?.AddLog ($"⚠️ Недостаточно USDC на споте ({spotBalance:F2}) для перевода ({toTransfer:F2})");
                    return;
                }
            }

            _ui?.AddLog ($"🤖 ИИ-автосетка: {symbol} | Баланс: {balance:F2} USDC");
            _ui?.AddLog ($"   Диапазон: ±{grid.RangePercent:P0} | Уровней: {grid.Levels} | Инвестиции: {grid.InvestmentPercent:P0} ({investmentUsdc:F2} USDC)");

            _gridBot = new GridBot (_futuresClient, (PositionManager)_positionManager, msg => _ui?.AddLog (msg));
            _gridBot.OnTrade += async trade =>
            {
                _ui?.AddTradeToHistory (trade);
                string emoji = trade.PnL >= 0 ? "🟢" : "🔴";
                await SendTradeNotification ($"{emoji} <b>СЕТКА ПРОДАЖА</b>\n📊 {trade.Symbol}\n💵 Вход: {trade.EntryPrice:F4} → Выход: {trade.ExitPrice:F4}\n📈 PnL: {trade.PnL:+F2;-F2} USDC ({trade.PnLPercent:+F2;-F2}%)");
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
            _pairManager.SetWebSocketManager (_webSocketManager);
            _ui?.AddLog ($"🔌 WebSocket эндпоинт: {(useFutures ? "фьючерсы (fstream.binance.com)" : "спот (stream.binance.com)")}");

            await _wallet.UpdateBalance ();
            decimal initBal = _wallet.GetTotalBalance ("USDC");
            _ui?.UpdateWalletDisplay (initBal.ToString ("F2"));
            _ui?.UpdateDrawdown (initBal);
            _ui?.AddBalancePoint (DateTime.Now, initBal);
            await _pairManager.UpdatePairsAsync ();
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
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"Futures config error: {ex.Message}"); }

            // Инициализация фьючерсов (если включены или ключи настроены)
            bool futuresEnabled = _ui?.FuturesEnabled == true || _tradingSettings?.FuturesEnabled == true;
            string tempFuturesKey = "";
            string tempFuturesSecret = "";
            try
            {
                var cfg = _config;
                if (cfg != null)
                {
                    tempFuturesKey = cfg.FuturesApiKey ?? "";
                    tempFuturesSecret = cfg.FuturesApiSecret ?? "";
                    // Фоллбэк на спот-ключи
                    if (string.IsNullOrEmpty (tempFuturesKey))
                        tempFuturesKey = cfg.ApiKey ?? "";
                    if (string.IsNullOrEmpty (tempFuturesSecret))
                        tempFuturesSecret = cfg.ApiSecret ?? "";
                }
            }
            catch { }
            if (!futuresEnabled && !string.IsNullOrEmpty (tempFuturesKey) && !string.IsNullOrEmpty (tempFuturesSecret))
            {
                futuresEnabled = true;
                _tradingSettings.FuturesEnabled = true;
                _ui?.AddLog ("⚙️ Фьючерсы: ключи обнаружены, автоматическое включение");
            }
            if (futuresEnabled)
            {
                try
                {
                    string futuresKey = tempFuturesKey;
                    string futuresSecret = tempFuturesSecret;

                    if (string.IsNullOrEmpty (futuresKey) || string.IsNullOrEmpty (futuresSecret))
                    {
                        _ui?.AddLog ("⚠️ Фьючерсные API ключи не настроены. Укажите futuresApiKey/futuresApiSecret в config.json");
                    }
                    else
                    {
                        var futuresClient = new BinanceFuturesClient (futuresKey, futuresSecret);
                        _futuresClient = futuresClient;
                        _wallet.SetFuturesClient (futuresClient);
                        await futuresClient.SyncTimeAsync ();
                        await futuresClient.SetMarginTypeAsync ("BTCUSDT", "ISOLATED");
                        await futuresClient.SetPositionModeAsync (true); // Hedge Mode

                        int leverage = _tradingSettings?.FuturesLeverage ?? 5;
                        await futuresClient.SetLeverageAsync ("BTCUSDT", leverage);
                        _ui?.AddLog ($"✅ Фьючерсы: Изолированная маржа, Hedge Mode, плечо {leverage}x");
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

            // ═══════════════════════════════════════════════════
            // TradingView Webhook Server
            // ═══════════════════════════════════════════════════
            if (_tradingSettings?.TradingViewEnabled == true)
            {
                int port = _tradingSettings.WebhookPort > 0 ? _tradingSettings.WebhookPort : 8765;
                _tradingViewHandler = new TradingViewWebhookService (
                    _ui, _client, _tradingSettings,
                    async (sym, indicators, balance) => await _orderExecutor.ExecuteBuyAsync (sym, indicators, balance),
                    async (sym) => await _orderExecutor.ExecuteSellAsync (sym),
                    () => _wallet.GetTotalBalance ("USDC").ToString (CultureInfo.InvariantCulture),
                    (sym) => _webSocketManager?.GetCurrentPrice (sym) ?? 0);

                _webhookServer = new WebhookServer (port, msg => _ui?.AddLog (msg));
                _webhookServer.Start (async (source, body) => await _tradingViewHandler.HandleWebhookAsync (source, body));
                _ui?.AddLog ($"📡 TradingView webhook: порт {port}, секрет {(_tradingSettings.TradingViewSecret?.Length > 0 ? "настроен" : "НЕТ")}");
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
        }

        /// <summary>
        /// Загружает пары и индикаторы для отображения в таблице до старта торговли.
        /// Вызывается при запуске приложения, чтобы таблица не была пустой.
        /// </summary>
        public async Task LoadPairsForDisplayAsync (MainWindowViewModel ui)
        {
            try
            {
                await _wallet.UpdateBalance ();
                decimal balance = _wallet.GetTotalBalance ("USDC");

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
                string[] whitelist = PairManager.GetWhitelistForBalance (balance);
                int maxPositions = PairManager.GetMaxPositionsForBalance (balance);

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

                    int delayMs = _positionManager.Count > 0 ? 60000 : 3600000;
                    await Task.Delay (delayMs, _shutdownCts?.Token ?? CancellationToken.None);
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
                    try { await Task.Delay (5000, _shutdownCts?.Token ?? CancellationToken.None); }
                    catch (OperationCanceledException) { break; }
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
            if (_klinesCache.TryGetValue (cacheKey, out var cached) && DateTime.UtcNow < cached.Expiry)
            {
                return cached.Klines;
            }
            var klines = await _client.GetKlinesAsync (symbol, interval, limit);
            if (klines != null)
            {
                _klinesCache[cacheKey] = (klines, DateTime.UtcNow + TimeSpan.FromSeconds (30));
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

                    // Circuit breaker: пауза при последовательных ошибках API
                    if (DateTime.UtcNow < _circuitBreakerUntil)
                    {
                        await Task.Delay (10000, _shutdownCts?.Token ?? CancellationToken.None);
                        continue;
                    }

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
                    List<string> pairs = _pairManager.GetActivePairs ();
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
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"CandleInterval read error: {ex.Message}"); }

                    // Мульти-таймфрейм: читаем из TradingSettings
                    if (_ui != null)
                    {
                        candleInterval = _ui.MainTimeframe ?? "1h";
                        entryInterval = _ui.EntryTimeframe ?? "5m";
                    }

                    // 4. Анализ пар (параллельный)
                    if (_ui != null)
                    {
                        _strategy.FastSmaPeriod = _ui.FastSma;
                        _strategy.SlowSmaPeriod = _ui.SlowSma;
                        _strategy.RsiPeriod = _ui.RsiPeriod;
                        _strategy.MainTimeframe = _ui.MainTimeframe ?? "1h";
                        _strategy.EntryTimeframe = _ui.EntryTimeframe ?? "5m";
                    }

                    var analysisResults = new System.Collections.Concurrent.ConcurrentBag<(string Symbol, (TradeAction Action, string Reason, Dictionary<string, decimal> Indicators) Analysis, bool HasPosition)>();
                    using var analysisSemaphore = new SemaphoreSlim (5, 5);
                    var analysisTasks = pairs.Select (async sym =>
                    {
                        await analysisSemaphore.WaitAsync ();
                        try
                        {
                            var klines = await GetKlinesCachedAsync (sym, candleInterval, 100);
                            if (klines == null || klines.Count < 30) return;

                            var analysis = await _strategy.AnalyzeAsync (sym, klines);
                            bool hasPosition = _positionManager.TryGet (sym, out _);
                            analysisResults.Add ((sym, analysis, hasPosition));
                        }
                        finally { analysisSemaphore.Release (); }
                    });
                    await Task.WhenAll (analysisTasks);

                    foreach (var (sym, analysis, hasPosition) in analysisResults)
                    {
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

                        // 5. Исполнение сигналов (только с подтверждением + новостной фильтр)
                        bool traded = false;

                        // Диагностика: почему Buy не исполняется
                        if (analysis.Action == TradeAction.Buy && !hasPosition)
                        {
                            if (!confirmed)
                            {
                                _ui?.AddLog ($"🔍 {sym}: Buy ЗАБЛОКИРОВАН — multi-TF подтверждение НЕ пройдено ({entryInterval})");
                            }
                            else if (_positionManager.Count >= (_ui?.MaxConcurrentTrades ?? 3))
                            {
                                _ui?.AddLog ($"🔍 {sym}: Buy ЗАБЛОКИРОВАН — макс. позиций {_positionManager.Count}/{_ui?.MaxConcurrentTrades ?? 3}");
                            }
                        }

                        if (analysis.Action == TradeAction.Buy && !hasPosition && confirmed && _positionManager.Count < (_ui?.MaxConcurrentTrades ?? 3))
                        {
                            // Расчёт реальной экспозиции открытых позиций
                            decimal currentTotalExposure = 0;
                            foreach (string openSym in _positionManager.GetSymbols ())
                            {
                                if (_positionManager.TryGet (openSym, out var openPos))
                                {
                                    decimal openPrice = await _client.GetPriceAsync (openSym);
                                    if (openPrice > 0)
                                        currentTotalExposure += openPos.Quantity * openPrice;
                                }
                            }

                            // Проверка лимитов RiskManager — реальный размер сделки (как в OrderExecutor)
                            decimal maxRiskPerTrade = RiskCalculator.CalculateRiskAmount (spotBalance, 0.02m);
                            var riskCheck = _riskManager.CanOpenPosition (_positionManager.Count, maxRiskPerTrade, currentTotalExposure);
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
                                _ui?.AddLog ($"⚠️ {sym}: Экстремальная жадность (FG={fgCached?.Value}), снижаю размер позиции на 50%");
                                // Снижаем размер позиции при экстремальной жадности
                                decimal reducedSpotBalance = spotBalance * 0.5m;
                                await _orderExecutor.ExecuteBuyAsync (sym, analysis.Indicators, reducedSpotBalance);
                                traded = true;
                            }
                            else
                            {
                                await _orderExecutor.ExecuteBuyAsync (sym, analysis.Indicators, spotBalance);
                                traded = true;
                            }
                        }
                        else if (analysis.Action == TradeAction.Sell && hasPosition && confirmed)
                        {
                            await _orderExecutor.ExecuteSellAsync (sym);
                            traded = true;
                        }

                        // 6. Volume Breakout стратегия (если включена)
                        if (_ui?.VolumeBreakoutEnabled == true && !hasPosition && _positionManager.Count < (_ui?.MaxConcurrentTrades ?? 3))
                        {
                            var symKlines = await GetKlinesCachedAsync (sym, candleInterval, 100);
                            if (symKlines != null && _volumeBreakout.CheckVolumeBreakout (symKlines))
                            {
                                _ui?.AddLog ($"🚀 {sym}: Прорыв объёма — сигнал!");
                                await _orderExecutor.ExecuteBuyAsync (sym, analysis.Indicators, spotBalance);
                                traded = true;
                            }
                        }

                        // 7. DCA стратегия (если включена)
                        if (_ui?.DcaEnabled == true && !hasPosition)
                        {
                            var symKlines2 = await GetKlinesCachedAsync (sym, candleInterval, 100);
                            if (symKlines2 != null && _dcaStrategy.ShouldBuy (sym, symKlines2, spotBalance))
                            {
                                decimal buyAmount = _dcaStrategy.CalculateBuyAmount (spotBalance);
                                _ui?.AddLog ($"📊 {sym}: DCA покупка на {buyAmount:F2} USDC");
                                var indicators = new Dictionary<string, decimal> (analysis.Indicators);
                                indicators["dcaBuyAmount"] = buyAmount;
                                await _orderExecutor.ExecuteBuyAsync (sym, indicators, spotBalance);
                                traded = true;
                            }
                        }

                        // Однократное обновление баланса за итерацию по паре
                        if (traded)
                        {
                            spotBalance = await _client.GetAccountBalanceAsync ("USDC");
                        }
                    }

                    _consecutiveErrors = 0;
                    await Task.Delay (30000, _shutdownCts?.Token ?? CancellationToken.None);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    string msg = ex.Message;
                    _ui?.AddLog ($"❌ Торговый цикл: {msg}");
                    _consecutiveErrors++;

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

                    // Circuit breaker: пауза после 5 последовательных ошибок
                    if (_consecutiveErrors >= CircuitBreakerThreshold)
                    {
                        _circuitBreakerUntil = DateTime.UtcNow + CircuitBreakerCooldown;
                        _ui?.AddLog ($"⚠️ Обрыв цепи: торговля приостановлена на {CircuitBreakerCooldown.TotalMinutes} мин ({_consecutiveErrors} ошибок подряд)");
                        _ = SendTradeNotification ($"⚠️ Обрыв цепи: {_consecutiveErrors} ошибок подряд, торговля приостановлена на {CircuitBreakerCooldown.TotalMinutes} мин");
                    }

                    await Task.Delay (10000, _shutdownCts?.Token ?? CancellationToken.None);
                }
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine ($"SendTradeNotification error: {ex.Message}");
            }
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
                    posDetails += $"\n  • {kvp.Key}: Вход={kvp.Value.EntryPrice:F4}, PnL={profit:+F2;-F2}%";
                }
            }

            return $"🤖 *Статус:* {status}\n" +
                   $"💰 *USDC:* {balance:F2}\n" +
                   $"📊 *Позиций:* {posCount}{posDetails}\n" +
                   $"📈 *PnL:* {pnl:+F2;-F2} USDC\n" +
                   $"🎯 *Винрейт:* {winRate:F1}% ({totalTrades} сделок)" +
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
        public bool IsTelegramEnabled() => _telegram != null && _telegram.IsEnabled;

        public async Task<bool> TestTelegramAsync()
        {
            if (_telegram == null) return false;
            return await _telegram.TestConnectionAsync ();
        }

    }
}
