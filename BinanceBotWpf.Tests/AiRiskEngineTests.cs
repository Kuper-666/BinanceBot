using BinanceBotWpf.Services;
using Xunit;

namespace BinanceBotWpf.Tests
{
    public class AiRiskEngineTests
    {
        private readonly AiRiskEngine _engine;

        public AiRiskEngineTests ()
        {
            // AiRiskEngine needs MlModelManager and BinanceClient, but we can test
            // the grid parameter logic by checking the public API behavior
            _engine = new AiRiskEngine (null, null, null);
        }

        [Fact]
        public async System.Threading.Tasks.Task CalculateRiskAsync_BelowMinBalance_ReturnsMinimalRisk ()
        {
            // Balance below MinTradableBalance (50 USDC) should block trading
            var result = await _engine.CalculateRiskAsync (
                symbol: "BTCUSDC",
                balance: 30m,
                price: 100000m,
                fastSma: 99000m,
                slowSma: 98000m,
                rsi: 40m,
                volumeRatio: 1.0m,
                macdHist: 0.001m,
                bbWidth: 0.03m,
                obv: 0m);

            Assert.Equal (0.003m, result.RiskPerTradePercent);
            Assert.Equal (0, result.Grid.Levels); // Grid disabled for low balance
        }

        [Fact]
        public async System.Threading.Tasks.Task CalculateRiskAsync_GridDisabled_Below500USDC ()
        {
            // GridBot requires min 500 USDC
            var result = await _engine.CalculateRiskAsync (
                symbol: "ETHUSDC",
                balance: 200m,
                price: 3000m,
                fastSma: 2950m,
                slowSma: 2900m,
                rsi: 45m,
                volumeRatio: 1.0m,
                macdHist: 0.0005m,
                bbWidth: 0.025m,
                obv: 0m);

            Assert.Equal (0, result.Grid.Levels);
            Assert.Equal (0, result.Grid.InvestmentPercent);
        }

        [Fact]
        public async System.Threading.Tasks.Task CalculateRiskAsync_NormalBalance_GridEnabled ()
        {
            // Normal balance should have grid enabled with proper levels
            var result = await _engine.CalculateRiskAsync (
                symbol: "BTCUSDC",
                balance: 1000m,
                price: 100000m,
                fastSma: 99000m,
                slowSma: 98000m,
                rsi: 45m,
                volumeRatio: 1.0m,
                macdHist: 0.001m,
                bbWidth: 0.03m,
                obv: 0m);

            Assert.True (result.Grid.Levels >= 10, $"Expected >= 10 levels, got {result.Grid.Levels}");
            Assert.True (result.Grid.Levels <= 20, $"Expected <= 20 levels, got {result.Grid.Levels}");
            Assert.True (result.Grid.RangePercent > 0, "RangePercent should be positive");
        }

        [Fact]
        public async System.Threading.Tasks.Task CalculateRiskAsync_StepPercent_InRange ()
        {
            // stepPercent = rangePercent / levels should be in 0.5%-1.5%
            var result = await _engine.CalculateRiskAsync (
                symbol: "BTCUSDC",
                balance: 1000m,
                price: 100000m,
                fastSma: 99000m,
                slowSma: 98000m,
                rsi: 45m,
                volumeRatio: 1.0m,
                macdHist: 0.001m,
                bbWidth: 0.03m,
                obv: 0m);

            if (result.Grid.Levels > 0)
            {
                decimal stepPercent = result.Grid.RangePercent / result.Grid.Levels;
                Assert.True (stepPercent >= 0.005m && stepPercent <= 0.015m,
                    $"StepPercent {stepPercent:P2} should be between 0.5% and 1.5%");
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task CalculateRiskAsync_StopLoss_AlwaysPositive ()
        {
            var result = await _engine.CalculateRiskAsync (
                symbol: "BTCUSDC",
                balance: 500m,
                price: 100000m,
                fastSma: 99000m,
                slowSma: 98000m,
                rsi: 45m,
                volumeRatio: 1.0m,
                macdHist: 0.001m,
                bbWidth: 0.03m,
                obv: 0m);

            Assert.True (result.StopLossPercent > 0, "StopLoss must be positive");
            Assert.True (result.TakeProfitPercent > 0, "TakeProfit must be positive");
            Assert.True (result.TakeProfitPercent > result.StopLossPercent,
                "TakeProfit should be greater than StopLoss");
        }
    }
}
