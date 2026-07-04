using System;
using System.Collections.Generic;

namespace BinanceBotWpf.Models
{
    public static class TechnicalAnalysis
    {
        public static List<decimal?> SMA(List<decimal> data, int period)
        {
            int count = data.Count;
            var result = new List<decimal?> (count);
            for (int i = 0; i < count; i++)
            {
                if (i < period - 1) { result.Add (null); continue; }
                decimal sum = 0;
                for (int j = i - period + 1; j <= i; j++) sum += data[j];
                result.Add (sum / period);
            }
            return result;
        }

        public static List<decimal?> EMA(List<decimal> data, int period)
        {
            int count = data.Count;
            var result = new List<decimal?> (count);
            if (count == 0) return result;
            decimal multiplier = 2.0m / ( period + 1 );
            decimal currentEma = data[0];
            result.Add (currentEma);
            for (int i = 1; i < count; i++)
            {
                currentEma = ( data[i] - currentEma ) * multiplier + currentEma;
                result.Add (i < period - 1 ? null : currentEma);
            }
            return result;
        }

        public static List<decimal?> RSI(List<decimal> data, int period)
        {
            int count = data.Count;
            var result = new List<decimal?> (count);
            for (int i = 0; i < count; i++) result.Add (null);
            if (count <= period) return result;

            decimal avgGain = 0, avgLoss = 0;
            for (int i = 1; i <= period; i++)
            {
                decimal diff = data[i] - data[i - 1];
                if (diff > 0) avgGain += diff;
                else avgLoss += Math.Abs (diff);
            }

            avgGain /= period;
            avgLoss /= period;
            result[period] = avgLoss == 0 ? 100 : 100 - 100 / ( 1 + avgGain / avgLoss );

            for (int i = period + 1; i < count; i++)
            {
                decimal diff = data[i] - data[i - 1];
                decimal gain = diff > 0 ? diff : 0;
                decimal loss = diff < 0 ? Math.Abs (diff) : 0;

                avgGain = ( avgGain * ( period - 1 ) + gain ) / period;
                avgLoss = ( avgLoss * ( period - 1 ) + loss ) / period;

                result[i] = avgLoss == 0 ? 100 : 100 - 100 / ( 1 + avgGain / avgLoss );
            }
            return result;
        }

