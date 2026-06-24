using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BinanceBotWpf.Services
{
    public class FearGreedData
    {
        public int Value { get; set; }
        public string Classification { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
    }

    public class FearGreedIndexProvider : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly Action<string> _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private FearGreedData? _cached;
        private DateTime _lastFetch = DateTime.MinValue;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

        private const string ApiUrl = "https://api.alternative.me/fng/?limit=1&format=json";

        public FearGreedIndexProvider(Action<string> logger)
        {
            _logger = logger;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "BinanceBot/1.20");
        }

        public async Task<FearGreedData?> GetCurrentAsync(CancellationToken ct = default)
        {
            if (_cached != null && DateTime.UtcNow - _lastFetch < CacheTtl)
                return _cached;

            if (!await _gate.WaitAsync(0, ct))
                return _cached;

            try
            {
                var response = await _httpClient.GetAsync(ApiUrl, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger?.Invoke($"[FearGreed] HTTP {(int)response.StatusCode}");
                    return _cached;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                var doc = JObject.Parse(json);
                var data = doc["data"]?[0];

                if (data == null) return _cached;

                _cached = new FearGreedData
                {
                    Value = int.Parse(data["value"]?.ToString() ?? "0"),
                    Classification = data["value_classification"]?.ToString() ?? "Unknown",
                    Timestamp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(data["timestamp"]?.ToString() ?? "0")).DateTime
                };
                _lastFetch = DateTime.UtcNow;

                _logger?.Invoke($"[FearGreed] Value={_cached.Value} ({_cached.Classification})");
                return _cached;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[FearGreed] Error: {ex.Message}");
                return _cached;
            }
            finally
            {
                _gate.Release();
            }
        }

        public bool IsExtremeGreed() => _cached != null && _cached.Value >= 75;
        public bool IsExtremeFear() => _cached != null && _cached.Value <= 25;

        public void Dispose()
        {
            _httpClient?.Dispose();
            _gate?.Dispose();
        }
    }
}
