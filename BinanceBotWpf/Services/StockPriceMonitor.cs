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
        private readonly bool _isTestnet;
        private DateTime _lastMarketClosedLog = DateTime.MinValue;
        private DateTime _lastTestnetLog = DateTime.MinValue;

        private readonly List<string> _trackedSymbols = new ()
        {
            "AAPLUSDT", "MSFTUSDT", "GOOGLUSDT", "AMZNUSDT", "TSLAUSDT"
        };
        private const string FuturesApiBaseUrl = "https://fapi.binance.com";

        public StockPriceMonitor(Action<string> logger, bool isTestnet = false)
        {
            _logger = logger;
            _isTestnet = isTestnet;
            _httpClient = new HttpClient { BaseAddress = new Uri (FuturesApiBaseUrl) };
        }

        private bool IsTradingHours()
        {
            if (_isTestnet) return false;
            try
            {
                var eastern = TimeZoneInfo.FindSystemTimeZoneById ("Eastern Standard Time");
                var nowEastern = TimeZoneInfo.ConvertTimeFromUtc (DateTime.UtcNow, eastern);
                if (nowEastern.DayOfWeek == DayOfWeek.Saturday || nowEastern.DayOfWeek == DayOfWeek.Sunday)
                    return false;
                var start = new TimeSpan (9, 30, 0);
                var end = new TimeSpan (16, 0, 0);
                return nowEastern.TimeOfDay >= start && nowEastern.TimeOfDay < end;
            }
            catch
            {
                // Если не удалось определить часовой пояс, считаем рынок открытым
                return true;
            }
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

            if (_isTestnet)
            {
                if (DateTime.UtcNow - _lastTestnetLog > TimeSpan.FromHours (1))
                {
                    _logger?.Invoke ("ℹ️ Режим тестовой сети: мониторинг акций отключён.");
                    _lastTestnetLog = DateTime.UtcNow;
                }
                return results;
            }

            if (!IsTradingHours ())
            {
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
                        lock (results) { results.Add ((symbol, price, changePercent, volume)); }
                    }
                }));
            }
            await Task.WhenAll (tasks);
            return results;
        }
    }
}