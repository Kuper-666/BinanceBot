using System;
using System.Collections.Generic;
using System.Linq;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services
{
    public class AdaptiveAgent
    {
        private readonly Action<string> _logger;
        private readonly decimal _slMultiplier;
        private readonly decimal _periodMultiplier;
        private decimal _lastAdaptiveFactor = 1.0m;
        private DateTime _lastCalculation = DateTime.MinValue;
        private readonly TimeSpan _cooldown = TimeSpan.FromMinutes (5);

        public decimal AdaptiveFactor => _lastAdaptiveFactor;

        public AdaptiveAgent (Action<string> logger, decimal slMultiplier = 0.4m, decimal periodMultiplier = 0.3m)
        {
            _logger = logger;
            _slMultiplier = slMultiplier;
            _periodMultiplier = periodMultiplier;
        }

        public AdaptiveResult Calculate (List<BinanceKline> klines)
        {
            var result = new AdaptiveResult ();
            if (klines == null || klines.Count < 100)
            {
                result.Factor = 1.0m;
                result.LsmaWindowMultiplier = 1.0m;
                result.SlMultiplier = 1.0m;
                result.Regime = "Normal";
                return result;
            }

            if (DateTime.UtcNow - _lastCalculation < _cooldown)
            {
                result.Factor = _lastAdaptiveFactor;
                result.LsmaWindowMultiplier = 1.0m;
                result.SlMultiplier = 1.0m;
                result.Regime = "Cooldown";
                return result;
            }

            var closes = klines.Select (k => k.Close).ToList ();
            var highs = klines.Select (k => k.High).ToList ();
            var lows = klines.Select (k => k.Low).ToList ();
            var volumes = klines.Select (k => k.Volume).ToList ();
            int lookback = Math.Min (100, klines.Count);

            decimal currentAtr = CalculateCurrentATR (highs, lows, closes, 14);
            decimal avgAtr = CalculateAverageATR (highs, lows, closes, 14, lookback);
            decimal atrRatio = avgAtr > 0 ? currentAtr / avgAtr : 1.0m;
            result.CurrentAtr = currentAtr;
            result.AvgAtr = avgAtr;

            decimal recentVolumeAvg = volumes.Skip (Math.Max (0, volumes.Count - 20)).Average ();
            decimal olderVolumeAvg = volumes.Skip (Math.Max (0, volumes.Count - 50)).Take (30).Average ();
            decimal volumeChange = olderVolumeAvg > 0 ? recentVolumeAvg / olderVolumeAvg : 1.0m;
            result.VolumeChange = volumeChange;

            decimal priceVolatility = CalculatePriceVolatility (closes, lookback);
            result.PriceVolatility = priceVolatility;

            decimal adx = CalculateADX (highs, lows, closes, 14);
            result.Adx = adx;
            decimal trendStrength = MapRange (adx, 0m, 60m, 0.7m, 1.3m);
            result.TrendStrength = trendStrength;

            decimal atrFactor = MapRange (atrRatio, 0.5m, 2.0m, 0.7m, 1.5m);
            decimal volumeFactor = MapRange (volumeChange, 0.5m, 2.0m, 0.8m, 1.3m);
            decimal volatilityFactor = MapRange (priceVolatility, 0.005m, 0.05m, 0.8m, 1.4m);

            decimal factor = (atrFactor * 0.36m + volumeFactor * 0.27m + volatilityFactor * 0.27m + trendStrength * 0.10m);
            factor = Math.Clamp (factor, 0.5m, 1.5m);

            result.Factor = factor;
            result.AtrFactor = atrFactor;
            result.VolumeFactor = volumeFactor;
            result.VolatilityFactor = volatilityFactor;
            result.TrendStrengthFactor = trendStrength;
            result.LsmaWindowMultiplier = 1.0m + (factor - 1.0m) * _periodMultiplier;
            result.SlMultiplier = 1.0m + (factor - 1.0m) * _slMultiplier;

            if (factor > 1.2m)
                result.Regime = "High Volatility";
            else if (factor < 0.8m)
                result.Regime = "Low Volatility";
            else
                result.Regime = "Normal";

            _lastAdaptiveFactor = factor;
            _lastCalculation = DateTime.UtcNow;

            _logger?.Invoke ($"🔧 AdaptiveAgent: factor={factor:F3} (ATR={atrRatio:F2}x, Vol={volumeChange:F2}x, σ={priceVolatility:F4}, ADX={adx:F1}) regime={result.Regime}");

            return result;
        }

        private decimal CalculateCurrentATR (List<decimal> highs, List<decimal> lows, List<decimal> closes, int period)
        {
            var atrList = TechnicalAnalysis.ATR (highs, lows, closes, period);
            return atrList.LastOrDefault () ?? 0;
        }

        private decimal CalculateAverageATR (List<decimal> highs, List<decimal> lows, List<decimal> closes, int period, int lookback)
        {
            var atrList = TechnicalAnalysis.ATR (highs, lows, closes, period);
            var valid = atrList.Where (v => v.HasValue).TakeLast (lookback / 2).Select (v => v.Value).ToList ();
            return valid.Count > 0 ? valid.Average () : 0;
        }

        private decimal CalculatePriceVolatility (List<decimal> closes, int lookback)
        {
            int start = Math.Max (0, closes.Count - lookback);
            var slice = closes.Skip (start).ToList ();
            if (slice.Count < 2) return 0;

            decimal mean = slice.Average ();
            decimal sumSq = slice.Select (v => (v - mean) * (v - mean)).Sum ();
            decimal stdDev = (decimal)Math.Sqrt ((double)(sumSq / slice.Count));
            return mean > 0 ? stdDev / mean : 0;
        }

        private static decimal MapRange (decimal value, decimal fromLow, decimal fromHigh, decimal toLow, decimal toHigh)
        {
            value = Math.Clamp (value, fromLow, fromHigh);
            decimal t = (value - fromLow) / (fromHigh - fromLow);
            return toLow + t * (toHigh - toLow);
        }

        private decimal CalculateADX (List<decimal> highs, List<decimal> lows, List<decimal> closes, int period)
        {
            if (highs.Count < period + 1 || lows.Count < period + 1 || closes.Count < period + 1)
            {
                return 25m;
            }

            var trList = new List<decimal> ();
            var plusDmList = new List<decimal> ();
            var minusDmList = new List<decimal> ();

            for (int i = 1; i < highs.Count; i++)
            {
                decimal highDiff = highs[i] - highs[i - 1];
                decimal lowDiff = lows[i - 1] - lows[i];
                decimal tr = Math.Max (highs[i] - lows[i], Math.Max (Math.Abs (highs[i] - closes[i - 1]), Math.Abs (lows[i] - closes[i - 1])));
                trList.Add (tr);
                plusDmList.Add (highDiff > lowDiff && highDiff > 0 ? highDiff : 0m);
                minusDmList.Add (lowDiff > highDiff && lowDiff > 0 ? lowDiff : 0m);
            }

            decimal smoothTr = 0m;
            decimal smoothPlusDm = 0m;
            decimal smoothMinusDm = 0m;

            int startIdx = period;
            if (startIdx > trList.Count)
            {
                return 25m;
            }

            for (int i = 0; i < period && i < trList.Count; i++)
            {
                smoothTr += trList[i];
                smoothPlusDm += plusDmList[i];
                smoothMinusDm += minusDmList[i];
            }

            var dxList = new List<decimal> ();

            for (int i = period; i < trList.Count; i++)
            {
                smoothTr = smoothTr - smoothTr / period + trList[i];
                smoothPlusDm = smoothPlusDm - smoothPlusDm / period + plusDmList[i];
                smoothMinusDm = smoothMinusDm - smoothMinusDm / period + minusDmList[i];

                decimal plusDi = smoothTr > 0 ? (smoothPlusDm / smoothTr) * 100m : 0m;
                decimal minusDi = smoothTr > 0 ? (smoothMinusDm / smoothTr) * 100m : 0m;
                decimal diSum = plusDi + minusDi;
                decimal dx = diSum > 0 ? Math.Abs (plusDi - minusDi) / diSum * 100m : 0m;
                dxList.Add (dx);
            }

            if (dxList.Count == 0)
            {
                return 25m;
            }

            decimal adx = dxList.Average ();
            return Math.Clamp (adx, 0m, 100m);
        }
    }

    public class AdaptiveResult
    {
        public decimal Factor { get; set; } = 1.0m;
        public decimal LsmaWindowMultiplier { get; set; } = 1.0m;
        public decimal SlMultiplier { get; set; } = 1.0m;
        public string Regime { get; set; } = "Normal";
        public decimal CurrentAtr { get; set; }
        public decimal AvgAtr { get; set; }
        public decimal VolumeChange { get; set; }
        public decimal PriceVolatility { get; set; }
        public decimal AtrFactor { get; set; }
        public decimal VolumeFactor { get; set; }
        public decimal VolatilityFactor { get; set; }
        public decimal Adx { get; set; }
        public decimal TrendStrength { get; set; }
        public decimal TrendStrengthFactor { get; set; }
    }
}