        public static (List<decimal?> Upper, List<decimal?> Middle, List<decimal?> Lower) BollingerBands(List<decimal> data, int period, decimal k = 2)
        {
            int count = data.Count;
            var middle = SMA (data, period);
            var upper = new List<decimal?> (count);
            var lower = new List<decimal?> (count);
            for (int i = 0; i < count; i++) { upper.Add (null); lower.Add (null); }

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
                    decimal stdDev = (decimal)Math.Sqrt ((double)( sumOfSquares / period ));
                    upper[i] = midVal + k * stdDev;
                    lower[i] = midVal - k * stdDev;
                }
            }
            return (upper, middle, lower);
        }

        public static List<decimal?> ATR(List<decimal> highs, List<decimal> lows, List<decimal> closes, int period)
        {
            int count = highs.Count;
            var result = new List<decimal?> (count);
            for (int i = 0; i < count; i++) result.Add (null);
            if (count <= 1 || count < period) return result;

            decimal sum = highs[0] - lows[0];
            for (int i = 1; i < period; i++)
            {
                decimal tr1 = highs[i] - lows[i];
                decimal tr2 = Math.Abs (highs[i] - closes[i - 1]);
                decimal tr3 = Math.Abs (lows[i] - closes[i - 1]);
                sum += Math.Max (tr1, Math.Max (tr2, tr3));
            }
            decimal currentAtr = sum / period;
            result[period - 1] = currentAtr;

            for (int i = period; i < count; i++)
            {
                decimal tr1 = highs[i] - lows[i];
                decimal tr2 = Math.Abs (highs[i] - closes[i - 1]);
                decimal tr3 = Math.Abs (lows[i] - closes[i - 1]);
                decimal tr = Math.Max (tr1, Math.Max (tr2, tr3));
                currentAtr = ( currentAtr * ( period - 1 ) + tr ) / period;
                result[i] = currentAtr;
            }
            return result;
        }

        public static (List<decimal?> MacdLine, List<decimal?> SignalLine, List<decimal?> Histogram) MACD(List<decimal> data, int fast = 12, int slow = 26, int signal = 9)
        {
            int count = data.Count;
            var fastEma = EMA (data, fast);
            var slowEma = EMA (data, slow);

            var macdLineWithNulls = new List<decimal?> (count);
            var nonNullMacd = new List<decimal> (count);
            for (int i = 0; i < count; i++)
            {
                if (fastEma[i].HasValue && slowEma[i].HasValue)
                {
                    decimal val = fastEma[i]!.Value - slowEma[i]!.Value;
                    nonNullMacd.Add (val);
                    macdLineWithNulls.Add (val);
                }
                else macdLineWithNulls.Add (null);
            }

            var signalEma = EMA (nonNullMacd, signal);
            int offset = count - signalEma.Count;
            var signalLineWithNulls = new List<decimal?> (count);
            for (int i = 0; i < offset; i++) signalLineWithNulls.Add (null);
            signalLineWithNulls.AddRange (signalEma);

            var histogram = new List<decimal?> (count);
            for (int i = 0; i < count; i++)
            {
                if (macdLineWithNulls[i].HasValue && signalLineWithNulls[i].HasValue)
                    histogram.Add (macdLineWithNulls[i]!.Value - signalLineWithNulls[i]!.Value);
                else histogram.Add (null);
            }
            return (macdLineWithNulls, signalLineWithNulls, histogram);
        }

        public static decimal StandardDeviation(List<decimal> values)
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
            return (decimal)Math.Sqrt ((double)( sumSq / count ));
        }

        public static List<decimal?> LSMA(List<decimal> data, int period)
        {
            int count = data.Count;
            var result = new List<decimal?> (count);
            for (int i = 0; i < count; i++) result.Add (null);
            if (count < period) return result;

            for (int i = period - 1; i < count; i++)
            {
                decimal sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
                for (int j = 0; j < period; j++)
                {
                    decimal x = j;
                    decimal y = data[i - period + 1 + j];
                    sumX += x;
                    sumY += y;
                    sumXY += x * y;
                    sumX2 += x * x;
                }
                decimal denominator = period * sumX2 - sumX * sumX;
                if (denominator == 0) { result[i] = sumY / period; continue; }
                decimal slope = (period * sumXY - sumX * sumY) / denominator;
                decimal intercept = (sumY - slope * sumX) / period;
                result[i] = intercept + slope * (period - 1);
            }
            return result;
        }

        /// <summary>Расчёт Average True Range (ATR) в процентах от цены.</summary>
        public static List<decimal?> ATRPercent(List<decimal> highs, List<decimal> lows, List<decimal> closes, int period)
        {
            var atr = ATR (highs, lows, closes, period);
            int count = closes.Count;
            var result = new List<decimal?> (count);
            for (int i = 0; i < count; i++)
            {
                if (atr[i].HasValue && closes[i] > 0)
                    result.Add (atr[i]!.Value / closes[i]);
                else
                    result.Add (null);
            }
            return result;
        }

        /// <summary>Расчёт скользящей гистограммы объёма (отношение текущего объёма к SMA объёма).</summary>
        public static List<decimal?> VolumeHistogram(List<decimal> volumes, int period)
        {
            int count = volumes.Count;
            var result = new List<decimal?> (count);
            if (count < period)
            {
                for (int i = 0; i < count; i++) result.Add (null);
                return result;
            }

            for (int i = 0; i < period - 1; i++) result.Add (null);

            decimal sum = 0;
            for (int i = 0; i < period; i++) sum += volumes[i];
            for (int i = period - 1; i < count; i++)
            {
                decimal avg = sum / period;
                result.Add (avg > 0 ? volumes[i] / avg : 1m);
                if (i + 1 < count)
                    sum += volumes[i + 1] - volumes[i - period + 1];
            }
            return result;
        }

        /// <summary>Расчёт On-Balance Volume (OBV) для списка свечей.</summary>
        public static List<decimal> OBV(List<BinanceKline> klines)
        {
            int count = klines?.Count ?? 0;
            var obv = new List<decimal> (count);
            if (count == 0) return obv;
            decimal currentObv = klines[0].Volume;
            obv.Add (currentObv);
            for (int i = 1; i < count; i++)
            {
                if (klines[i].Close > klines[i - 1].Close)
                    currentObv += klines[i].Volume;
                else if (klines[i].Close < klines[i - 1].Close)
                    currentObv -= klines[i].Volume;
                obv.Add (currentObv);
            }
            return obv;
        }
    }
}