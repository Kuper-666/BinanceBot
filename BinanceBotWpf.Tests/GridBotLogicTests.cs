using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using BinanceBotWpf.Exchange;
using BinanceBotWpf.Services;
using Moq;
using Xunit;

namespace BinanceBotWpf.Tests
{
    public class GridBotLogicTests
    {
        private static readonly string[] _testPairs = { "DOGEUSDC", "XRPUSDC", "ADAUSDC", "SOLUSDC" };

        [Theory]
        [InlineData(0.074120, 20.0, 1.0, 269)]
        [InlineData(0.074120, 10.0, 1.0, 134)]
        [InlineData(0.074120, 40.0, 1.0, 539)]
        [InlineData(1.0, 100.0, 10.0, 100)]
        [InlineData(2.5, 50.0, 5.0, 20)]
        public void CalculateQuantity_RoundsCorrectly (decimal price, decimal usdAmount, decimal stepSize, decimal expected)
        {
            decimal raw = usdAmount / price;
            decimal adjusted = Math.Floor (raw / stepSize) * stepSize;

            Assert.Equal (expected, adjusted);
            Assert.Equal (0, adjusted % stepSize);
        }

        [Theory]
        [InlineData(0.0772, 0.0001, 772)]
        [InlineData(0.0772, 0.001, 77)]
        [InlineData(0.0772, 0.01, 8)]
        [InlineData(46510.24, 0.01, 4651024)]
        public void TickSize_Rounding_ProducesValidPrice (decimal price, decimal tickSize, decimal expectedTicks)
        {
            decimal rounded = Math.Round (price / tickSize) * tickSize;
            decimal ticks = rounded / tickSize;

            Assert.Equal (expectedTicks, ticks);
            Assert.Equal (0, rounded % tickSize);
        }

        [Fact]
        public void GridLevels_BuyBelowCenter_SellAboveCenter ()
        {
            decimal centerPrice = 0.0772m;
            decimal stepPercent = 0.03m / 2;
            int gridLevels = 2;

            decimal[] buyLevels = new decimal[gridLevels];
            decimal[] sellLevels = new decimal[gridLevels];

            for (int i = 0; i < gridLevels; i++)
            {
                buyLevels[i] = centerPrice * (1 - stepPercent * (i + 1));
                sellLevels[i] = centerPrice * (1 + stepPercent * (i + 1));
            }

            foreach (decimal buyLevel in buyLevels)
                Assert.True (buyLevel < centerPrice, $"Buy level {buyLevel} should be below center {centerPrice}");

            foreach (decimal sellLevel in sellLevels)
                Assert.True (sellLevel > centerPrice, $"Sell level {sellLevel} should be above center {centerPrice}");

            for (int i = 1; i < gridLevels; i++)
            {
                Assert.True (buyLevels[i] < buyLevels[i - 1], "Buy levels should decrease (further from center)");
                Assert.True (sellLevels[i] > sellLevels[i - 1], "Sell levels should increase (further from center)");
            }
        }

        [Fact]
        public void GridLevels_MonotonicallySymmetric ()
        {
            decimal centerPrice = 0.0772m;
            decimal stepPercent = 0.03m / 4;
            int gridLevels = 4;

            decimal[] buyLevels = new decimal[gridLevels];
            decimal[] sellLevels = new decimal[gridLevels];

            for (int i = 0; i < gridLevels; i++)
            {
                buyLevels[i] = centerPrice * (1 - stepPercent * (i + 1));
                sellLevels[i] = centerPrice * (1 + stepPercent * (i + 1));
            }

            for (int i = 0; i < gridLevels; i++)
            {
                decimal buyDist = centerPrice - buyLevels[i];
                decimal sellDist = sellLevels[i] - centerPrice;
                Assert.Equal (Math.Round (buyDist, 10), Math.Round (sellDist, 10));
            }
        }

        [Fact]
        public void AutoAdjust_ReducesLevels_WhenPerLevelBelowMinNotional ()
        {
            decimal totalInvestment = 10.0m;
            int gridLevels = 4;
            decimal minNotional = 5.0m;

            decimal perLevelUsdc = totalInvestment / (gridLevels * 2);
            Assert.Equal (1.25m, perLevelUsdc);

            while (perLevelUsdc < minNotional && gridLevels > 1)
            {
                gridLevels--;
                perLevelUsdc = totalInvestment / (gridLevels * 2);
            }

            Assert.Equal (1, gridLevels);
            Assert.Equal (5.0m, perLevelUsdc);
        }

        [Fact]
        public void AutoAdjust_IncreasesInvestment_WhenOneLevelStillBelowMin ()
        {
            decimal totalInvestment = 2.0m;
            int gridLevels = 1;
            decimal minNotional = 5.0m;

            decimal perLevelUsdc = totalInvestment / (gridLevels * 2);
            Assert.Equal (1.0m, perLevelUsdc);

            if (perLevelUsdc < minNotional)
            {
                perLevelUsdc = minNotional;
                totalInvestment = perLevelUsdc * 2;
            }

            Assert.Equal (5.0m, perLevelUsdc);
            Assert.Equal (10.0m, totalInvestment);
        }

        [Fact]
        public void PerLevelUsdc_Calculation ()
        {
            decimal totalInvestment = 11.0m;
            int gridLevels = 1;
            decimal perLevelUsdc = totalInvestment / (gridLevels * 2);

            Assert.Equal (5.5m, perLevelUsdc);
        }

        [Theory]
        [InlineData(5.43, 5.0, true)]
        [InlineData(4.99, 5.0, false)]
        [InlineData(5.0, 5.0, true)]
        [InlineData(10.0, 5.0, true)]
        public void Notional_Check_CorrectlyFilterOrders (decimal notional, decimal minNotional, bool shouldPass)
        {
            bool passes = notional >= minNotional;
            Assert.Equal (shouldPass, passes);
        }
    }
}
