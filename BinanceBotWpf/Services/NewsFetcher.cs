using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using BinanceBotWpf.Services.Strategies;

namespace BinanceBotWpf.Services
{
    public class NewsFetcher : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly NewsSentinel _sentinel;
        private readonly Action<string> _logger;
        private Timer _timer;
        private readonly TimeSpan _interval = TimeSpan.FromMinutes(10);
        private readonly HashSet<string> _seenTitles = new();
        private bool _cacheSeeded;

        private static readonly string[] NegativeKeywords = new[]
        {
            "hack", "hacked", "exploit", "vulnerability", "ban", "regulation",
            "lawsuit", "sec", "cftc", "fraud", "scam", "crash", "dump",
            "delisting", "halt", "suspension", "emergency", "fear", "panic",
            "collapse", "bankrupt", "insolvency", "fine", "penalty"
        };

        private static readonly string[] PositiveKeywords = new[]
        {
            "listing", "launchpool", "airdrop", "partnership", "adoption",
            "etf", "approval", "upgrade", "milestone", "bullish", "surge",
            "rally", "record", "all-time", "institutional", "mainstream",
            "integration", "launch", "support"
        };

        public NewsFetcher(HttpClient httpClient, NewsSentinel sentinel, Action<string> logger)
        {
            _httpClient = httpClient;
            _sentinel = sentinel;
            _logger = logger;
        }

        public void Start()
        {
            _timer = new Timer(async _ => await FetchAsync(), null, TimeSpan.FromSeconds(30), _interval);
            _logger?.Invoke("📰 NewsFetcher: запущен (интервал 10 мин)");
        }

        public void Stop()
        {
            _timer?.Dispose();
            _timer = null;
            _logger?.Invoke("📰 NewsFetcher: остановлен");
        }

        private async Task FetchAsync()
        {
            try
            {
                await FetchGoogleNewsRssAsync();
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"⚠️ NewsFetcher ошибка: {ex.Message}");
            }
        }

        private async Task FetchGoogleNewsRssAsync()
        {
            if (!_cacheSeeded)
            {
                _seenTitles.UnionWith(_sentinel.GetRecentTitles(24));
                _cacheSeeded = true;
            }

            string[] feeds = new[]
            {
                "https://news.google.com/rss/search?q=crypto+hack+OR+ban+OR+regulation+OR+listing+when:1d&hl=en",
                "https://news.google.com/rss/search?q=bitcoin+OR+ethereum+OR+binance+when:1d&hl=en"
            };

            var candidates = new List<(string title, string source, string sentiment, int impact, string symbols)>();
            int skipped = 0;

            foreach (string feedUrl in feeds)
            {
                try
                {
                    string xml = await _httpClient.GetStringAsync(feedUrl);
                    var doc = XDocument.Parse(xml);
                    var items = doc.Descendants("item");

                    foreach (var item in items)
                    {
                        string title = item.Element("title")?.Value ?? "";
                        string source = item.Element("source")?.Value ?? "Google News";

                        if (string.IsNullOrWhiteSpace(title)) continue;
                        if (!_seenTitles.Add(title)) { skipped++; continue; }

                        string sentiment = ClassifySentiment(title);
                        int impact = CalculateImpact(title);
                        string symbols = ExtractSymbols(title);

                        candidates.Add((title, source, sentiment, impact, symbols));
                    }
                }
                catch
                {
                    // Skip failed feed
                }
            }

            if (candidates.Count > 0)
            {
                int inserted = _sentinel.InsertNewsBatch(candidates);
                _sentinel.CleanupOldNews(48);
                _logger?.Invoke($"📰 NewsFetcher: +{inserted} новых, {skipped} пропущено (кеш)");
            }
        }

        private string ClassifySentiment(string title)
        {
            string lower = title.ToLowerInvariant();
            int negativeScore = NegativeKeywords.Count(k => lower.Contains(k));
            int positiveScore = PositiveKeywords.Count(k => lower.Contains(k));

            if (negativeScore > positiveScore) return "negative";
            if (positiveScore > negativeScore) return "positive";
            return "neutral";
        }

        private int CalculateImpact(string title)
        {
            string lower = title.ToLowerInvariant();
            int score = 0;

            if (lower.Contains("hack") || lower.Contains("exploit") || lower.Contains("vulnerability")) score += 5;
            if (lower.Contains("ban") || lower.Contains("regulation") || lower.Contains("sec")) score += 4;
            if (lower.Contains("etf") || lower.Contains("listing") || lower.Contains("launchpool")) score += 3;
            if (lower.Contains("crash") || lower.Contains("dump") || lower.Contains("surge") || lower.Contains("rally")) score += 3;
            if (lower.Contains("partnership") || lower.Contains("adoption")) score += 2;

            return Math.Min(score, 10);
        }

        private string ExtractSymbols(string title)
        {
            string[] knownSymbols = { "BTC", "ETH", "BNB", "SOL", "XRP", "DOGE", "ADA", "AVAX", "DOT", "MATIC", "LINK", "UNI", "ATOM", "LTC", "FIL", "APT", "ARB", "OP", "SUI", "PEPE", "WIF", "FET", "TAO", "RENDER", "INJ" };
            string upper = title.ToUpperInvariant();
            string found = string.Join(",", knownSymbols.Where(s => upper.Contains(s)));
            return string.IsNullOrEmpty(found) ? "*" : found;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
