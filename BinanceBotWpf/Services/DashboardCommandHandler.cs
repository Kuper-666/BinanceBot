using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BinanceBotWpf.ViewModels;

namespace BinanceBotWpf.Services
{
    public class DashboardCommandHandler
    {
        private readonly MainWindowViewModel _ui;
        private readonly Func<bool> _isRunning;
        private readonly Func<Task> _stopTrading;
        private readonly Func<MainWindowViewModel, Task> _startTrading;
        private readonly Func<Task> _runBacktest;
        private readonly Func<Task> _runOptimization;
        private readonly Func<Task<bool>> _testTelegram;
        private readonly Func<string, Task> _closePosition;

        public DashboardCommandHandler (
            MainWindowViewModel ui,
            Func<bool> isRunning,
            Func<Task> stopTrading,
            Func<MainWindowViewModel, Task> startTrading,
            Func<Task> runBacktest = null,
            Func<Task> runOptimization = null,
            Func<Task<bool>> testTelegram = null,
            Func<string, Task> closePosition = null)
        {
            _ui = ui;
            _isRunning = isRunning;
            _stopTrading = stopTrading;
            _startTrading = startTrading;
            _runBacktest = runBacktest;
            _runOptimization = runOptimization;
            _testTelegram = testTelegram;
            _closePosition = closePosition;
        }

        public async Task HandleAsync (string action, Dictionary<string, object> data)
        {
            try
            {
                switch (action)
                {
                    case "start":
                        if (!_isRunning ())
                        {
                            _ = _startTrading (_ui);
                            _ui?.AddLog ("🚀 Бот запущен из дашборда");
                        }
                        break;

                    case "stop":
                        if (_isRunning ())
                        {
                            await _stopTrading ();
                            _ui?.AddLog ("⏹️ Бот остановлен из дашборда");
                        }
                        break;

                    case "retrain":
                        _ui?.AddLog ("🔄 Переобучение ML запущено из дашборда");
                        if (_runOptimization != null)
                        {
                            _ = Task.Run (async () =>
                            {
                                try { await _runOptimization (); }
                                catch (Exception ex) { _ui?.AddLog ($"❌ Ошибка оптимизации: {ex.Message}"); }
                            });
                        }
                        break;

                    case "backtest":
                        _ui?.AddLog ("📊 Бэктест запущен из дашборда");
                        if (_runBacktest != null)
                        {
                            _ = Task.Run (async () =>
                            {
                                try { await _runBacktest (); }
                                catch (Exception ex) { _ui?.AddLog ($"❌ Ошибка бэктеста: {ex.Message}"); }
                            });
                        }
                        break;

                    case "export":
                        _ui?.AddLog ("📁 Экспорт запущен из дашборда");
                        try
                        {
                            string exportDir = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Export");
                            if (!Directory.Exists (exportDir)) Directory.CreateDirectory (exportDir);
                            string fileName = $"trades_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                            string filePath = Path.Combine (exportDir, fileName);
                            var trades = _ui?.TradesHistory;
                            if (trades != null && trades.Count > 0)
                            {
                                var lines = new List<string> { "Symbol,EntryPrice,ExitPrice,PnL%,OpenTime,CloseTime,Reason" };
                                foreach (var t in trades)
                                {
                                    lines.Add ($"{t.Symbol},{t.EntryPrice},{t.ExitPrice},{t.PnLPercent},{t.OpenTime:O},{t.CloseTime:O},{t.Reason}");
                                }
                                await File.WriteAllLinesAsync (filePath, lines);
                                _ui?.AddLog ($"✅ Экспорт: {filePath} ({trades.Count} сделок)");
                            }
                            else
                            {
                                _ui?.AddLog ("⚠️ Нет сделок для экспорта");
                            }
                        }
                        catch (Exception ex)
                        {
                            _ui?.AddLog ($"❌ Ошибка экспорта: {ex.Message}");
                        }
                        break;

                    case "test_telegram":
                        _ui?.AddLog ("📱 Тест Telegram...");
                        if (_testTelegram != null)
                        {
                            bool ok = await _testTelegram ();
                            _ui?.AddLog (ok ? "✅ Telegram: тест пройден" : "❌ Telegram: тест не удался");
                        }
                        else
                        {
                            _ui?.AddLog ("⚠️ Telegram не настроен");
                        }
                        break;

                    case "close_position":
                        if (data.TryGetValue ("symbol", out var symObj))
                        {
                            string sym = symObj?.ToString ();
                            if (!string.IsNullOrEmpty (sym))
                            {
                                _ui?.AddLog ($"🔒 Закрытие позиции {sym} из дашборда...");
                                if (_closePosition != null)
                                    await _closePosition (sym);
                            }
                        }
                        break;

                    case "settings":
                        _ui?.AddLog ("⚙️ Настройки обновлены из дашборда");
                        break;
                }
            }
            catch (Exception ex)
            {
                _ui?.AddLog ($"❌ Ошибка обработки команды дашборда: {ex.Message}");
            }
        }
    }
}
