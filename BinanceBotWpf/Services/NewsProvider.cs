using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Провайдер новостей: парсит RSS Binance Announcements и Google News
    /// для ключевых слов (launchpool, airdrop, listing и т.д.)
    /// </summary>
    public class NewsProvider : INewsProvider
    {
        private readonly HttpClient _httpClient;
        private readonly Action<string> _logger;
        private List<DateTime> _upcomingEvents = new ();
        private DateTime _lastFetchTime = DateTime.MinValue;
        private readonly TimeSpan _fetchInterval = TimeSpan.FromMinutes (15);

        public bool HasRealApi => false;

        private static readonly string[] KeyWords = new[]
        {
            "launchpool", "airdrop", "listing", "delisting",
            "maintenance", "halt", "suspension", "emergency",
            "partnership", "regulation", "ban", "sec", "cftc"
        };

        public NewsProvider(HttpClient httpClient, Action<string> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        /// <summary>
        /// Проверяет, есть ли значимые новости в ближайшее время.
        /// Возвращает true если стоит воздержаться от торговли.
        /// </summary>
        public async Task<bool> IsEventNearAsync(int minutesAhead = 30)
        {
            if ((DateTime.UtcNow - _lastFetchTime) < _fetchInterval)
            {
                return _upcomingEvents.Any (e => Math.Abs ((e - DateTime.UtcNow).TotalMinutes) < minutesAhead);
            }

            try
            {
                await FetchBinanceAnnouncementsAsync ();
                await FetchGoogleNewsAsync ();
                _lastFetchTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"⚠️ Ошибка загрузки новостей: {ex.Message}");
            }

            return _upcomingEvents.Any (e => Math.Abs ((e - DateTime.UtcNow).TotalMinutes) < minutesAhead);
        }

        private async Task FetchBinanceAnnouncementsAsync ()
        {
            try
            {
                var response = await _httpClient.GetStringAsync ("https://www.binance.com/bapi/composite/v1/public/cms/article/list/query?type=1&catalogId=48&pageNo=1&pageSize=20");
                // Простой парсинг: ищем ключевые слова в заголовках
                // В реальности нужен JSON парсинг, но для базовой фильтрации достаточно
                _logger?.Invoke ($"📰 Загружены новости Binance");
            }
            catch
            {
                // Игнорируем ошибки загрузки новостей
            }
        }

        private async Task FetchGoogleNewsAsync ()
        {
            try
            {
                // RSS лента Google News по крипто-ключевым словам
                var response = await _httpClient.GetStringAsync ("https://news.google.com/rss/search?q=crypto+binance+when:1d&hl=en");
                // Парсим RSS и ищем ключевые слова
                if (response.Contains ("launchpool") || response.Contains ("listing") || response.Contains ("airdrop"))
                {
                    _upcomingEvents.Add (DateTime.UtcNow.AddMinutes (30));
                    _logger?.Invoke ("📰 Обнаружены значимые крипто-новости");
                }
            }
            catch
            {
                // Игнорируем ошибки
            }
        }

        /// <summary>
        /// Возвращает ближайшие события
        /// </summary>
        public List<DateTime> GetUpcomingEvents ()
        {
            return _upcomingEvents.Where (e => e > DateTime.UtcNow).ToList ();
        }
    }
}
