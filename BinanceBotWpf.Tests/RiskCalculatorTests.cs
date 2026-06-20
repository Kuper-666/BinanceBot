using BinanceBotWpf.Services;
using Xunit;

namespace BinanceBotWpf.Tests
{
    public class RiskCalculatorTests
    {
        [Fact]
        public async System.Threading.Tasks.Task CalculateDynamicRiskAsync_NeverBelowTwoPercent()
        {
            var calc = new RiskCalculator (client: null, ui: null, logger: null);

            decimal risk = await calc.CalculateDynamicRiskAsync (totalBalance: 1000m, baseRisk: 0.10m, volatility: 0.30m, aiRiskLevel: 3);

            // Math.Clamp ограничивает риск снизу 2% даже при максимальной волатильности и высоком ИИ-риске
            Assert.True (risk >= 1000m * 0.02m);
        }

        [Fact]
        public async System.Threading.Tasks.Task CalculateDynamicRiskAsync_NeverAboveTwentyFivePercent()
        {
            var calc = new RiskCalculator (client: null, ui: null, logger: null);

            decimal risk = await calc.CalculateDynamicRiskAsync (totalBalance: 1000m, baseRisk: 0.10m, volatility: 0.005m, aiRiskLevel: 1);

            Assert.True (risk <= 1000m * 0.25m);
        }

        [Fact]
        public async System.Threading.Tasks.Task CalculateDynamicRiskAsync_HighAiRisk_ReducesRiskComparedToMedium()
        {
            var calc = new RiskCalculator (client: null, ui: null, logger: null);

            decimal riskMedium = await calc.CalculateDynamicRiskAsync (1000m, 0.10m, 0.05m, aiRiskLevel: 2);
            decimal riskHigh = await calc.CalculateDynamicRiskAsync (1000m, 0.10m, 0.05m, aiRiskLevel: 3);

            Assert.True (riskHigh <= riskMedium, "Высокий ИИ-риск должен снижать или сохранять размер позиции, но не увеличивать его");
        }

        // --- Регрессионные тесты на баг "бот видит сигнал BUY, но молча ничего не покупает" ---

        [Fact]
        public void CalculatePositionQuantity_SmallBalance_RaisesToMinNotional_InsteadOfSilentlyFailing()
        {
            // Воспроизводит реальный сценарий пользователя: баланс 73 USDC, риск 2% = 1.46 USDC,
            // что ниже минимального ордера на бирже. Раньше это приводило к молчаливому отказу.
            decimal riskAmount = 73m * 0.02m; // 1.46
            decimal price = 63483.61m; // BTCUSDC
            decimal stepSize = 0.00001m;
            decimal balance = 73m;

            var (qty, result) = RiskCalculator.CalculatePositionQuantity (riskAmount, price, stepSize, balance);

            Assert.Equal (RiskCalculator.QuantityResult.Ok, result);
            Assert.True (qty > 0, "Количество должно быть поднято до минимального ордера, а не остаться нулевым");
            Assert.True (qty * price >= 5m, "Итоговая стоимость ордера должна покрывать минимальный notional биржи");
            Assert.True (qty * price <= balance, "Ордер не должен превышать доступный баланс");
        }

        [Fact]
        public void CalculatePositionQuantity_BalanceBelowMinNotional_ReturnsInsufficientBalance()
        {
            // Если даже весь баланс меньше минимального ордера — покупка невозможна физически,
            // это должно быть явно отражено в результате, а не приводить к попытке купить на 0.
            decimal riskAmount = 0.05m;
            decimal price = 63483.61m;
            decimal stepSize = 0.00001m;
            decimal balance = 3m; // меньше минимального notional

            var (qty, result) = RiskCalculator.CalculatePositionQuantity (riskAmount, price, stepSize, balance);

            Assert.Equal (RiskCalculator.QuantityResult.InsufficientBalanceForMinNotional, result);
            Assert.Equal (0m, qty);
        }

        [Fact]
        public void CalculatePositionQuantity_NormalRiskAboveMinNotional_UsesRiskAmountAsIs()
        {
            // Когда расчётный риск уже выше минимального ордера, сумма не должна искусственно завышаться
            decimal riskAmount = 50m;
            decimal price = 100m;
            decimal stepSize = 0.001m;
            decimal balance = 1000m;

            var (qty, result) = RiskCalculator.CalculatePositionQuantity (riskAmount, price, stepSize, balance);

            Assert.Equal (RiskCalculator.QuantityResult.Ok, result);
            // qty * price должно быть близко к 50, а не к минимальному notional (6)
            Assert.True (qty * price >= 49m && qty * price <= 50m);
        }

        [Fact]
        public void CalculatePositionQuantity_QuantityExceedsBalance_ReturnsExceedsAvailableBalance()
        {
            // Риск формально посчитан, но из-за округления по stepSize итоговая сумма
            // оказывается больше доступного баланса
            decimal riskAmount = 99.999m;
            decimal price = 100m;
            decimal stepSize = 1m; // грубый шаг лота вынуждает округлить вверх при минимальном notional
            decimal balance = 50m; // баланс меньше even minNotional требований после округления

            var (qty, result) = RiskCalculator.CalculatePositionQuantity (riskAmount, price, stepSize, balance, minNotional: 60m);

            Assert.Equal (RiskCalculator.QuantityResult.InsufficientBalanceForMinNotional, result);
            Assert.Equal (0m, qty);
        }

        [Fact]
        public void CalculatePositionQuantity_ZeroPrice_DoesNotThrow_ReturnsZeroQuantity()
        {
            var (qty, result) = RiskCalculator.CalculatePositionQuantity (riskAmount: 10m, price: 0m, stepSize: 0.001m, currentBalance: 100m);

            Assert.Equal (0m, qty);
            Assert.Equal (RiskCalculator.QuantityResult.ZeroQuantityAfterRounding, result);
        }

        [Fact]
        public void CalculatePositionQuantity_ZeroStepSize_DoesNotThrow_ReturnsZeroQuantity()
        {
            // Защита от деления на ноль, если GetStepSizeAsync вернёт 0 из-за сбоя API
            var (qty, result) = RiskCalculator.CalculatePositionQuantity (riskAmount: 10m, price: 100m, stepSize: 0m, currentBalance: 100m);

            Assert.Equal (0m, qty);
            Assert.Equal (RiskCalculator.QuantityResult.ZeroQuantityAfterRounding, result);
        }

        [Fact]
        public void CalculatePositionQuantity_ResultQuantity_IsAlwaysMultipleOfStepSize()
        {
            decimal riskAmount = 25m;
            decimal price = 37.41m;
            decimal stepSize = 0.01m;
            decimal balance = 200m;

            var (qty, result) = RiskCalculator.CalculatePositionQuantity (riskAmount, price, stepSize, balance);

            Assert.Equal (RiskCalculator.QuantityResult.Ok, result);
            decimal remainder = qty % stepSize;
            // Допуск на погрешность decimal-арифметики
            Assert.True (remainder < 0.0000001m || stepSize - remainder < 0.0000001m,
                $"Количество {qty} не кратно шагу лота {stepSize}");
        }
    }
}
