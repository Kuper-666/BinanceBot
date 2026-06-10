using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BinanceBotWpf.Models;
using BinanceBotWpf.ViewModels;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Автоматическая оптимизация параметров стратегии на основе исторических данных
    /// </summary>
    public class StrategyOptimizer
    {
        private readonly BinanceClient _client;
        private readonly MainWindowViewModel _ui;
        private readonly Action<string> _logger;
        private readonly BacktestEngine _backtest;

        private readonly string _settingsPath;
        private readonly string _historyPath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Data", "optimization_history.json");

        public StrategyOptimizer(BinanceClient client, MainWindowViewModel ui, Action<string> logger)
        {
            _client = client;
            _ui = ui;
            _logger = logger;
            _backtest = new BacktestEngine (logger);
            _settingsPath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Data", "optimized_params.json");
        }

        /// <summary>
        /// Запуск оптимизации на истории топ-пар
        /// </summary>
        public async Task<bool> RunOptimizationAsync()
        {
            _logger?.Invoke ("🚀 Запуск оптимизации параметров стратегии...");

            try
            {
                // Получаем топ-5 пар по объёму
                var topPairs = await _client.GetTopVolumePairsAsync ("USDC", 5);
                if (topPairs.Count == 0)
                {
                    _logger?.Invoke ("❌ Не удалось получить список пар");
                    return false;
                }

                // Собираем исторические данные
                var allKlines = new List<BinanceKline> ();
                foreach (var pair in topPairs)
                {
                    var klines = await _client.GetKlinesAsync (pair, "5m", 500);
                    if (klines != null && klines.Count > 100)
                    {
                        allKlines.AddRange (klines);
                        _logger?.Invoke ($"📥 Загружено {klines.Count} свечей для {pair}");
                    }
                    await Task.Delay (1000);
                }

                if (allKlines.Count < 200)
                {
                    _logger?.Invoke ($"❌ Недостаточно исторических данных: {allKlines.Count} свечей");
                    return false;
                }

                // Параметры для перебора
                var fastPeriods = new List<int> { 5, 8, 9, 10, 13 };
                var slowPeriods = new List<int> { 13, 17, 21, 34, 50 };
                var rsiPeriods = new List<int> { 7, 14, 21 };
                var stopLosses = new List<decimal> { 0.015m, 0.02m, 0.025m, 0.03m };
                var takeProfits = new List<decimal> { 0.03m, 0.04m, 0.05m, 0.06m };

                _logger?.Invoke ("🧮 Перебор параметров...");

                // ОБЪЯВЛЯЕМ ПЕРЕМЕННУЮ bestResult
                BacktestEngine.BacktestResult bestResult = null;
                var bestParams = new Dictionary<string, object> ();

                int totalCombinations = fastPeriods.Count * slowPeriods.Count * rsiPeriods.Count * stopLosses.Count * takeProfits.Count;
                int current = 0;

                foreach (var fast in fastPeriods)
                {
                    foreach (var slow in slowPeriods.Where (s => s > fast))
                    {
                        foreach (var rsiP in rsiPeriods)
                        {
                            foreach (var sl in stopLosses)
                            {
                                foreach (var tp in takeProfits.Where (t => t > sl))
                                {
                                    current++;

                                    var result = await _backtest.RunAsync (allKlines, fast, slow, rsiP, sl, tp);

                                    if (result != null && ( bestResult == null || result.TotalReturn > bestResult.TotalReturn ) && result.TotalTrades >= 5)
                                    {
                                        bestResult = result;
                                        bestParams["FastSma"] = fast;
                                        bestParams["SlowSma"] = slow;
                                        bestParams["RsiPeriod"] = rsiP;
                                        bestParams["StopLossPercent"] = sl;
                                        bestParams["TakeProfitPercent"] = tp;

                                        _logger?.Invoke ($"📊 Новый лучший результат: доходность {result.TotalReturn:F2}%, win rate {result.WinRate:F1}%, сделок {result.TotalTrades}");
                                    }

                                    if (current % 50 == 0)
                                    {
                                        _logger?.Invoke ($"🔄 Оптимизация: {current}/{totalCombinations} ({current * 100 / totalCombinations}%)");
                                    }
                                }
                            }
                        }
                    }
                }

                if (bestResult != null && bestParams.Count > 0)
                {
                    await SaveOptimizationResult (bestParams, bestResult);
                    ApplyParameters (bestParams);

                    _logger?.Invoke ($"✅ Оптимизация завершена! Доходность: {bestResult.TotalReturn:F2}%");
                    return true;
                }

                _logger?.Invoke ("⚠️ Не удалось найти оптимальные параметры");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Ошибка оптимизации: {ex.Message}");
                return false;
            }
        }

        private async Task SaveOptimizedParams(Dictionary<string, object> parameters)
        {
            try
            {
                string dir = Path.GetDirectoryName (_settingsPath);
                if (!Directory.Exists (dir)) Directory.CreateDirectory (dir);

                string json = JsonSerializer.Serialize (parameters, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync (_settingsPath, json);

                _logger?.Invoke ($"💾 Параметры сохранены в {_settingsPath}");
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Ошибка сохранения: {ex.Message}");
            }
        }

        private void ApplyParameters(Dictionary<string, object> parameters)
        {
            try
            {
                if (parameters.TryGetValue ("FastSma", out var fast))
                    _ui.FastSma = Convert.ToInt32 (fast);
                if (parameters.TryGetValue ("SlowSma", out var slow))
                    _ui.SlowSma = Convert.ToInt32 (slow);
                if (parameters.TryGetValue ("RsiPeriod", out var rsiPeriod))
                    _ui.RsiBuyThreshold = Convert.ToInt32 (rsiPeriod);
                if (parameters.TryGetValue ("StopLossPercent", out var sl))
                    _ui.StopLossPercent = Convert.ToDecimal (sl);
                if (parameters.TryGetValue ("TakeProfitPercent", out var tp))
                    _ui.TakeProfitPercent = Convert.ToDecimal (tp);

                _ui.SaveSettings ();
                _logger?.Invoke ($"📈 Применены новые параметры: Fast={_ui.FastSma}, Slow={_ui.SlowSma}, SL={_ui.StopLossPercent:P0}, TP={_ui.TakeProfitPercent:P0}");
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Ошибка применения параметров: {ex.Message}");
            }
        }

        /// <summary>
        /// Загрузка сохранённых оптимизированных параметров
        /// </summary>
        public async Task<Dictionary<string, object>> LoadOptimizedParamsAsync()
        {
            if (!File.Exists (_settingsPath)) return new Dictionary<string, object> ();

            try
            {
                string json = await File.ReadAllTextAsync (_settingsPath);
                return JsonSerializer.Deserialize<Dictionary<string, object>> (json) ?? new Dictionary<string, object> ();
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"⚠️ Ошибка загрузки параметров: {ex.Message}");
                return new Dictionary<string, object> ();
            }
        }

        // Класс для записи в историю:
        public class OptimizationRecord
        {
            public DateTime Timestamp { get; set; }
            public Dictionary<string, object> Parameters { get; set; }
            public decimal TotalReturn { get; set; }
            public decimal WinRate { get; set; }
            public decimal MaxDrawdown { get; set; }
            public int TotalTrades { get; set; }
        }

        // Метод сохранения результата:
        public async Task SaveOptimizationResult(Dictionary<string, object> parameters, BacktestEngine.BacktestResult result)
        {
            try
            {
                var history = await LoadOptimizationHistoryAsync ();

                history.Insert (0, new OptimizationRecord
                {
                    Timestamp = DateTime.UtcNow,
                    Parameters = new Dictionary<string, object> (parameters),
                    TotalReturn = result.TotalReturn,
                    WinRate = result.WinRate,
                    MaxDrawdown = result.MaxDrawdown,
                    TotalTrades = result.TotalTrades
                });

                // Ограничиваем историю 50 записями
                if (history.Count > 50)
                    history.RemoveAt (history.Count - 1);

                string json = JsonSerializer.Serialize (history, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync (_historyPath, json);

                _logger?.Invoke ($"💾 Результат оптимизации сохранён в историю");
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"⚠️ Ошибка сохранения истории: {ex.Message}");
            }
        }

        // Метод загрузки истории:
        public async Task<List<OptimizationRecord>> LoadOptimizationHistoryAsync()
        {
            if (!File.Exists (_historyPath))
                return new List<OptimizationRecord> ();

            try
            {
                string json = await File.ReadAllTextAsync (_historyPath);
                return JsonSerializer.Deserialize<List<OptimizationRecord>> (json) ?? new List<OptimizationRecord> ();
            }
            catch
            {
                return new List<OptimizationRecord> ();
            }
        }

        // Метод отката к предыдущим параметрам:
        public async Task<bool> RollbackToPreviousParameters()
        {
            var history = await LoadOptimizationHistoryAsync ();
            if (history.Count < 2)
            {
                _logger?.Invoke ("⚠️ Нет предыдущих параметров для отката");
                return false;
            }

            var previous = history[1];
            ApplyParameters (previous.Parameters);

            _logger?.Invoke ($"🔄 Откат к параметрам от {previous.Timestamp:yyyy-MM-dd HH:mm:ss}");
            _logger?.Invoke ($"   Доходность была: {previous.TotalReturn:F2}%, Win Rate: {previous.WinRate:F1}%");

            return true;
        }
    }
}