using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BinanceBotWpf.Models;
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
        private readonly TradingSettings _tradingSettings;

        public DashboardCommandHandler (
            MainWindowViewModel ui,
            Func<bool> isRunning,
            Func<Task> stopTrading,
            Func<MainWindowViewModel, Task> startTrading,
            Func<Task> runBacktest = null,
            Func<Task> runOptimization = null,
            Func<Task<bool>> testTelegram = null,
            Func<string, Task> closePosition = null,
            TradingSettings tradingSettings = null)
        {
            _ui = ui;
            _isRunning = isRunning;
            _stopTrading = stopTrading;
            _startTrading = startTrading;
            _runBacktest = runBacktest;
            _runOptimization = runOptimization;
            _testTelegram = testTelegram;
            _closePosition = closePosition;
            _tradingSettings = tradingSettings;
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
                        if (_tradingSettings != null && data.ContainsKey ("strategy"))
                        {
                            try
                            {
                                int applied = 0;
                                if (data["strategy"] is Dictionary<string, object> strat)
                                {
                                    if (strat.TryGetValue ("fastSma", out var v) && v is long fastSma) { _tradingSettings.FastSmaPeriod = (int)fastSma; applied++; }
                                    if (strat.TryGetValue ("slowSma", out v) && v is long slowSma) { _tradingSettings.SlowSmaPeriod = (int)slowSma; applied++; }
                                    if (strat.TryGetValue ("rsiPeriod", out v) && v is long rsiPeriod) { _tradingSettings.RsiPeriod = (int)rsiPeriod; applied++; }
                                    if (strat.TryGetValue ("stopLoss", out v) && v is double stopLoss) { _tradingSettings.StopLossPercent = (decimal)(stopLoss / 100.0); applied++; }
                                    if (strat.TryGetValue ("takeProfit", out v) && v is double takeProfit) { _tradingSettings.TakeProfitPercent = (decimal)(takeProfit / 100.0); applied++; }
                                    if (strat.TryGetValue ("riskPerTrade", out v) && v is double riskPerTrade) { _tradingSettings.RiskPerTradePercent = (decimal)(riskPerTrade / 100.0); applied++; }
                                    if (strat.TryGetValue ("maxPositions", out v) && v is long maxPositions) { _tradingSettings.MaxConcurrentTrades = (int)maxPositions; applied++; }
                                    if (strat.TryGetValue ("leverage", out v) && v is long leverage) { _tradingSettings.FuturesLeverage = (int)leverage; applied++; }
                                }
                                if (data["echelons"] is Dictionary<string, object> ech)
                                {
                                    if (ech.TryGetValue ("adaptiveAgent", out var v) && v is bool adaptiveAgent) { _tradingSettings.AdaptiveAgentEnabled = adaptiveAgent; applied++; }
                                    if (ech.TryGetValue ("adaptiveSlMult", out v) && v is double slMult) { _tradingSettings.AdaptiveSlMultiplier = (decimal)slMult; applied++; }
                                    if (ech.TryGetValue ("adaptivePeriodMult", out v) && v is double periodMult) { _tradingSettings.AdaptivePeriodMultiplier = (decimal)periodMult; applied++; }
                                    if (ech.TryGetValue ("signalValidator", out v) && v is bool signalValidator) { _tradingSettings.SignalValidatorEnabled = signalValidator; applied++; }
                                    if (ech.TryGetValue ("validatorVolThreshold", out v) && v is double volThreshold) { _tradingSettings.ValidatorVolumeThreshold = (decimal)volThreshold; applied++; }
                                    if (ech.TryGetValue ("validatorAtrThreshold", out v) && v is double atrThreshold) { _tradingSettings.ValidatorAtrThreshold = (decimal)atrThreshold; applied++; }
                                    if (ech.TryGetValue ("validatorRsiLow", out v) && v is long rsiLow) { _tradingSettings.ValidatorRsiLow = (int)rsiLow; applied++; }
                                    if (ech.TryGetValue ("validatorRsiHigh", out v) && v is long rsiHigh) { _tradingSettings.ValidatorRsiHigh = (int)rsiHigh; applied++; }
                                    if (ech.TryGetValue ("newsSentinel", out v) && v is bool newsSentinel) { _tradingSettings.NewsSentinelEnabled = newsSentinel; applied++; }
                                    if (ech.TryGetValue ("newsBlockMinutes", out v) && v is long blockMinutes) { _tradingSettings.NewsSentinelBlockMinutes = (int)blockMinutes; applied++; }
                                }
                                if (data["gridBot"] is Dictionary<string, object> grid)
                                {
                                    if (grid.TryGetValue ("enabled", out var v) && v is bool gridEnabled) { _tradingSettings.GridBotEnabled = gridEnabled; applied++; }
                                    if (grid.TryGetValue ("levels", out v) && v is long levels) { _tradingSettings.GridLevels = (int)levels; applied++; }
                                    if (grid.TryGetValue ("rangePercent", out v) && v is double rangePercent) { _tradingSettings.GridRangePercent = (decimal)(rangePercent / 100.0); applied++; }
                                    if (grid.TryGetValue ("investmentPercent", out v) && v is double investPercent) { _tradingSettings.TotalInvestmentPercent = (decimal)(investPercent / 100.0); applied++; }
                                    if (grid.TryGetValue ("defaultPairs", out v) && v is string gridSymbol) { _tradingSettings.GridSymbol = gridSymbol; applied++; }
                                }
                                if (data.ContainsKey ("tradingView") && data["tradingView"] is Dictionary<string, object> tv)
                                {
                                    if (tv.TryGetValue ("enabled", out var v) && v is bool tvEnabled) { _tradingSettings.TradingViewEnabled = tvEnabled; applied++; }
                                    if (tv.TryGetValue ("secret", out v) && v is string tvSecret) { _tradingSettings.TradingViewSecret = tvSecret; applied++; }
                                }
                                _ = _tradingSettings.SaveAsync ();
                                _ui?.AddLog ($"⚙️ Настройки обновлены из дашборда ({applied} параметров)");
                            }
                            catch (Exception ex)
                            {
                                _ui?.AddLog ($"❌ Ошибка применения настроек: {ex.Message}");
                            }
                        }
                        else
                        {
                            _ui?.AddLog ("⚙️ Настройки обновлены из дашборда");
                        }
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
