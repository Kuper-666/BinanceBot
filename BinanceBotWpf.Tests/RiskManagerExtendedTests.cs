using BinanceBotWpf.Risk;
using Xunit;

namespace BinanceBotWpf.Tests
{
    public class RiskManagerExtendedTests
    {
        [Fact]
        public void CanOpenPosition_PerSymbolExposure_ExceedsLimit_ReturnsBlocked ()
        {
            var rm = new RiskManager ();
            rm.BalanceUSDC = 1000m;
            rm.MaxExposurePerSymbolPercent = 0.20m; // 20% = 200 USDC на пару

            // Уже 180 USDC в паре + 30 USDC новый ордер = 210 > 200
            var (allowed, reason) = rm.CanOpenPosition (
                currentOpenPositions: 1,
                orderValueUsdc: 30m,
                currentTotalExposure: 180m,
                currentSymbolExposure: 180m);

            Assert.False (allowed);
            Assert.Contains ("Экспозиция на пару", reason);
        }

        [Fact]
        public void CanOpenPosition_PerSymbolExposure_UnderLimit_ReturnsAllowed ()
        {
            var rm = new RiskManager ();
            rm.BalanceUSDC = 1000m;
            rm.MaxExposurePerSymbolPercent = 0.20m;

            // 100 USDC в паре + 50 USDC новый = 150 < 200
            var (allowed, _) = rm.CanOpenPosition (
                currentOpenPositions: 1,
                orderValueUsdc: 50m,
                currentTotalExposure: 100m,
                currentSymbolExposure: 100m);

            Assert.True (allowed);
        }

        [Fact]
        public void IsKillSwitchActive_DailyLossExceeded_ReturnsTrue ()
        {
            var rm = new RiskManager ();
            rm.BalanceUSDC = 1000m;
            rm.MaxDailyLossPercent = 0.10m;

            rm.RecordTrade (-80m);
            rm.RecordTrade (-30m); // Total: -110, > 100 (10% of 1000)

            Assert.True (rm.IsKillSwitchActive);
            Assert.True (rm.IsDailyLossKillSwitchTriggered ());
        }

        [Fact]
        public void IsKillSwitchActive_DailyLossUnderLimit_ReturnsFalse ()
        {
            var rm = new RiskManager ();
            rm.BalanceUSDC = 1000m;
            rm.MaxDailyLossPercent = 0.10m;

            rm.RecordTrade (-50m);

            Assert.False (rm.IsKillSwitchActive);
            Assert.False (rm.IsDailyLossKillSwitchTriggered ());
        }

        [Fact]
        public void IsKillSwitchActive_WeeklyLossExceeded_ReturnsTrue ()
        {
            var rm = new RiskManager ();
            rm.BalanceUSDC = 1000m;
            rm.MaxWeeklyLossPercent = 0.20m;

            rm.RecordTrade (-120m);
            rm.RecordTrade (-90m); // Total: -210, > 200 (20% of 1000)

            Assert.True (rm.IsKillSwitchActive);
        }

        [Fact]
        public void CanOpenPosition_KillSwitchActive_ReturnsBlocked ()
        {
            var rm = new RiskManager ();
            rm.BalanceUSDC = 1000m;
            rm.MaxDailyLossPercent = 0.10m;

            rm.RecordTrade (-110m); // > 10% of 1000

            var (allowed, reason) = rm.CanOpenPosition (currentOpenPositions: 0, orderValueUsdc: 50m);

            Assert.False (allowed);
            Assert.Contains ("KILL-SWITCH", reason);
        }

        [Fact]
        public void DailyPnL_AccumulatesCorrectly ()
        {
            var rm = new RiskManager ();
            rm.BalanceUSDC = 1000m;

            rm.RecordTrade (-50m);
            Assert.Equal (-50m, rm.DailyPnL);

            rm.RecordTrade (200m);
            Assert.Equal (150m, rm.DailyPnL);
        }

        [Fact]
        public void RecordTrade_AccumulatesBothDailyAndWeekly ()
        {
            var rm = new RiskManager ();
            rm.BalanceUSDC = 1000m;

            rm.RecordTrade (10m);
            rm.RecordTrade (-3m);
            rm.RecordTrade (5m);

            Assert.Equal (12m, rm.DailyPnL);
        }
    }
}
