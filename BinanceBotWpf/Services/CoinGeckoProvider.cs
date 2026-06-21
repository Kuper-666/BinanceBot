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
    /// Провайдер данных CoinGecko (фундаментальные метрики: market cap, volume, price changes, rank).
    /// Endpoint: https://api.coingecko.com/api/v3
    /// Free tier: ~30 requests/min, без API ключа.
    /// Кеширование 10 мин + rate limiting (1.2s между запросами).
    /// </summary>
    public class CoinGeckoProvider : IDisposable
    {
        private const string BaseUrl = "https://api.coingecko.com/api/v3";
        private const string UserAgent = "BinanceBotWpf/1.0";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan CoinIdMapTtl = TimeSpan.FromHours(1);

        private readonly HttpClient _httpClient;
        private readonly Action<string> _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);

        // Кеш данных: base-asset (BTC) -> (CryptoAssetData, UpdatedAt)
        private readonly Dictionary<string, (CryptoAssetData Data, DateTime UpdatedAt)> _cache = new();

        // Карта coin_id для CoinGecko: base-asset (BTC) -> coin_id (bitcoin)
        private readonly Dictionary<string, string> _coinIdMap = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _coinIdMapUpdated = DateTime.MinValue;

        public CoinGeckoProvider(Action<string> logger = null)
        {
            _logger = logger;
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(20);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        /// <summary>
        /// Получить рыночные данные для списка базовых активов (BTC, ETH, ...).
        /// Возвращает частично заполненные CryptoAssetData (только фундаментальные метрики).
        /// </summary>
        public async Task<Dictionary<string, CryptoAssetData>> GetMarketDataAsync(List<string> baseAssets)
        {
            if (baseAssets == null || baseAssets.Count == 0)
                return new Dictionary<string, CryptoAssetData>();

            await _gate.WaitAsync();
            try
            {
                // Обновляем карту coin_id при необходимости
                await EnsureCoinIdMapAsync();

                var result = new Dictionary<string, CryptoAssetData>();
                var now = DateTime.UtcNow;

                // Сначала отдаём из кеша
                var missing = new List<(string Asset, string CoinId)>();
                foreach (var asset in baseAssets)
                {
                    string key = asset.ToUpperInvariant();
                    if (_cache.TryGetValue(key, out var entry) && (now - entry.UpdatedAt) < CacheTtl)
                    {
                        result[key] = entry.Data;
                    }
                    else if (_coinIdMap.TryGetValue(key, out var coinId))
                    {
                        missing.Add((key, coinId));
                    }
                    else
                    {
                        _logger?.Invoke($"⚠️ CoinGecko: не найден coin_id для {key}");
                    }
                }

                if (missing.Count == 0)
                    return result;

                // Запрашиваем недостающие данные пачками по ~20 (ограничение CoinGecko)
                // Free endpoint /coins/markets позволяет передать ids через запятую
                int batchSize = 20;
                for (int i = 0; i < missing.Count; i += batchSize)
                {
                    var batch = missing.Skip(i).Take(batchSize).ToList();
                    string ids = string.Join(",", batch.Select(b => b.CoinId));

                    // Rate limiting: 1.2s между запросами (free tier ~30 req/min)
                    if (i > 0) await Task.Delay(1200);

                    try
                    {
                        string url = $"{BaseUrl}/coins/markets?vs_currency=usd&ids={ids}&order=market_cap_desc&sparkline=false&price_change_percentage=24h,7d,30d";
                        var response = await _httpClient.GetAsync(url);
                        if (!response.IsSuccessStatusCode)
                        {
                            _logger?.Invoke($"⚠️ CoinGecko: HTTP {(int)response.StatusCode}");
                            continue;
                        }

                        string json = await response.Content.ReadAsStringAsync();
                        var data = JArray.Parse(json);

                        foreach (var item in data)
                        {
                            string symbol = item["symbol"]?.ToString().ToUpperInvariant();
                            if (string.IsNullOrEmpty(symbol)) continue;

                            var assetData = new CryptoAssetData
                            {
                                Symbol = symbol,
                                MarketCap = ParseDecimal(item["market_cap"]),
                                Volume24h = ParseDecimal(item["total_volume"]),
                                PriceChange24h = ParseDecimal(item["price_change_percentage_24h"]),
                                PriceChange7d = ParsePriceChange(item["price_change_percentage_7d_in_currency"]),
                                PriceChange30d = ParsePriceChange(item["price_change_percentage_30d_in_currency"]),
                                CirculatingSupply = ParseDecimal(item["circulating_supply"]),
                                CoinGeckoRank = ParseInt(item["market_cap_rank"])
                            };

                            _cache[symbol] = (assetData, now);
                            result[symbol] = assetData;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Invoke($"⚠️ CoinGecko (batch): {ex.Message}");
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"❌ CoinGecko: ошибка получения данных — {ex.Message}");
                return new Dictionary<string, CryptoAssetData>();
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Обеспечивает наличие карты symbol -> coin_id.
        /// Загружает топ-100 монет с CoinGecko (/coins/markets с пустыми ids).
        /// </summary>
        private async Task EnsureCoinIdMapAsync()
        {
            if (_coinIdMap.Count > 0 && (DateTime.UtcNow - _coinIdMapUpdated) < CoinIdMapTtl)
                return;

            try
            {
                string url = $"{BaseUrl}/coins/markets?vs_currency=usd&order=market_cap_desc&per_page=100&page=1&sparkline=false";
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) return;

                string json = await response.Content.ReadAsStringAsync();
                var data = JArray.Parse(json);

                foreach (var item in data)
                {
                    string id = item["id"]?.ToString();
                    string symbol = item["symbol"]?.ToString().ToUpperInvariant();
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(symbol))
                    {
                        _coinIdMap[symbol] = id;
                    }
                }

                _coinIdMapUpdated = DateTime.UtcNow;
                _logger?.Invoke($"✅ CoinGecko: загружена карта coin_id ({_coinIdMap.Count} активов)");
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"⚠️ CoinGecko: не удалось загрузить карту coin_id — {ex.Message}");
            }
        }

        /// <summary>
        /// Fallback-поиск coin_id через /search endpoint.
        /// </summary>
        private async Task<string> SearchCoinIdAsync(string asset)
        {
            try
            {
                string url = $"{BaseUrl}/search?query={Uri.EscapeDataString(asset)}";
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;

                string json = await response.Content.ReadAsStringAsync();
                var root = JObject.Parse(json);
                var coins = root["coins"] as JArray;
                if (coins == null || coins.Count == 0) return null;

                // Ищем точное совпадение по symbol
                foreach (var coin in coins)
                {
                    if (coin["symbol"]?.ToString().Equals(asset, StringComparison.OrdinalIgnoreCase) == true)
                        return coin["id"]?.ToString();
                }

                // Иначе берём первый результат
                return coins.First["id"]?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static decimal? ParseDecimal(JToken token) =>
            token != null && decimal.TryParse(token.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal v) ? v : (decimal?)null;

        private static decimal? ParsePriceChange(JToken token)
        {
            if (token == null) return null;
            // CoinGecko иногда возвращает null как JValue.Null
            if (token.Type == JTokenType.Null) return null;
            return decimal.TryParse(token.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal v) ? v : (decimal?)null;
        }

        private static int? ParseInt(JToken token) =>
            token != null && int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : (int?)null;

        public void Dispose()
        {
            _httpClient?.Dispose();
            _gate?.Dispose();
        }
    }
}
