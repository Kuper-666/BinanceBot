using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public class StockPriceMonitor
    {
        private readonly HttpClient _httpClient;
        private readonly Action<string> _logger;

        private DateTime _lastMarketClosedLog = DateTime.MinValue;

        // Торгуемые символы (фьючерсные пары USDT)
        private readonly List<string> _trackedSymbols = new List<string>
        {
            "AAPLUSDT", "MSFTUSDT", "GOOGLUSDT", "AMZNUSDT", "TSLAUSDT"
        };

        private const string FuturesApiBaseUrl = "https://fapi.binance.com";

        public StockPriceMonitor(Action<string> logger)
        {
            _httpClient = new HttpClient { BaseAddress = new Uri (FuturesApiBaseUrl) };
            _logger = logger;
        }

        // Проверка торговых часов (Нью-Йорк, 9:30-16:00, Пн-Пт)
        private bool IsTradingHours()
        {
            var eastern = TimeZoneInfo.FindSystemTimeZoneById ("Eastern Standard Time");
            var nowEastern = TimeZoneInfo.ConvertTimeFromUtc (DateTime.UtcNow, eastern);
            if (nowEastern.DayOfWeek == DayOfWeek.Saturday || nowEastern.DayOfWeek == DayOfWeek.Sunday)
                return false;
            var start = new TimeSpan (9, 30, 0);
            var end = new TimeSpan (16, 0, 0);
            return nowEastern.TimeOfDay >= start && nowEastern.TimeOfDay < end;
        }

        public async Task<JObject> Get24hrTickerAsync(string symbol)
        {
            try
            {
                var response = await _httpClient.GetAsync ($"/fapi/v1/ticker/24hr?symbol={symbol}");
                var content = await response.Content.ReadAsStringAsync ();
                if (response.IsSuccessStatusCode) return JObject.Parse (content);
                _logger?.Invoke ($"⚠️ Ошибка {symbol}: {content}");
                return null;
            }
            catch (Exception ex) { _logger?.Invoke ($"❌ Исключение {symbol}: {ex.Message}"); return null; }
        }

        public async Task<List<(string Symbol, decimal Price, decimal PriceChangePercent, decimal Volume)>> FetchAllTrackedStocksAsync()
        {
            var results = new List<(string, decimal, decimal, decimal)> ();

            // Проверка торговых часов (Нью-Йорк, 9:30-16:00, Пн-Пт)
            if (!IsTradingHours ())
            {
                // Логируем не чаще раза в час
                if (DateTime.UtcNow - _lastMarketClosedLog > TimeSpan.FromHours (1))
                {
                    _logger?.Invoke ("ℹ️ Рынок акций закрыт. Обновление данных приостановлено.");
                    _lastMarketClosedLog = DateTime.UtcNow;
                }
                return results;
            }

            var tasks = new List<Task> ();
            foreach (var symbol in _trackedSymbols)
            {
                tasks.Add (Task.Run (async () =>
                {
                    var data = await Get24hrTickerAsync (symbol);
                    if (data != null)
                    {
                        decimal price = (decimal)data["lastPrice"];
                        decimal changePercent = (decimal)data["priceChangePercent"];
                        decimal volume = (decimal)data["quoteVolume"];
                        lock (results)
                        {
                            results.Add ((symbol, price, changePercent, volume));
                        }
                    }
                }));
            }

            await Task.WhenAll (tasks);
            return results;
        }
    }
}