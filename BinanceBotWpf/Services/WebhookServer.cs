using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public class WebhookServer : IDisposable
    {
        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private Func<string, string, Task<string>> _handler;
        private readonly Action<string> _logger;
        private readonly int _port;

        public bool IsRunning { get; private set; }

        public WebhookServer (int port, Action<string> logger)
        {
            _port = port;
            _logger = logger;
        }

        public void Start (Func<string, string, Task<string>> handler)
        {
            if (IsRunning) return;
            _handler = handler;
            _cts = new CancellationTokenSource ();
            _listener = new HttpListener ();
            _listener.Prefixes.Add ($"http://+:{_port}/");
            _listener.Prefixes.Add ($"http://localhost:{_port}/");

            try
            {
                _listener.Start ();
                IsRunning = true;
                _logger?.Invoke ($"📡 WebhookServer: слушаю порт {_port}");
                _ = Task.Run (() => ListenLoop (_cts.Token));
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ WebhookServer: не удалось запустить — {ex.Message}");
            }
        }

        public void Stop ()
        {
            if (!IsRunning) return;
            IsRunning = false;
            try { _cts?.Cancel (); } catch { }
            try { _listener?.Stop (); } catch { }
            try { _listener?.Close (); } catch { }
            _logger?.Invoke ($"📡 WebhookServer: остановлен");
        }

        private async Task ListenLoop (CancellationToken token)
        {
            while (!token.IsCancellationRequested && IsRunning)
            {
                try
                {
                    var context = await _listener.GetContextAsync ();
                    _ = Task.Run (() => HandleContext (context));
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger?.Invoke ($"⚠️ WebhookServer ошибка: {ex.Message}");
                }
            }
        }

        private async Task HandleContext (HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                string path = request.Url?.AbsolutePath?.TrimStart ('/') ?? "";
                string source = "";

                // Extract source from path: /webhook/{source} or /webhook
                if (path.StartsWith ("webhook/", StringComparison.OrdinalIgnoreCase))
                {
                    source = path.Substring (8);
                }

                if (request.HttpMethod == "POST")
                {
                    string body;
                    using (var reader = new System.IO.StreamReader (request.InputStream, request.ContentEncoding))
                    {
                        body = await reader.ReadToEndAsync ();
                    }

                    _logger?.Invoke ($"📡 Webhook: POST /{path} ({body.Length} байт)");

                    string result = _handler != null
                        ? await _handler (source, body)
                        : "{\"status\":\"error\",\"error\":\"no handler\"}";

                    byte[] buffer = Encoding.UTF8.GetBytes (result);
                    response.StatusCode = 200;
                    response.ContentType = "application/json";
                    response.ContentLength64 = buffer.Length;
                    await response.OutputStream.WriteAsync (buffer, 0, buffer.Length);
                }
                else
                {
                    // GET /webhook = health check
                    string health = "{\"status\":\"ok\",\"service\":\"BinanceBot\",\"version\":\"" + AppConstants.AppVersion + "\"}";
                    byte[] buffer = Encoding.UTF8.GetBytes (health);
                    response.StatusCode = 200;
                    response.ContentType = "application/json";
                    response.ContentLength64 = buffer.Length;
                    await response.OutputStream.WriteAsync (buffer, 0, buffer.Length);
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"⚠️ WebhookServer handler error: {ex.Message}");
                try
                {
                    byte[] errBuffer = Encoding.UTF8.GetBytes ($"{{\"status\":\"error\",\"error\":\"{ex.Message}\"}}");
                    response.StatusCode = 500;
                    response.ContentType = "application/json";
                    response.ContentLength64 = errBuffer.Length;
                    await response.OutputStream.WriteAsync (errBuffer, 0, errBuffer.Length);
                }
                catch { }
            }
            finally
            {
                try { response.Close (); } catch { }
            }
        }

        public void Dispose ()
        {
            Stop ();
            _cts?.Dispose ();
        }
    }
}
