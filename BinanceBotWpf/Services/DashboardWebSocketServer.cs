using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BinanceBotWpf.Services
{
    public class DashboardWebSocketServer
    {
        public const string ApiVersion = "1.0.0";
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private readonly ConcurrentDictionary<WebSocket, HashSet<string>> _clients = new ();
        private readonly ConcurrentDictionary<WebSocket, (int Count, DateTime WindowStart)> _rateLimits = new ();
        private readonly ILogger<DashboardWebSocketServer> _log;
        private const int MaxMessagesPerSecond = 50;

        public bool IsRunning => _listener?.IsListening == true;
        public int ClientCount => _clients.Count;
        public Func<string, Dictionary<string, object>, Task> OnCommand { get; set; }

        public DashboardWebSocketServer (ILogger<DashboardWebSocketServer> logger)
        {
            _log = logger;
        }

        public async Task StartAsync (int port = 8765)
        {
            _cts = new CancellationTokenSource ();
            _listener = new HttpListener ();
            _listener.Prefixes.Add ($"http://localhost:{port}/");
            _listener.Start ();
            _log.LogInformation ("Dashboard WS server started on port {Port}", port);

            _ = Task.Run (() => AcceptLoopAsync (_cts.Token));

            // Авто-открытие браузера
            try
            {
                Process.Start (new ProcessStartInfo
                {
                    FileName = $"http://localhost:{port}",
                    UseShellExecute = true
                });
            }
            catch { }

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
            _log.LogInformation ("Dashboard WS server stopped");
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

        public void BroadcastEquity (List<Dictionary<string, object>> equityPoints)
        {
            _ = BroadcastAsync ("equity", equityPoints);
        }

        public void BroadcastStats (Dictionary<string, object> stats)
        {
            _ = BroadcastAsync ("stats", stats);
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
                        _ = SendAsync (ws, new { channel = "welcome", data = new { apiVersion = ApiVersion, serverTime = DateTime.UtcNow.ToString ("o") } });
                        _ = Task.Run (() => ReceiveLoopAsync (ws, ct));
                    }
                    else
                    {
                        ServeStaticFile (ctx);
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
                    _log.LogError (ex, "Dashboard WS accept error");
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
            if (IsRateLimited (ws))
            {
                return;
            }

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
                else if (type == "ping")
                {
                    _ = SendAsync (ws, new { channel = "pong", data = new { ts = DateTime.UtcNow.ToString ("o") } });
                }
                else if (type == "command" || type == "settings")
                {
                    string action = root.TryGetProperty ("action", out JsonElement actionEl) ? actionEl.GetString () : type;
                    var data = new Dictionary<string, object> ();
                    if (root.TryGetProperty ("data", out JsonElement dataEl) && dataEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in dataEl.EnumerateObject ())
                        {
                            data[prop.Name] = prop.Value.ToString ();
                        }
                    }
                    _ = OnCommand?.Invoke (action, data);
                    _ = SendAsync (ws, new { channel = "command_ack", data = new { action, ok = true } });
                }
            }
            catch
            {
            }
        }

        private async Task SendAsync (WebSocket ws, object data)
        {
            if (ws.State != WebSocketState.Open) return;
            try
            {
                string json = JsonSerializer.Serialize (data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                byte[] bytes = Encoding.UTF8.GetBytes (json);
                await ws.SendAsync (new ArraySegment<byte> (bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch { }
        }

        private bool IsRateLimited (WebSocket ws)
        {
            DateTime now = DateTime.UtcNow;
            _rateLimits.AddOrUpdate (ws,
                _ => (1, now),
                (_, existing) =>
                {
                    if (( now - existing.WindowStart ).TotalSeconds >= 1)
                    {
                        return (1, now);
                    }
                    if (existing.Count >= MaxMessagesPerSecond)
                    {
                        return existing;
                    }
                    return (existing.Count + 1, existing.WindowStart);
                });

            if (_rateLimits.TryGetValue (ws, out var state) && state.Count >= MaxMessagesPerSecond && ( now - state.WindowStart ).TotalSeconds < 1)
            {
                return true;
            }
            return false;
        }

        private static readonly Dictionary<string, string> MimeTypes = new ()
        {
            [".html"] = "text/html",
            [".js"] = "application/javascript",
            [".css"] = "text/css",
            [".json"] = "application/json",
            [".svg"] = "image/svg+xml",
            [".png"] = "image/png",
            [".ico"] = "image/x-icon",
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
        };

        private static string _staticDir;

        private static string GetStaticDir ()
        {
            if (!string.IsNullOrEmpty (_staticDir) && Directory.Exists (_staticDir))
                return _staticDir;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates = new[]
            {
                Path.Combine (baseDir, "dashboard"),
                Path.Combine (baseDir, "..", "..", "..", "..", "binance-dashboard", "dist"),
                Path.Combine (baseDir, "binance-dashboard", "dist"),
            };

            foreach (string candidate in candidates)
            {
                string full = Path.GetFullPath (candidate);
                if (Directory.Exists (full) && File.Exists (Path.Combine (full, "index.html")))
                {
                    _staticDir = full;
                    return _staticDir;
                }
            }

            return null;
        }

        private void ServeStaticFile (HttpListenerContext ctx)
        {
            string staticDir = GetStaticDir ();
            if (string.IsNullOrEmpty (staticDir))
            {
                string html = "<html><body style='background:#0a0a0a;color:#fff;font-family:monospace;padding:40px'>" +
                    "<h2>BinanceBot Dashboard</h2>" +
                    "<p>Dashboard static files not found.</p>" +
                    "<p>Build the React app first:<br><code>cd binance-dashboard && npm run build</code></p>" +
                    "<p>Then copy <code>dist/</code> to the bot's output directory as <code>dashboard/</code></p>" +
                    "</body></html>";
                byte[] bytes = Encoding.UTF8.GetBytes (html);
                ctx.Response.ContentType = "text/html";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write (bytes, 0, bytes.Length);
                ctx.Response.Close ();
                return;
            }

            string localPath = ctx.Request.Url.LocalPath.TrimStart ('/');
            if (string.IsNullOrEmpty (localPath))
                localPath = "index.html";

            string filePath = Path.Combine (staticDir, localPath.Replace ('/', Path.DirectorySeparatorChar));

            if (!File.Exists (filePath))
            {
                filePath = Path.Combine (staticDir, "index.html");
            }

            string ext = Path.GetExtension (filePath).ToLowerInvariant ();
            ctx.Response.ContentType = MimeTypes.TryGetValue (ext, out string mime) ? mime : "application/octet-stream";

            byte[] fileBytes = File.ReadAllBytes (filePath);
            ctx.Response.ContentLength64 = fileBytes.Length;
            ctx.Response.OutputStream.Write (fileBytes, 0, fileBytes.Length);
            ctx.Response.Close ();
        }
    }
}
