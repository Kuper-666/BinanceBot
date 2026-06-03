using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

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
                    var id = link ?? title; // уникальный идентификатор

                    if (_processedIds.Contains (id)) continue;

                    if (IsRelevantAirdrop (title))
                    {
                        var message = FormatTelegramMessage (title, pubDate, link);
                        await _telegram.SendMessageAsync (message);
                        _processedIds.Add (id);
                        _logger?.Invoke ($"✉️ Отправлено уведомление об аирдропе: {title}");
                        await Task.Delay (1000);
                    }
                    else
                    {
                        // Помечаем как обработанные даже нерелевантные, чтобы не проверять их снова
                        _processedIds.Add (id);
                    }
                }

                if (_isFirstRun)
                {
                    _logger?.Invoke ("✅ Модуль уведомлений об аирдропах запущен.");
                    _isFirstRun = false;
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"⚠️ Не удалось загрузить RSS аирдропов: {ex.Message}");
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
                   lowerTitle.Contains ("staking");
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