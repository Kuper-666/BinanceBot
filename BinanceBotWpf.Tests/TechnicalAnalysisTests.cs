using System.Collections.Generic;
using System.Linq;
using BinanceBotWpf.Models;
using Xunit;

namespace BinanceBotWpf.Tests
{
    public class TechnicalAnalysisTests
    {
        [Fact]
        public void SMA_CalculatesSimpleAverageCorrectly()
        {
            var data = new List<decimal> { 1, 2, 3, 4, 5 };

            var sma = TechnicalAnalysis.SMA (data, period: 3);

            // Первые (period - 1) значений — null, недостаточно данных
            Assert.Null (sma[0]);
            Assert.Null (sma[1]);
            // (1+2+3)/3 = 2
            Assert.Equal (2m, sma[2]);
            // (2+3+4)/3 = 3
            Assert.Equal (3m, sma[3]);
            // (3+4+5)/3 = 4
            Assert.Equal (4m, sma[4]);
        }

        [Fact]
        public void SMA_OnConstantValues_EqualsThatConstant()
        {
            var data = Enumerable.Repeat (50m, 30).ToList ();

            var sma = TechnicalAnalysis.SMA (data, period: 10);

            Assert.Equal (50m, sma.Last ());
        }

        [Fact]
        public void RSI_OnConstantPrices_ReturnsNeutralValue()
        {
            // Цена не меняется => нет ни прибыли, ни убытка => RSI должен быть 100,
            // т.к. формула трактует avgLoss == 0 как "100" (особый случай в реализации проекта)
            var data = Enumerable.Repeat (100m, 30).ToList ();

            var rsi = TechnicalAnalysis.RSI (data, period: 14);

            Assert.Equal (100m, rsi[14]);
        }

        [Fact]
        public void RSI_OnConsistentUptrend_ApproachesHundred()
        {
            var data = new List<decimal> ();
            for (int i = 0; i < 30; i++) data.Add (100m + i); // монотонный рост

            var rsi = TechnicalAnalysis.RSI (data, period: 14);

            Assert.NotNull (rsi[20]);
            Assert.True (rsi[20] > 90m, $"При непрерывном росте цены RSI должен быть близок к 100, получили {rsi[20]}");
        }

        [Fact]
        public void RSI_OnConsistentDowntrend_ApproachesZero()
        {
            var data = new List<decimal> ();
            for (int i = 0; i < 30; i++) data.Add (200m - i); // монотонное падение

            var rsi = TechnicalAnalysis.RSI (data, period: 14);

            Assert.NotNull (rsi[20]);
            Assert.True (rsi[20] < 10m, $"При непрерывном падении цены RSI должен быть близок к 0, получили {rsi[20]}");
        }

        [Fact]
        public void RSI_IsAlwaysWithinValidRange()
        {
            var data = new List<decimal> { 100, 102, 98, 105, 95, 110, 90, 115, 85, 120, 80, 130, 70, 140, 60, 150 };

            var rsi = TechnicalAnalysis.RSI (data, period: 14);

            foreach (var value in rsi.Where (v => v.HasValue))
            {
                Assert.InRange (value.Value, 0m, 100m);
            }
        }

        [Fact]
        public void BollingerBands_UpperIsAlwaysAboveLower()
        {
            var data = new List<decimal> { 100, 102, 101, 105, 98, 110, 95, 108, 97, 103, 101, 99, 106, 94, 112, 96, 109, 100, 104, 102, 107, 99, 105, 101 };

            var bb = TechnicalAnalysis.BollingerBands (data, period: 20, k: 2);

            for (int i = 0; i < data.Count; i++)
            {
                if (bb.Upper[i].HasValue && bb.Lower[i].HasValue)
                {
                    Assert.True (bb.Upper[i] >= bb.Lower[i],
                        $"Верхняя полоса Боллинджера на индексе {i} оказалась ниже нижней — ошибка расчёта стандартного отклонения");
                }
            }
        }

        [Fact]
        public void BollingerBands_OnConstantPrices_BandsCollapseToMiddle()
        {
            // При нулевой волатильности стандартное отклонение = 0, верхняя и нижняя полосы
            // должны совпадать со средней линией
            var data = Enumerable.Repeat (50m, 30).ToList ();

            var bb = TechnicalAnalysis.BollingerBands (data, period: 20, k: 2);

            Assert.Equal (bb.Middle.Last (), bb.Upper.Last ());
            Assert.Equal (bb.Middle.Last (), bb.Lower.Last ());
        }

        [Fact]
        public void ATR_IsNeverNegative()
        {
            var highs = new List<decimal> { 105, 103, 108, 100, 110, 98, 112, 95 };
            var lows = new List<decimal> { 95, 97, 92, 90, 100, 88, 102, 85 };
            var closes = new List<decimal> { 100, 100, 100, 95, 105, 92, 108, 90 };

            var atr = TechnicalAnalysis.ATR (highs, lows, closes, period: 3);

            foreach (var value in atr.Where (v => v.HasValue))
            {
                Assert.True (value.Value >= 0m, "ATR не может быть отрицательным");
            }
        }

        [Fact]
        public void MACD_HistogramIsDifferenceBetweenMacdAndSignal()
        {
            var data = new List<decimal> ();
            for (int i = 0; i < 60; i++) data.Add (100m + i * 0.5m);

            var macd = TechnicalAnalysis.MACD (data, fast: 12, slow: 26, signal: 9);

            for (int i = 0; i < data.Count; i++)
            {
                if (macd.MacdLine[i].HasValue && macd.SignalLine[i].HasValue && macd.Histogram[i].HasValue)
                {
                    decimal expected = macd.MacdLine[i].Value - macd.SignalLine[i].Value;
                    Assert.Equal (expected, macd.Histogram[i].Value, precision: 8);
                }
            }
        }

        [Fact]
        public void OBV_IncreasesOnPriceUp_DecreasesOnPriceDown()
        {
            var klines = new List<BinanceKline>
            {
                new () { Close = 100, Volume = 10 },
                new () { Close = 105, Volume = 5 },  // цена выросла -> OBV += 5
                new () { Close = 102, Volume = 3 },  // цена упала -> OBV -= 3
                new () { Close = 102, Volume = 7 },  // цена не изменилась -> OBV без изменений
            };

            var obv = TechnicalAnalysis.OBV (klines);

            Assert.Equal (10m, obv[0]);
            Assert.Equal (15m, obv[1]); // 10 + 5
            Assert.Equal (12m, obv[2]); // 15 - 3
            Assert.Equal (12m, obv[3]); // без изменений
        }

        [Fact]
        public void OBV_OnEmptyList_ReturnsEmptyList()
        {
            var obv = TechnicalAnalysis.OBV (new List<BinanceKline> ());

            Assert.Empty (obv);
        }
    }
}
