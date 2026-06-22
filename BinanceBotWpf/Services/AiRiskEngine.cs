using System;
using System.Threading.Tasks;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// ИИ-движок риска: автоматически рассчитывает параметры риска и сетки
    /// на основе предсказаний ML-модели, волатильности и баланса.
    /// </summary>
    public class AiRiskEngine
    {
        private readonly MlModelManager _mlManager;
        private readonly BinanceClient _client;
        private readonly Action<string> _logger;

        public AiRiskEngine(MlModelManager mlManager, BinanceClient client, Action<string> logger)
        {
            _mlManager = mlManager;
            _client = client;
            _logger = logger;
        }

        /// <summary>
        /// Полный расчёт параметров риска на основе ИИ.
        /// Возвращает динамические параметры для сделки.
        /// </summary>
        public async Task<AiRiskResult> CalculateRiskAsync(string symbol, decimal balance, decimal price,
            decimal fastSma, decimal slowSma, decimal rsi, decimal volumeRatio, decimal macdHist, decimal bbWidth, decimal obv)
        {
            var result = new AiRiskResult ();

            // 1. Получаем предсказание ML
            decimal atr = 0;
            try { atr = await _client.GetATRAsync (symbol, 14); } catch { }
            if (atr <= 0) atr = price * 0.02m;

            var prediction = _mlManager.PredictRisk (fastSma, slowSma, rsi, volumeRatio, atr, macdHist, bbWidth, obv);
            int aiRiskLevel = prediction.RiskLevel == "Low Risk" ? 1 : (prediction.RiskLevel == "Medium Risk" ? 2 : 3);

            // 2. Волатильность市场的
            decimal volatility = bbWidth > 0 ? bbWidth : 0.05m;
            volatility = Math.Clamp (volatility, 0.005m, 0.30m);

            // 3. Базовый риск от баланса: больше баланс → консервативнее
            decimal balanceFactor = CalculateBalanceFactor (balance);

            // 4. Риск от ИИ: высокая уверенность → больше риска, низкая → меньше
            decimal aiFactor = CalculateAiFactor (prediction.Probability, aiRiskLevel);

            // 5. Риск от волатильности: высокая волатильность → меньше риска
            decimal volatilityFactor = CalculateVolatilityFactor (volatility);

            // 6. Итоговый риск: 1% * баланс * ИИ * волатильность
            decimal baseRiskPercent = 0.01m; // 1% базовый
            result.RiskPerTradePercent = Math.Clamp (
                baseRiskPercent * balanceFactor * aiFactor * volatilityFactor,
                0.003m,  // Минимум 0.3%
                0.02m    // Максимум 2%
            );

            decimal prob = (decimal)prediction.Probability;

            // R/R Ratio от ИИ: высокая уверенность → можно ставить больше
            result.RiskRewardRatio = Math.Clamp (
                2.0m + (prob - 0.5m) * 4m, // 2.0 при 0.5, до 4.0 при 1.0
                1.5m,
                4.0m
            );

            // 8. SL от ATR
            decimal slDistance = atr > 0 && atr / price < 0.15m ? atr * 1.5m : price * 0.015m;
            result.StopLossPercent = slDistance / price;
            result.TakeProfitPercent = result.StopLossPercent * result.RiskRewardRatio;

            // 9. Параметры сетки от баланса и волатильности
            result.Grid = CalculateGridParameters (balance, volatility, atr, price, aiRiskLevel);

            // 10. Лог
            _logger?.Invoke ($"🤖 ИИ Риск: {prediction.RiskLevel} ({prediction.Probability:P0})");
            _logger?.Invoke ($"   Риск/сделку: {result.RiskPerTradePercent:P2} ({balance * result.RiskPerTradePercent:F2} USDC)");
            _logger?.Invoke ($"   R/R: 1:{result.RiskRewardRatio:F1}, SL: {result.StopLossPercent:P2}, TP: {result.TakeProfitPercent:P2}");
            _logger?.Invoke ($"   Сетка: ±{result.Grid.RangePercent:P0}, {result.Grid.Levels} уровней, {result.Grid.InvestmentPercent:P0} капитала");

            return result;
        }

        /// <summary>
        /// Баланс-фактор: больше баланс → ниже процент (крупные суммы консервативнее)
        /// </summary>
        private decimal CalculateBalanceFactor(decimal balance)
        {
            if (balance < 50) return 1.0m;      // Маленький баланс — полный риск
            if (balance < 200) return 0.9m;     // Средний
            if (balance < 1000) return 0.8m;    // Крупный
            if (balance < 5000) return 0.7m;    // Очень крупный
            return 0.6m;                         // Максимальный — максимально консервативно
        }

        /// <summary>
        /// ИИ-фактор: высокая уверенность прибыли → больше риска
        /// </summary>
        private decimal CalculateAiFactor(float probability, int riskLevel)
        {
            decimal prob = (decimal)probability;

            // Low Risk (1) + высокая вероятность → агрессивнее
            if (riskLevel == 1 && prob > 0.8m) return 1.3m;
            if (riskLevel == 1) return 1.1m;

            // Medium Risk (2) — стандартный множитель
            if (riskLevel == 2 && prob > 0.7m) return 1.0m;
            if (riskLevel == 2) return 0.85m;

            // High Risk (3) — консервативно
            if (riskLevel == 3 && prob < 0.5m) return 0.4m;
            return 0.6m;
        }

        /// <summary>
        /// Фактор волатильности: высокая волатильность → меньше риска
        /// </summary>
        private decimal CalculateVolatilityFactor(decimal volatility)
        {
            // Нормальная волатильность 2-5% — полный риск
            if (volatility <= 0.05m) return 1.0m;
            // 5-10% — снижаем
            if (volatility <= 0.10m) return 0.8m;
            // 10-15% — значительно снижаем
            if (volatility <= 0.15m) return 0.6m;
            // >15% — минимальный риск
            return 0.4m;
        }

        /// <summary>
        /// Автоматический расчёт параметров сетки на основе баланса и волатильности
        /// </summary>
        private GridParameters CalculateGridParameters(decimal balance, decimal volatility, decimal atr, decimal price, int aiRiskLevel)
        {
            var grid = new GridParameters ();
            decimal minNotional = 6m; // Минимальный нотионал Binance

            // Инвестиции в сетку: от баланса
            if (balance < 100)
                grid.InvestmentPercent = 0.15m;
            else if (balance < 500)
                grid.InvestmentPercent = 0.20m;
            else
                grid.InvestmentPercent = 0.25m;

            // Снижаем инвестиции при высоком ИИ-риске
            if (aiRiskLevel == 3) grid.InvestmentPercent *= 0.6m;
            else if (aiRiskLevel == 2) grid.InvestmentPercent *= 0.8m;

            grid.InvestmentPercent = Math.Clamp (grid.InvestmentPercent, 0.10m, 0.30m);

            decimal investmentUsdc = balance * grid.InvestmentPercent;

            // Диапазон сетки: от волатильности
            if (volatility <= 0.03m)
                grid.RangePercent = 0.05m;
            else if (volatility <= 0.06m)
                grid.RangePercent = 0.08m;
            else if (volatility <= 0.10m)
                grid.RangePercent = 0.12m;
            else
                grid.RangePercent = 0.15m;

            // Количество уровней: рассчитываем чтобы каждый ордер был >= minNotional
            // 2 ордера на уровень (buy + sell), значит总投资 / (уровни * 2) >= minNotional
            int maxLevels = Math.Max (1, (int)(investmentUsdc / (minNotional * 2)));
            grid.Levels = Math.Clamp (maxLevels, 1, 15);

            // Если инвестиций не хватает даже на 1 уровень — подтягиваем до минимума
            decimal perLevel = investmentUsdc / (grid.Levels * 2);
            if (perLevel < minNotional)
            {
                investmentUsdc = minNotional * 2 * grid.Levels; // минимум на каждый buy+sell
                grid.InvestmentPercent = investmentUsdc / balance;
                grid.InvestmentPercent = Math.Clamp (grid.InvestmentPercent, 0.10m, 0.50m);
                _logger?.Invoke ($"⚠️ Автоподстройка: инвестиции увеличены до {grid.InvestmentPercent:P0} ({investmentUsdc:F2} USDC) для покрытия минимума");
            }

            grid.UseDynamicStep = atr > 0 && volatility > 0.03m;

            _logger?.Invoke ($"🔲 Авто-сетка: баланс={balance:F2}, инвестиции={grid.InvestmentPercent:P0} ({balance * grid.InvestmentPercent:F2} USDC), уровней={grid.Levels}, на уровень={balance * grid.InvestmentPercent / (grid.Levels * 2):F2} USDC");

            return grid;
        }
    }

    /// <summary>
    /// Результат расчёта ИИ-риска
    /// </summary>
    public class AiRiskResult
    {
        public decimal RiskPerTradePercent { get; set; } = 0.01m;
        public decimal RiskRewardRatio { get; set; } = 3.0m;
        public decimal StopLossPercent { get; set; } = 0.015m;
        public decimal TakeProfitPercent { get; set; } = 0.045m;
        public GridParameters Grid { get; set; } = new GridParameters ();
    }

    /// <summary>
    /// Параметры автоматической сетки
    /// </summary>
    public class GridParameters
    {
        public decimal RangePercent { get; set; } = 0.10m;     // ±10%
        public int Levels { get; set; } = 10;                  // 10 уровней
        public decimal InvestmentPercent { get; set; } = 0.20m; // 20% капитала
        public bool UseDynamicStep { get; set; } = false;      // ATR-based шаг
    }
}
