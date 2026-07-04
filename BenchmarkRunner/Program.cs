using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace BenchmarkRunner
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("════════════════════════════════════════════════════════════════");
            Console.WriteLine("  PERFORMANCE BENCHMARK — BinanceBotWpf Optimized Code Paths");
            Console.WriteLine("════════════════════════════════════════════════════════════════");
            Console.WriteLine();

            int iterations = 1000;
            var random = new Random(42);

            // Generate test data: 200 candlesticks
            var closes = GeneratePrices(200, 50000m, random);
            var highs = closes.Select(c => c * (1m + (decimal)random.NextDouble() * 0.02m)).ToList();
            var lows = closes.Select(c => c * (1m - (decimal)random.NextDouble() * 0.02m)).ToList();
            var volumes = Enumerable.Range(0, 200).Select(_ => (decimal)(random.NextDouble() * 1000 + 100)).ToList();

            Console.WriteLine($"  Data: {closes.Count} candles, {iterations} iterations each");
            Console.WriteLine();

            Console.WriteLine("  ┌─────────────────────────────────────────────────────────────┐");
            Console.WriteLine("  │              OPTIMIZED vs OLD (A/B comparison)             │");
            Console.WriteLine("  └─────────────────────────────────────────────────────────────┘");
            Console.WriteLine();

            // === RSI ===
            BenchmarkPair("RSI(14)",
                () => RSI_Optimized(closes, 14),
                () => RSI_Old(closes, 14),
                iterations);

            // === BollingerBands ===
            BenchmarkPair("BollingerBands(20)",
                () => BollingerBands_Optimized(closes, 20, 2m),
                () => BollingerBands_Old(closes, 20, 2m),
                iterations);

            // === ATR ===
            BenchmarkPair("ATR(14)",
                () => ATR_Optimized(highs, lows, closes, 14),
                () => ATR_Old(highs, lows, closes, 14),
                iterations);

            // === MACD ===
            BenchmarkPair("MACD(12,26,9)",
                () => MACD_Optimized(closes, 12, 26, 9),
                () => MACD_Old(closes, 12, 26, 9),
                iterations);

            // === StandardDeviation ===
            BenchmarkPair("StandardDeviation(50)",
                () => StdDev_Optimized(closes.Take(50).ToList()),
                () => StdDev_Old(closes.Take(50).ToList()),
                iterations);

            // === LSMA ===
            BenchmarkPair("LSMA(20)",
                () => LSMA_Optimized(closes, 20),
                () => LSMA_Old(closes, 20),
                iterations);

            // === VolumeHistogram ===
            BenchmarkPair("VolumeHistogram(20)",
                () => VolumeHistogram_Optimized(volumes, 20),
                () => VolumeHistogram_Old(volumes, 20),
                iterations);

            // === Full pair analysis ===
            BenchmarkPair("Full pair analysis",
                () => FullAnalysis_Optimized(closes, highs, lows, volumes),
                () => FullAnalysis_Old(closes, highs, lows, volumes),
                iterations / 10);

            // === Micro-benchmarks for specific optimizations ===
            Console.WriteLine();
            Console.WriteLine("  ┌─────────────────────────────────────────────────────────────┐");
            Console.WriteLine("  │         Micro-benchmarks: specific optimizations           │");
            Console.WriteLine("  └─────────────────────────────────────────────────────────────┘");
            Console.WriteLine();

            // Enumerable.Repeat vs new+Add
            BenchmarkPair("List init: Repeat+ToList vs new+Add",
                () => { var r = new List<decimal?>(200); for (int i = 0; i < 200; i++) r.Add(null); },
                () => { var r = Enumerable.Repeat((decimal?)null, 200).ToList(); },
                iterations);

            // .Select().ToList() vs direct
            BenchmarkPair("Copy: Select().ToList() vs for-loop",
                () => { var r = new List<decimal>(200); for (int i = 0; i < 200; i++) r.Add(closes[i]); },
                () => { var r = closes.Select(c => c).ToList(); },
                iterations);

            // TakeLast().Average() vs index-based
            BenchmarkPair("Avg: TakeLast(20).Average() vs index",
                () => { decimal s = 0; for (int i = 180; i < 200; i++) s += closes[i]; decimal avg = s / 20; },
                () => { var _ = closes.TakeLast(20).Average(); },
                iterations);

            // Math.Pow vs diff*diff
            BenchmarkPair("Square: Math.Pow vs diff*diff",
                () => { for (int i = 0; i < 200; i++) { decimal d = closes[i] - 50000m; decimal sq = d * d; } },
                () => { for (int i = 0; i < 200; i++) { decimal d = closes[i] - 50000m; decimal sq = (decimal)Math.Pow((double)d, 2); } },
                iterations);

            // String interpolation vs tuple key
            BenchmarkPair("Cache key: $string vs tuple",
                () => { var key = ("BTCUSDT", "1h", 100); },
                () => { string key = $"{"BTCUSDT"}_{"1h"}_{100}"; },
                iterations);

            // GetRange vs index param
            BenchmarkPair("Sublist: GetRange vs index param",
                () => { var sub = closes.GetRange(0, 199); },
                () => { decimal sum = 0; for (int i = 0; i < 199; i++) sum += closes[i]; },
                iterations);

            Console.WriteLine();
            Console.WriteLine("════════════════════════════════════════════════════════════════");
            Console.WriteLine("  Benchmark complete");
            Console.WriteLine("════════════════════════════════════════════════════════════════");
        }

        static void RunBenchmark(string name, int iterations, Action action)
        {
            // Warmup
            for (int i = 0; i < 10; i++) action();

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) action();
            sw.Stop();

            double avgUs = sw.Elapsed.TotalMicroseconds / iterations;
            Console.WriteLine($"  {name,-50} {avgUs,10:F1} us/call  ({iterations} calls, {sw.ElapsedMilliseconds} ms total)");
        }

        static void BenchmarkPair(string name, Action optimized, Action old, int iterations)
        {
            // Warmup
            for (int i = 0; i < 10; i++) { optimized(); old(); }

            var swOld = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) old();
            swOld.Stop();

            var swNew = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) optimized();
            swNew.Stop();

            double oldUs = swOld.Elapsed.TotalMicroseconds / iterations;
            double newUs = swNew.Elapsed.TotalMicroseconds / iterations;
            double speedup = oldUs > 0 ? oldUs / newUs : 0;
            string delta = speedup > 1 ? $"{speedup:F1}x FASTER" : $"{1.0 / speedup:F1}x slower";

            Console.WriteLine($"  {name}");
            Console.WriteLine($"    OLD: {oldUs,8:F1} us/call");
            Console.WriteLine($"    NEW: {newUs,8:F1} us/call  →  {delta}");
            Console.WriteLine();
        }

        static List<decimal> GeneratePrices(int count, decimal startPrice, Random rng)
        {
            var prices = new List<decimal>(count);
            decimal price = startPrice;
            for (int i = 0; i < count; i++)
            {
                price *= (1m + (decimal)(rng.NextDouble() - 0.5) * 0.02m);
                prices.Add(Math.Round(price, 2));
            }
            return prices;
        }

        // ═══════════════════════════════════════════
        //  OPTIMIZED INDICATORS (mirror TechnicalAnalysis.cs)
        // ═══════════════════════════════════════════

        static List<decimal?> SMA_Optimized(List<decimal> data, int period)
        {
            int count = data.Count;
            var result = new List<decimal?>(count);
            for (int i = 0; i < count; i++)
            {
                if (i < period - 1) { result.Add(null); continue; }
                decimal sum = 0;
                for (int j = i - period + 1; j <= i; j++) sum += data[j];
                result.Add(sum / period);
            }
            return result;
        }

        static List<decimal?> RSI_Optimized(List<decimal> data, int period)
        {
            int count = data.Count;
            var result = new List<decimal?>(count);
            for (int i = 0; i < count; i++) result.Add(null);
            if (count <= period) return result;

            decimal avgGain = 0, avgLoss = 0;
            for (int i = 1; i <= period; i++)
            {
                decimal diff = data[i] - data[i - 1];
                if (diff > 0) avgGain += diff;
                else avgLoss += Math.Abs(diff);
            }
            avgGain /= period;
            avgLoss /= period;
            result[period] = avgLoss == 0 ? 100 : 100 - 100 / (1 + avgGain / avgLoss);

            for (int i = period + 1; i < count; i++)
            {
                decimal diff = data[i] - data[i - 1];
                decimal gain = diff > 0 ? diff : 0;
                decimal loss = diff < 0 ? Math.Abs(diff) : 0;
                avgGain = (avgGain * (period - 1) + gain) / period;
                avgLoss = (avgLoss * (period - 1) + loss) / period;
                result[i] = avgLoss == 0 ? 100 : 100 - 100 / (1 + avgGain / avgLoss);
            }
            return result;
        }

        static (List<decimal?> Upper, List<decimal?> Middle, List<decimal?> Lower) BollingerBands_Optimized(List<decimal> data, int period, decimal k)
        {
            int count = data.Count;
            var middle = SMA_Optimized(data, period);
            var upper = new List<decimal?>(count);
            var lower = new List<decimal?>(count);
            for (int i = 0; i < count; i++) { upper.Add(null); lower.Add(null); }

            for (int i = period - 1; i < count; i++)
            {
                decimal? mid = middle[i];
                if (mid.HasValue)
                {
                    decimal midVal = mid.Value;
                    decimal sumOfSquares = 0;
                    for (int j = i - period + 1; j <= i; j++)
                    {
                        decimal diff = data[j] - midVal;
                        sumOfSquares += diff * diff;
                    }
                    decimal stdDev = (decimal)Math.Sqrt((double)(sumOfSquares / period));
                    upper[i] = midVal + k * stdDev;
                    lower[i] = midVal - k * stdDev;
                }
            }
            return (upper, middle, lower);
        }

        static List<decimal?> ATR_Optimized(List<decimal> highs, List<decimal> lows, List<decimal> closes, int period)
        {
            int count = highs.Count;
            var result = new List<decimal?>(count);
            for (int i = 0; i < count; i++) result.Add(null);
            if (count <= 1 || count < period) return result;

            decimal sum = highs[0] - lows[0];
            for (int i = 1; i < period; i++)
            {
                decimal tr1 = highs[i] - lows[i];
                decimal tr2 = Math.Abs(highs[i] - closes[i - 1]);
                decimal tr3 = Math.Abs(lows[i] - closes[i - 1]);
                sum += Math.Max(tr1, Math.Max(tr2, tr3));
            }
            decimal currentAtr = sum / period;
            result[period - 1] = currentAtr;

            for (int i = period; i < count; i++)
            {
                decimal tr1 = highs[i] - lows[i];
                decimal tr2 = Math.Abs(highs[i] - closes[i - 1]);
                decimal tr3 = Math.Abs(lows[i] - closes[i - 1]);
                decimal tr = Math.Max(tr1, Math.Max(tr2, tr3));
                currentAtr = (currentAtr * (period - 1) + tr) / period;
                result[i] = currentAtr;
            }
            return result;
        }

        static (List<decimal?> MacdLine, List<decimal?> SignalLine, List<decimal?> Histogram) MACD_Optimized(List<decimal> data, int fast, int slow, int signal)
        {
            int count = data.Count;
            var fastEma = EMA_Optimized(data, fast);
            var slowEma = EMA_Optimized(data, slow);

            var macdLineWithNulls = new List<decimal?>(count);
            var nonNullMacd = new List<decimal>(count);
            for (int i = 0; i < count; i++)
            {
                if (fastEma[i].HasValue && slowEma[i].HasValue)
                {
                    decimal val = fastEma[i]!.Value - slowEma[i]!.Value;
                    nonNullMacd.Add(val);
                    macdLineWithNulls.Add(val);
                }
                else macdLineWithNulls.Add(null);
            }

            var signalEma = EMA_Optimized(nonNullMacd, signal);
            int offset = count - signalEma.Count;
            var signalLineWithNulls = new List<decimal?>(count);
            for (int i = 0; i < offset; i++) signalLineWithNulls.Add(null);
            signalLineWithNulls.AddRange(signalEma);

            var histogram = new List<decimal?>(count);
            for (int i = 0; i < count; i++)
            {
                if (macdLineWithNulls[i].HasValue && signalLineWithNulls[i].HasValue)
                    histogram.Add(macdLineWithNulls[i]!.Value - signalLineWithNulls[i]!.Value);
                else histogram.Add(null);
            }
            return (macdLineWithNulls, signalLineWithNulls, histogram);
        }

        static List<decimal?> EMA_Optimized(List<decimal> data, int period)
        {
            int count = data.Count;
            var result = new List<decimal?>(count);
            if (count == 0) return result;
            decimal multiplier = 2.0m / (period + 1);
            decimal currentEma = data[0];
            result.Add(currentEma);
            for (int i = 1; i < count; i++)
            {
                currentEma = (data[i] - currentEma) * multiplier + currentEma;
                result.Add(i < period - 1 ? null : currentEma);
            }
            return result;
        }

        static decimal StdDev_Optimized(List<decimal> values)
        {
            int count = values.Count;
            if (count == 0) return 0;
            decimal sum = 0;
            for (int i = 0; i < count; i++) sum += values[i];
            decimal avg = sum / count;
            decimal sumSq = 0;
            for (int i = 0; i < count; i++)
            {
                decimal diff = values[i] - avg;
                sumSq += diff * diff;
            }
            return (decimal)Math.Sqrt((double)(sumSq / count));
        }

        static List<decimal?> LSMA_Optimized(List<decimal> data, int period)
        {
            int count = data.Count;
            var result = new List<decimal?>(count);
            for (int i = 0; i < count; i++) result.Add(null);
            if (count < period) return result;

            for (int i = period - 1; i < count; i++)
            {
                decimal sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
                for (int j = 0; j < period; j++)
                {
                    decimal x = j;
                    decimal y = data[i - period + 1 + j];
                    sumX += x; sumY += y; sumXY += x * y; sumX2 += x * x;
                }
                decimal denom = period * sumX2 - sumX * sumX;
                if (denom == 0) { result[i] = sumY / period; continue; }
                decimal slope = (period * sumXY - sumX * sumY) / denom;
                decimal intercept = (sumY - slope * sumX) / period;
                result[i] = intercept + slope * (period - 1);
            }
            return result;
        }

        static List<decimal?> VolumeHistogram_Optimized(List<decimal> volumes, int period)
        {
            int count = volumes.Count;
            var result = new List<decimal?>(count);
            if (count < period)
            {
                for (int i = 0; i < count; i++) result.Add(null);
                return result;
            }
            for (int i = 0; i < period - 1; i++) result.Add(null);
            decimal sum = 0;
            for (int i = 0; i < period; i++) sum += volumes[i];
            for (int i = period - 1; i < count; i++)
            {
                decimal avg = sum / period;
                result.Add(avg > 0 ? volumes[i] / avg : 1m);
                if (i + 1 < count)
                    sum += volumes[i + 1] - volumes[i - period + 1];
            }
            return result;
        }

        class SMAEngine
        {
            public decimal CalcSMA(List<decimal> data, int period, int count = -1)
            {
                int dataCount = count >= 0 ? count : data.Count;
                if (dataCount < period) return 0m;
                decimal sum = 0;
                for (int i = dataCount - period; i < dataCount; i++)
                    sum += data[i];
                return sum / period;
            }
        }

        // ═══════════════════════════════════════════
        //  OLD-STYLE INDICATORS (before optimization)
        // ═══════════════════════════════════════════

        static List<decimal?> RSI_Old(List<decimal> data, int period)
        {
            var result = Enumerable.Repeat((decimal?)null, data.Count).ToList();
            if (data.Count <= period) return result;
            decimal avgGain = 0, avgLoss = 0;
            for (int i = 1; i <= period; i++)
            {
                decimal diff = data[i] - data[i - 1];
                if (diff > 0) avgGain += diff;
                else avgLoss += Math.Abs(diff);
            }
            avgGain /= period; avgLoss /= period;
            result[period] = avgLoss == 0 ? 100 : 100 - 100 / (1 + avgGain / avgLoss);
            for (int i = period + 1; i < data.Count; i++)
            {
                decimal diff = data[i] - data[i - 1];
                decimal gain = diff > 0 ? diff : 0;
                decimal loss = diff < 0 ? Math.Abs(diff) : 0;
                avgGain = (avgGain * (period - 1) + gain) / period;
                avgLoss = (avgLoss * (period - 1) + loss) / period;
                result[i] = avgLoss == 0 ? 100 : 100 - 100 / (1 + avgGain / avgLoss);
            }
            return result;
        }

        static (List<decimal?> Upper, List<decimal?> Middle, List<decimal?> Lower) BollingerBands_Old(List<decimal> data, int period, decimal k)
        {
            var upper = Enumerable.Repeat((decimal?)null, data.Count).ToList();
            var middle = SMA_Optimized(data, period);
            var lower = Enumerable.Repeat((decimal?)null, data.Count).ToList();
            for (int i = period - 1; i < data.Count; i++)
            {
                decimal? mid = middle[i];
                if (mid.HasValue)
                {
                    decimal sumOfSquares = 0;
                    for (int j = 0; j < period; j++)
                        sumOfSquares += (decimal)Math.Pow((double)(data[i - j] - mid.Value), 2);
                    decimal stdDev = (decimal)Math.Sqrt((double)(sumOfSquares / period));
                    upper[i] = mid.Value + k * stdDev;
                    lower[i] = mid.Value - k * stdDev;
                }
            }
            return (upper, middle, lower);
        }

        static List<decimal?> ATR_Old(List<decimal> highs, List<decimal> lows, List<decimal> closes, int period)
        {
            var result = Enumerable.Repeat((decimal?)null, highs.Count).ToList();
            if (highs.Count <= 1 || highs.Count < period) return result;
            var tr = new List<decimal> { highs[0] - lows[0] };
            for (int i = 1; i < highs.Count; i++)
            {
                decimal tr1 = highs[i] - lows[i];
                decimal tr2 = Math.Abs(highs[i] - closes[i - 1]);
                decimal tr3 = Math.Abs(lows[i] - closes[i - 1]);
                tr.Add(Math.Max(tr1, Math.Max(tr2, tr3)));
            }
            decimal currentAtr = tr.Take(period).Average();
            result[period - 1] = currentAtr;
            for (int i = period; i < tr.Count; i++)
            {
                currentAtr = (currentAtr * (period - 1) + tr[i]) / period;
                result[i] = currentAtr;
            }
            return result;
        }

        static (List<decimal?> MacdLine, List<decimal?> SignalLine, List<decimal?> Histogram) MACD_Old(List<decimal> data, int fast, int slow, int signal)
        {
            var fastEma = EMA_Optimized(data, fast);
            var slowEma = EMA_Optimized(data, slow);
            var macdLine = new List<decimal>();
            var macdLineWithNulls = new List<decimal?>();
            for (int i = 0; i < data.Count; i++)
            {
                if (fastEma[i].HasValue && slowEma[i].HasValue)
                {
                    decimal val = fastEma[i]!.Value - slowEma[i]!.Value;
                    macdLine.Add(val);
                    macdLineWithNulls.Add(val);
                }
                else macdLineWithNulls.Add(null);
            }
            var signalEma = EMA_Optimized(macdLine, signal);
            var signalLineWithNulls = Enumerable.Repeat((decimal?)null, data.Count - signalEma.Count).Concat(signalEma).ToList();
            var histogram = new List<decimal?>();
            for (int i = 0; i < data.Count; i++)
            {
                if (macdLineWithNulls[i].HasValue && signalLineWithNulls[i].HasValue)
                    histogram.Add(macdLineWithNulls[i]!.Value - signalLineWithNulls[i]!.Value);
                else histogram.Add(null);
            }
            return (macdLineWithNulls, signalLineWithNulls, histogram);
        }

        static decimal StdDev_Old(List<decimal> values)
        {
            if (values == null || values.Count == 0) return 0;
            decimal avg = values.Average();
            decimal sumSq = values.Select(v => (v - avg) * (v - avg)).Sum();
            return (decimal)Math.Sqrt((double)(sumSq / values.Count));
        }

        static List<decimal?> LSMA_Old(List<decimal> data, int period)
        {
            var result = Enumerable.Repeat((decimal?)null, data.Count).ToList();
            if (data.Count < period) return result;
            for (int i = period - 1; i < data.Count; i++)
            {
                decimal sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
                for (int j = 0; j < period; j++)
                {
                    decimal x = j; decimal y = data[i - period + 1 + j];
                    sumX += x; sumY += y; sumXY += x * y; sumX2 += x * x;
                }
                decimal denom = period * sumX2 - sumX * sumX;
                if (denom == 0) { result[i] = sumY / period; continue; }
                decimal slope = (period * sumXY - sumX * sumY) / denom;
                decimal intercept = (sumY - slope * sumX) / period;
                result[i] = intercept + slope * (period - 1);
            }
            return result;
        }

        static List<decimal?> VolumeHistogram_Old(List<decimal> volumes, int period)
        {
            var result = Enumerable.Repeat((decimal?)null, volumes.Count).ToList();
            if (volumes.Count < period) return result;
            decimal sum = 0;
            for (int i = 0; i < period; i++) sum += volumes[i];
            for (int i = period - 1; i < volumes.Count; i++)
            {
                decimal avg = sum / period;
                result[i] = avg > 0 ? volumes[i] / avg : 1m;
                if (i + 1 < volumes.Count)
                    sum += volumes[i + 1] - volumes[i - period + 1];
            }
            return result;
        }

        static void FullAnalysis_Optimized(List<decimal> closes, List<decimal> highs, List<decimal> lows, List<decimal> volumes)
        {
            var sma9 = SMA_Optimized(closes, 9);
            var sma21 = SMA_Optimized(closes, 21);
            var rsi = RSI_Optimized(closes, 14);
            var bb = BollingerBands_Optimized(closes, 20, 2m);
            var atr = ATR_Optimized(highs, lows, closes, 14);
            var macd = MACD_Optimized(closes, 12, 26, 9);
            var lsma = LSMA_Optimized(closes, 20);
            var vol = VolumeHistogram_Optimized(volumes, 20);
        }

        static void FullAnalysis_Old(List<decimal> closes, List<decimal> highs, List<decimal> lows, List<decimal> volumes)
        {
            var sma9 = SMA_Optimized(closes, 9);
            var sma21 = SMA_Optimized(closes, 21);
            var rsi = RSI_Old(closes, 14);
            var bb = BollingerBands_Old(closes, 20, 2m);
            var atr = ATR_Old(highs, lows, closes, 14);
            var macd = MACD_Old(closes, 12, 26, 9);
            var lsma = LSMA_Old(closes, 20);
            var vol = VolumeHistogram_Old(volumes, 20);
        }
    }
}
