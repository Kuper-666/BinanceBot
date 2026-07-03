using System;
using BinanceBotWpf.Services;
using Xunit;

namespace BinanceBotWpf.Tests
{
    public class WebSocketPriceManagerTests
    {
        [Fact]
        public void IsPriceFresh_NoData_ReturnsFalse ()
        {
            var wpm = new WebSocketPriceManager (msg => { });
            Assert.False (wpm.IsPriceFresh ("DOGEUSDC"));
        }

        [Fact]
        public void IsPriceFresh_JustUpdated_ReturnsTrue ()
        {
            var wpm = new WebSocketPriceManager (msg => { });
            wpm.UpdatePrice ("DOGEUSDC", 0.10m);
            Assert.True (wpm.IsPriceFresh ("DOGEUSDC"));
        }

        [Fact]
        public void GetPriceAgeSeconds_NoData_ReturnsMinusOne ()
        {
            var wpm = new WebSocketPriceManager (msg => { });
            Assert.Equal (-1, wpm.GetPriceAgeSeconds ("DOGEUSDC"));
        }

        [Fact]
        public void GetPriceAgeSeconds_JustUpdated_ReturnsSmall ()
        {
            var wpm = new WebSocketPriceManager (msg => { });
            wpm.UpdatePrice ("DOGEUSDC", 0.10m);
            double age = wpm.GetPriceAgeSeconds ("DOGEUSDC");
            Assert.True (age >= 0 && age < 1);
        }

        [Fact]
        public void UpdatePrice_StoresCorrectly ()
        {
            var wpm = new WebSocketPriceManager (msg => { });
            wpm.UpdatePrice ("DOGEUSDC", 0.1234m);
            decimal price = wpm.GetCurrentPrice ("DOGEUSDC");
            Assert.Equal (0.1234m, price);
        }

        [Fact]
        public void GetStaleSymbols_WithFreshData_ReturnsEmpty ()
        {
            var wpm = new WebSocketPriceManager (msg => { });
            wpm.MaxPriceAgeSeconds = 30;
            wpm.UpdatePrice ("DOGEUSDC", 0.10m);

            string[] stale = wpm.GetStaleSymbols ();
            Assert.Empty (stale);
        }

        [Fact]
        public void GetCurrentPrice_UnknownSymbol_ReturnsZero ()
        {
            var wpm = new WebSocketPriceManager (msg => { });
            Assert.Equal (0m, wpm.GetCurrentPrice ("NONEXISTENT"));
        }
    }
}
