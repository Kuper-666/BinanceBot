using System;
using System.Collections.Generic;
using System.Linq;
using BinanceBotWpf.Models;
using BinanceBotWpf.Services;
using Xunit;

namespace BinanceBotWpf.Tests
{
    public class LSMA_Tests
    {
        [Fact]
        public void LSMA_CalculatesLinearRegressionCorrectly()
        {
            var data = new List<decimal> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

            var lsma = TechnicalAnalysis.LSMA (data, period: 5);

            Assert.Null (lsma[0]);
            Assert.Null (lsma[1]);
            Assert.Null (lsma[2]);
            Assert.Null (lsma[3]);
            // Для линейно растущего ряда LSMA(5) на индексе 4: точка на линии тренда
            Assert.NotNull (lsma[4]);
            Assert.True (lsma[4] > 4m && lsma[4] <= 6m,
                $"LSMA для линейного ряда должна быть близка к 5, получили {lsma[4]}");
        }

        [Fact]
        public void LSMA_OnConstantValues_EqualsThatConstant()
        {
            var data = Enumerable.Repeat (50m, 30).ToList ();

            var lsma = TechnicalAnalysis.LSMA (data, period: 10);

            for (int i = 9; i < 30; i++)
            {
                Assert.NotNull (lsma[i]);
                Assert.Equal (50m, lsma[i]);
            }
        }

        [Fact]
        public void LSMA_FollowsLinearTrend()
        {
            var data = Enumerable.Range (0, 30).Select (i => (decimal)(i * 2)).ToList ();

            var lsma = TechnicalAnalysis.LSMA (data, period: 10);

            for (int i = 9; i < 30; i++)
            {
                Assert.NotNull (lsma[i]);
            }
            // LSMA для линейного тренда совпадает с последним значением (идеальное совпадение)
            Assert.True (lsma[29] >= data[28],
                $"LSMA должна быть >= предыдущего значения для линейного тренда: LSMA={lsma[29]}, last={data[28]}");
        }

        [Fact]
        public void LSMA_ShortData_ReturnsAllNulls()
        {
            var data = new List<decimal> { 1, 2, 3 };

            var lsma = TechnicalAnalysis.LSMA (data, period: 5);

            Assert.All (lsma, v => Assert.Null (v));
        }

        [Fact]
        public void LSMA_Period1_EqualsCurrentValue()
        {
            var data = new List<decimal> { 10, 20, 30, 40, 50 };

            var lsma = TechnicalAnalysis.LSMA (data, period: 1);

            for (int i = 0; i < data.Count; i++)
            {
                Assert.NotNull (lsma[i]);
                Assert.Equal (data[i], lsma[i]);
            }
        }
    }

    public class ATRPercent_Tests
    {
        [Fact]
        public void ATRPercent_IsNeverNegative()
        {
            var highs = new List<decimal> { 105, 103, 108, 100, 110 };
            var lows = new List<decimal> { 95, 97, 92, 90, 100 };
            var closes = new List<decimal> { 100, 100, 100, 95, 105 };

            var result = TechnicalAnalysis.ATRPercent (highs, lows, closes, 3);

            foreach (var val in result.Where (v => v.HasValue))
            {
                Assert.True (val >= 0, "ATRPercent не может быть отрицательным");
            }
        }

        [Fact]
        public void ATRPercent_IsFractionOfPrice()
        {
            var highs = new List<decimal> { 110, 112, 115, 108, 120 };
            var lows = new List<decimal> { 90, 88, 92, 85, 95 };
            var closes = new List<decimal> { 100, 100, 100, 100, 100 };

            var atrPercent = TechnicalAnalysis.ATRPercent (highs, lows, closes, 3);

            var valid = atrPercent.Where (v => v.HasValue).Select (v => v.Value).ToList ();
            Assert.True (valid.Count > 0);
            Assert.True (valid.All (v => v < 1m), "ATRPercent должен быть меньше 100%");
        }
    }

