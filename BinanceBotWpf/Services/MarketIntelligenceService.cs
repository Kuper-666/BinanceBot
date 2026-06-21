using BinanceBotWpf.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Единый фасад для трёх внешних источников рыночных данных:
    /// CoinGecko (фундаментал), CryptoCompare (соцсети), LunarCrush (sentiment).
    ///
    /// Агрегирует данные в единый кэш CryptoAssetData по базовому активу (BTC, ETH, ...),
    /// предоставляет удобные методы для фильтрации пар и оценки sentiment.
    ///
    /// ВАЖНО: все источники опциональны. Если какой-то из них недоступен
    /// (нет ключа, ошибка сети, rate limit) — сервис деградирует плавно,
    /// используя данные из оставшихся источников, и никогда не роняет торговлю.
    /// </summary>
    public class MarketIntelligenceService
    {
        private readonly CoinGeckoProvider _coingecko;
        private readonly CryptoCompareProvider _cryptocompare;
        private readonly LunarCrushProvider _lunarcrush;
        private readonly Action<string> _logger;

        // Единый кэш: base-asset (BTC) -> агрегированные данные
        private readonly ConcurrentDictionary<string, CryptoAssetData> _cache = new ();
        private DateTime _lastRefresh = DateTime.MinValue;
        private readonly SemaphoreSlim _refreshGate = new (1, 1);

        /// <summary>
        /// Пороги фильтрации по умолчанию (можно переопределить в вызове FilterByFundamentals).
        /// </summary>
        public const int DefaultMaxRank = 200;
        public const decimal DefaultMinSentiment = -0.4m;
        public const decimal DefaultMinLiquidity = 1_000_000m;

        public MarketIntelligenceService(
            CoinGeckoProvider coingecko,
            CryptoCompareProvider cryptocompare,
            LunarCrushProvider lunarcrush,
            Action<string> logger = null)
        {
            _coingecko = coingecko;
            _cryptocompare = cryptocompare;
            _lunarcrush = lunarcrush;
            _logger = logger;
        }

        /// <summary>
        /// Дата последнего успешного обновления кэша (UTC).
        /// </summary>
        public DateTime LastRefresh => _lastRefresh;

        /// <summary>Активен ли хоть один sentiment-источник (для логирования).</summary>
        public bool IsLunarCrushEnabled => _lunarcrush?.IsEnabled ?? false;

        /// <summary>
        /// Обновить данные кэша для списка торговых пар (Binance-формат: "BTCUSDC").
        /// Вызывается фоновым циклом раз в 10 минут. Все источники опрашиваются
        /// параллельно, ошибки каждого изолируются.
        /// </summary>
        public async Task RefreshAsync(List<string> tradingPairs)
        {
            if (tradingPairs == null || tradingPairs.Count == 0) return;

            await _refreshGate.WaitAsync ();
            try
            {
                var baseAssets = tradingPairs
                    .Select (ToBaseAsset)
                    .Where (a => !string.IsNullOrEmpty (a))
                    .Distinct ()
                    .ToList ();

                // Параллельный опрос всех источников; каждый падает независимо
                var cgTask = SafeAsync (() => _coingecko?.GetMarketDataAsync (baseAssets));
                var ccTask = SafeAsync (() => _cryptocompare?.GetSocialStatsAsync (baseAssets));
                var lcTask = SafeAsync (() => _lunarcrush?.GetAssetsDataAsync (baseAssets));

                await Task.WhenAll (cgTask, ccTask, lcTask);

                var coingecko = await cgTask ?? new Dictionary<string, CryptoAssetData> ();
                var cryptocompare = await ccTask ?? new Dictionary<string, CryptoAssetData> ();
                var lunarcrush = await lcTask ?? new Dictionary<string, CryptoAssetData> ();

                // Мерджим три источника в единый кэш
                foreach (var asset in baseAssets)
                {
                    var merged = new CryptoAssetData { Symbol = asset };

                    if (coingecko.TryGetValue (asset, out var cg))
                        MergeInto (merged, cg);

                    if (cryptocompare.TryGetValue (asset, out var cc))
                        MergeInto (merged, cc);

                    if (lunarcrush.TryGetValue (asset, out var lc))
                        MergeInto (merged, lc);

                    _cache[asset] = merged;
                }

                _lastRefresh = DateTime.UtcNow;
                _logger?.Invoke ($"🌐 Данные рынка обновлены: CoinGecko {coingecko.Count}, CryptoCompare {cryptocompare.Count}, LunarCrush {lunarcrush.Count} активов");
            }
            finally
            {
                _refreshGate.Release ();
            }
        }

        /// <summary>
        /// Получить агрегированные данные для одного символа (Binance-формат "BTCUSDC" или base "BTC").
        /// </summary>
        public CryptoAssetData GetAssetData(string symbol)
        {
            string baseAsset = ToBaseAsset (symbol);
            return _cache.TryGetValue (baseAsset, out var data) ? data : null;
        }

        /// <summary>
        /// Композитный sentiment score для пары: от -1 (медвежьи) до +1 (бычьи).
        /// Возвращает 0, если данных нет.
        /// </summary>
        public decimal GetSentimentScore(string symbol)
        {
            var data = GetAssetData (symbol);
            return data?.CompositeSentimentScore ?? 0m;
        }

        /// <summary>
        /// Фильтрует список торговых пар по фундаментальным показателям:
        ///   - исключает пары с rank выше maxRank (если данные есть);
        ///   - исключает пары с sentiment ниже minSentiment (если данные есть);
        ///   - исключает пары с ликвидностью ниже minLiquidity (по объёму CoinGecko).
        ///
        /// Если данных по паре нет — оставляет её (не штрафуем за отсутствие данных,
        /// чтобы не блокировать торговлю при сбое API).
        /// </summary>
        public List<string> FilterByFundamentals(
            List<string> pairs,
            int maxRank = DefaultMaxRank,
            decimal minSentiment = DefaultMinSentiment,
            decimal minLiquidity = DefaultMinLiquidity)
        {
            if (pairs == null || pairs.Count == 0) return new List<string> ();

            var kept = new List<string> ();
            var excluded = new List<string> ();

            foreach (var pair in pairs)
            {
                var data = GetAssetData (pair);
                if (data == null)
                {
                    // Нет данных — оставляем (деградация без блокировки торговли)
                    kept.Add (pair);
                    continue;
                }

                bool rankOk = !data.CoinGeckoRank.HasValue || data.CoinGeckoRank.Value <= maxRank;
                bool sentimentOk = data.CompositeSentimentScore >= minSentiment;
                bool liquidityOk = !data.Volume24h.HasValue || data.Volume24h.Value >= minLiquidity;

                if (rankOk && sentimentOk && liquidityOk)
                {
                    kept.Add (pair);
                }
                else
                {
                    excluded.Add (pair);
                }
            }

            if (excluded.Count > 0 && _logger != null)
            {
                var reasons = excluded.Select (p =>
                {
                    var d = GetAssetData (p);
                    if (d == null) return p;
                    var parts = new List<string> ();
                    if (d.CoinGeckoRank.HasValue && d.CoinGeckoRank.Value > maxRank) parts.Add ($"rank #{d.CoinGeckoRank}");
                    if (d.CompositeSentimentScore < minSentiment) parts.Add ($"sentiment {d.CompositeSentimentScore:F2}");
                    if (d.Volume24h.HasValue && d.Volume24h.Value < minLiquidity) parts.Add ($"volume {d.Volume24h.Value:N0}");
                    return parts.Count > 0 ? $"{p} ({string.Join (", ", parts)})" : p;
                });
                _logger ($"🚫 Исключено по фундаменталу: {string.Join (", ", reasons)}");
            }

            return kept;
        }

        /// <summary>Очищает кэш (например, при остановке бота).</summary>
        public void ClearCache() => _cache.Clear ();

        // --- Вспомогательные методы ---

        /// <summary>Преобразует Binance-символ "BTCUSDC" → базовый актив "BTC".</summary>
        public static string ToBaseAsset(string symbol)
        {
            if (string.IsNullOrEmpty (symbol)) return string.Empty;
            string s = symbol.ToUpperInvariant ();
            foreach (var quote in new[] { "USDC", "USDT", "BUSD", "FDUSD", "TUSD" })
            {
                if (s.EndsWith (quote))
                    return s.Substring (0, s.Length - quote.Length);
            }
            return s;
        }

        /// <summary>Безопасный вызов асинхронной операции: возвращает null при ошибке.</summary>
        private async Task<Dictionary<string, CryptoAssetData>> SafeAsync(Func<Task<Dictionary<string, CryptoAssetData>>> factory)
        {
            if (factory == null) return new Dictionary<string, CryptoAssetData> ();
            try
            {
                return await factory ();
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"⚠️ MarketIntel: источник недоступен — {ex.Message}");
                return new Dictionary<string, CryptoAssetData> ();
            }
        }

        /// <summary>Дополняет target непустыми полями из source (source перебивает target).</summary>
        private static void MergeInto(CryptoAssetData target, CryptoAssetData source)
        {
            if (source == null) return;
            if (source.MarketCap.HasValue) target.MarketCap = source.MarketCap;
            if (source.Volume24h.HasValue) target.Volume24h = source.Volume24h;
            if (source.PriceChange24h.HasValue) target.PriceChange24h = source.PriceChange24h;
            if (source.PriceChange7d.HasValue) target.PriceChange7d = source.PriceChange7d;
            if (source.PriceChange30d.HasValue) target.PriceChange30d = source.PriceChange30d;
            if (source.CirculatingSupply.HasValue) target.CirculatingSupply = source.CirculatingSupply;
            if (source.CoinGeckoRank.HasValue) target.CoinGeckoRank = source.CoinGeckoRank;
            if (source.TwitterFollowers.HasValue) target.TwitterFollowers = source.TwitterFollowers;
            if (source.RedditSubscribers.HasValue) target.RedditSubscribers = source.RedditSubscribers;
            if (source.RedditActiveUsers.HasValue) target.RedditActiveUsers = source.RedditActiveUsers;
            if (source.GalaxyScore.HasValue) target.GalaxyScore = source.GalaxyScore;
            if (source.AltRank.HasValue) target.AltRank = source.AltRank;
            if (source.Sentiment.HasValue) target.Sentiment = source.Sentiment;
            if (source.SocialVolume.HasValue) target.SocialVolume = source.SocialVolume;
        }
    }
}
