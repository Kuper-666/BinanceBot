using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BinanceBotWpf.Models;
using BinanceBotWpf.ViewModels;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Основной сервис торговли (упрощённая версия после рефакторинга)
    /// </summary>
    public class TradingService
    {
        // Основные компоненты
        private readonly BinanceClient _client;
        private readonly WalletManager _wallet;
        private readonly EarnManager _earn;
        private readonly BalanceRebalancer _rebalancer;
        private readonly PositionManager _positionManager;
        private MlModelManager _mlManager;
        private readonly TradingStrategy _strategy;
        private readonly SignalFilter _signalFilter;
        private readonly PositionProtector _positionProtector;
        private WebSocketPriceManager _webSocketManager;

        private MainWindowViewModel _ui;
        private bool _isRunning;
        private TelegramNotifier _telegram;

        // Списки и кэш
        private List<string> _activePairs = new ();
        private readonly object _pairsLock = new ();
        private readonly Dictionary<string, DateTime> _lastBuyTime = new ();
        private readonly List<string> _recentErrors = new ();
        private readonly int MaxErrors = 20;

        // Настройки
        private readonly string _telegramBotToken;
        private readonly string _telegramChatId;
        private readonly TimeSpan _ordersFetchInterval = TimeSpan.FromHours (4);
        private DateTime _lastOrdersFetch = DateTime.MinValue;

        // Флаги циклов
        private bool _balanceLoopEnabled = true;
        private bool _tradingLoopEnabled = true;

        // TradingService.cs, конструктор:
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
            _strategy = new TradingStrategy (null);
            _signalFilter = new SignalFilter (null);
            _positionProtector = new PositionProtector (client, _positionManager, null);

            // ✅ ИНИЦИАЛИЗАЦИЯ WebSocket менеджера
            _webSocketManager = new WebSocketPriceManager (null);
        }

        private bool _loggerSet = false;

        public void SetLogger(Action<string> logger)
        {
            if (_loggerSet) return;
            _loggerSet = true;

            _wallet.OnLogGenerated += logger;
            _earn.OnLogGenerated += logger;
            _rebalancer.OnLogGenerated += logger;
            _client.OnLogGenerated += logger;

            // Пересоздаём MlModelManager с логгером
            string modelPath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "trading_model.zip");
            _mlManager = new MlModelManager (modelPath, logger);
            _strategy.SetMlManager (_mlManager);

            // У новых сервисов логгер уже установлен в конструкторе
            if (_webSocketManager == null)
            {
                _webSocketManager = new WebSocketPriceManager (logger);
                logger ("✅ WebSocket менеджер инициализирован");
            }

            // Инициализация Telegram
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
                    if (_telegram == null)
                    {
                        _telegram = new TelegramNotifier (tgToken, tgChatId);
                        _telegram.OnStatusChanged += (isEnabled, msg) =>
                        {
                            logger (isEnabled ? $"✅ Telegram: {msg}" : $"❌ Telegram: {msg}");
                            _ui?.RefreshTelegramStatus ();
                        };
                        _telegram.StartListening (HandleTelegramCommand);
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
        }

        public async Task StartTradingAsync(MainWindowViewModel vm)
        {
            if (_isRunning) return;
            _ui = vm;
            _isRunning = true;
            if (_ui != null) _ui.IsRunning = true;

            SetLogger (vm.AddLog);
            await InitAsync ();

            _ = Task.Run (BalanceLoop);
            _ = Task.Run (TradingLoop);
            _ = Task.Run (AutoOptimizeLoop);
        }

        public void StopTrading()
        {
            _isRunning = false;
            if (_ui != null) _ui.IsRunning = false;
            _webSocketManager?.Dispose ();
            _webSocketManager = null;
        }

        public decimal GetCurrentPriceForSymbol(string symbol) => GetCurrentPrice (symbol);

        private async Task InitAsync()
        {
            await _wallet.UpdateBalance ();
            await UpdatePairs ();
            await LoadPositions ();
            _ui?.AddLog (_client.IsTestnet ? "⚠️ ТЕСТОВАЯ СЕТЬ" : "✅ РЕАЛЬНАЯ СЕТЬ");
        }

        private async Task UpdatePairs()
        {
            try
            {
                var newPairs = await _client.GetTopVolumePairsAsync ("USDC", 10);
                newPairs = newPairs.Where (p => !p.Contains ("USD1") && !p.Contains ("UUSDC")).ToList ();

                if (newPairs.Count > 0)
                {
                    lock (_pairsLock) { _activePairs = newPairs; }
                    var newSymbols = newPairs.Except (_webSocketManager.GetSubscribedSymbols ()).ToArray ();
                    if (newSymbols.Any ())
                        await _webSocketManager.SubscribeToSymbolsAsync (newSymbols);
                    _ui?.AddLog ($"📊 Обновлено {_activePairs.Count} пар");
                }
            }
            catch (Exception ex) { _ui?.AddLog ($"❌ Ошибка обновления пар: {ex.Message}"); }
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
                if (!_balanceLoopEnabled) { await Task.Delay (5000); continue; }
                await Task.Delay (60000);
                if (!_isRunning) break;

                await _wallet.UpdateBalance ();
                decimal bal = _wallet.GetTotalBalance ("USDC");
                _ui?.UpdateWalletDisplay (bal.ToString ("F2"));
                _ui?.UpdateDrawdown (bal);
                _ui?.AddBalancePoint (DateTime.Now, bal);

                // Ребаланс при низком балансе
                decimal spotBalance = await _client.GetAccountBalanceAsync ("USDC");
                if (spotBalance < 10)
                {
                    var openSymbols = new HashSet<string> (_positionManager.GetSymbols ());
                    await _rebalancer.AutoConvertAssetsToUsdcAsync (_client, _isRunning, openSymbols);
                }
            }
        }

        /// <summary>
        /// Возвращает клиент Binance для доступа к API
        /// </summary>
        public BinanceClient GetBinanceClient() => _client;
        private async Task TradingLoop()
        {
            while (_isRunning)
            {
                try
                {
                    if (!_tradingLoopEnabled) { await Task.Delay (5000); continue; }

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
                        await Task.Delay (60000);
                        continue;
                    }

                    // 3. Получение списка пар
                    List<string> pairs;
                    lock (_pairsLock) { pairs = new List<string> (_activePairs); }
                    if (pairs.Count == 0) { await Task.Delay (5000); continue; }

                    // 4. Анализ пар
                    foreach (var sym in pairs)
                    {
                        var klines = await _client.GetKlinesAsync (sym, "5m", 50);
                        if (klines == null || klines.Count < 30) continue;

                        if (_ui != null)
                        {
                            _strategy.FastSmaPeriod = _ui.FastSma;
                            _strategy.SlowSmaPeriod = _ui.SlowSma;
                            _strategy.RsiPeriod = _ui.RsiPeriod;
                        }

                        var analysis = await _strategy.AnalyzeAsync (sym, klines);
                        bool hasPosition = _positionManager.TryGet (sym, out _);

                        // Обновление UI
                        if (analysis.Indicators.ContainsKey ("price"))
                        {
                            _ui.UpdateMarketTable (sym, analysis.Indicators["price"].ToString ("F4"),
                                hasPosition, analysis.Action,
                                analysis.Indicators.ContainsKey ("fastSma") ? analysis.Indicators["fastSma"] : 0,
                                analysis.Indicators.ContainsKey ("slowSma") ? analysis.Indicators["slowSma"] : 0);
                        }

                        // 5. Исполнение сигналов
                        if (analysis.Action == TradeAction.Buy && !hasPosition && _positionManager.Count < ( _ui?.MaxConcurrentTrades ?? 3 ))
                        {
                            await ExecuteBuy (sym, analysis.Indicators, spotBalance);
                            spotBalance = await _client.GetAccountBalanceAsync ("USDC");
                        }
                        else if (analysis.Action == TradeAction.Sell && hasPosition)
                        {
                            await ExecuteSell (sym);
                            spotBalance = await _client.GetAccountBalanceAsync ("USDC");
                        }
                    }

                    await Task.Delay (60000);
                }
                catch (Exception ex)
                {
                    _ui?.AddLog ($"❌ TradingLoop: {ex.Message}");
                    await Task.Delay (10000);
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

            // Убран жесткий фильтр, полагаемся на анализ стратегии и ИИ
            int aiRiskLevel = indicators.ContainsKey ("aiRiskLevel") ? (int)indicators["aiRiskLevel"] : 2;

            if (aiRiskLevel == 3)
            {
                _ui?.AddLog ($"⚠️ {symbol}: Сигнал проигнорирован из-за высокого риска (ИИ)");
                return;
            }

            // Расчёт динамического риска с учетом ИИ
            var riskCalc = new RiskCalculator (_client, _ui, msg => _ui?.AddLog (msg));
            decimal volatility = indicators.ContainsKey ("bbWidth") ? indicators["bbWidth"] : 0.05m;
            decimal riskAmount = await riskCalc.CalculateDynamicRiskAsync (currentBalance, 0.10m, volatility, aiRiskLevel);

            decimal qty = riskAmount / price;
            decimal stepSize = await _client.GetStepSizeAsync (symbol);
            qty = Math.Floor (qty / stepSize) * stepSize;

            // Binance отклоняет ордера дешевле минимального notional (обычно $5-10 на спотовых парах).
            // Если расчётный риск ниже минимума, но баланс позволяет — поднимаем сумму ордера до минимума,
            // вместо того чтобы молча отправлять заведомо отклоняемый ордер.
            const decimal minNotional = 6m; // запас над типичным минимумом Binance в $5
            if (qty * price < minNotional)
            {
                if (currentBalance >= minNotional)
                {
                    decimal minQty = minNotional / price;
                    qty = Math.Ceiling (minQty / stepSize) * stepSize;
                    _ui?.AddLog ($"ℹ️ {symbol}: расчётный риск {riskAmount:F2} USDC ниже минимального ордера, сумма поднята до {qty * price:F2} USDC");
                }
                else
                {
                    _ui?.AddLog ($"⏸ {symbol}: сигнал BUY проигнорирован — баланс {currentBalance:F2} USDC недостаточен для минимального ордера ({minNotional:F2} USDC)");
                    return;
                }
            }

            if (qty <= 0)
            {
                _ui?.AddLog ($"⏸ {symbol}: сигнал BUY проигнорирован — нулевое количество после округления по шагу лота");
                return;
            }
            if (qty * price > currentBalance)
            {
                _ui?.AddLog ($"⏸ {symbol}: сигнал BUY проигнорирован — стоимость ордера {qty * price:F2} USDC превышает доступный баланс {currentBalance:F2} USDC");
                return;
            }

            // Проверка кулдауна
            if (_lastBuyTime.TryGetValue (symbol, out var lastTime) && DateTime.UtcNow - lastTime < TimeSpan.FromMinutes (2))
            {
                _ui?.AddLog ($"⏸ {symbol}: сигнал BUY проигнорирован — кулдаун после прошлой покупки ({( TimeSpan.FromMinutes (2) - ( DateTime.UtcNow - lastTime ) ).TotalSeconds:F0} сек осталось)");
                return;
            }
            _lastBuyTime[symbol] = DateTime.UtcNow;

            // Исполнение ордера
            _ui?.AddLog ($"💵 Покупка {qty} {symbol} по {price:F4}");
            var order = await _client.PlaceOrder (symbol, "BUY", "MARKET", qty);

            if (order != null)
            {
                var pos = new OpenPosition
                {
                    Symbol = symbol,
                    Quantity = qty,
                    EntryPrice = price,
                    OpenTime = DateTime.UtcNow,
                    StopLossPrice = price * ( 1 - ( _ui?.StopLossPercent ?? 0.02m ) ),
                    TakeProfitPrice = price * ( 1 + ( _ui?.TakeProfitPercent ?? 0.04m ) ),
                    HighestPrice = price,
                    HighestPriceSinceOpen = price,
                    OcoOrderListId = 0
                };

                _positionManager.AddOrUpdate (symbol, pos);
                _ui?.AddLog ($"✅ Куплено {qty} {symbol}");
                _ui?.UpdatePositionsStatus (_positionManager.Count, _ui?.MaxConcurrentTrades ?? 3, _positionManager.GetSymbols ());
            }
        }

        private async Task ExecuteSell(string symbol)
        {
            if (!_positionManager.TryGet (symbol, out var pos)) return;

            string asset = symbol.Replace ("USDC", "");
            decimal spotBalance = await _client.GetAccountBalanceAsync (asset);
            decimal price = GetCurrentPrice (symbol);

            decimal qtyToSell = Math.Min (pos.Quantity, spotBalance);
            if (qtyToSell <= 0.000001m)
            {
                _positionManager.Remove (symbol);
                return;
            }

            // Отмена OCO ордера
            if (pos.OcoOrderListId != 0)
            {
                await _client.CancelOcoOrder (symbol, pos.OcoOrderListId);
            }

            // Продажа
            var order = await _client.PlaceOrder (symbol, "SELL", "MARKET", qtyToSell);
            if (order != null)
            {
                decimal pnl = ( price - pos.EntryPrice ) * qtyToSell;
                decimal pnlPct = ( price / pos.EntryPrice - 1 ) * 100;

                _ui?.AddLog ($"🔒 Закрыта {symbol}: PnL {pnl:F2} ({pnlPct:F2}%)");

                var trade = new TradeLog
                {
                    Symbol = symbol,
                    EntryPrice = pos.EntryPrice,
                    ExitPrice = price,
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
                _positionManager.Remove (symbol);
                _ui?.UpdatePositionsStatus (_positionManager.Count, _ui?.MaxConcurrentTrades ?? 3, _positionManager.GetSymbols ());
            }
        }

        // Добавленный метод HandleTelegramCommand
        private async Task HandleTelegramCommand(string command, string chatId)
        {
            if (_telegram == null) return;

            // Защита от несанкционированного доступа (только для владельца)
            if (chatId != _telegram.GetChatId ())
            {
                _ui?.AddLog ($"⚠️ Попытка несанкционированного управления от ChatId {chatId} заблокирована.");
                return;
            }

            string cmd = command.Trim ();

            // Обработка кнопок
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
                    await _telegram.SendMessageAsync ($"💰 Баланс USDC: {_wallet.GetTotalBalance ("USDC"):F2}", chatId);
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
                        await StartTradingAsync (_ui);
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
                    // TODO: добавить вызов переобучения
                    break;
                case "/pnl":
                    await _telegram.SendMessageAsync ($"📈 Общий PnL: {_ui?.TotalPnL ?? 0:F2} USDC\n🎯 Win Rate: {_ui?.WinRate ?? 0:F1}%", chatId);
                    break;
                case "/update":
                    await _telegram.SendMessageAsync ("🔄 Проверяю обновления...", chatId);
                    var updater = new UpdateManager (msg => _ui?.AddLog (msg));
                    bool updated = await updater.CheckAndUpdateAsync (silent: false);
                    if (updated)
                        await _telegram.SendMessageAsync ("✅ Обновление установлено. Бот будет перезапущен.", chatId);
                    else
                        await _telegram.SendMessageAsync ("✅ Обновлений не найдено.", chatId);
                    break;
                case "/errors":
                    string errors;
                    lock (_recentErrors) { errors = _recentErrors.Count == 0 ? "✅ Нет ошибок" : string.Join ("\n", _recentErrors); }
                    await _telegram.SendMessageAsync ($"📋 <b>Последние ошибки:</b>\n{errors}", chatId);
                    break;
                case "/performance":
                    await _telegram.SendMessageAsync (GetPerformanceStats (), chatId);
                    break;
                case "/help":
                    string help = "🤖 *Команды:*\n" +
                        "/status – состояние\n" +
                        "/balance – баланс\n" +
                        "/stop – стоп торговли\n" +
                        "/start – старт\n" +
                        "/export – экспорт\n" +
                        "/retrain – переобучить ML\n" +
                        "/pnl – статистика PnL\n" +
                        "/update – проверить обновления\n" +
                        "/errors – ошибки\n" +
                        "/performance – детальная статистика\n" +
                        "/help – помощь";
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
            while (_isRunning)
            {
                // Ждём 24 часа
                await Task.Delay (TimeSpan.FromHours (24));

                if (!_isRunning) break;

                _ui?.AddLog ("🧠 Запуск автоматической оптимизации параметров (ежедневная)...");

                var optimizer = new StrategyOptimizer (_client, _ui, _ui.AddLog);
                bool success = await optimizer.RunOptimizationAsync ();

                if (success)
                {
                    _ui?.AddLog ("✅ Ежедневная оптимизация завершена");
                }
                else
                {
                    _ui?.AddLog ("⚠️ Ежедневная оптимизация не дала результатов");
                }
            }
        }

        public bool IsTelegramEnabled() => _telegram != null && _telegram.IsEnabled;

        public async Task<bool> TestTelegramAsync()
        {
            if (_telegram == null) return false;
            return await _telegram.TestConnectionAsync ();
        }
    }
}