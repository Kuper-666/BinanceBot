using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace BinanceBotWpf.Exchange
{
    public class OrderBookCache : IOrderBookProvider
    {
        private readonly ConcurrentDictionary<string, OrderBookSnapshot> _snapshots = new ();
        private readonly Action<string> _logger;

        public OrderBookCache (Action<string> logger)
        {
            _logger = logger;
        }

        public void OnDepthUpdate (string symbol, JToken data)
        {
            if (data == null)
            {
                return;
            }

            JArray bidsArray = data["bids"] as JArray;
            JArray asksArray = data["asks"] as JArray;

            List<OrderBookLevel> bids = ParsePriceArray (bidsArray);
            List<OrderBookLevel> asks = ParsePriceArray (asksArray);

            OrderBookSnapshot snapshot = new OrderBookSnapshot
            {
                Symbol = symbol,
                Bids = bids,
                Asks = asks
            };

            _snapshots[symbol] = snapshot;
        }

        public OrderBookSnapshot GetCurrentSnapshot (string symbol)
        {
            _snapshots.TryGetValue (symbol, out OrderBookSnapshot snapshot);
            return snapshot;
        }

        private List<OrderBookLevel> ParsePriceArray (JArray array)
        {
            List<OrderBookLevel> levels = new List<OrderBookLevel> ();
            if (array == null)
            {
                return levels;
            }

            foreach (JToken token in array)
            {
                JArray pair = token as JArray;
                if (pair != null && pair.Count >= 2)
                {
                    levels.Add (new OrderBookLevel
                    {
                        Price = decimal.Parse (pair[0].ToString ()),
                        Quantity = decimal.Parse (pair[1].ToString ())
                    });
                }
            }

            return levels;
        }
    }
}
