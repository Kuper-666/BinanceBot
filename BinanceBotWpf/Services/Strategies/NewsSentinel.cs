using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BinanceBotWpf.Services.Strategies
{
    public class NewsSentinel
    {
        private readonly string _dbPath;
        private readonly Action<string> _logger;
        private bool _diskFull;
        private readonly int _maxNewsAgeHours = 6;
        private readonly int _highImpactThreshold = 3;

        public NewsSentinel (Action<string> logger, string customDbPath = null)
        {
            _logger = logger;
            _dbPath = customDbPath ?? Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Data", "news.db");
            EnsureDatabase ();
        }

        private void EnsureDatabase ()
        {
            try
            {
                string dir = Path.GetDirectoryName (_dbPath);
                if (!Directory.Exists (dir)) Directory.CreateDirectory (dir);

                using var connection = new SqliteConnection ($"Data Source={_dbPath}");
                connection.Open ();
                using var cmd = connection.CreateCommand ();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS news (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        title TEXT NOT NULL,
                        source TEXT,
                        sentiment TEXT,
                        impact INTEGER DEFAULT 0,
                        symbols TEXT,
                        fetched_at TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_fetched_at ON news(fetched_at);
                    CREATE INDEX IF NOT EXISTS idx_sentiment ON news(sentiment);
                ";
                cmd.ExecuteNonQuery ();

                // Remove existing duplicates before creating unique index
                cmd.CommandText = @"
                    DELETE FROM news WHERE id NOT IN (
                        SELECT MAX(id) FROM news GROUP BY title
                    );
                ";
                int removed = cmd.ExecuteNonQuery ();
                if (removed > 0)
                {
                    _logger?.Invoke ($"🧹 NewsSentinel: удалено {removed} дублей при запуске");
                }

                cmd.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_unique_title ON news(title);";
                cmd.ExecuteNonQuery ();
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"⚠️ Ошибка инициализации SQLite: {ex.Message}");
            }
        }

        /// <summary>
        /// Проверяет наличие высокорисковых новостей для конкретной пары.
        /// При проверке конкретной пары считает только новости, упоминающие именно её (не generic `*`).
        /// </summary>
        public bool IsHighImpactNewsActive (string symbol = null)
        {
            try
            {
                using var connection = new SqliteConnection ($"Data Source={_dbPath}");
                connection.Open ();
                using var cmd = connection.CreateCommand ();

                string cutoff = DateTime.UtcNow.AddHours (-_maxNewsAgeHours).ToString ("yyyy-MM-ddTHH:mm:ssZ");

                if (!string.IsNullOrEmpty (symbol))
                {
                    string baseSymbol = symbol.Replace ("USDC", "").Replace ("USDT", "");
                    cmd.CommandText = @"
                        SELECT COUNT(DISTINCT title) FROM news
                        WHERE sentiment = 'negative'
                        AND impact >= @threshold
                        AND fetched_at >= @cutoff
                        AND symbols LIKE @sym
                        AND symbols != '*'
                    ";
                    cmd.Parameters.AddWithValue ("@sym", $"%{baseSymbol}%");
                }
                else
                {
                    cmd.CommandText = @"
                        SELECT COUNT(DISTINCT title) FROM news
                        WHERE sentiment = 'negative'
                        AND impact >= @threshold
                        AND fetched_at >= @cutoff
                    ";
                }

                cmd.Parameters.AddWithValue ("@threshold", _highImpactThreshold);
                cmd.Parameters.AddWithValue ("@cutoff", cutoff);

                long count = (long)(cmd.ExecuteScalar () ?? 0);
                if (count > 0)
                {
                    _logger?.Invoke ($"⚠️ NewsSentinel: обнаружено {count} негативных новостей за {_maxNewsAgeHours}ч");
                }
                return count > 0;
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"⚠️ NewsSentinel query error: {ex.Message}");
                return false;
            }
        }

        public List<NewsItem> GetRecentNews (int hours = 6)
        {
            var result = new List<NewsItem> ();
            try
            {
                using var connection = new SqliteConnection ($"Data Source={_dbPath}");
                connection.Open ();
                using var cmd = connection.CreateCommand ();
                string cutoff = DateTime.UtcNow.AddHours (-hours).ToString ("yyyy-MM-ddTHH:mm:ssZ");
                cmd.CommandText = "SELECT title, source, sentiment, impact, symbols, fetched_at FROM news WHERE fetched_at >= @cutoff GROUP BY title ORDER BY fetched_at DESC LIMIT 50";
                cmd.Parameters.AddWithValue ("@cutoff", cutoff);

                using var reader = cmd.ExecuteReader ();
                while (reader.Read ())
                {
                    result.Add (new NewsItem
                    {
                        Title = reader.GetString (0),
                        Source = reader.IsDBNull (1) ? "" : reader.GetString (1),
                        Sentiment = reader.IsDBNull (2) ? "neutral" : reader.GetString (2),
                        Impact = reader.IsDBNull (3) ? 0 : reader.GetInt32 (3),
                        Symbols = reader.IsDBNull (4) ? "" : reader.GetString (4),
                        FetchedAt = reader.GetString (5)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"⚠️ NewsSentinel read error: {ex.Message}");
            }
            return result;
        }

        public int InsertNews (string title, string source, string sentiment, int impact, string symbols)
        {
            if (_diskFull) return 0;
            try
            {
                using var connection = new SqliteConnection ($"Data Source={_dbPath}");
                connection.Open ();
                using var cmd = connection.CreateCommand ();
                cmd.CommandText = @"
                    INSERT OR IGNORE INTO news (title, source, sentiment, impact, symbols, fetched_at)
                    VALUES (@title, @source, @sentiment, @impact, @symbols, @fetched_at)
                ";
                cmd.Parameters.AddWithValue ("@title", title);
                cmd.Parameters.AddWithValue ("@source", source ?? "");
                cmd.Parameters.AddWithValue ("@sentiment", sentiment ?? "neutral");
                cmd.Parameters.AddWithValue ("@impact", impact);
                cmd.Parameters.AddWithValue ("@symbols", symbols ?? "*");
                cmd.Parameters.AddWithValue ("@fetched_at", DateTime.UtcNow.ToString ("yyyy-MM-ddTHH:mm:ssZ"));
                return cmd.ExecuteNonQuery ();
            }
            catch (Exception ex)
            {
                string msg = ex.Message;
                _logger?.Invoke ($"⚠️ NewsSentinel insert error: {msg}");
                if (msg.Contains ("disk is full") || msg.Contains ("database or disk is full"))
                    _diskFull = true;
                return 0;
            }
        }

        public int CleanupOldNews (int maxAgeHours = 48)
        {
            try
            {
                using var connection = new SqliteConnection ($"Data Source={_dbPath}");
                connection.Open ();
                using var cmd = connection.CreateCommand ();
                string cutoff = DateTime.UtcNow.AddHours (-maxAgeHours).ToString ("yyyy-MM-ddTHH:mm:ssZ");
                cmd.CommandText = "DELETE FROM news WHERE fetched_at < @cutoff";
                cmd.Parameters.AddWithValue ("@cutoff", cutoff);
                return cmd.ExecuteNonQuery ();
            }
            catch { return 0; }
        }

        public NewsSentinelStats GetStats ()
        {
            var stats = new NewsSentinelStats ();
            try
            {
                using var connection = new SqliteConnection ($"Data Source={_dbPath}");
                connection.Open ();
                using var cmd = connection.CreateCommand ();
                string cutoff = DateTime.UtcNow.AddHours (-_maxNewsAgeHours).ToString ("yyyy-MM-ddTHH:mm:ssZ");
                cmd.CommandText = "SELECT sentiment, COUNT(*) FROM news WHERE fetched_at >= @cutoff GROUP BY sentiment";
                cmd.Parameters.AddWithValue ("@cutoff", cutoff);

                using var reader = cmd.ExecuteReader ();
                while (reader.Read ())
                {
                    string sentiment = reader.GetString (0);
                    int count = reader.GetInt32 (1);
                    switch (sentiment)
                    {
                        case "positive": stats.PositiveCount = count; break;
                        case "negative": stats.NegativeCount = count; break;
                        case "neutral": stats.NeutralCount = count; break;
                    }
                }
                stats.TotalCount = stats.PositiveCount + stats.NegativeCount + stats.NeutralCount;
            }
            catch { }
            return stats;
        }
    }

    public class NewsItem
    {
        public string Title { get; set; } = "";
        public string Source { get; set; } = "";
        public string Sentiment { get; set; } = "neutral";
        public int Impact { get; set; }
        public string Symbols { get; set; } = "*";
        public string FetchedAt { get; set; } = "";
    }

    public class NewsSentinelStats
    {
        public int TotalCount { get; set; }
        public int PositiveCount { get; set; }
        public int NegativeCount { get; set; }
        public int NeutralCount { get; set; }
    }
}
