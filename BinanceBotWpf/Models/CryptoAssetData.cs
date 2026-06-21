using System;

namespace BinanceBotWpf.Models
{
    /// <summary>
    /// Единая модель данных для рыночной разведки.
    /// Агрегирует фундаментальные (CoinGecko), социальные (CryptoCompare) и sentiment (LunarCrush) метрики.
    /// </summary>
    public class CryptoAssetData
    {
        public string Symbol { get; set; }

        // --- CoinGecko: фундаментальные метрики ---
        public decimal? MarketCap { get; set; }
        public decimal? Volume24h { get; set; }
        public decimal? PriceChange24h { get; set; }
        public decimal? PriceChange7d { get; set; }
        public decimal? PriceChange30d { get; set; }
        public decimal? CirculatingSupply { get; set; }
        public int? CoinGeckoRank { get; set; }

        // --- CryptoCompare: социальные метрики ---
        public int? TwitterFollowers { get; set; }
        public int? RedditSubscribers { get; set; }
        public int? RedditActiveUsers { get; set; }

        // --- LunarCrush: sentiment-метрики ---
        public decimal? GalaxyScore { get; set; }
        public decimal? AltRank { get; set; }
        public decimal? Sentiment { get; set; }
        public decimal? SocialVolume { get; set; }

        // --- Вычисляемые свойства ---

        /// <summary>
        /// Композитный sentiment score от -1 до +1.
        /// Веса: Sentiment (LunarCrush) 50%, PriceChange7d (CoinGecko) 30%, GalaxyScore 20%.
        /// </summary>
        public decimal CompositeSentimentScore
        {
            get
            {
                double score = 0;
                double totalWeight = 0;

                // LunarCrush Sentiment: диапазон 0..1, нормализуем в -1..+1
                if (Sentiment.HasValue && Sentiment.Value > 0)
                {
                    score += (double)((Sentiment.Value - 0.5m) * 2) * 0.5;
                    totalWeight += 0.5;
                }

                // PriceChange7d: нормализуем в -1..+1 (±20% = край)
                if (PriceChange7d.HasValue)
                {
                    double normalized = Math.Max(-1, Math.Min(1, (double)PriceChange7d.Value / 20.0));
                    score += normalized * 0.3;
                    totalWeight += 0.3;
                }

                // GalaxyScore: нормализуем в 0..1 (макс ~80)
                if (GalaxyScore.HasValue && GalaxyScore.Value > 0)
                {
                    double normalized = Math.Min(1, (double)GalaxyScore.Value / 80.0);
                    score += (normalized * 2 - 1) * 0.2; // -1..+1
                    totalWeight += 0.2;
                }

                if (totalWeight == 0) return 0;
                return (decimal)(score / totalWeight);
            }
        }

        /// <summary>Форматированная капитализация: "1.2T" / "45.3B" / "890M".</summary>
        public string FormattedMarketCap
        {
            get
            {
                if (!MarketCap.HasValue || MarketCap.Value <= 0) return "—";
                if (MarketCap.Value >= 1_000_000_000_000m) return $"{MarketCap.Value / 1_000_000_000_000m:F1}T";
                if (MarketCap.Value >= 1_000_000_000m) return $"{MarketCap.Value / 1_000_000_000m:F1}B";
                if (MarketCap.Value >= 1_000_000m) return $"{MarketCap.Value / 1_000_000m:F1}M";
                return $"{MarketCap.Value / 1_000m:F1}K";
            }
        }

        /// <summary>Форматированный sentiment с эмодзи: "🟢 0.72" / "🔴 -0.31" / "⚪ —".</summary>
        public string FormattedSentiment
        {
            get
            {
                decimal s = CompositeSentimentScore;
                return s > 0.3m ? $"🟢 {s:F2}" : s < -0.3m ? $"🔴 {s:F2}" : $"⚪ {s:F2}";
            }
        }
    }
}