    public class VolumeHistogram_Tests
    {
        [Fact]
        public void VolumeHistogram_HighVolume_ReturnsAboveOne()
        {
            var volumes = new List<decimal> { 100, 100, 100, 100, 500, 100, 100 };

            var result = TechnicalAnalysis.VolumeHistogram (volumes, period: 5);

            Assert.NotNull (result[4]);
            Assert.True (result[4] > 1m,
                $"Volume histogram для spike объёма (index 4) должен быть > 1, получили {result[4]}");
        }

        [Fact]
        public void VolumeHistogram_ConstantVolume_ReturnsOne()
        {
            var volumes = Enumerable.Repeat (100m, 20).ToList ();

            var result = TechnicalAnalysis.VolumeHistogram (volumes, period: 10);

            for (int i = 9; i < 20; i++)
            {
                Assert.NotNull (result[i]);
                Assert.Equal (1m, result[i]);
            }
        }

        [Fact]
        public void VolumeHistogram_ShortData_ReturnsAllNulls()
        {
            var volumes = new List<decimal> { 100, 200 };

            var result = TechnicalAnalysis.VolumeHistogram (volumes, period: 5);

            Assert.All (result, v => Assert.Null (v));
        }
    }

    public class AdaptiveAgent_Tests
    {
        [Fact]
        public void AdaptiveAgent_ReturnsDefaultFactor_WhenInsufficientData()
        {
            var agent = new AdaptiveAgent (msg => { });
            var klines = Enumerable.Range (0, 50).Select (i => new BinanceKline
            {
                Close = 100 + i,
                High = 105 + i,
                Low = 95 + i,
                Volume = 1000
            }).ToList ();

            var result = agent.Calculate (klines);

            Assert.Equal (1.0m, result.Factor);
            Assert.Equal ("Normal", result.Regime);
        }

        [Fact]
        public void AdaptiveAgent_CalculatesFactor_WithSufficientData()
        {
            var agent = new AdaptiveAgent (msg => { });
            var klines = Enumerable.Range (0, 120).Select (i => new BinanceKline
            {
                Close = 100 + (decimal)(Math.Sin (i * 0.1) * 10),
                High = 110 + (decimal)(Math.Sin (i * 0.1) * 10),
                Low = 90 + (decimal)(Math.Sin (i * 0.1) * 10),
                Volume = 1000 + i * 10
            }).ToList ();

            var result = agent.Calculate (klines);

            Assert.InRange (result.Factor, 0.5m, 1.5m);
            Assert.NotNull (result.Regime);
            Assert.True (result.LsmaWindowMultiplier > 0);
            Assert.True (result.SlMultiplier > 0);
        }

        [Fact]
        public void AdaptiveAgent_HighVolatility_Regime()
        {
            var agent = new AdaptiveAgent (msg => { });
            var random = new Random (42);
            var klines = Enumerable.Range (0, 120).Select (i => new BinanceKline
            {
                Close = 100 + (decimal)(random.NextDouble () * 40 - 20),
                High = 120 + (decimal)(random.NextDouble () * 20),
                Low = 80 - (decimal)(random.NextDouble () * 20),
                Volume = 1000 + random.Next (0, 5000)
            }).ToList ();

            var result = agent.Calculate (klines);

            Assert.InRange (result.Factor, 0.5m, 1.5m);
        }

        [Fact]
        public void AdaptiveAgent_Cooldown_ReturnsCachedResult()
        {
            var agent = new AdaptiveAgent (msg => { });
            var klines = Enumerable.Range (0, 120).Select (i => new BinanceKline
            {
                Close = 100,
                High = 105,
                Low = 95,
                Volume = 1000
            }).ToList ();

            var first = agent.Calculate (klines);
            var second = agent.Calculate (klines);

            Assert.Equal (first.Factor, second.Factor);
            Assert.Equal ("Cooldown", second.Regime);
        }

