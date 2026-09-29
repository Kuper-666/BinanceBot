using BinanceBotWpf.Services;
using Xunit;

namespace BinanceBotWpf.Tests
{
    public class TradeCostsTests
    {
        [Fact]
        public void Fees_AreChargedOnBothSides()
        {
            decimal fees = TradeCosts.Fees (100m, 110m, 1m);

            Assert.Equal (0.21m, fees);
        }

        [Fact]
        public void NetPnL_IsGrossMinusFees()
        {
            decimal net = TradeCosts.NetPnL (100m, 110m, 1m);

            Assert.Equal (9.79m, net);
        }

        [Fact]
        public void NetPnLPercent_IsRelativeToCostBasis()
        {
            decimal percent = TradeCosts.NetPnLPercent (100m, 110m, 1m);

            Assert.Equal (9.79m, percent);
        }

        [Fact]
        public void NetPnL_WinnerBecomesLoser_WhenFeeExceedsGrossProfit()
        {
            decimal net = TradeCosts.NetPnL (100m, 100.15m, 1m);

            Assert.True (net < 0);
        }

        [Fact]
        public void ZeroQuantity_ProducesZero()
        {
            Assert.Equal (0m, TradeCosts.Fees (100m, 110m, 0m));
            Assert.Equal (0m, TradeCosts.NetPnL (100m, 110m, 0m));
            Assert.Equal (0m, TradeCosts.NetPnLPercent (0m, 110m, 1m));
        }
    }
}
