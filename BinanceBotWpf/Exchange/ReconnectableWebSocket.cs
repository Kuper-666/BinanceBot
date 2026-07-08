using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BinanceBotWpf.Exchange
{
    public class ReconnectableWebSocket : IDisposable
    {
        private readonly string _url;
        private readonly Action<string> _logger;
        private int _reconnectDelay = 1000;
        private const int MaxReconnectDelay = 30000;
        private CancellationTokenSource _cts;
        private ClientWebSocket _ws;
        private bool _disposed;

        public ReconnectableWebSocket (string url, Action<string> logger)
        {
            _url = url;
            _logger = logger;
        }

        public async Task StartAsync (Func<ClientWebSocket, CancellationToken, Task> messageLoop)
        {
            if (_disposed)
            {
                return;
            }

            _cts?.Cancel ();
            _cts?.Dispose ();
            _cts = new CancellationTokenSource ();
            CancellationToken token = _cts.Token;

            _ = Task.Run (async () =>
            {
                while (!token.IsCancellationRequested && !_disposed)
                {
                    _ws = new ClientWebSocket ();
                    try
                    {
                        await _ws.ConnectAsync (new Uri (_url), token);
                        _logger?.Invoke ($"WebSocket подключён: {_url}");
                        _reconnectDelay = 1000;

                        await messageLoop (_ws, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        string msg = ex.Message;
                        bool isNormalDisconnect = msg.Contains ("remote party closed") ||
                                                  msg.Contains ("Graceful") ||
                                                  (ex is WebSocketException wsEx && wsEx.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely);
                        _logger?.Invoke (isNormalDisconnect
                            ? $"WebSocket: нормальное закрытие соединения"
                            : $"WebSocket ошибка: {msg}");
                    }
                    finally
                    {
                        try
                        {
                            if (_ws != null &&
                                (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived))
                            {
                                using CancellationTokenSource closeCts = new CancellationTokenSource (TimeSpan.FromSeconds (5));
                                await _ws.CloseAsync (WebSocketCloseStatus.NormalClosure, "", closeCts.Token);
                            }
                        }
                        catch
                        {
                        }

                        try
                        {
                            _ws?.Dispose ();
                        }
                        catch
                        {
                        }

                        _ws = null;
                    }

                    if (!token.IsCancellationRequested && !_disposed)
                    {
                        _logger?.Invoke ($"WebSocket: переподключение через {_reconnectDelay / 1000}с...");
                        try
                        {
                            await Task.Delay (_reconnectDelay, token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }

                        _reconnectDelay = Math.Min (_reconnectDelay * 2, MaxReconnectDelay);
                    }
                }
            }, token);

            await Task.CompletedTask;
        }

        public async Task StopAsync ()
        {
            if (_cts != null)
            {
                await _cts.CancelAsync ();
            }

            if (_ws != null &&
                (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived))
            {
                try
                {
                    using CancellationTokenSource closeCts = new CancellationTokenSource (TimeSpan.FromSeconds (5));
                    await _ws.CloseAsync (WebSocketCloseStatus.NormalClosure, "", closeCts.Token);
                }
                catch
                {
                }
            }
        }

        public void Dispose ()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _cts?.Cancel ();
                _cts?.Dispose ();
            }
            catch
            {
            }

            try
            {
                if (_ws != null &&
                    (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived))
                {
                    _ = _ws.CloseAsync (WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
            }
            catch
            {
            }
            finally
            {
                try
                {
                    _ws?.Dispose ();
                }
                catch
                {
                }
            }
        }

        public static async Task<string> ReadFullMessageAsync (ClientWebSocket ws, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[4096];
            WebSocketReceiveResult result = await ws.ReceiveAsync (new ArraySegment<byte> (buffer), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString (buffer, 0, result.Count);
            }

            using System.IO.MemoryStream ms = new System.IO.MemoryStream ();
            ms.Write (buffer, 0, result.Count);
            while (!result.EndOfMessage && !cancellationToken.IsCancellationRequested)
            {
                result = await ws.ReceiveAsync (new ArraySegment<byte> (buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    ms.Write (buffer, 0, result.Count);
                }
            }

            return Encoding.UTF8.GetString (ms.ToArray ());
        }
    }
}
