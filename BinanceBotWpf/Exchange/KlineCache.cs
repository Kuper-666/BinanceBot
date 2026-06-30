using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace BinanceBotWpf.Exchange
{
    public class KlineCache
    {
        private readonly ConcurrentDictionary<string, List<BinanceKline>> _realTimeData = new ();
        private readonly ConcurrentDictionary<string, DateTime> _lastUpdateTime = new ();
        private readonly Action<string> _logger;

        public KlineCache (Action<string> logger)
        {
            _logger = logger;
        }

        public System.Threading.Tasks.Task<List<BinanceKline>> GetKlinesAsync (string symbol, string interval, int limit)
        {
            string key = $"{symbol}_{interval}";
            if (_realTimeData.TryGetValue (key, out List<BinanceKline> klines))
            {
                int count = Math.Min (limit, klines.Count);
                List<BinanceKline> result = klines.Skip (klines.Count - count).Take (count).ToList ();
                return System.Threading.Tasks.Task.FromResult (result);
            }

            return System.Threading.Tasks.Task.FromResult (new List<BinanceKline> ());
        }

        public void OnKlineUpdate (KlineUpdate update)
        {
            string key = $"{update.Symbol}_{update.Interval}";
            List<BinanceKline> klines = _realTimeData.GetOrAdd (key, _ => new List<BinanceKline> ());

            if (update.IsFinal)
            {
                BinanceKline newKline = new BinanceKline
                {
                    OpenTime = update.OpenTime,
                    Open = update.Open,
                    High = update.High,
                    Low = update.Low,
                    Close = update.Close,
                    Volume = update.Volume
                };

                klines.Add (newKline);

                if (klines.Count > 1000)
                {
                    klines.RemoveRange (0, klines.Count - 1000);
                }
            }
            else if (klines.Count > 0)
            {
                BinanceKline lastKline = klines[klines.Count - 1];
                lastKline.High = Math.Max (lastKline.High, update.High);
                lastKline.Low = Math.Min (lastKline.Low, update.Low);
                lastKline.Close = update.Close;
                lastKline.Volume = update.Volume;
            }

            _lastUpdateTime[key] = DateTime.UtcNow;
        }

        public void SeedFromRest (string symbol, string interval, List<BinanceKline> historical)
        {
            string key = $"{symbol}_{interval}";
            if (historical == null || historical.Count == 0)
            {
                return;
            }

            List<BinanceKline> klines = _realTimeData.GetOrAdd (key, _ => new List<BinanceKline> ());
            foreach (BinanceKline kline in historical)
            {
                bool exists = klines.Any (k => k.OpenTime == kline.OpenTime);
                if (!exists)
                {
                    klines.Add (kline);
                }
            }

            klines.Sort ((a, b) => a.OpenTime.CompareTo (b.OpenTime));

            if (klines.Count > 1000)
            {
                klines.RemoveRange (0, klines.Count - 1000);
            }

            _lastUpdateTime[key] = DateTime.UtcNow;
        }

        public bool HasRealTimeData (string symbol, string interval)
        {
            string key = $"{symbol}_{interval}";
            if (_lastUpdateTime.TryGetValue (key, out DateTime lastUpdate))
            {
                return (DateTime.UtcNow - lastUpdate).TotalSeconds < 60;
            }

            return false;
        }
    }
}
