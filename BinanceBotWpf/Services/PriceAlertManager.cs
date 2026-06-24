using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public enum PriceAlertDirection
    {
        Above,
        Below
    }

    public class PriceAlert
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal TargetPrice { get; set; }
        public PriceAlertDirection Direction { get; set; }
        public bool Triggered { get; set; }
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    }

    public class PriceAlertManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, PriceAlert> _alerts = new();
        private readonly Func<string, Task> _notifyTelegram;
        private readonly Action<string> _logger;
        private readonly Func<string, decimal> _getCurrentPrice;
        private readonly Timer _checkTimer;

        public event Action<PriceAlert> OnAlertTriggered;

        public PriceAlertManager(
            Func<string, decimal> getCurrentPrice,
            Func<string, Task>? notifyTelegram,
            Action<string> logger)
        {
            _getCurrentPrice = getCurrentPrice;
            _notifyTelegram = notifyTelegram;
            _logger = logger;

            _checkTimer = new Timer(async _ => await CheckAlertsAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        }

        public string AddAlert(string symbol, decimal targetPrice, PriceAlertDirection direction)
        {
            var alert = new PriceAlert
            {
                Symbol = symbol.ToUpperInvariant(),
                TargetPrice = targetPrice,
                Direction = direction
            };

            _alerts[alert.Id] = alert;
            _logger?.Invoke($"[PriceAlert] Added: {alert.Symbol} {direction} {targetPrice} (id={alert.Id})");
            return alert.Id;
        }

        public bool RemoveAlert(string id)
        {
            return _alerts.TryRemove(id, out _);
        }

        public List<PriceAlert> GetAllAlerts()
        {
            return _alerts.Values.ToList();
        }

        private async Task CheckAlertsAsync()
        {
            foreach (var kvp in _alerts)
            {
                var alert = kvp.Value;
                if (alert.Triggered) continue;

                try
                {
                    decimal currentPrice = _getCurrentPrice(alert.Symbol);
                    if (currentPrice == 0) continue;

                    bool triggered = alert.Direction == PriceAlertDirection.Above
                        ? currentPrice >= alert.TargetPrice
                        : currentPrice <= alert.TargetPrice;

                    if (triggered)
                    {
                        alert.Triggered = true;

                        string direction = alert.Direction == PriceAlertDirection.Above ? "⬆️ ABOVE" : "⬇️ BELOW";
                        string msg = $"🔔 Price Alert: {alert.Symbol} {direction} {alert.TargetPrice}\nCurrent: {currentPrice}";

                        _logger?.Invoke($"[PriceAlert] TRIGGERED: {alert.Symbol} {alert.Direction} {alert.TargetPrice} (current={currentPrice})");

                        OnAlertTriggered?.Invoke(alert);

                        if (_notifyTelegram != null)
                        {
                            try
                            {
                                await _notifyTelegram(msg);
                            }
                            catch (Exception ex)
                            {
                                _logger?.Invoke($"[PriceAlert] Telegram notify failed: {ex.Message}");
                            }
                        }
                    }
                }
                catch
                {
                    // Price not available, skip
                }
            }
        }

        public void Dispose()
        {
            _checkTimer?.Dispose();
        }
    }
}
