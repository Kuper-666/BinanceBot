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
                    await Task.Delay (1000); // Пауза между запросами
                }

                if (allKlines.Count < 200)
                {
                    _logger?.Invoke ($"❌ Недостаточно исторических данных: {allKlines.Count} свечей");
                    return false;
                }

                // Оптимизация параметров
                var fastPeriods = new List<int> { 5, 8, 9, 10, 13 };
                var slowPeriods = new List<int> { 13, 17, 21, 34, 50 };
                var rsiPeriods = new List<int> { 7, 14, 21 };
                var stopLosses = new List<decimal> { 0.015m, 0.02m, 0.025m, 0.03m };
                var takeProfits = new List<decimal> { 0.03m, 0.04m, 0.05m, 0.06m };

                _logger?.Invoke ("🧮 Перебор параметров...");

                var bestParams = await _backtest.OptimizeParametersAsync (
                    allKlines, fastPeriods, slowPeriods, rsiPeriods, stopLosses, takeProfits);

                if (bestParams.Count > 0)
                {
                    // Сохраняем результаты
                    await SaveOptimizedParams (bestParams);

                    // Применяем параметры
                    ApplyParameters (bestParams);

                    _logger?.Invoke ($"✅ Оптимизация завершена! Параметры сохранены.");
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
    }
}