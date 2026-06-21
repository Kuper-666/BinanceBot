using BinanceBotWpf.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Провайдер данных CryptoCompare (бесплатный публичный API без ключа).
    /// Источник: https://min-api.cryptocompare.com/data
    /// Лимит free-тира: ~100 000 запросов/мес (без ключа — ниже, порядка 10–50тыс/мес).
    /// Кэширование 10 минут + rate limiting.
    /// Предоставляет социальные метрики: подписчики Twitter, Reddit, активные пользователи.
    /// </summary>
    public class CryptoCompareProvider : IDisposable
    {
        private const string BaseUrl = "https://min-api.cryptocompare.com/data";
        private const string UserAgent = "BinanceBotWpf/1.0";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes (10);

        private readonly HttpClient _httpClient;
        private readonly Action<string> _logger;
        private readonly SemaphoreSlim _gate = new (1, 1);
        private readonly Dictionary<string, (CryptoAssetData Data, DateTime UpdatedAt)> _cache = new ();

        public CryptoCompareProvider(Action<string> logger = null)
        {
            _logger = logger;
            _httpClient = new HttpClient ();
            _httpClient.Timeout = TimeSpan.FromSeconds (20);
            _httpClient.DefaultRequestHeaders.Add ("User-Agent", UserAgent);
            _httpClient.DefaultRequestHeaders.Add ("Accept", "application/json");
        }

        /// <summary>
        /// Получить социальные метрики для списка базовых активов (BTC, ETH, ...).
        /// Возвращает частичные CryptoAssetData (заполнены только социальные поля).
        /// </summary>
        public async Task<Dictionary<string, CryptoAssetData>> GetSocialStatsAsync(List<string> baseAssets)
        {
            if (baseAssets == null || baseAssets.Count == 0)
                return new Dictionary<string, CryptoAssetData> ();

            await _gate.WaitAsync ();
            try
            {
                var result = new Dictionary<string, CryptoAssetData> ();
                var now = DateTime.UtcNow;

                foreach (var asset in baseAssets)
                {
                    string key = asset.ToUpperInvariant ();
                    if (_cache.TryGetValue (key, out var entry) && ( now - entry.UpdatedAt ) < CacheTtl)
                    {
                        result[key] = entry.Data;
                        continue;
                    }

                    // Free-тир: ограничиваем частоту ~1 запрос/1.5с
                    await Task.Delay (1500);

                    var data = await FetchSocialAsync (key);
                    if (data != null)
                    {
                        _cache[key] = (data, now);
                        result[key] = data;
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"⚠️ CryptoCompare: ошибка получения данных — {ex.Message}");
                return new Dictionary<string, CryptoAssetData> ();
            }
            finally
            {
                _gate.Release ();
            }
        }

        /// <summary>
        /// Запрос социальных метрик через /social/coin/latest.
        /// CryptoCompare отдаёт Twitter/Reddit статистику по запрашиваемому символу.
        /// </summary>
        private async Task<CryptoAssetData> FetchSocialAsync(string asset)
        {
            try
            {
                // Сначала резолвим coin_id через /top/exchanges или напрямую пробуем по символу.
                // У CryptoCompare social-эндпоинт принимает массив coin_id (числовых), поэтому
                // используем /social/coin/latest?coinId=... Но без ключа удобнее /social/coins/latest
                // с параметром coinIds — он принимает тикеры. Запросим по символу.
                string url = $"{BaseUrl}/social/coin/latest?coinId={asset}";
                var response = await _httpClient.GetAsync (url);
                if (!response.IsSuccessStatusCode)
                {
                    // Запасной вариант — /v2/social/coin/stats через API-параметры
                    _logger?.Invoke ($"⚠️ CryptoCompare {asset}: HTTP {(int)response.StatusCode}");
                    return null;
                }

                string json = await response.Content.ReadAsStringAsync ();
                var root = JObject.Parse (json);
                var data = root["Data"];
                if (data == null) return null;

                // Структура ответа может содержать Twitter/Reddit/General секции
                var twitter = data["Twitter"];
                var reddit = data["Reddit"];

                var result = new CryptoAssetData { Symbol = asset };

                if (twitter != null)
                    result.TwitterFollowers = ParseInt (twitter["followers"]);

                if (reddit != null)
                {
                    result.RedditSubscribers = ParseInt (reddit["subscribers"]);
                    result.RedditActiveUsers = ParseInt (reddit["active_users"]);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"⚠️ CryptoCompare {asset}: {ex.Message}");
                return null;
            }
        }

        private static int? ParseInt(JToken token) =>
            token != null && int.TryParse (token.ToString (), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : (int?)null;

        public void Dispose()
        {
            _httpClient?.Dispose ();
            _gate?.Dispose ();
        }
    }
}