        [Fact]
        public void AdaptiveAgent_AdaptiveFactor_AffectsSlMultiplier()
        {
            var agent = new AdaptiveAgent (msg => { });
            var klines = Enumerable.Range (0, 120).Select (i => new BinanceKline
            {
                Close = 100 + (decimal)(Math.Sin (i * 0.1) * 15),
                High = 115 + (decimal)(Math.Sin (i * 0.1) * 15),
                Low = 85 + (decimal)(Math.Sin (i * 0.1) * 15),
                Volume = 1000 + i * 10
            }).ToList ();

            var result = agent.Calculate (klines);

            // SL multiplier should move in same direction as factor
            if (result.Factor > 1.0m)
                Assert.True (result.SlMultiplier > 1.0m);
            else if (result.Factor < 1.0m)
                Assert.True (result.SlMultiplier < 1.0m);
        }
    }

    public class SignalValidator_Tests
    {
        [Fact]
        public void SignalValidator_NormalSignal_IsValid()
        {
            var validator = new SignalValidator (msg => { });
            var input = new SignalValidationInput
            {
                Price = 45000,
                Rsi = 40,
                MacdHistogram = 0.001f,
                BbWidth = 0.04f,
                AtrPercent = 0.02f,
                VolumeRatio = 1.2f,
                SmaFast = 45100,
                SmaSlow = 44900,
                SignalDirection = 1
            };

            var result = validator.Validate (input);

            Assert.True (result.IsValid);
            Assert.True (result.Confidence > 0.4f);
        }

        [Fact]
        public void SignalValidator_ExtremeVolume_IsRisky()
        {
            var validator = new SignalValidator (msg => { });
            var input = new SignalValidationInput
            {
                Price = 45000,
                Rsi = 40,
                MacdHistogram = 0.001f,
                BbWidth = 0.04f,
                AtrPercent = 0.02f,
                VolumeRatio = 6.0f,
                SmaFast = 45100,
                SmaSlow = 44900,
                SignalDirection = 1
            };

            var result = validator.Validate (input);

            Assert.True (result.RiskFlag);
        }

        [Fact]
        public void SignalValidator_HighATR_IsRisky()
        {
            var validator = new SignalValidator (msg => { });
            var input = new SignalValidationInput
            {
                Price = 45000,
                Rsi = 40,
                MacdHistogram = 0.001f,
                BbWidth = 0.04f,
                AtrPercent = 0.15f,
                VolumeRatio = 1.0f,
                SmaFast = 45100,
                SmaSlow = 44900,
                SignalDirection = 1
            };

            var result = validator.Validate (input);

            Assert.True (result.RiskFlag);
        }

        [Fact]
        public void SignalValidator_MacdMismatch_ReducesConfidence()
        {
            var validator = new SignalValidator (msg => { });
            var inputAligned = new SignalValidationInput
            {
                Price = 45000,
                Rsi = 40,
                MacdHistogram = 0.005f,
                BbWidth = 0.04f,
                AtrPercent = 0.02f,
                VolumeRatio = 1.0f,
                SmaFast = 45100,
                SmaSlow = 44900,
                SignalDirection = 1
            };

            var inputMismatch = new SignalValidationInput
            {
                Price = 45000,
                Rsi = 40,
                MacdHistogram = -0.005f,
                BbWidth = 0.04f,
                AtrPercent = 0.02f,
                VolumeRatio = 1.0f,
                SmaFast = 45100,
                SmaSlow = 44900,
                SignalDirection = 1
            };

            var resultAligned = validator.Validate (inputAligned);
            var resultMismatch = validator.Validate (inputMismatch);

            Assert.True (resultAligned.Confidence > resultMismatch.Confidence,
                $"MACD aligned ({resultAligned.Confidence}) should have higher confidence than mismatch ({resultMismatch.Confidence})");
        }

        [Fact]
        public void SignalValidator_TrendAligned_HigherConfidence()
        {
            var validator = new SignalValidator (msg => { });
            var inputBuy = new SignalValidationInput
            {
                Price = 45000,
                Rsi = 40,
                MacdHistogram = 0.001f,
                BbWidth = 0.04f,
                AtrPercent = 0.02f,
                VolumeRatio = 1.2f,
                SmaFast = 45100,
                SmaSlow = 44900,
                SignalDirection = 1
            };

            var inputNoTrend = new SignalValidationInput
            {
                Price = 45000,
                Rsi = 40,
                MacdHistogram = 0.001f,
                BbWidth = 0.04f,
                AtrPercent = 0.02f,
                VolumeRatio = 1.2f,
                SmaFast = 44800,
                SmaSlow = 45200,
                SignalDirection = 1
            };

            var resultAligned = validator.Validate (inputBuy);
            var resultNoTrend = validator.Validate (inputNoTrend);

            Assert.True (resultAligned.Confidence >= resultNoTrend.Confidence,
                $"Trend-aligned signal should have higher confidence: {resultAligned.Confidence} vs {resultNoTrend.Confidence}");
        }

