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

            decimal atrFactor = MapRange (atrRatio, 0.5m, 2.0m, 0.7m, 1.5m);
            decimal volumeFactor = MapRange (volumeChange, 0.5m, 2.0m, 0.8m, 1.3m);
            decimal volatilityFactor = MapRange (priceVolatility, 0.005m, 0.05m, 0.8m, 1.4m);

            decimal factor = (atrFactor * 0.4m + volumeFactor * 0.3m + volatilityFactor * 0.3m);
            factor = Math.Clamp (factor, 0.5m, 1.5m);

            result.Factor = factor;
            result.AtrFactor = atrFactor;
            result.VolumeFactor = volumeFactor;
            result.VolatilityFactor = volatilityFactor;
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

            _logger?.Invoke ($"🔧 AdaptiveAgent: factor={factor:F3} (ATR={atrRatio:F2}x, Vol={volumeChange:F2}x, σ={priceVolatility:F4}) regime={result.Regime}");

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
    }
}
