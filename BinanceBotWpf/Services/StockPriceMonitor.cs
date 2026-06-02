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

        // Символы для отслеживания (формат: тикер + USDC)
        // На Binance акции торгуются в паре с USDC, например AAPLUSDC, TSLAUSDC
        private readonly List<string> _trackedSymbols = new List<string>
        {
            "AAPLUSDC",
            "MSFTUSDC",
            "GOOGLUSDC",
            "AMZNUSDC",
            "TSLAUSDC",
            "NVDAUSDC",
            "METAUSDC"
        };

        public StockPriceMonitor(Action<string> logger)
        {
            _httpClient = new HttpClient ();
            _httpClient.BaseAddress = new Uri ("https://api.binance.com");
            _logger = logger;
        }

        // Получение 24-часовой статистики через публичный эндпоинт (без API-ключа)
        public async Task<JObject> Get24hrTickerAsync(string symbol)
        {
            try
            {
                var requestUrl = $"/api/v3/ticker/24hr?symbol={symbol}";
                var response = await _httpClient.GetAsync (requestUrl);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync ();
                    return JObject.Parse (json);
                }
                else
                {
                    var error = await response.Content.ReadAsStringAsync ();
                    _logger?.Invoke ($"⚠️ Ошибка получения данных для {symbol}: HTTP {response.StatusCode}, {error}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Исключение при запросе {symbol}: {ex.Message}");
                return null;
            }
        }

        // Получение текущей цены
        public async Task<decimal?> GetPriceAsync(string symbol)
        {
            try
            {
                var requestUrl = $"/api/v3/ticker/price?symbol={symbol}";
                var response = await _httpClient.GetAsync (requestUrl);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync ();
                    var data = JObject.Parse (json);
                    return (decimal)data["price"];
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Ошибка получения цены {symbol}: {ex.Message}");
                return null;
            }
        }

        // Основной метод для обновления всех отслеживаемых акций
        public async Task<List<(string Symbol, decimal Price, decimal PriceChangePercent, decimal Volume)>> FetchAllTrackedStocksAsync()
        {
            var results = new List<(string, decimal, decimal, decimal)> ();
            var tasks = new List<Task> ();

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