using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BinanceBotWpf.Exchange;
using BinanceBotWpf.Services;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BinanceBotWpf.Tests
{
    public class GridBotOrderTests : IDisposable
    {
        private readonly string _tempFile;

        public GridBotOrderTests ()
        {
            _tempFile = Path.Combine (Path.GetTempPath (), $"gridbot_test_{Guid.NewGuid():N}.json");
        }

        public void Dispose ()
        {
            try { if (File.Exists (_tempFile)) File.Delete (_tempFile); } catch { }
        }

        [Fact]
        public async Task PlaceLimitOrder_Fails_ShouldNotAddToActiveOrders ()
        {
            var mockClient = new Mock<IBinanceFuturesClient> ();
            mockClient.Setup (c => c.GetStepSizeAsync (It.IsAny<string> ()))
                .ReturnsAsync (1.0m);
            mockClient.Setup (c => c.GetTickSizeAsync (It.IsAny<string> ()))
                .ReturnsAsync (0.0001m);
            mockClient.Setup (c => c.GetMinNotionalAsync (It.IsAny<string> ()))
                .ReturnsAsync (5.0m);
            mockClient.Setup (c => c.GetATRAsync (It.IsAny<string> (), It.IsAny<int> (), It.IsAny<string> ()))
                .ReturnsAsync (0.001m);
            mockClient.Setup (c => c.PlaceLimitOrder (It.IsAny<string> (), It.IsAny<string> (), It.IsAny<decimal> (), It.IsAny<decimal> ()))
                .ReturnsAsync ((JObject)null);

            var positionManager = new PositionManager (_tempFile, msg => { });
            var bot = new GridBot (mockClient.Object, positionManager, msg => { });

            await bot.StartAsync ("DOGEUSDC", 0.0772m, 0.03m, 2, 11.0m);

            Assert.Equal (0, bot.ActiveOrdersCount);
            Assert.Empty (bot.ActiveOrderIds);
        }

        [Fact]
        public async Task PlaceLimitOrder_Succeeds_ShouldAddToActiveOrders ()
        {
            var mockClient = new Mock<IBinanceFuturesClient> ();
            mockClient.Setup (c => c.GetStepSizeAsync (It.IsAny<string> ()))
                .ReturnsAsync (1.0m);
            mockClient.Setup (c => c.GetTickSizeAsync (It.IsAny<string> ()))
                .ReturnsAsync (0.0001m);
            mockClient.Setup (c => c.GetMinNotionalAsync (It.IsAny<string> ()))
                .ReturnsAsync (5.0m);
            mockClient.Setup (c => c.GetATRAsync (It.IsAny<string> (), It.IsAny<int> (), It.IsAny<string> ()))
                .ReturnsAsync (0.001m);
            mockClient.Setup (c => c.PlaceLimitOrder (It.IsAny<string> (), It.IsAny<string> (), It.IsAny<decimal> (), It.IsAny<decimal> ()))
                .ReturnsAsync (new JObject { ["orderId"] = "12345" });

            var positionManager = new PositionManager (_tempFile, msg => { });
            var bot = new GridBot (mockClient.Object, positionManager, msg => { });

            await bot.StartAsync ("DOGEUSDC", 0.0772m, 0.03m, 2, 11.0m);

            Assert.True (bot.ActiveOrdersCount > 0, "Bot should have active orders after successful placement");
        }

        [Fact]
        public async Task PlaceLimitOrder_AllFail_NoneShouldBeActive ()
        {
            var mockClient = new Mock<IBinanceFuturesClient> ();
            mockClient.Setup (c => c.GetStepSizeAsync (It.IsAny<string> ()))
                .ReturnsAsync (1.0m);
            mockClient.Setup (c => c.GetTickSizeAsync (It.IsAny<string> ()))
                .ReturnsAsync (0.0001m);
            mockClient.Setup (c => c.GetMinNotionalAsync (It.IsAny<string> ()))
                .ReturnsAsync (5.0m);
            mockClient.Setup (c => c.GetATRAsync (It.IsAny<string> (), It.IsAny<int> (), It.IsAny<string> ()))
                .ReturnsAsync (0.001m);
            mockClient.Setup (c => c.PlaceLimitOrder (It.IsAny<string> (), It.IsAny<string> (), It.IsAny<decimal> (), It.IsAny<decimal> ()))
                .ReturnsAsync ((JObject)null);

            var positionManager = new PositionManager (_tempFile, msg => { });
            var bot = new GridBot (mockClient.Object, positionManager, msg => { });

            await bot.StartAsync ("DOGEUSDC", 0.0772m, 0.03m, 3, 20.0m);

            Assert.Equal (0, bot.ActiveOrdersCount);
        }

        [Fact]
        public async Task MixedResults_SomeFail_SomeSucceed ()
        {
            int callCount = 0;
            var mockClient = new Mock<IBinanceFuturesClient> ();
            mockClient.Setup (c => c.GetStepSizeAsync (It.IsAny<string> ()))
                .ReturnsAsync (1.0m);
            mockClient.Setup (c => c.GetTickSizeAsync (It.IsAny<string> ()))
                .ReturnsAsync (0.0001m);
            mockClient.Setup (c => c.GetMinNotionalAsync (It.IsAny<string> ()))
                .ReturnsAsync (5.0m);
            mockClient.Setup (c => c.GetATRAsync (It.IsAny<string> (), It.IsAny<int> (), It.IsAny<string> ()))
                .ReturnsAsync (0.001m);
            mockClient.Setup (c => c.PlaceLimitOrder (It.IsAny<string> (), It.IsAny<string> (), It.IsAny<decimal> (), It.IsAny<decimal> ()))
                .ReturnsAsync (() =>
                {
                    callCount++;
                    return callCount % 2 == 0 ? new JObject { ["orderId"] = callCount.ToString () } : null;
                });

            var positionManager = new PositionManager (_tempFile, msg => { });
            var bot = new GridBot (mockClient.Object, positionManager, msg => { });

            await bot.StartAsync ("DOGEUSDC", 0.0772m, 0.03m, 2, 11.0m);

            int totalCalls = callCount;
            Assert.True (bot.ActiveOrdersCount < totalCalls, "Some orders should have failed");
            Assert.True (bot.ActiveOrdersCount > 0, "Some orders should have succeeded");
        }

        [Fact]
        public async Task PlaceLimitOrder_ReceivesOrderId_StoredCorrectly ()
        {
            var mockClient = new Mock<IBinanceFuturesClient> ();
            mockClient.Setup (c => c.GetStepSizeAsync (It.IsAny<string> ()))
                .ReturnsAsync (1.0m);
            mockClient.Setup (c => c.GetTickSizeAsync (It.IsAny<string> ()))
                .ReturnsAsync (0.0001m);
            mockClient.Setup (c => c.GetMinNotionalAsync (It.IsAny<string> ()))
                .ReturnsAsync (5.0m);
            mockClient.Setup (c => c.GetATRAsync (It.IsAny<string> (), It.IsAny<int> (), It.IsAny<string> ()))
                .ReturnsAsync (0.001m);
            mockClient.Setup (c => c.PlaceLimitOrder (It.IsAny<string> (), It.IsAny<string> (), It.IsAny<decimal> (), It.IsAny<decimal> ()))
                .ReturnsAsync (new JObject { ["orderId"] = "99999" });

            var positionManager = new PositionManager (_tempFile, msg => { });
            var bot = new GridBot (mockClient.Object, positionManager, msg => { });

            await bot.StartAsync ("DOGEUSDC", 0.0772m, 0.03m, 1, 11.0m);

            Assert.True (bot.ActiveOrdersCount > 0);
            Assert.Contains ("99999", bot.ActiveOrderIds.Values);
        }
    }
}
