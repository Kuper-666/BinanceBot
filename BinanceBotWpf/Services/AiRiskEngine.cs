using System;
using System.Threading.Tasks;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// ИИ-движок риска: автоматически рассчитывает параметры риска и сетки
    /// на основе предсказаний ML-модели, волатильности и баланса.
    /// </summary>
    public class AiRiskEngine : IAiRiskEngine
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

            // 0. Hard-stop: недостаточно средств для торговли (защита от слива малого депозита).
            //    При балансе ниже минимума возвращаем минимальный риск — сделка не откроется.
            const decimal MinTradableBalance = 5m;
            if (balance < MinTradableBalance)
            {
                result.RiskPerTradePercent = 0.003m; // минимум — фактически блокирует сделку
                result.RiskRewardRatio = 1.5m;
                result.StopLossPercent = 0.015m;
                result.TakeProfitPercent = result.StopLossPercent * result.RiskRewardRatio;
                result.Grid = CalculateGridParameters (balance, 0.05m, price * 0.02m, price, 3);
                _logger?.Invoke ($"🛑 ИИ Риск: баланс {balance:F2} USDC < минимума {MinTradableBalance} USDC — торговля блокируется");
                return result;
            }

            // 1. Получаем предсказание ML
            decimal atr = 0;
            if (_client != null) try { atr = await _client.GetATRAsync (symbol, 14); } catch { }
            if (atr <= 0) atr = price * 0.02m;

            var prediction = _mlManager != null
                ? _mlManager.PredictRisk (fastSma, slowSma, rsi, volumeRatio, atr, macdHist, bbWidth, obv)
                : (IsProfitable: true, Probability: 1.0f, RiskLevel: "Low Risk");
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

            // 6. Итоговый риск: 12% базовый (нужно для покрытия MIN_NOTIONAL при малых депозитах)
            decimal baseRiskPercent = 0.12m; // 12% базовый
            result.RiskPerTradePercent = Math.Clamp (
                baseRiskPercent * balanceFactor * aiFactor * volatilityFactor,
                0.05m,  // Минимум 5%
                0.20m   // Максимум 20%
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
        /// Баланс-фактор: меньше баланс → ниже процент (маленький депозит консервативнее).
        /// Раньше логика была инвертирована (мало денег → полный риск), что ведёт к
        /// быстрому сливу депозита, особенно с плечом. Теперь наоборот.
        /// </summary>
        private decimal CalculateBalanceFactor(decimal balance)
        {
            // Недостаточно средств для осмысленной торговли — минимальный риск
            if (balance < 50) return 0.3m;       // Маленький баланс — консервативно
            if (balance < 200) return 0.6m;      // Средний
            if (balance < 1000) return 0.8m;     // Крупный
            if (balance < 5000) return 0.9m;     // Очень крупный
            return 1.0m;                          // Максимальный — можно рисковать полнее
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
            decimal minNotional = 5m; // Default, будет перезаписан из exchangeInfo

            // GridBot требует минимум 500 USDC — иначе сетка из 1-2 уровней бессмысленна,
            // комиссии съедают всю прибыль, и баланс не покрывает minNotional на все уровни.
            const decimal MinGridBalance = 500m;
            if (balance < MinGridBalance)
            {
                grid.Levels = 0;
                grid.InvestmentPercent = 0;
                grid.RangePercent = 0;
                grid.UseDynamicStep = false;
                _logger?.Invoke ($"⛔ Сетка отключена: баланс {balance:F2} USDC < минимума {MinGridBalance} USDC");
                return grid;
            }

            // Инвестиции в сетку: от баланса
            grid.InvestmentPercent = 0.25m;

            // Снижаем инвестиции при высоком ИИ-риске
            if (aiRiskLevel == 3) grid.InvestmentPercent *= 0.6m;
            else if (aiRiskLevel == 2) grid.InvestmentPercent *= 0.8m;

            grid.InvestmentPercent = Math.Clamp (grid.InvestmentPercent, 0.10m, 0.30m);

            decimal investmentUsdc = balance * grid.InvestmentPercent;

            // Диапазон сетки: от волатильности.
            // Для малых балансов (< 200 USDC) используем узкий диапазон (1.5%–3%),
            // чтобы не раздувать ордера за пределы реальной волатильности.
            if (balance < 200)
            {
                grid.RangePercent = volatility <= 0.03m ? 0.015m : (volatility <= 0.06m ? 0.025m : 0.035m);
            }
            else if (volatility <= 0.03m)
                grid.RangePercent = 0.05m;
            else if (volatility <= 0.06m)
                grid.RangePercent = 0.08m;
            else if (volatility <= 0.10m)
                grid.RangePercent = 0.12m;
            else
                grid.RangePercent = 0.15m;

            // Количество уровней: 10–20 в каждую сторону.
            // Для 500 USDC с плечом 5x (2500 покуп. способность) — 15 уровней по ~8 USDC/ордер.
            // minNotional = 6 USDC (Binance минимум). Каждый ордер = buy + sell →总投资/(levels*2) >= minNotional.
            int maxLevelsByFunds = Math.Max (1, (int)(investmentUsdc / (minNotional * 2)));
            int minLevelsForGrid = balance < 1000 ? 10 : 15;
            grid.Levels = Math.Clamp (Math.Max (maxLevelsByFunds, minLevelsForGrid), 10, 20);

            // Если инвестиций не хватает на минимальный набор уровней — подтягиваем до минимума
            decimal perLevel = investmentUsdc / (grid.Levels * 2);
            if (perLevel < minNotional)
            {
                investmentUsdc = minNotional * 2 * grid.Levels;
                grid.InvestmentPercent = investmentUsdc / balance;
                grid.InvestmentPercent = Math.Clamp (grid.InvestmentPercent, 0.10m, 0.50m);
                _logger?.Invoke ($"⚠️ Автоподстройка: инвестиции увеличены до {grid.InvestmentPercent:P0} ({investmentUsdc:F2} USDC) для покрытия минимума ({grid.Levels} уровней)");
            }

            grid.UseDynamicStep = atr > 0 && volatility > 0.03m;

            // Корректируем диапазон чтобы stepPercent = rangePercent/levels был в 0.5%–1.5%.
            // Если шаг слишком мелкий — расширяем диапазон; если слишком крупный — сужаем.
            decimal calculatedStep = grid.RangePercent / grid.Levels;
            const decimal MinStep = 0.005m; // 0.5%
            const decimal MaxStep = 0.015m; // 1.5%
            if (calculatedStep < MinStep)
                grid.RangePercent = MinStep * grid.Levels;
            else if (calculatedStep > MaxStep)
                grid.RangePercent = MaxStep * grid.Levels;

            _logger?.Invoke ($"🔲 Авто-сетка: баланс={balance:F2}, инвестиции={grid.InvestmentPercent:P0} ({balance * grid.InvestmentPercent:F2} USDC), уровней={grid.Levels}, шаг={grid.RangePercent / grid.Levels * 100:F2}%, диапазон=±{grid.RangePercent * 100:F1}%");

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