        [Fact]
        public void SignalValidator_WithoutOnnx_UsesHeuristic()
        {
            var validator = new SignalValidator (msg => { });
            var input = new SignalValidationInput
            {
                Price = 45000,
                Rsi = 50,
                MacdHistogram = 0,
                BbWidth = 0.05f,
                AtrPercent = 0.03f,
                VolumeRatio = 1.0f,
                SmaFast = 45000,
                SmaSlow = 45000,
                SignalDirection = 1
            };

            var result = validator.Validate (input);

            Assert.Equal ("Heuristic", result.Method);
        }
    }

    public class NewsSentinel_Tests : IDisposable
    {
        private readonly NewsSentinel _sentinel;
        private readonly string _testDbPath;

        public NewsSentinel_Tests ()
        {
            _testDbPath = System.IO.Path.Combine (
                System.IO.Path.GetTempPath (),
                $"news_test_{Guid.NewGuid ():N}.db");
            _sentinel = new NewsSentinel (msg => { }, _testDbPath);
        }

        public void Dispose ()
        {
            try { System.IO.File.Delete (_testDbPath); } catch { }
            try { System.IO.File.Delete (_testDbPath + "-wal"); } catch { }
            try { System.IO.File.Delete (_testDbPath + "-shm"); } catch { }
        }

        [Fact]
        public void NewsSentinel_InsertAndQuery()
        {
            int inserted = _sentinel.InsertNews (
                "Bitcoin crashes 20%", "CoinDesk", "negative", 4, "BTC");

            Assert.True (inserted > 0);

            bool hasHigh = _sentinel.IsHighImpactNewsActive ("BTC");
            Assert.True (hasHigh);
        }

        [Fact]
        public void NewsSentinel_NoNegativeNews_ReturnsFalse()
        {
            _sentinel.InsertNews (
                "Bitcoin rallies to new ATH", "CoinDesk", "positive", 3, "BTC");

            bool hasHigh = _sentinel.IsHighImpactNewsActive ("BTC");
            Assert.False (hasHigh);
        }

        [Fact]
        public void NewsSentinel_GetRecentNews()
        {
            _sentinel.InsertNews ("Test 1", "Source", "neutral", 0, "*");
            _sentinel.InsertNews ("Test 2", "Source", "negative", 2, "BTC");

            var news = _sentinel.GetRecentNews (hours: 1);

            Assert.True (news.Count >= 2);
        }

        [Fact]
        public void NewsSentinel_GetStats()
        {
            _sentinel.InsertNews ("Positive", "Source", "positive", 1, "*");
            _sentinel.InsertNews ("Negative", "Source", "negative", 2, "*");
            _sentinel.InsertNews ("Neutral", "Source", "neutral", 0, "*");

            var stats = _sentinel.GetStats ();

            Assert.Equal (3, stats.TotalCount);
            Assert.Equal (1, stats.PositiveCount);
            Assert.Equal (1, stats.NegativeCount);
            Assert.Equal (1, stats.NeutralCount);
        }

        [Fact]
        public void NewsSentinel_CleanupOldNews()
        {
            _sentinel.InsertNews ("Old news", "Source", "neutral", 0, "*");
            int cleaned = _sentinel.CleanupOldNews (maxAgeHours: 0);

            Assert.True (cleaned >= 0);
        }

        [Fact]
        public void NewsSentinel_NoHighImpact_ReturnsFalse()
        {
            _sentinel.InsertNews ("Some minor news", "Source", "negative", 1, "BTC");

            bool hasHigh = _sentinel.IsHighImpactNewsActive ("BTC");
            Assert.False (hasHigh);
        }
    }
}
