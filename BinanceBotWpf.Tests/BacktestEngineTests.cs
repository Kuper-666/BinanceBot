using System;
using System.Collections.Generic;
using BinanceBotWpf.Services;
using BinanceBotWpf.Services.Strategies;
using Xunit;

namespace BinanceBotWpf.Tests
{
    public class BacktestEngineTests
    {
        private readonly BacktestEngine _engine = new (logger: null);

        /// <summary>
        /// Генерирует синтетический ряд свечей с управляемым трендом,
        /// чтобы тесты были детерминированными и не зависели от реальных рыночных данных.
        /// </summary>
        private static List<BinanceKline> GenerateKlines(int count, decimal startPrice, decimal stepPercent)
        {
            var klines = new List<BinanceKline> ();
            decimal price = startPrice;
            var time = new DateTime (2026, 1, 1);

            for (int i = 0; i < count; i++)
            {
                price *= ( 1 + stepPercent );
                klines.Add (new BinanceKline
                {
                    OpenTime = time.AddMinutes (5 * i),
                    Open = price,
                    High = price * 1.001m,
                    Low = price * 0.999m,
                    Close = price,
                    Volume = 100m
                });
            }

            return klines;
        }

        [Fact]
        public void Run_ReturnsNull_WhenNotEnoughData()
        {
            var klines = GenerateKlines (10, 100m, 0.001m);

            var result = _engine.Run (klines, fastSmaPeriod: 9, slowSmaPeriod: 21, rsiPeriod: 14,
                stopLossPercent: 0.02m, takeProfitPercent: 0.04m);

            Assert.Null (result);
        }

        [Fact]
        public void Run_WithNullKlines_ReturnsNull()
        {
            var result = _engine.Run (null, 9, 21, 14, 0.02m, 0.04m);

            Assert.Null (result);
        }

        [Fact]
        public void Run_OnFlatMarket_ProducesNoTrades()
        {
            // Цена не меняется => SMA быстрая и медленная совпадают => нет золотого/смертельного креста
            var klines = GenerateKlines (200, 100m, 0m);

            var result = _engine.Run (klines, fastSmaPeriod: 9, slowSmaPeriod: 21, rsiPeriod: 14,
                stopLossPercent: 0.02m, takeProfitPercent: 0.04m);

            Assert.NotNull (result);
            Assert.Equal (0, result.TotalTrades);
            Assert.Equal (0m, result.TotalReturn);
        }

        [Fact]
        public void Run_ReturnIsBoundedByPriceMovement_NoUnrealisticGains()
        {
            // Регрессионный тест на баг "склейки пар": на одной непрерывной паре с разумным
            // трендом доходность не должна улетать в тысячи процентов даже при компаундинге,
            // если цена за период вырастает максимум в несколько раз.
            var klines = GenerateKlines (400, 100m, 0.003m); // плавный восходящий тренд

            var result = _engine.Run (klines, fastSmaPeriod: 9, slowSmaPeriod: 21, rsiPeriod: 14,
                stopLossPercent: 0.02m, takeProfitPercent: 0.04m);

            Assert.NotNull (result);
            // Цена за весь период выросла максимум в (1.003)^400 ≈ в 3.3 раза, то есть ~230%.
            // Реальная доходность стратегии не должна на порядок превышать движение базового актива.
            Assert.True (result.TotalReturn < 500m,
                $"Доходность {result.TotalReturn}% нереалистично высока для движения цены в этом ряду — похоже на баг расчёта");
        }

        [Fact]
        public void Run_WinRateIsConsistentWithTradeCounts()
        {
            var klines = GenerateKlines (500, 100m, 0.002m);

            var result = _engine.Run (klines, fastSmaPeriod: 9, slowSmaPeriod: 21, rsiPeriod: 14,
                stopLossPercent: 0.02m, takeProfitPercent: 0.04m);

            Assert.NotNull (result);
            Assert.Equal (result.WinningTrades + result.LosingTrades, result.TotalTrades);

            if (result.TotalTrades > 0)
            {
                decimal expectedWinRate = (decimal)result.WinningTrades / result.TotalTrades * 100;
                Assert.Equal (expectedWinRate, result.WinRate, precision: 4);
            }
        }

        [Fact]
        public void Run_MaxDrawdownIsNeverNegative()
        {
            // На падающем рынке максимальная просадка не должна быть отрицательным числом —
            // это указывало бы на ошибку в формуле (peakCapital - capital) / peakCapital.
            var klines = GenerateKlines (400, 200m, -0.002m);

            var result = _engine.Run (klines, fastSmaPeriod: 9, slowSmaPeriod: 21, rsiPeriod: 14,
                stopLossPercent: 0.02m, takeProfitPercent: 0.04m);

            Assert.NotNull (result);
            Assert.True (result.MaxDrawdown >= 0m);
        }

        [Fact]
        public void Run_EmptyKlines_ReturnsNull()
        {
            var result = _engine.Run (new List<BinanceKline> (), 9, 21, 14, 0.02m, 0.04m);

            Assert.Null (result);
        }

        [Fact]
        public void Run_SingleKline_ReturnsNull()
        {
            var klines = GenerateKlines (1, 100m, 0.01m);

            var result = _engine.Run (klines, 9, 21, 14, 0.02m, 0.04m);

            Assert.Null (result);
        }

        [Fact]
        public void Run_ExtremeVolatility_ProducesValidResult()
        {
            var klines = GenerateKlines (500, 100m, 0.05m);

            var result = _engine.Run (klines, fastSmaPeriod: 9, slowSmaPeriod: 21, rsiPeriod: 14,
                stopLossPercent: 0.02m, takeProfitPercent: 0.04m);

            Assert.NotNull (result);
            Assert.True (result.MaxDrawdown >= 0m);
            Assert.Equal (result.WinningTrades + result.LosingTrades, result.TotalTrades);
        }

        [Fact]
        public void Run_TightStopLoss_ProducesValidResult()
        {
            var klines = GenerateKlines (500, 100m, 0.005m);

            var result = _engine.Run (klines, fastSmaPeriod: 9, slowSmaPeriod: 21, rsiPeriod: 14,
                stopLossPercent: 0.001m, takeProfitPercent: 0.10m);

            Assert.NotNull (result);
            Assert.Equal (result.WinningTrades + result.LosingTrades, result.TotalTrades);
            Assert.True (result.MaxDrawdown >= 0m);
        }
    }
}
