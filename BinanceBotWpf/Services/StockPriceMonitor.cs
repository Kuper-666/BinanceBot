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

        // Фьючерсные символы (TradFi Perpetuals) 
        // ВАЖНО: Убедитесь, что эти символы актуальны на Binance Futures.
        private readonly List<string> _trackedSymbols = new List<string>
        {
            "AAPLUSDT",   // Apple
            "MSFTUSDT",   // Microsoft
            "GOOGLUSDT",  // Alphabet (Google)
            "AMZNUSDT",   // Amazon
            "TSLAUSDT"    // Tesla
        };

        // Базовый URL для Futures API (публичные эндпоинты обычно не требуют ключа)
        private const string FuturesApiBaseUrl = "https://fapi.binance.com";

        public StockPriceMonitor(Action<string> logger)
        {
            _httpClient = new HttpClient ();
            _httpClient.BaseAddress = new Uri (FuturesApiBaseUrl);
            _logger = logger;
        }

        // Проверка, является ли текущее время торговым (Нью-Йорк, 9:30-16:00, Пн-Пт)
        private bool IsTradingHours()
        {
            var now = DateTime.UtcNow;
            var easternZone = TimeZoneInfo.FindSystemTimeZoneById ("Eastern Standard Time");
            var easternTime = TimeZoneInfo.ConvertTimeFromUtc (now, easternZone);

            if (easternTime.DayOfWeek == DayOfWeek.Saturday || easternTime.DayOfWeek == DayOfWeek.Sunday)
                return false;

            var tradingStart = new TimeSpan (9, 30, 0);
            var tradingEnd = new TimeSpan (16, 0, 0);
            var currentTime = easternTime.TimeOfDay;

            return currentTime >= tradingStart && currentTime < tradingEnd;
        }

        public async Task<JObject> Get24hrTickerAsync(string symbol)
        {
            try
            {
                var requestUrl = $"/fapi/v1/ticker/24hr?symbol={symbol}";
                var response = await _httpClient.GetAsync (requestUrl);
                var content = await response.Content.ReadAsStringAsync ();

                if (response.IsSuccessStatusCode)
                {
                    return JObject.Parse (content);
                }
                else
                {
                    _logger?.Invoke ($"⚠️ Ошибка получения 24hr данных для {symbol}: {content}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Исключение при запросе 24hr данных для {symbol}: {ex.Message}");
                return null;
            }
        }

        public async Task<decimal?> GetPriceAsync(string symbol)
        {
            try
            {
                var requestUrl = $"/fapi/v1/ticker/price?symbol={symbol}";
                var response = await _httpClient.GetAsync (requestUrl);
                var content = await response.Content.ReadAsStringAsync ();

                if (response.IsSuccessStatusCode)
                {
                    var data = JObject.Parse (content);
                    return (decimal)data["price"];
                }
                else
                {
                    _logger?.Invoke ($"⚠️ Ошибка получения цены для {symbol}: {content}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Исключение при запросе цены для {symbol}: {ex.Message}");
                return null;
            }
        }

        public async Task<List<(string Symbol, decimal Price, decimal PriceChangePercent, decimal Volume)>> FetchAllTrackedStocksAsync()
        {
            var results = new List<(string, decimal, decimal, decimal)> ();
            var tasks = new List<Task> ();

            // Если рынок закрыт, не делаем API-запросы, а возвращаем пустой список
            if (!IsTradingHours ())
            {
                _logger?.Invoke ("ℹ️ Рынок закрыт. Обновление данных приостановлено.");
                return results;
            }

            foreach (var symbol in _trackedSymbols)
            {
                var task = Task.Run (async () =>
                {
                    var data = await Get24hrTickerAsync (symbol);
                    if (data != null)
                    {
                        var price = (decimal)data["lastPrice"];
                        var changePercent = (decimal)data["priceChangePercent"];
                        var volume = (decimal)data["quoteVolume"];
                        lock (results)
                        {
                            results.Add ((symbol, price, changePercent, volume));
                        }
                    }
                });
                tasks.Add (task);
            }

            await Task.WhenAll (tasks);
            return results;
        }
    }
}