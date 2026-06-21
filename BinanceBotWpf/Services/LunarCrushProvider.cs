using BinanceBotWpf.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Провайдер данных LunarCrush (social sentiment).
    /// Источник: https://lunarcrush.com/api2/public или https://api.lunarcrush.com/v2
    ///
    /// ВАЖНО: публичный API LunarCrush требует API-ключ (передаётся в заголовке
    /// или query-параметром). Без ключа эндпоинт вернёт 401. Поэтому ключ опционален:
    ///   - если ключ задан → данные тянутся (galaxy_score, alt_rank, sentiment, social_volume);
    ///   - если ключ НЕ задан → провайдер молча отключается, не ломая торговлю.
    ///
    /// Бесплатный ключ можно получить на https://lunarcrush.com/developers
    /// и вписать в config.json в поле LunarCrushApiKey.
    /// </summary>
    public class LunarCrushProvider : IDisposable
    {
        private const string BaseUrl = "https://api.lunarcrush.com/v2";
        private const string UserAgent = "BinanceBotWpf/1.0";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes (10);

        private readonly HttpClient _httpClient;
        private readonly Action<string> _logger;
        private readonly SemaphoreSlim _gate = new (1, 1);
        private readonly Dictionary<string, (CryptoAssetData Data, DateTime UpdatedAt)> _cache = new ();
        private readonly string _apiKey;

        /// <summary>True, если ключ задан и провайдер активен.</summary>
        public bool IsEnabled => !string.IsNullOrWhiteSpace (_apiKey);

        public LunarCrushProvider(string apiKey = null, Action<string> logger = null)
        {
            _logger = logger;
            _apiKey = apiKey;
            _httpClient = new HttpClient ();
            _httpClient.Timeout = TimeSpan.FromSeconds (20);
            _httpClient.DefaultRequestHeaders.Add ("User-Agent", UserAgent);
            _httpClient.DefaultRequestHeaders.Add ("Accept", "application/json");
            if (IsEnabled)
                _httpClient.DefaultRequestHeaders.Add ("Authorization", $"Bearer {_apiKey}");
        }

        /// <summary>
        /// Получить sentiment-метрики для списка базовых активов.
        /// Возвращает частичные CryptoAssetData (заполнены sentiment-поля).
        /// Если ключ не задан — возвращает пустой словарь, ничего не запрашивая.
        /// </summary>
        public async Task<Dictionary<string, CryptoAssetData>> GetAssetsDataAsync(List<string> baseAssets)
        {
            if (!IsEnabled || baseAssets == null || baseAssets.Count == 0)
                return new Dictionary<string, CryptoAssetData> ();

            await _gate.WaitAsync ();
            try
            {
                var result = new Dictionary<string, CryptoAssetData> ();
                var now = DateTime.UtcNow;

                // LunarCrush принимает сразу список символов в одном запросе
                var missing = new List<string> ();
                foreach (var asset in baseAssets)
                {
                    string key = asset.ToUpperInvariant ();
                    if (_cache.TryGetValue (key, out var entry) && ( now - entry.UpdatedAt ) < CacheTtl)
                        result[key] = entry.Data;
                    else
                        missing.Add (key);
                }

                if (missing.Count == 0)
                    return result;

                // Один пакетный запрос на все недостающие символы
                await Task.Delay (1200);
                var fetched = await FetchBatchAsync (missing);
                foreach (var kv in fetched)
                {
                    _cache[kv.Key] = (kv.Value, now);
                    result[kv.Key] = kv.Value;
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"⚠️ LunarCrush: ошибка получения данных — {ex.Message}");
                return new Dictionary<string, CryptoAssetData> ();
            }
            finally
            {
                _gate.Release ();
            }
        }

        /// <summary>
        /// Пакетный запрос /v2/assets?symbols=BTC,ETH,...
        /// Извлекает galaxy_score, alt_rank, sentiment, social_volume.
        /// </summary>
        private async Task<Dictionary<string, CryptoAssetData>> FetchBatchAsync(List<string> assets)
        {
            var result = new Dictionary<string, CryptoAssetData> ();
            try
            {
                string symbols = string.Join (",", assets.Select (a => a.ToUpperInvariant ()));
                string url = $"{BaseUrl}?data=assets&data_points=1&symbols={symbols}";
                var response = await _httpClient.GetAsync (url);
                if (!response.IsSuccessStatusCode)
                {
                    _logger?.Invoke ($"⚠️ LunarCrush: HTTP {(int)response.StatusCode} (проверьте ключ в config.json)");
                    return result;
                }

                string json = await response.Content.ReadAsStringAsync ();
                var root = JObject.Parse (json);
                var data = root["data"] as JArray;
                if (data == null) return result;

                foreach (var item in data)
                {
                    string symbol = item["symbol"]?.ToString ().ToUpperInvariant ();
                    if (string.IsNullOrEmpty (symbol)) continue;

                    result[symbol] = new CryptoAssetData
                    {
                        Symbol = symbol,
                        GalaxyScore = ParseDecimal (item["galaxy_score"]),
                        AltRank = ParseDecimal (item["alt_rank"]),
                        Sentiment = ParseDecimal (item["sentiment"]),
                        SocialVolume = ParseDecimal (item["social_volume_24h"] ?? item["social_volume"])
                    };
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"⚠️ LunarCrush (batch): {ex.Message}");
            }
            return result;
        }

        private static decimal? ParseDecimal(JToken token) =>
            token != null && decimal.TryParse (token.ToString (), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal v) ? v : (decimal?)null;

        public void Dispose()
        {
            _httpClient?.Dispose ();
            _gate?.Dispose ();
        }
    }
}
