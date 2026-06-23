using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public class DashboardWebSocketServer
    {
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private readonly ConcurrentDictionary<WebSocket, HashSet<string>> _clients = new ();
        private readonly Action<string> _logger;

        public bool IsRunning => _listener?.IsListening == true;
        public int ClientCount => _clients.Count;

        public DashboardWebSocketServer (Action<string> logger)
        {
            _logger = logger;
        }

        public async Task StartAsync (int port = 8765)
        {
            _cts = new CancellationTokenSource ();
            _listener = new HttpListener ();
            _listener.Prefixes.Add ($"http://+:{port}/");
            _listener.Start ();
            _logger?.Invoke ($"📡 Dashboard WS server started on port {port}");

            _ = Task.Run (() => AcceptLoopAsync (_cts.Token));
            await Task.CompletedTask;
        }

        public void Stop ()
        {
            _cts?.Cancel ();
            foreach (var client in _clients.Keys.ToList ())
            {
                try { client.CloseAsync (WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait (1000); } catch { }
                try { client.Dispose (); } catch { }
            }
            _clients.Clear ();
            try { _listener?.Stop (); } catch { }
            _listener = null;
            _logger?.Invoke ("📡 Dashboard WS server stopped");
        }

        public async Task BroadcastAsync (string channel, object data)
        {
            if (_clients.IsEmpty)
            {
                return;
            }

            string json = JsonSerializer.Serialize (new { channel, data }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            byte[] bytes = Encoding.UTF8.GetBytes (json);

            var deadClients = new List<WebSocket> ();

            foreach (var kvp in _clients)
            {
                WebSocket ws = kvp.Key;
                HashSet<string> channels = kvp.Value;

                if (!channels.Contains (channel))
                {
                    continue;
                }

                if (ws.State != WebSocketState.Open)
                {
                    deadClients.Add (ws);
                    continue;
                }

                try
                {
                    await ws.SendAsync (new ArraySegment<byte> (bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch
                {
                    deadClients.Add (ws);
                }
            }

            foreach (WebSocket dead in deadClients)
            {
                _clients.TryRemove (dead, out _);
                try { dead.Dispose (); } catch { }
            }
        }

        public void BroadcastPrices (List<Dictionary<string, object>> pairsData)
        {
            _ = BroadcastAsync ("prices", pairsData);
        }

        public void BroadcastPositions (List<Dictionary<string, object>> positions)
        {
            _ = BroadcastAsync ("positions", positions);
        }

        public void BroadcastTrades (List<Dictionary<string, object>> trades)
        {
            _ = BroadcastAsync ("trades", trades);
        }

        public void BroadcastLogs (string logs)
        {
            _ = BroadcastAsync ("logs", logs);
        }

        public void BroadcastEchelons (Dictionary<string, object> echelons)
        {
            _ = BroadcastAsync ("echelons", echelons);
        }

        private async Task AcceptLoopAsync (CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    HttpListenerContext ctx = await _listener.GetContextAsync ();
                    if (ctx.Request.IsWebSocketRequest)
                    {
                        HttpListenerWebSocketContext wsCtx = await ctx.AcceptWebSocketAsync ((string)null);
                        WebSocket ws = wsCtx.WebSocket;
                        _clients.TryAdd (ws, new HashSet<string> ());
                        _ = Task.Run (() => ReceiveLoopAsync (ws, ct));
                    }
                    else
                    {
                        ctx.Response.StatusCode = 400;
                        ctx.Response.Close ();
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.Invoke ($"❌ Dashboard WS accept error: {ex.Message}");
                }
            }
        }

        private async Task ReceiveLoopAsync (WebSocket ws, CancellationToken ct)
        {
            byte[] buffer = new byte[4096];
            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    WebSocketReceiveResult result = await ws.ReceiveAsync (new ArraySegment<byte> (buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                    {
                        string msg = Encoding.UTF8.GetString (buffer, 0, result.Count);
                        HandleClientMessage (ws, msg);
                    }
                }
            }
            catch { }
            finally
            {
                _clients.TryRemove (ws, out _);
                try { ws.Dispose (); } catch { }
            }
        }

        private void HandleClientMessage (WebSocket ws, string msg)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse (msg);
                JsonElement root = doc.RootElement;
                string type = root.TryGetProperty ("type", out JsonElement typeEl) ? typeEl.GetString () : "";
                string channel = root.TryGetProperty ("channel", out JsonElement chEl) ? chEl.GetString () : "";

                if (type == "subscribe" && !string.IsNullOrEmpty (channel))
                {
                    if (_clients.TryGetValue (ws, out HashSet<string> channels))
                    {
                        channels.Add (channel);
                    }
                }
                else if (type == "unsubscribe" && !string.IsNullOrEmpty (channel))
                {
                    if (_clients.TryGetValue (ws, out HashSet<string> channels))
                    {
                        channels.Remove (channel);
                    }
                }
            }
            catch
            {
            }
        }
    }
}
