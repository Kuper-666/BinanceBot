using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public class WebSocketPriceManager : IDisposable
    {
        private readonly Action<string> _logger;
        private readonly ConcurrentDictionary<string, decimal> _currentPrices = new ();
        private readonly ConcurrentDictionary<string, ClientWebSocket> _sockets = new ();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _ctsDict = new ();
        private readonly string _wsBaseUrl;
        private bool _disposed;

        public WebSocketPriceManager (Action<string> logger, bool useFuturesEndpoint = false)
        {
            _logger = logger;
            _wsBaseUrl = useFuturesEndpoint
                ? "wss://fstream.binance.com/ws"
                : "wss://stream.binance.com:9443/ws";
        }

        public async Task SubscribeToSymbolsAsync(string[] symbols)
        {
            foreach (var symbol in symbols)
            {
                if (_sockets.ContainsKey (symbol)) continue;

                var cts = new CancellationTokenSource ();
                _ctsDict[symbol] = cts;

                _ = Task.Run (() => ConnectAndListen (symbol.ToUpperInvariant (), cts.Token));
                await Task.Delay (100);
            }
        }

        private async Task ConnectAndListen(string symbol, CancellationToken cancellationToken)
        {
            string streamName = $"{symbol.ToLowerInvariant ()}@ticker";
            string url = $"{_wsBaseUrl}/{streamName}";

            int reconnectDelay = 1000;
            const int maxReconnectDelay = 30000;

            while (!cancellationToken.IsCancellationRequested)
            {
                var ws = new ClientWebSocket ();
                _sockets[symbol] = ws;

                try
                {
                    await ws.ConnectAsync (new Uri (url), cancellationToken);
                    _logger?.Invoke ($"✅ WebSocket подключён к {symbol}");
                    reconnectDelay = 1000; // Сброс задержки при успешном подключении

                    var buffer = new byte[4096];

                    while (ws.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                    {
                        var result = await ws.ReceiveAsync (new ArraySegment<byte> (buffer), cancellationToken);

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            var json = Encoding.UTF8.GetString (buffer, 0, result.Count);
                            using var doc = JsonDocument.Parse (json);

                            if (doc.RootElement.TryGetProperty ("c", out var priceElement))
                            {
                                string priceStr = priceElement.GetString ();
                                if (decimal.TryParse (priceStr, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out decimal price))
                                {
                                    _currentPrices[symbol] = price;
                                }
                            }
                        }
                        else if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await ws.CloseAsync (WebSocketCloseStatus.NormalClosure, "", cancellationToken);
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.Invoke ($"❌ WebSocket ошибка {symbol}: {ex.Message}");
                }
                finally
                {
                    _sockets.TryRemove (symbol, out _);
                    try { ws.Dispose (); } catch { }
                }

                // Авто-переподключение с экспоненциальной задержкой
                if (!cancellationToken.IsCancellationRequested)
                {
                    _logger?.Invoke ($"🔄 WebSocket: переподключение к {symbol} через {reconnectDelay / 1000}с...");
                    await Task.Delay (reconnectDelay, cancellationToken);
                    reconnectDelay = Math.Min (reconnectDelay * 2, maxReconnectDelay);
                }
            }
        }

        public decimal GetCurrentPrice(string symbol)
        {
            _currentPrices.TryGetValue (symbol.ToUpperInvariant (), out var price);
            return price;
        }

        public void UpdatePrice(string symbol, decimal price)
        {
            _currentPrices[symbol.ToUpperInvariant ()] = price;
        }

        public string[] GetSubscribedSymbols()
        {
            return _sockets.Keys.ToArray ();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var cts in _ctsDict.Values)
            {
                try
                {
                    cts.Cancel ();
                    cts.Dispose ();
                }
                catch { }
            }

            foreach (var ws in _sockets.Values)
            {
                try
                {
                    if (ws.State == WebSocketState.Open)
                    {
                        ws.CloseAsync (WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait (1000);
                    }
                    ws.Dispose ();
                }
                catch { }
            }

            _sockets.Clear ();
            _ctsDict.Clear ();
        }
    }
}