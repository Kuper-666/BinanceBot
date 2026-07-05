using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BinanceBotWpf.Models;
using BinanceBotWpf.ViewModels;

namespace BinanceBotWpf.Services
{
    public class TelegramCommandHandler
    {
        private readonly TelegramNotifier _telegram;
        private readonly BinanceClient _client;
        private readonly WalletManager _wallet;
        private readonly PositionManager _positionManager;
        private readonly TradingSettings _tradingSettings;
        private readonly MlModelManager _mlManager;
        private readonly WebSocketPriceManager _webSocketManager;
        private readonly BackupService _backupService;
        private readonly FearGreedIndexProvider _fearGreedProvider;
        private readonly PriceAlertManager _priceAlertManager;
        private readonly MainWindowViewModel _ui;
        private readonly List<string> _recentErrors;
        private readonly List<string> _activePairs;
        private readonly Func<bool> _isRunning;
        private readonly Func<Task> _stopTrading;
        private readonly Func<MainWindowViewModel, Task> _startTrading;
        private readonly Func<string, Task> _startAutoGrid;
        private readonly Func<Task> _stopGrid;
        private readonly Func<string> _getStatusText;
        private readonly Func<string> _getPerformanceStats;
        private readonly Action<string> _log;

        // Command cooldown:防止 спам тяжёлых команд
        private readonly Dictionary<string, DateTime> _lastCommandTime = new ();
        private static readonly TimeSpan CommandCooldown = TimeSpan.FromSeconds (3);

        public TelegramCommandHandler (
            TelegramNotifier telegram,
            BinanceClient client,
            WalletManager wallet,
            PositionManager positionManager,
            TradingSettings tradingSettings,
            MlModelManager mlManager,
            WebSocketPriceManager webSocketManager,
            BackupService backupService,
            FearGreedIndexProvider fearGreedProvider,
            PriceAlertManager priceAlertManager,
            MainWindowViewModel ui,
            List<string> recentErrors,
            List<string> activePairs,
            Func<bool> isRunning,
            Func<Task> stopTrading,
            Func<MainWindowViewModel, Task> startTrading,
            Func<string, Task> startAutoGrid,
            Func<Task> stopGrid,
            Func<string> getStatusText,
            Func<string> getPerformanceStats,
            Action<string> log)
        {
            _telegram = telegram;
            _client = client;
            _wallet = wallet;
            _positionManager = positionManager;
            _tradingSettings = tradingSettings;
            _mlManager = mlManager;
            _webSocketManager = webSocketManager;
            _backupService = backupService;
            _fearGreedProvider = fearGreedProvider;
            _priceAlertManager = priceAlertManager;
            _ui = ui;
            _recentErrors = recentErrors;
            _activePairs = activePairs;
            _isRunning = isRunning;
            _stopTrading = stopTrading;
            _startTrading = startTrading;
            _startAutoGrid = startAutoGrid;
            _stopGrid = stopGrid;
            _getStatusText = getStatusText;
            _getPerformanceStats = getPerformanceStats;
            _log = log;
        }

