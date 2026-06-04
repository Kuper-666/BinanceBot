using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        private MarketDataProvider _marketData;
        private OrderExecutor _orderExecutor;
        private RiskCalculator _riskCalc;
        private DataCollector _dataCollector;
        private MainWindowViewModel _ui;
        private bool _isRunning;
        private List<string> _activePairs = new ();
        private readonly object _pairsLock = new object ();
        private TelegramNotifier _telegram;
        private DateTime _lastRetrainTime = DateTime.MinValue;
        private readonly TimeSpan _minRetrainInterval = TimeSpan.FromHours (1);
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

        // Флаги для остановки циклов
        private bool _balanceLoopEnabled = true;
        private bool _pairsLoopEnabled = true;
        private bool _earnLoopEnabled = true;
        private bool _dustLoopEnabled = true;
        private bool _autoRetrainLoopEnabled = true;
        private bool _orderHistoryLoopEnabled = true;
        private bool _tradingLoopEnabled = true;

        public TradingService(BinanceClient client, WalletManager wallet, EarnManager earn, BalanceRebalancer rebalancer = null,
                              decimal minUsdcBalance = 5.50m, string telegramBotToken = "", string telegramChatId = "")
        {
            _client = client;
            _wallet = wallet;
            _earn = earn;
            _rebalancer = rebalancer ?? new BalanceRebalancer (new object (), 0.1m);
            _telegramBotToken = telegramBotToken;
            _telegramChatId = telegramChatId;
            string dataDir = System.IO.Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Data");
            string logsDir = System.IO.Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Logs");
            _positionManager = new PositionManager (System.IO.Path.Combine (dataDir, "open_positions.json"), null);
            _mlManager = new MlModelManager (System.IO.Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "trading_model.zip"), null);
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
                string configPath = System.IO.Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "config.txt");
                if (System.IO.File.Exists (configPath))
                {
                    var lines = System.IO.File.ReadAllLines (configPath);
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

            // Инициализация сервисов, зависящих от UI и логгера
            _marketData = new MarketDataProvider (_client, _ui, logger);
            _riskCalc = new RiskCalculator (_client, _ui, logger);
            _orderExecutor = new OrderExecutor (_client, _positionManager, _dataLogger, logger, _ui);
            _dataCollector = new DataCollector (_client, _mlManager, logger, _ui);
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
                    _ui?.AddLog ("⚠️ Не найдено активных пар");
                }
            }
            catch (Exception ex) { _ui?.AddLog ($"❌ Ошибка обновления пар: {ex.Message}"); }
        }

        // ============ Фоновые циклы ============
        private async Task BalanceLoop()
        {
            while (_isRunning)
            {
                if (!_balanceLoopEnabled) { await Task.Delay (5000); continue; }
                await Task.Delay (60000);
                if (!_isRunning) break;
                await _wallet.UpdateBalance ();
                decimal bal = _wallet.GetTotalBalance ("USDC");
                _ui?.UpdateWalletDisplay (bal.ToString ("F2"));
                _ui?.UpdateDrawdown (bal);
                _ui?.AddBalancePoint (DateTime.Now, bal);
            }
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
                await _dataCollector.FetchAndRetrainFromOrderHistoryAsync (_activePairs, _ui.FastSma, _ui.SlowSma);
            }
        }

        private async Task OrderHistoryCollectorLoop()
        {
            while (_isRunning)
            {
                if (!_orderHistoryLoopEnabled) { await Task.Delay (5000); continue; }
                await Task.Delay (_ordersFetchInterval);
                if (!_isRunning) break;
                await _dataCollector.FetchAndRetrainFromOrderHistoryAsync (_activePairs, _ui.FastSma, _ui.SlowSma);
            }
        }

        private async Task TradingLoop()
        {
            while (_isRunning)
            {
                if (!_tradingLoopEnabled) { await Task.Delay (5000); continue; }
                try
                {
                    decimal spotBalance = await _client.GetAccountBalanceAsync ("USDC");
                    decimal totalBalance = _wallet.GetTotalBalance ("USDC");
                    _ui?.UpdateWalletDisplay (totalBalance.ToString ("F2"));

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
                        }
                    }

                    List<string> pairs;
                    lock (_pairsLock) { pairs = new List<string> (_activePairs); }
                    if (pairs.Count == 0) { await Task.Delay (5000); continue; }

                    var signals = await _marketData.AnalyzePairsAsync (pairs, _ui.FastSma, _ui.SlowSma);
                    foreach (var sig in signals)
                    {
                        bool hasPos = _positionManager.TryGet (sig.Symbol, out _);
                        if (sig.Action == TradeAction.Buy && !hasPos && _positionManager.Count < 3)
                        {
                            if (spotBalance < 10) continue;
                            decimal riskCapital = await _riskCalc.CalculateDynamicRiskAsync (totalBalance, _ui.MaxRiskPercent, sig.Volatility);
                            decimal qty = await _riskCalc.CalculatePositionSizeAsync (sig.Symbol, riskCapital, sig.Price);
                            if (qty <= 0) continue;
                            spotBalance = await _orderExecutor.ExecuteBuyAsync (
                                sig.Symbol, sig.Price, qty, spotBalance,
                                _ui.StopLossPercent, _ui.TakeProfitPercent,
                                async (newBalance, _) => { spotBalance = newBalance; _ui.UpdateWalletDisplay (totalBalance.ToString ("F2")); }
                            );
                        }
                        else if (sig.Action == TradeAction.Sell && hasPos)
                        {
                            if (_positionManager.TryGet (sig.Symbol, out var pos))
                                await _orderExecutor.ExecuteSellAsync (sig.Symbol, sig.Price, pos, async () =>
                                {
                                    _ui.UpdatePositionsStatus (_positionManager.Count, 3, _positionManager.GetSymbols ());
                                    spotBalance = await _client.GetAccountBalanceAsync ("USDC");
                                });
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

        // ============ Вспомогательные методы ============
        private async Task LogErrorToTelegram(string error, bool sendToTelegram = true)
        {
            _ui?.AddLog ($"❌ {error}");
            lock (_recentErrors) { _recentErrors.Insert (0, $"{DateTime.Now:HH:mm:ss} - {error}"); if (_recentErrors.Count > MaxErrors) _recentErrors.RemoveAt (_recentErrors.Count - 1); }
            if (sendToTelegram && _telegram != null)
                await _telegram.SendErrorNotification (error);
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

        private string GetPerformanceStats()
        {
            var totalTrades = _ui.TotalTrades;
            if (totalTrades == 0) return "Нет сделок для статистики.";
            var wins = _ui.WinningTrades;
            var losses = _ui.LosingTrades;
            var totalPnL = _ui.TotalPnL;
            var winRate = wins * 100.0m / totalTrades;
            var avgWin = wins > 0 ? _ui.TotalPnL / wins : 0; // упрощённо
            var avgLoss = losses > 0 ? Math.Abs (( _ui.TotalPnL - totalPnL ) / losses) : 0;
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

        public void StopTrading() => _isRunning = false;

        private async Task HandleTelegramCommand(string command, string chatId)
        {
            string cmd = command.Trim ();
            _ui?.AddLog ($"📨 Получена команда: '{cmd}'");
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
                    _ = Task.Run (() => _dataCollector.FetchAndRetrainFromOrderHistoryAsync (_activePairs, _ui.FastSma, _ui.SlowSma));
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
                    await _telegram.SendMessageAsync ("📊 Функция графика временно недоступна. Используйте /performance.", chatId);
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
    }
}