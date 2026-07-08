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
            // Balance below MinTradableBalance (5 USDC) should block trading
            var result = await _engine.CalculateRiskAsync (
                symbol: "BTCUSDC",
                balance: 3m,
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
            // GridBot requires min ~20 USDC (2 levels * 2 sides * 5 minNotional)
            var result = await _engine.CalculateRiskAsync (
                symbol: "ETHUSDC",
                balance: 10m,
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

        [Fact]
        public async System.Threading.Tasks.Task CalculateRiskAsync_ZeroBalance_ReturnsMinimalRisk ()
        {
            var result = await _engine.CalculateRiskAsync (
                symbol: "BTCUSDC",
                balance: 0m,
                price: 100000m,
                fastSma: 99000m,
                slowSma: 98000m,
                rsi: 50m,
                volumeRatio: 1.0m,
                macdHist: 0m,
                bbWidth: 0.03m,
                obv: 0m);

            Assert.Equal (0, result.Grid.Levels);
        }

        [Fact]
        public async System.Threading.Tasks.Task CalculateRiskAsync_ExtremeRSI_StillReturnsValid ()
        {
            var result = await _engine.CalculateRiskAsync (
                symbol: "ETHUSDC",
                balance: 500m,
                price: 3000m,
                fastSma: 3100m,
                slowSma: 2900m,
                rsi: 95m,
                volumeRatio: 2.0m,
                macdHist: 0.01m,
                bbWidth: 0.05m,
                obv: 1000m);

            Assert.True (result.StopLossPercent > 0);
            Assert.True (result.TakeProfitPercent > result.StopLossPercent);
        }

        [Fact]
        public async System.Threading.Tasks.Task CalculateRiskAsync_VeryLowBalance_GridDisabled ()
        {
            var result = await _engine.CalculateRiskAsync (
                symbol: "DOGEUSDC",
                balance: 1m,
                price: 0.15m,
                fastSma: 0.14m,
                slowSma: 0.13m,
                rsi: 30m,
                volumeRatio: 0.5m,
                macdHist: -0.001m,
                bbWidth: 0.02m,
                obv: -100m);

            Assert.Equal (0, result.Grid.Levels);
            Assert.True (result.RiskPerTradePercent > 0);
        }

        [Fact]
        public async System.Threading.Tasks.Task CalculateRiskAsync_NegativeMACD_StillReturnsValid ()
        {
            var result = await _engine.CalculateRiskAsync (
                symbol: "SOLUSDC",
                balance: 200m,
                price: 150m,
                fastSma: 155m,
                slowSma: 160m,
                rsi: 35m,
                volumeRatio: 0.8m,
                macdHist: -0.5m,
                bbWidth: 0.04m,
                obv: -500m);

            Assert.True (result.StopLossPercent > 0);
            Assert.True (result.TakeProfitPercent > 0);
        }
    }
}