        public async Task HandleAsync (string command, string chatId)
        {
            if (_telegram == null) return;

            if (chatId != _telegram.GetChatId ())
            {
                _ui?.AddLog ($"⚠️ Попытка несанкционированного управления от ChatId {chatId} заблокирована.");
                return;
            }

            string cmd = command.Trim ();
            string originalText = cmd;

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

            // Cooldown:防止 спам тяжёлых команд
            string cooldownKey = cmd.Split (' ')[0];
            if (_lastCommandTime.TryGetValue (cooldownKey, out DateTime lastTime)
                && DateTime.UtcNow - lastTime < CommandCooldown)
            {
                return; // Тихо игнорируем повтор
            }
            _lastCommandTime[cooldownKey] = DateTime.UtcNow;

            try
            {
            switch (cmd)
            {
                case "/status":
                    await _telegram.SendMessageAsync (_getStatusText (), chatId);
                    break;
                case "/balance":
                    await _telegram.SendMessageAsync ($"💰 Баланс USDC: {_wallet.GetTotalBalance ("USDC"):F2}", chatId);
                    break;
                case "/stop":
                    if (_isRunning ())
                    {
                        await _stopTrading ();
                        await _telegram.SendMessageAsync ("⏹️ Торговля остановлена.", chatId);
                    }
                    else
                        await _telegram.SendMessageAsync ("Бот уже остановлен.", chatId);
                    break;
                case "/start":
                    if (!_isRunning () && _ui != null)
                    {
                        await _telegram.SendMessageAsync ("🔄 Запуск торгового бота...", chatId);
                        try
                        {
                            await _startTrading (_ui);
                            await _telegram.SendMessageAsync ("✅ Торговый бот запущен.", chatId);
                        }
                        catch (Exception ex)
                        {
                            await _telegram.SendMessageAsync ($"❌ Ошибка запуска: {ex.Message}", chatId);
                        }
                    }
                    else
                        await _telegram.SendMessageAsync ("Бот уже запущен.", chatId);
                    break;
                case "/export":
                    try
                    {
                        string csvPath = _ui?.ExportTradesCsv ();
                        if (!string.IsNullOrEmpty (csvPath))
                            await _telegram.SendMessageAsync ($"📁 История сделок экспортирована:\n<code>{csvPath}</code>", chatId);
                        else
                            await _telegram.SendMessageAsync ("⚠️ Нет сделок для экспорта.", chatId);
                    }
                    catch (Exception ex)
                    {
                        await _telegram.SendMessageAsync ($"❌ Ошибка экспорта: {ex.Message}", chatId);
                    }
                    break;
                case "/retrain":
                    await _telegram.SendMessageAsync ("🔄 Запускаю переобучение ML модели...", chatId);
                    try
                    {
                        var dc = new DataCollector (_client, _mlManager, msg => _ui?.AddLog (msg), _ui);
                        await dc.FetchAndRetrainFromOrderHistoryAsync (
                            _activePairs,
                            _tradingSettings?.FastSmaPeriod ?? 12,
                            _tradingSettings?.SlowSmaPeriod ?? 26);
                        await _telegram.SendMessageAsync ("✅ Переобучение ML завершено.", chatId);
                    }
                    catch (Exception ex)
                    {
                        await _telegram.SendMessageAsync ($"❌ Ошибка переобучения: {ex.Message}", chatId);
                    }
                    break;
                case "/chart":
                case "/pnl":
                    try
                    {
                        var trades = _ui?.TradesHistory;
                        if (trades == null || trades.Count == 0)
                        {
                            await _telegram.SendMessageAsync ("📊 Нет данных о сделках для графика.", chatId);
                            break;
                        }

                        int last = Math.Min (trades.Count, 10);
                        var recentTrades = trades.Skip (Math.Max (0, trades.Count - last)).ToList ();
                        decimal runningPnL = 0;
                        int wins = 0;
                        string chartText = "📈 <b>PnL по сделкам (последние " + last + "):</b>\n\n";

                        foreach (var trade in recentTrades)
                        {
                            runningPnL += trade.PnL;
                            if (trade.PnL > 0) wins++;
                            string bar = "";
                            int bars = (int)(Math.Abs (trade.PnL) * 5);
                            bars = Math.Min (bars, 20);
                            string barChar = trade.PnL >= 0 ? "🟩" : "🟥";
                            for (int i = 0; i < bars; i++) bar += barChar;
                            chartText += $"{trade.Symbol} {( trade.PnL >= 0 ? "+" : "" )}{trade.PnL:F2} {bar}\n";
                        }

                        decimal wr = last > 0 ? (decimal)wins / last * 100 : 0;
                        chartText += $"\n💰 <b>Итого: {( runningPnL >= 0 ? "+" : "" )}{runningPnL:F2} USDC</b>";
                        chartText += $"\n🎯 Win Rate: {wr:F0}% ({wins}/{last})";
                        await _telegram.SendMessageAsync (chartText, chatId);
                    }
                    catch (Exception ex)
                    {
                        await _telegram.SendMessageAsync ($"❌ Ошибка графика: {ex.Message}", chatId);
                    }
                    break;
                case "/stats":
                    try
                    {
                        if (_ui == null || _ui.TotalTrades == 0)
                        {
                            await _telegram.SendMessageAsync ("📊 Пока нет сделок для статистики.", chatId);
                            break;
                        }

                        string pf = _ui.ProfitFactor > 0 ? _ui.ProfitFactor.ToString ("F2") : "—";
                        string bestText = _ui.BestTrade != null
                            ? $"{_ui.BestTrade.Symbol} +{_ui.BestTrade.PnL:F2} USDC ({_ui.BestTrade.CloseTime:dd.MM HH:mm})"
                            : "—";
                        string worstText = _ui.WorstTrade != null
                            ? $"{_ui.WorstTrade.Symbol} {_ui.WorstTrade.PnL:F2} USDC ({_ui.WorstTrade.CloseTime:dd.MM HH:mm})"
                            : "—";

                        string statsText = "📊 <b>СТАТИСТИКА</b>\n\n" +
                            $"💰 PnL: {(_ui.TotalPnL >= 0 ? "+" : "")}{_ui.TotalPnL:F2} USDC\n" +
                            $"🎯 Win Rate: {_ui.WinRate:F1}% ({_ui.WinningTrades}/{_ui.TotalTrades})\n" +
                            $"📈 Сделок: {_ui.TotalTrades} (✅{_ui.WinningTrades} / ❌{_ui.LosingTrades})\n\n" +
                            $"⚖️ Profit Factor: {pf}\n" +
                            $"📊 Ср. прибыль: +{_ui.AverageWin:F2} USDC\n" +
                            $"📊 Ср. убыток: -{_ui.AverageLoss:F2} USDC\n\n" +
                            $"🔥 Серия побед: {_ui.MaxWinStreak}\n" +
                            $"❄️ Серия поражений: {_ui.MaxLoseStreak}\n\n" +
                            $"🏆 Лучшая: {bestText}\n" +
                            $"💀 Худшая: {worstText}";

                        // Per-symbol breakdown
                        var symStats = _ui.PerSymbolStats;
                        if (symStats.Count > 0)
                        {
                            statsText += "\n\n📊 <b>По парам:</b>";
                            foreach (var kvp in symStats.OrderByDescending (k => k.Value.PnL))
                            {
                                string symPnL = kvp.Value.PnL >= 0 ? $"+{kvp.Value.PnL:F2}" : $"{kvp.Value.PnL:F2}";
                                decimal symWR = kvp.Value.Total > 0 ? (decimal)kvp.Value.Wins / kvp.Value.Total * 100 : 0;
                                statsText += $"\n  {kvp.Key}: {kvp.Value.Total} сделок, WR {symWR:F0}%, {symPnL}";
                            }
                        }

                        await _telegram.SendMessageAsync (statsText, chatId);
                    }
                    catch (Exception ex)
                    {
                        await _telegram.SendMessageAsync ($"❌ Ошибка статистики: {ex.Message}", chatId);
                    }
                    break;
                case "/history":
                    try
                    {
                        var histTrades = _ui?.TradesHistory;
                        if (histTrades == null || histTrades.Count == 0)
                        {
                            await _telegram.SendMessageAsync ("📋 Пока нет сделок в истории.", chatId);
                            break;
                        }

                        int count = Math.Min (histTrades.Count, 15);
                        decimal histPnL = 0;
                        int histWins = 0;
                        string histText = $"📋 <b>ИСТОРИЯ СДЕЛОК (последние {count}):</b>\n\n";

                        for (int i = 0; i < count; i++)
                        {
                            var t = histTrades[i];
                            histPnL += t.PnL;
                            if (t.PnL > 0) histWins++;
                            string sign = t.PnL >= 0 ? "+" : "";
                            histText += $"{i + 1}. {t.CloseTime:dd.MM HH:mm} {t.Symbol} {t.Action} {sign}{t.PnL:F2} ({t.PnLPercent:F1}%) {t.Reason}\n";
                        }

                        decimal histWR = count > 0 ? (decimal)histWins / count * 100 : 0;
                        histText += $"\n💰 PnL ({count}): {( histPnL >= 0 ? "+" : "" )}{histPnL:F2} USDC | WR: {histWR:F0}%";
                        await _telegram.SendMessageAsync (histText, chatId);
                    }
                    catch (Exception ex)
                    {
                        await _telegram.SendMessageAsync ($"❌ Ошибка истории: {ex.Message}", chatId);
                    }
                    break;
                case "/update":
                    await _telegram.SendMessageAsync ("🔄 Проверяю обновления...", chatId);
                    var updater = new UpdateManager (msg => _ui?.AddLog (msg));
                    bool updated = await updater.CheckAndUpdateAsync (silent: false, hasOpenPositions: _positionManager.Count > 0);
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
                    await _telegram.SendMessageAsync (_getPerformanceStats (), chatId);
                    break;
                case "/grid":
                    await _telegram.SendMessageAsync ("⚠️ Grid-режим временно отключён, используйте спот-стратегию.", chatId);
                    break;
                case "/futures":
                    if (_ui != null)
                    {
                        _ui.FuturesEnabled = !_ui.FuturesEnabled;
                        string futStatus = _ui.FuturesEnabled ? "✅ Включены" : "❌ Отключены";
                        await _telegram.SendMessageAsync ($"📊 Фьючерсы: {futStatus}", chatId);
                    }
                    break;
                case "/dca":
                    if (_ui != null)
                    {
                        _ui.DcaEnabled = !_ui.DcaEnabled;
                        string dcaStatus = _ui.DcaEnabled ? "✅ Включён" : "❌ Отключён";
                        await _telegram.SendMessageAsync ($"📊 DCA: {dcaStatus}", chatId);
                    }
                    break;
                case "/optimize":
                    await _telegram.SendMessageAsync ("🧠 Запускаю оптимизацию...", chatId);
                    decimal optBalance = _wallet?.GetTotalBalance ("USDC") ?? 0;
                    var optimizer = new StrategyOptimizer (_client, _ui, _ui.AddLog);
                    bool optSuccess = await optimizer.RunOptimizationAsync (optBalance);
                    await _telegram.SendMessageAsync (optSuccess ? "✅ Оптимизация завершена" : "⚠️ Оптимизация не дала результатов", chatId);
                    break;
                case "/rollback":
                    var rollbackOptimizer = new StrategyOptimizer (_client, _ui, _ui.AddLog);
                    bool rolled = await rollbackOptimizer.RollbackToPreviousParameters ();
                    await _telegram.SendMessageAsync (rolled ? "🔄 Откат выполнен" : "⚠️ Нет предыдущих параметров", chatId);
                    break;
                case "/backup":
                    await _backupService.CreateBackupAsync ();
                    await _telegram.SendMessageAsync ("💾 Бэкап создан", chatId);
                    break;
                case "/restore":
                    var backups = _backupService.GetAvailableBackups ();
                    if (backups.Length > 0)
                    {
                        bool restored = await _backupService.RestoreFromBackupAsync (backups[0]);
                        await _telegram.SendMessageAsync (restored ? "✅ Конфигурация восстановлена" : "❌ Ошибка восстановления", chatId);
                    }
                    else
                        await _telegram.SendMessageAsync ("⚠️ Нет доступных бэкапов", chatId);
                    break;
                case "/feargreed":
                case "/fg":
                    var fgData = await _fearGreedProvider?.GetCurrentAsync ();
                    if (fgData != null)
                    {
                        string emoji = fgData.Value >= 75 ? "🔴" : fgData.Value <= 25 ? "🟢" : "🟡";
                        await _telegram.SendMessageAsync ($"{emoji} *Fear & Greed Index*\nValue: {fgData.Value}\nClassification: {fgData.Classification}", chatId);
                    }
                    else
                        await _telegram.SendMessageAsync ("⚠️ Не удалось получить Fear & Greed Index", chatId);
                    break;
                case "/alert":
                    string[] alertParts = originalText.Split (' ');
                    if (alertParts.Length >= 3)
                    {
                        string alertSymbol = alertParts[1].ToUpper ();
                        if (decimal.TryParse (alertParts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out decimal alertPrice))
                        {
                            PriceAlertDirection dir = alertPrice > 0 ? PriceAlertDirection.Above : PriceAlertDirection.Below;
                            alertPrice = Math.Abs (alertPrice);
                            string alertId = _priceAlertManager.AddAlert (alertSymbol, alertPrice, dir);
                            await _telegram.SendMessageAsync ($"🔔 Alert set: {alertSymbol} {dir} {alertPrice} (id={alertId})", chatId);
                        }
                        else
                            await _telegram.SendMessageAsync ("⚠️ Формат: /alert BTCUSDT 110000", chatId);
                    }
                    else
                        await _telegram.SendMessageAsync ("⚠️ Формат: /alert <SYMBOL> <PRICE>\nПример: /alert BTCUSDT 110000", chatId);
                    break;
                case "/alerts":
                    var alerts = _priceAlertManager.GetAllAlerts ();
                    if (alerts.Count == 0)
                        await _telegram.SendMessageAsync ("📋 Нет активных алертов", chatId);
                    else
                    {
                        string alertList = "📋 *Price Alerts:*\n";
                        foreach (var a in alerts)
                        {
                            string status = a.Triggered ? "✅" : "⏳";
                            alertList += $"{status} {a.Symbol} {a.Direction} {a.TargetPrice} (id={a.Id})\n";
                        }
                        await _telegram.SendMessageAsync (alertList, chatId);
                    }
                    break;
                case "/set":
                    string[] parts = originalText.Split (' ');
                    if (parts.Length >= 3)
                    {
                        string param = parts[1].ToLower ();
                        if (decimal.TryParse (parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out decimal val))
                        {
                            switch (param)
                            {
                                case "risk":
                                    _ui.RiskPerTradePercent = val / 100m;
                                    await _telegram.SendMessageAsync ($"✅ RiskPerTrade = {val}%", chatId);
                                    break;
                                case "rr":
                                    _ui.RiskRewardRatio = val;
                                    await _telegram.SendMessageAsync ($"✅ RiskRewardRatio = {val}", chatId);
                                    break;
                                default:
                                    await _telegram.SendMessageAsync ("⚠️ Неизвестный параметр. Доступные: risk, rr", chatId);
                                    break;
                            }
                        }
                        else
                            await _telegram.SendMessageAsync ("⚠️ Неверное значение. Пример: /set risk 1.5", chatId);
                    }
                    else
                        await _telegram.SendMessageAsync ("⚠️ Формат: /set <параметр> <значение>\nДоступные: risk, rr", chatId);
                    break;
                case "/help":
                    string help = "🤖 *Команды:*\n" +
                        "/status – состояние\n" +
                        "/balance – баланс\n" +
                        "/stop – стоп торговли\n" +
                        "/start – старт\n" +
                        "/export – экспорт CSV\n" +
                        "/retrain – переобучить ML\n" +
                        "/pnl – графики PnL\n" +
                        "/stats – полная статистика\n" +
                        "/history – история сделок\n" +
                        "/futures – вкл/выкл фьючерсы\n" +
                        "/dca – вкл/выкл DCA\n" +
                        "/optimize – оптимизация\n" +
                        "/rollback – откат параметров\n" +
                        "/set risk/rr – параметры\n" +
                        "/backup – бэкап\n" +
                        "/restore – восстановление\n" +
                        "/update – обновления\n" +
                        "/errors – ошибки\n" +
                        "/performance – статистика\n" +
                        "/feargreed /fg – Fear & Greed\n" +
                        "/alert SYMBOL PRICE – алерт\n" +
                        "/alerts – список алертов\n" +
                        "/help – помощь";
                    await _telegram.SendMessageAsync (help, chatId);
                    break;
                default:
                    await _telegram.SendMessageAsync ("Неизвестная команда. /help", chatId);
                    break;
            }
            }
            catch (Exception ex)
            {
                await _telegram.SendMessageAsync ($"❌ Ошибка: {ex.Message}", chatId);
            }
        }
    }
}
