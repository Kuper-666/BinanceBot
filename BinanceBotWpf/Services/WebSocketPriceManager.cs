using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private readonly ConcurrentDictionary<string, DateTime> _lastMessageTime = new ();
        private readonly ConcurrentDictionary<string, ClientWebSocket> _sockets = new ();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _ctsDict = new ();
        private readonly string _wsBaseUrl;
        private bool _disposed;
        private CancellationTokenSource _restPollCts;
        private Task _restPollTask;
        private Func<string, Task<decimal>> _restPriceFetcher;

        /// <summary>
        /// Максимальный допустимый возраст цены в секундах. После этого цена считается протухшей.
        /// </summary>
        public int MaxPriceAgeSeconds { get; set; } = 30;

        public WebSocketPriceManager (Action<string> logger, bool useFuturesEndpoint = false)
        {
            _logger = logger;
            _wsBaseUrl = useFuturesEndpoint
                ? "wss://fstream.binance.com/ws"
                : "wss://stream.binance.com:9443/ws";
        }

        public async Task SubscribeToSymbolsAsync(string[] symbols)
        {
            if (_disposed) return;
            foreach (var symbol in symbols)
            {
                if (_disposed) break;
                if (_sockets.ContainsKey (symbol)) continue;

                // Dispose old CTS if re-subscribing
                if (_ctsDict.TryRemove (symbol, out var oldCts))
                {
                    try { oldCts.Cancel (); oldCts.Dispose (); } catch { }
                }

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

            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                var ws = new ClientWebSocket ();
                _sockets[symbol] = ws;

                try
                {
                    await ws.ConnectAsync (new Uri (url), cancellationToken);
                    _logger?.Invoke ($"✅ WebSocket подключён к {symbol}");
                    reconnectDelay = 1000;

                    var buffer = new byte[4096];

                    while (ws.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                    {
                        var result = await ws.ReceiveAsync (new ArraySegment<byte> (buffer), cancellationToken);

                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            // Handle multi-frame messages
                            string json;
                            if (result.EndOfMessage)
                            {
                                json = Encoding.UTF8.GetString (buffer, 0, result.Count);
                            }
                            else
                            {
                                using var ms = new System.IO.MemoryStream ();
                                ms.Write (buffer, 0, result.Count);
                                while (!result.EndOfMessage && !cancellationToken.IsCancellationRequested)
                                {
                                    result = await ws.ReceiveAsync (new ArraySegment<byte> (buffer), cancellationToken);
                                    if (result.MessageType == WebSocketMessageType.Text)
                                        ms.Write (buffer, 0, result.Count);
                                }
                                json = Encoding.UTF8.GetString (ms.ToArray ());
                            }

                            using var doc = JsonDocument.Parse (json);
                            if (doc.RootElement.TryGetProperty ("c", out var priceElement))
                            {
                                string priceStr = priceElement.GetString ();
                                if (decimal.TryParse (priceStr, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out decimal price))
                                {
                                    _currentPrices[symbol] = price;
                                    _lastMessageTime[symbol] = DateTime.UtcNow;
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

                if (!cancellationToken.IsCancellationRequested && !_disposed)
                {
                    _logger?.Invoke ($"🔄 WebSocket: переподключение к {symbol} через {reconnectDelay / 1000}с...");
                    try { await Task.Delay (reconnectDelay, cancellationToken); } catch (OperationCanceledException) { break; }
                    reconnectDelay = Math.Min (reconnectDelay * 2, maxReconnectDelay);
                }
            }
        }

        public decimal GetCurrentPrice(string symbol)
        {
            _currentPrices.TryGetValue (symbol.ToUpperInvariant (), out var price);
            return price;
        }

        /// <summary>
        /// Проверяет, актуальна ли цена для символа (получена недавно).
        /// </summary>
        public bool IsPriceFresh(string symbol)
        {
            if (!_lastMessageTime.TryGetValue (symbol.ToUpperInvariant (), out var lastTime))
                return false;
            return (DateTime.UtcNow - lastTime).TotalSeconds <= MaxPriceAgeSeconds;
        }

        /// <summary>
        /// Возвращает возраст цены в секундах (0 если нет данных).
        /// </summary>
        public double GetPriceAgeSeconds(string symbol)
        {
            if (!_lastMessageTime.TryGetValue (symbol.ToUpperInvariant (), out var lastTime))
                return -1;
            return (DateTime.UtcNow - lastTime).TotalSeconds;
        }

        /// <summary>
        /// Возвращает список символов с протухшими ценами.
        /// </summary>
        public string[] GetStaleSymbols()
        {
            var now = DateTime.UtcNow;
            var stale = new List<string> ();
            foreach (var kvp in _lastMessageTime)
            {
                if ((now - kvp.Value).TotalSeconds > MaxPriceAgeSeconds)
                    stale.Add (kvp.Key);
            }
            return stale.ToArray ();
        }

        public void UpdatePrice(string symbol, decimal price)
        {
            _currentPrices[symbol.ToUpperInvariant ()] = price;
            _lastMessageTime[symbol.ToUpperInvariant ()] = DateTime.UtcNow;
        }

        public string[] GetSubscribedSymbols()
        {
            return _sockets.Keys.ToArray ();
        }

        /// <summary>
        /// Запускает периодический REST-опрос цен для всех подписанных символов.
        /// Используется как fallback когда WS не получает данные.
        /// </summary>
        public void StartPeriodicRestFetch (Func<string, Task<decimal>> priceFetcher, int intervalMs = 5000)
        {
            StopPeriodicRestFetch ();
            _restPriceFetcher = priceFetcher;
            _restPollCts = new CancellationTokenSource ();
            _restPollTask = Task.Run (async () =>
            {
                while (!_restPollCts.Token.IsCancellationRequested && !_disposed)
                {
                    try { await Task.Delay (intervalMs, _restPollCts.Token); } catch (OperationCanceledException) { break; }

                    string[] symbols = GetSubscribedSymbols ();
                    foreach (string symbol in symbols)
                    {
                        if (_disposed || _restPollCts.Token.IsCancellationRequested) break;

                        if (IsPriceFresh (symbol)) continue;

                        try
                        {
                            decimal price = await priceFetcher (symbol);
                            if (price > 0)
                            {
                                UpdatePrice (symbol, price);
                            }
                        }
                        catch { }
                    }
                }
            });
        }

        public void StopPeriodicRestFetch ()
        {
            try { _restPollCts?.Cancel (); } catch { }
            try { _restPollCts?.Dispose (); } catch { }
            _restPollCts = null;
            _restPollTask = null;
        }

        /// <summary>
        /// Принудительно переподключает символы с протухшими ценами.
        /// Закрывает зависший сокет, чтобы ConnectAndListen запустил переподключение.
        /// Возвращает количество переподключённых символов.
        /// </summary>
        public int ForceReconnectStaleSymbols ()
        {
            string[] staleSymbols = GetStaleSymbols ();
            int reconnected = 0;

            foreach (string symbol in staleSymbols)
            {
                if (_sockets.TryRemove (symbol, out var ws))
                {
                    try
                    {
                        if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                        {
                            _ = ws.CloseAsync (WebSocketCloseStatus.NormalClosure, "stale price reconnect", CancellationToken.None);
                        }
                    }
                    catch { }
                    finally
                    {
                        try { ws.Dispose (); } catch { }
                    }

                    // Запускаем новое подключение через старый CTS (ConnectAndListen переподключится автоматически)
                    if (_ctsDict.TryGetValue (symbol, out var cts) && !cts.IsCancellationRequested)
                    {
                        _logger?.Invoke ($"🔄 WebSocket: принудительное переподключение к {symbol} (цена протухла)");
                        reconnected++;
                    }
                }
            }

            return reconnected;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopPeriodicRestFetch ();

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
                    if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                    {
                        _ = ws.CloseAsync (WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    }
                }
                catch { }
                finally
                {
                    try { ws.Dispose (); } catch { }
                }
            }

            _sockets.Clear ();
            _ctsDict.Clear ();
        }
    }
}