using System;
using System.Collections.Generic;
using System.Linq;

namespace BinanceBotWpf.Models
{
    public static class TechnicalAnalysis
    {
        public static List<decimal?> SMA(List<decimal> data, int period)
        {
            var result = new List<decimal?> ();
            for (int i = 0; i < data.Count; i++)
            {
                if (i < period - 1) result.Add (null);
                else
                {
                    decimal sum = 0;
                    for (int j = 0; j < period; j++) sum += data[i - j];
                    result.Add (sum / period);
                }
            }
            return result;
        }

        public static List<decimal?> EMA(List<decimal> data, int period)
        {
            var result = new List<decimal?> ();
            if (data.Count == 0) return result;
            decimal multiplier = 2.0m / ( period + 1 );
            decimal? currentEma = data[0];
            result.Add (currentEma);
            for (int i = 1; i < data.Count; i++)
            {
                currentEma = ( data[i] - currentEma ) * multiplier + currentEma;
                result.Add (i < period - 1 ? null : currentEma);
            }
            return result;
        }

        public static List<decimal?> RSI(List<decimal> data, int period)
        {
            var result = Enumerable.Repeat ((decimal?)null, data.Count).ToList ();
            if (data.Count <= period) return result;

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

            for (int i = period + 1; i < data.Count; i++)
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
            var upper = Enumerable.Repeat ((decimal?)null, data.Count).ToList ();
            var middle = SMA (data, period);
            var lower = Enumerable.Repeat ((decimal?)null, data.Count).ToList ();

            for (int i = period - 1; i < data.Count; i++)
            {
                decimal? mid = middle[i];
                if (mid.HasValue)
                {
                    decimal sumOfSquares = 0;
                    for (int j = 0; j < period; j++) sumOfSquares += (decimal)Math.Pow ((double)( data[i - j] - mid.Value ), 2);
                    decimal stdDev = (decimal)Math.Sqrt ((double)( sumOfSquares / period ));
                    upper[i] = mid.Value + k * stdDev;
                    lower[i] = mid.Value - k * stdDev;
                }
            }
            return (upper, middle, lower);
        }

        public static List<decimal?> ATR(List<decimal> highs, List<decimal> lows, List<decimal> closes, int period)
        {
            var result = Enumerable.Repeat ((decimal?)null, highs.Count).ToList ();
            if (highs.Count <= 1) return result;

            var tr = new List<decimal> { highs[0] - lows[0] };
            for (int i = 1; i < highs.Count; i++)
            {
                decimal tr1 = highs[i] - lows[i];
                decimal tr2 = Math.Abs (highs[i] - closes[i - 1]);
                decimal tr3 = Math.Abs (lows[i] - closes[i - 1]);
                tr.Add (Math.Max (tr1, Math.Max (tr2, tr3)));
            }

            decimal currentAtr = tr.Take (period).Average ();
            result[period - 1] = currentAtr;
            for (int i = period; i < tr.Count; i++)
            {
                currentAtr = ( currentAtr * ( period - 1 ) + tr[i] ) / period;
                result[i] = currentAtr;
            }
            return result;
        }

        public static (List<decimal?> MacdLine, List<decimal?> SignalLine, List<decimal?> Histogram) MACD(List<decimal> data, int fast = 12, int slow = 26, int signal = 9)
        {
            var fastEma = EMA (data, fast);
            var slowEma = EMA (data, slow);

            var macdLine = new List<decimal> ();
            var macdLineWithNulls = new List<decimal?> ();
            for (int i = 0; i < data.Count; i++)
            {
                if (fastEma[i].HasValue && slowEma[i].HasValue)
                {
                    decimal val = fastEma[i]!.Value - slowEma[i]!.Value;
                    macdLine.Add (val);
                    macdLineWithNulls.Add (val);
                }
                else macdLineWithNulls.Add (null);
            }

            var signalEma = EMA (macdLine, signal);
            var signalLineWithNulls = Enumerable.Repeat ((decimal?)null, data.Count - signalEma.Count).Concat (signalEma).ToList ();
            var histogram = new List<decimal?> ();
            for (int i = 0; i < data.Count; i++)
            {
                if (macdLineWithNulls[i].HasValue && signalLineWithNulls[i].HasValue)
                    histogram.Add (macdLineWithNulls[i]!.Value - signalLineWithNulls[i]!.Value);
                else histogram.Add (null);
            }
            return (macdLineWithNulls, signalLineWithNulls, histogram);
        }

        public static decimal StandardDeviation(List<decimal> values)
        {
            if (values == null || values.Count == 0) return 0;
            decimal avg = values.Average ();
            decimal sumSq = values.Select (v => ( v - avg ) * ( v - avg )).Sum ();
            return (decimal)Math.Sqrt ((double)( sumSq / values.Count ));
        }

        public static List<decimal?> LSMA(List<decimal> data, int period)
        {
            var result = Enumerable.Repeat ((decimal?)null, data.Count).ToList ();
            if (data.Count < period) return result;

            for (int i = period - 1; i < data.Count; i++)
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
            var result = new List<decimal?> ();
            for (int i = 0; i < closes.Count; i++)
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
            var result = Enumerable.Repeat ((decimal?)null, volumes.Count).ToList ();
            if (volumes.Count < period) return result;

            decimal sum = 0;
            for (int i = 0; i < period; i++) sum += volumes[i];
            for (int i = period - 1; i < volumes.Count; i++)
            {
                decimal avg = sum / period;
                result[i] = avg > 0 ? volumes[i] / avg : 1m;
                if (i + 1 < volumes.Count)
                {
                    sum += volumes[i + 1] - volumes[i - period + 1];
                }
            }
            return result;
        }

        /// <summary>Расчёт On-Balance Volume (OBV) для списка свечей.</summary>
        public static List<decimal> OBV(List<BinanceKline> klines)
        {
            var obv = new List<decimal> ();
            if (klines == null || klines.Count == 0) return obv;
            decimal currentObv = 0;
            for (int i = 0; i < klines.Count; i++)
            {
                if (i == 0) currentObv = klines[i].Volume;
                else
                {
                    if (klines[i].Close > klines[i - 1].Close)
                        currentObv += klines[i].Volume;
                    else if (klines[i].Close < klines[i - 1].Close)
                        currentObv -= klines[i].Volume;
                    // если цена не изменилась – OBV не меняется
                }
                obv.Add (currentObv);
            }
            return obv;
        }
    }
}