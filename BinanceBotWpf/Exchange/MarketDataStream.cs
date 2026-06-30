using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BinanceBotWpf.Exchange
{
    public class MarketDataStream : IDisposable
    {
        private readonly Action<string> _logger;
        private readonly ConcurrentDictionary<string, Action<KlineUpdate>> _klineCallbacks = new ();
        private readonly ConcurrentDictionary<string, Action<OrderBookSnapshot>> _orderBookCallbacks = new ();
        private ReconnectableWebSocket _ws;

        public MarketDataStream (Action<string> logger)
        {
            _logger = logger;
        }

        public Task StartAsync (string[] symbols, string[] intervals)
        {
            List<string> streams = new List<string> ();
            foreach (string symbol in symbols)
            {
                foreach (string interval in intervals)
                {
                    streams.Add ($"{symbol.ToLowerInvariant ()}@kline_{interval}");
                }
            }

            string[] depthSymbols = _orderBookCallbacks.Keys
                .Select (k => k.Split ('_')[0])
                .Distinct ()
                .ToArray ();
            foreach (string symbol in depthSymbols)
            {
                streams.Add ($"{symbol.ToLowerInvariant ()}@depth20@100ms");
            }

            string combinedStreams = string.Join ("/", streams);
            string url = $"wss://stream.binance.com:9443/stream?streams={combinedStreams}";

            _ws = new ReconnectableWebSocket (url, _logger);
            return _ws.StartAsync (MessageLoopAsync);
        }

        public void SubscribeKline (string symbol, string interval, Action<KlineUpdate> callback)
        {
            string key = $"{symbol}_{interval}";
            _klineCallbacks[key] = callback;
        }

        public void UnsubscribeKline (string symbol, string interval)
        {
            string key = $"{symbol}_{interval}";
            _klineCallbacks.TryRemove (key, out _);
        }

        public void SubscribeOrderBook (string symbol, int depthLevels, Action<OrderBookSnapshot> callback)
        {
            string key = $"{symbol}_{depthLevels}";
            _orderBookCallbacks[key] = callback;
        }

        public void UnsubscribeOrderBook (string symbol, int depthLevels)
        {
            string key = $"{symbol}_{depthLevels}";
            _orderBookCallbacks.TryRemove (key, out _);
        }

        private async Task MessageLoopAsync (ClientWebSocket ws, CancellationToken cancellationToken)
        {
            while (ws.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                string json = await ReconnectableWebSocket.ReadFullMessageAsync (ws, cancellationToken);
                if (json == null)
                {
                    break;
                }

                try
                {
                    JObject root = JObject.Parse (json);
                    if (root["stream"] == null)
                    {
                        continue;
                    }

                    string streamName = root["stream"].ToString ();
                    JObject data = root["data"] as JObject;
                    if (data == null)
                    {
                        continue;
                    }

                    if (streamName.Contains ("@kline_"))
                    {
                        HandleKlineMessage (streamName, data);
                    }
                    else if (streamName.Contains ("@depth"))
                    {
                        HandleDepthMessage (streamName, data);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Invoke ($"MarketDataStream: ошибка обработки сообщения: {ex.Message}");
                }
            }
        }

        private void HandleKlineMessage (string streamName, JObject data)
        {
            string symbol = data["s"]?.ToString ();
            JObject kline = data["k"] as JObject;
            if (symbol == null || kline == null)
            {
                return;
            }

            string interval = kline["i"]?.ToString ();
            if (interval == null)
            {
                return;
            }

            string key = $"{symbol}_{interval}";
            if (_klineCallbacks.TryGetValue (key, out Action<KlineUpdate> callback))
            {
                KlineUpdate update = new KlineUpdate
                {
                    Symbol = symbol,
                    Interval = interval,
                    OpenTime = DateTimeOffset.FromUnixTimeMilliseconds (data["E"]?.Value<long>() ?? 0).LocalDateTime,
                    Open = decimal.Parse (kline["o"]?.ToString () ?? "0"),
                    High = decimal.Parse (kline["h"]?.ToString () ?? "0"),
                    Low = decimal.Parse (kline["l"]?.ToString () ?? "0"),
                    Close = decimal.Parse (kline["c"]?.ToString () ?? "0"),
                    Volume = decimal.Parse (kline["v"]?.ToString () ?? "0"),
                    IsFinal = kline["x"]?.Value<bool>() ?? false
                };

                callback.Invoke (update);
            }
        }

        private void HandleDepthMessage (string streamName, JObject data)
        {
            string[] parts = streamName.Split ('@');
            if (parts.Length < 2)
            {
                return;
            }

            string symbol = parts[0].ToUpperInvariant ();
            List<OrderBookSnapshot> snapshots = new List<OrderBookSnapshot> ();

            foreach (KeyValuePair<string, Action<OrderBookSnapshot>> entry in _orderBookCallbacks)
            {
                string[] keyParts = entry.Key.Split ('_');
                if (keyParts[0].Equals (symbol, StringComparison.OrdinalIgnoreCase))
                {
                    List<OrderBookLevel> bids = ParsePriceArray (data["bids"] as JArray);
                    List<OrderBookLevel> asks = ParsePriceArray (data["asks"] as JArray);

                    OrderBookSnapshot snapshot = new OrderBookSnapshot
                    {
                        Symbol = symbol,
                        Bids = bids,
                        Asks = asks
                    };

                    snapshots.Add (snapshot);
                    entry.Value.Invoke (snapshot);
                }
            }
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

        public void Dispose ()
        {
            _ws?.Dispose ();
        }
    }
}
