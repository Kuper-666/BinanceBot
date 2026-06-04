using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace BinanceBotWpf.Services
{
    /// <summary>Периодическая проверка новых аирдропов на Binance (RSS + API).</summary>
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
            _timer = new Timer (CheckForUpdates, null, TimeSpan.Zero, TimeSpan.FromHours (6));
        }

        private async void CheckForUpdates(object state)
        {
            try { await FetchAndNotifyNewAirdrops (); }
            catch (Exception ex) { _logger?.Invoke ($"❌ Ошибка при проверке аирдропов: {ex.Message}"); }
        }

        private async Task FetchAndNotifyNewAirdrops()
        {
            await ParseRssAsync ();
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
                        await _telegram.SendMessageAsync (FormatTelegramMessage (title, pubDate, link));
                        _processedIds.Add (id);
                        await Task.Delay (1000);
                    }
                    else _processedIds.Add (id);
                }
            }
            catch (Exception ex) { _logger?.Invoke ($"⚠️ Ошибка RSS: {ex.Message}"); }
        }

        private async Task ParseApiAsync()
        {
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
                        await _telegram.SendMessageAsync (FormatTelegramMessage (title, releaseDate, $"https://www.binance.com/en/support/announcement/{code}"));
                        _processedIds.Add (code);
                        await Task.Delay (1000);
                    }
                    else _processedIds.Add (code);
                }
            }
            catch (Exception ex) { _logger?.Invoke ($"⚠️ Ошибка API: {ex.Message}"); }

            if (_isFirstRun)
            {
                _logger?.Invoke ("✅ Модуль уведомлений об аирдропах запущен.");
                _isFirstRun = false;
            }
        }

        private bool IsRelevantAirdrop(string title)
        {
            if (string.IsNullOrEmpty (title)) return false;
            var lower = title.ToLower ();
            return lower.Contains ("launchpool") || lower.Contains ("megadrop") || lower.Contains ("hodler airdrop") ||
                   lower.Contains ("simple earn") || lower.Contains ("staking") || lower.Contains ("airdrop");
        }

        private string FormatTelegramMessage(string title, string pubDate, string link) =>
            $"🎁 <b>Новый аирдроп на Binance!</b>\n\n📢 {title}\n📅 Анонсирован: {pubDate}\n\n🔗 Подробности: {link}";
    }
}