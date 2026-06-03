using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public class AirdropNotifier
    {
        private readonly TelegramNotifier _telegram;
        private readonly HttpClient _httpClient;
        private readonly Action<string> _logger;
        private readonly Timer _timer;
        private readonly HashSet<string> _processedIds;
        private bool _isFirstRun = true;

        public AirdropNotifier(TelegramNotifier telegram, Action<string> logger)
        {
            _telegram = telegram;
            _logger = logger;
            _httpClient = new HttpClient ();
            _processedIds = new HashSet<string> ();

            // Запускаем проверку каждые 6 часов
            _timer = new Timer (CheckForUpdates, null, TimeSpan.Zero, TimeSpan.FromHours (6));
        }

        private async void CheckForUpdates(object state)
        {
            try
            {
                await FetchAndNotifyNewAirdrops ();
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Ошибка при проверке аирдропов: {ex.Message}");
            }
        }

        private async Task FetchAndNotifyNewAirdrops()
        {
            // RSS-лента анонсов Binance (более стабильный источник)
            string rssUrl = "https://www.binance.com/en/support/announcement/rss";
            try
            {
                var response = await _httpClient.GetStringAsync (rssUrl);
                // Парсинг RSS (упрощённо – можно регулярными выражениями, но для простоты пропускаем)
                // В реальном проекте лучше использовать XmlDocument или SyndicationFeed.
                // Пока просто логируем, что RSS получен.
                _logger?.Invoke ("✅ RSS анонсов Binance получен (парсинг не реализован).");
                // Здесь нужна реализация парсинга RSS.
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"⚠️ Не удалось загрузить RSS аирдропов: {ex.Message}");
            }
        }
    }
}