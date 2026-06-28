using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public class WhaleAlert
    {
        public string Symbol { get; set; } = string.Empty;
        public string Side { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public decimal Quantity { get; set; }
        public decimal ValueUsdc { get; set; }
        public DateTime Time { get; set; }
    }

    public class WhaleMonitor : IWhaleMonitor
    {
        private readonly Action<string> _logger;
        private readonly decimal _thresholdUsdc;
        private CancellationTokenSource _cts;
        private ClientWebSocket _ws;

        public event Action<WhaleAlert> OnWhaleDetected;
        public List<WhaleAlert> RecentAlerts { get; } = new ();
        private const int MaxAlerts = 100;

        public WhaleMonitor(Action<string> logger, decimal thresholdUsdc = 100000)
        {
            _logger = logger;
            _thresholdUsdc = thresholdUsdc;
        }

        public async Task StartAsync(string[] symbols)
        {
            _cts = new CancellationTokenSource ();
            string streams = string.Join ("/", symbols.Select (s => $"{s.ToLowerInvariant ()}@aggTrade"));
            string url = $"wss://stream.binance.com:9443/stream?streams={streams}";

            _ = Task.Run (() => ListenLoop (url, _cts.Token));
            await Task.CompletedTask;
        }

        private async Task ListenLoop(string url, CancellationToken ct)
        {
            int reconnectDelay = 1000;
            const int maxReconnectDelay = 30000;

            while (!ct.IsCancellationRequested)
            {
                _ws = new ClientWebSocket ();
                try
                {
                    await _ws.ConnectAsync (new Uri (url), ct);
                    _logger?.Invoke ("🐋 Whale monitor подключён");
                    reconnectDelay = 1000;

                    var buffer = new byte[8192];
                    while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var result = await _ws.ReceiveAsync (new ArraySegment<byte> (buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Text)
                        {
                            string json = Encoding.UTF8.GetString (buffer, 0, result.Count);
                            ProcessMessage (json);
                        }
                        else if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await _ws.CloseAsync (WebSocketCloseStatus.NormalClosure, "", ct);
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger?.Invoke ($"❌ Whale monitor ошибка: {ex.Message}");
                }
                finally
                {
                    try { _ws?.Dispose (); } catch { }
                }

                if (!ct.IsCancellationRequested)
                {
                    await Task.Delay (reconnectDelay, ct);
                    reconnectDelay = Math.Min (reconnectDelay * 2, maxReconnectDelay);
                }
            }
        }

        private void ProcessMessage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse (json);
                var data = doc.RootElement.TryGetProperty ("data", out var d) ? d : doc.RootElement;

                string symbol = data.TryGetProperty ("s", out var s) ? s.GetString () : "";
                string side = data.TryGetProperty ("m", out var m) ? (m.GetBoolean () ? "SELL" : "BUY") : "";
                decimal price = data.TryGetProperty ("p", out var p) ? decimal.Parse (p.GetString (),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) : 0;
                decimal qty = data.TryGetProperty ("q", out var q) ? decimal.Parse (q.GetString (),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) : 0;

                decimal valueUsdc = price * qty;
                if (valueUsdc >= _thresholdUsdc)
                {
                    var alert = new WhaleAlert
                    {
                        Symbol = symbol,
                        Side = side,
                        Price = price,
                        Quantity = qty,
                        ValueUsdc = valueUsdc,
                        Time = DateTime.UtcNow
                    };

                    lock (RecentAlerts)
                    {
                        RecentAlerts.Insert (0, alert);
                        if (RecentAlerts.Count > MaxAlerts)
                            RecentAlerts.RemoveAt (RecentAlerts.Count - 1);
                    }

                    _logger?.Invoke ($"🐋 WHALE {side} {symbol}: ${valueUsdc:N0} ({qty} @ {price})");
                    OnWhaleDetected?.Invoke (alert);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            _cts?.Cancel ();
            try { _ws?.Dispose (); } catch { }
        }
    }
}
