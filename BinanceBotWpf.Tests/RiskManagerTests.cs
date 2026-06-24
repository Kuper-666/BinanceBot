using System.Reflection;
using BinanceBotWpf.Risk;
using Xunit;

namespace BinanceBotWpf.Tests
{
    public class RiskManagerTests
    {
        private static void SetBalance (RiskManager rm, decimal balance)
        {
            typeof (RiskManager).GetProperty ("BalanceUSDC", BindingFlags.Public | BindingFlags.Instance)
                ?.SetValue (rm, balance);
        }

        [Fact]
        public void CanOpenPosition_BelowMaxOpenOrders_ReturnsAllowed ()
        {
            var rm = new RiskManager ();
            SetBalance (rm, 1000m);
            var (allowed, reason) = rm.CanOpenPosition (currentOpenPositions: 2, orderValueUsdc: 50m);

            Assert.True (allowed);
            Assert.Equal ("OK", reason);
        }

        [Fact]
        public void CanOpenPosition_AtMaxOpenOrders_ReturnsBlocked ()
        {
            var rm = new RiskManager ();
            SetBalance (rm, 1000m);
            rm.MaxOpenOrders = 3;
            var (allowed, reason) = rm.CanOpenPosition (currentOpenPositions: 3, orderValueUsdc: 50m);

            Assert.False (allowed);
            Assert.Contains ("лимит", reason);
        }

        [Fact]
        public void CanOpenPosition_ExceedsMaxDailyLoss_ReturnsBlocked ()
        {
            var rm = new RiskManager ();
            SetBalance (rm, 500m);
            rm.MaxDailyLossPercent = 0.10m;

            rm.RecordTrade (-30m);
            rm.RecordTrade (-25m);

            var (allowed, reason) = rm.CanOpenPosition (currentOpenPositions: 0, orderValueUsdc: 50m);

            Assert.False (allowed);
            Assert.Contains ("Дневной убыток", reason);
        }

        [Fact]
        public void CanOpenPosition_DailyProfit_DoesNotBlock ()
        {
            var rm = new RiskManager ();
            SetBalance (rm, 1000m);
            rm.RecordTrade (10m);
            rm.RecordTrade (5m);

            var (allowed, _) = rm.CanOpenPosition (currentOpenPositions: 0, orderValueUsdc: 50m);

            Assert.True (allowed);
        }

        [Fact]
        public void RecordTrade_AccumulatesPnL ()
        {
            var rm = new RiskManager ();
            rm.RecordTrade (10m);
            rm.RecordTrade (-3m);
            rm.RecordTrade (5m);

            Assert.Equal (12m, rm.DailyPnL);
        }

        [Fact]
        public void CanOpenPosition_MaxExposure_ReturnsBlocked ()
        {
            var rm = new RiskManager ();
            SetBalance (rm, 1000m);
            rm.MaxExposurePercent = 0.50m;

            var (allowed, reason) = rm.CanOpenPosition (currentOpenPositions: 0, orderValueUsdc: 600m);

            Assert.False (allowed);
            Assert.Contains ("Экспозиция", reason);
        }

        [Fact]
        public void CanOpenPosition_AllLimitsDisabled_ReturnsAllowed ()
        {
            var rm = new RiskManager ();
            SetBalance (rm, 10000m);
            rm.MaxOpenOrders = 100;
            rm.MaxDailyLossPercent = 1.0m;
            rm.MaxExposurePercent = 1.0m;

            var (allowed, _) = rm.CanOpenPosition (currentOpenPositions: 50, orderValueUsdc: 1000m);

            Assert.True (allowed);
        }
    }
}
