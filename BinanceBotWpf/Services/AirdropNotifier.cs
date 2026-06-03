using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BinanceBotWpf.Services;

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

            // Запускаем проверку каждые 6 часов (21,600,000 миллисекунд)
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
            var url = "https://www.binance.com/bapi/apex/v1/public/apex/cms/article/list?type=1&catalogId=48&pageNo=1&pageSize=20";

            var response = await _httpClient.GetStringAsync (url);
            using var jsonDoc = JsonDocument.Parse (response);
            var articles = jsonDoc.RootElement.GetProperty ("data").GetProperty ("articles");

            foreach (var article in articles.EnumerateArray ())
            {
                var title = article.GetProperty ("title").GetString ();
                var releaseDate = article.GetProperty ("releaseDate").GetString ();
                var code = article.GetProperty ("code").GetString ();

                if (_processedIds.Contains (code))
                    continue;

                // Обрабатываем только анонсы Launchpool, Megadrop и HODLer Airdrops
                if (IsRelevantAirdrop (title))
                {
                    var message = FormatTelegramMessage (title, releaseDate);
                    await _telegram.SendMessageAsync (message);
                    _processedIds.Add (code);
                    _logger?.Invoke ($"✉️ Отправлено уведомление об аирдропе: {title}");
                    await Task.Delay (1000);
                }
            }

            if (_isFirstRun)
            {
                _logger?.Invoke ("✅ Модуль уведомлений об аирдропах запущен.");
                _isFirstRun = false;
            }
        }

        private bool IsRelevantAirdrop(string title)
        {
            if (string.IsNullOrEmpty (title)) return false;
            var lowerTitle = title.ToLower ();
            return lowerTitle.Contains ("launchpool") ||
                   lowerTitle.Contains ("megadrop") ||
                   lowerTitle.Contains ("hodler airdrop");
        }

        private string FormatTelegramMessage(string title, string releaseDate)
        {
            return $"🎁 <b>Новый аирдроп на Binance!</b>\n\n" +
                   $"📢 {title}\n" +
                   $"📅 Анонсирован: {releaseDate}\n\n" +
                   $"🔗 Подробности: https://www.binance.com/en/support/announcement";
        }
    }
}