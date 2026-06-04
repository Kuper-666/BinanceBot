using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

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
            // Проверка каждые 6 часов
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
            // 1. Парсим RSS ленту
            await ParseRssAsync ();
            // 2. Дополнительно проверяем через API (если доступно)
            await ParseApiAsync ();
        }

        private async Task ParseRssAsync()
        {
            string rssUrl = "https://www.binance.com/en/support/announcement/rss";
            try
            {
                var response = await _httpClient.GetStringAsync (rssUrl);
                var doc = XDocument.Parse (response);
                var items = doc.Descendants ("item");

                foreach (var item in items)
                {
                    var title = item.Element ("title")?.Value;
                    var pubDate = item.Element ("pubDate")?.Value;
                    var link = item.Element ("link")?.Value;
                    var id = link ?? title;

                    if (_processedIds.Contains (id)) continue;

                    if (IsRelevantAirdrop (title))
                    {
                        var message = FormatTelegramMessage (title, pubDate, link);
                        await _telegram.SendMessageAsync (message);
                        _processedIds.Add (id);
                        _logger?.Invoke ($"✉️ Отправлено уведомление об аирдропе (RSS): {title}");
                        await Task.Delay (1000);
                    }
                    else
                    {
                        _processedIds.Add (id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"⚠️ Ошибка RSS аирдропов: {ex.Message}");
            }
        }

        private async Task ParseApiAsync()
        {
            // Альтернативный API-эндпоинт (неофициальный, может работать)
            string apiUrl = "https://www.binance.com/bapi/apex/v1/public/apex/cms/article/list?type=1&catalogId=48&pageNo=1&pageSize=20";
            try
            {
                var response = await _httpClient.GetStringAsync (apiUrl);
                var json = JObject.Parse (response);
                var articles = json["data"]?["articles"] as JArray;
                if (articles == null) return;

                foreach (var article in articles)
                {
                    var title = article["title"]?.ToString ();
                    var releaseDate = article["releaseDate"]?.ToString ();
                    var code = article["code"]?.ToString ();
                    if (string.IsNullOrEmpty (code)) continue;

                    if (_processedIds.Contains (code)) continue;

                    if (IsRelevantAirdrop (title))
                    {
                        var message = FormatTelegramMessage (title, releaseDate, $"https://www.binance.com/en/support/announcement/{code}");
                        await _telegram.SendMessageAsync (message);
                        _processedIds.Add (code);
                        _logger?.Invoke ($"✉️ Отправлено уведомление об аирдропе (API): {title}");
                        await Task.Delay (1000);
                    }
                    else
                    {
                        _processedIds.Add (code);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"⚠️ Ошибка API аирдропов: {ex.Message}");
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
                   lowerTitle.Contains ("hodler airdrop") ||
                   lowerTitle.Contains ("simple earn") ||
                   lowerTitle.Contains ("staking") ||
                   lowerTitle.Contains ("airdrop") ||
                   lowerTitle.Contains ("new token") ||
                   lowerTitle.Contains ("token distribution");
        }

        private string FormatTelegramMessage(string title, string pubDate, string link)
        {
            return $"🎁 <b>Новый аирдроп на Binance!</b>\n\n" +
                   $"📢 {title}\n" +
                   $"📅 Анонсирован: {pubDate}\n\n" +
                   $"🔗 Подробности: {link}";
        }
    }
}