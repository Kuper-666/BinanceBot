using System;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

namespace BinanceBotWpf.Models
{
    public class BinanceClient : IDisposable
    {
        private readonly string _apiKey;
        private readonly string _apiSecret;
        private readonly HttpClient _httpClient;
        private long _serverTimeOffset = 0;
        private readonly bool _useTestnet;

        public BinanceClient(string apiKey, string apiSecret, bool useTestnet = false)
        {
            _apiKey = apiKey;
            _apiSecret = apiSecret;
            _useTestnet = useTestnet;
            _httpClient = new HttpClient ();
            _httpClient.Timeout = TimeSpan.FromSeconds (15);
            _httpClient.BaseAddress = new Uri (useTestnet ? "https://testnet.binance.vision" : "https://api.binance.com");
            _httpClient.DefaultRequestHeaders.Add ("X-MBX-APIKEY", _apiKey);
        }

        private long GetTimestamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds () + _serverTimeOffset;

        public async Task SyncTimeAsync()
        {
            try
            {
                var response = await _httpClient.GetStringAsync ("/api/v3/time");
                long serverTime = JObject.Parse (response)["serverTime"].Value<long> ();
                _serverTimeOffset = serverTime - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ();
            }
            catch { _serverTimeOffset = 0; }
        }

        private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, int maxRetries = 3)
        {
            int retryCount = 0;
            int delayMs = 1000;
            while (true)
            {
                var response = await _httpClient.SendAsync (request);
                if (response.IsSuccessStatusCode)
                    return response;

                if ((int)response.StatusCode == 418 || (int)response.StatusCode == 429)
                {
                    retryCount++;
                    if (retryCount > maxRetries)
                        throw new Exception ($"Rate limit превышен после {maxRetries} попыток");
                    await Task.Delay (delayMs);
                    delayMs *= 2;
                    continue;
                }
                return response;
            }
        }

        public async Task<JObject> PlaceOrder(string symbol, string side, string type, decimal quantity)
        {
            string query = $"symbol={symbol}&side={side}&type={type}&quantity={quantity.ToString (CultureInfo.InvariantCulture)}&timestamp={GetTimestamp ()}";
            string signature = CreateSignature (query);
            var content = new StringContent ($"{query}&signature={signature}", Encoding.UTF8, "application/x-www-form-urlencoded");
            var request = new HttpRequestMessage (HttpMethod.Post, "/api/v3/order") { Content = content };
            var response = await SendWithRetryAsync (request);
            string body = await response.Content.ReadAsStringAsync ();

            if (response.IsSuccessStatusCode)
            {
                return JObject.Parse (body);
            }
            else
            {
                // Просто выводим ошибку в консоль отладки (видно в окне "Вывод" Visual Studio)
                System.Diagnostics.Debug.WriteLine ($"PlaceOrder ERROR for {symbol}: {response.StatusCode} - {body}");
                return null;
            }
        }

        /// <summary>
        /// Выкуп из гибкого Earn-продукта на спотовый кошелёк с ожиданием фактического зачисления.
        /// </summary>
        public async Task<bool> RedeemFlexibleEarnWithWaitAsync(string asset, decimal amount, int maxWaitSeconds = 16)
        {
            try
            {
                // 1. Получаем productId для актива
                var earnPositions = await GetFlexibleEarnBalanceAsync ();
                var targetPosition = earnPositions?.FirstOrDefault (p => p["asset"]?.ToString () == asset);
                if (targetPosition == null)
                {
                    Debug.WriteLine ($"❌ Нет Earn-позиции для {asset}");
                    return false;
                }
                string productId = targetPosition["productId"]?.ToString ();
                if (string.IsNullOrEmpty (productId)) return false;

                // 2. Отправляем запрос на выкуп
                long timestamp = GetTimestamp ();
                string query = $"productId={productId}&amount={amount.ToString (CultureInfo.InvariantCulture)}&destAccount=SPOT&timestamp={timestamp}";
                string signature = CreateSignature (query);
                var content = new StringContent ($"{query}&signature={signature}", Encoding.UTF8, "application/x-www-form-urlencoded");
                var request = new HttpRequestMessage (HttpMethod.Post, "/sapi/v1/simple-earn/flexible/redeem") { Content = content };
                var response = await SendWithRetryAsync (request);
                string json = await response.Content.ReadAsStringAsync ();
                if (!response.IsSuccessStatusCode)
                {
                    Debug.WriteLine ($"❌ Ошибка выкупа: {json}");
                    return false;
                }

                // 3. Ожидаем фактического появления средств на споте
                decimal targetAmount = amount;
                int attempts = maxWaitSeconds / 2; // проверяем каждые 2 секунды
                for (int i = 0; i < attempts; i++)
                {
                    await Task.Delay (2000);
                    decimal spotBalance = await GetAccountBalanceAsync (asset);
                    if (spotBalance >= targetAmount - 0.00001m) // небольшой допуск
                    {
                        Debug.WriteLine ($"✅ Выкуп {amount} {asset} подтверждён. Баланс на споте: {spotBalance}");
                        return true;
                    }
                }
                Debug.WriteLine ($"⚠️ После выкупа баланс {asset} на споте ({await GetAccountBalanceAsync (asset)}) меньше требуемого {targetAmount}");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine ($"❌ Исключение при выкупе: {ex.Message}");
                return false;
            }
        }

        // Старый метод для совместимости (использует тот же механизм, но без ожидания)
        public async Task<bool> RedeemFlexibleEarnAsync(string asset, decimal amount)
        {
            // Для обратной совместимости вызываем метод с ожиданием (по умолчанию 16 секунд)
            return await RedeemFlexibleEarnWithWaitAsync (asset, amount, 16);
        }

        private string CreateSignature(string query)
        {
            using (var hmac = new HMACSHA256 (Encoding.UTF8.GetBytes (_apiSecret)))
            {
                return BitConverter.ToString (hmac.ComputeHash (Encoding.UTF8.GetBytes (query))).Replace ("-", "").ToLower ();
            }
        }

        public void Dispose() => _httpClient.Dispose ();

        public async Task<string> GetServerInfo()
        {
            var response = await _httpClient.GetAsync ("/api/v3/ping");
            return response.IsSuccessStatusCode ? "Подключено успешно" : "Ошибка подключения";
        }

        public async Task<List<BinanceKline>> GetKlinesAsync(string symbol, string interval, int limit = 500)
        {
            string url = $"/api/v3/klines?symbol={symbol}&interval={interval}&limit={limit}";
            var request = new HttpRequestMessage (HttpMethod.Get, url);
            var response = await SendWithRetryAsync (request);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync ();
                var data = JArray.Parse (json);
                return data.Select (item => new BinanceKline
                {
                    Close = decimal.Parse (item[4].ToString (), CultureInfo.InvariantCulture)
                }).ToList ();
            }
            return new List<BinanceKline> ();
        }

        public async Task<JObject> GetAccountInfoAsync()
        {
            long timestamp = GetTimestamp ();
            string query = $"timestamp={timestamp}";
            string signature = CreateSignature (query);
            var request = new HttpRequestMessage (HttpMethod.Get, $"/api/v3/account?{query}&signature={signature}");
            var response = await SendWithRetryAsync (request);
            if (response.IsSuccessStatusCode)
                return JObject.Parse (await response.Content.ReadAsStringAsync ());
            return null;
        }

        public async Task<decimal> GetAccountBalanceAsync(string asset)
        {
            var accountInfo = await GetAccountInfoAsync ();
            if (accountInfo != null && accountInfo["balances"] != null)
            {
                foreach (var balance in accountInfo["balances"])
                {
                    if (balance["asset"]?.ToString () == asset)
                        return balance["free"]?.Value<decimal> () ?? 0m;
                }
            }
            return 0m;
        }

        public async Task<JArray> GetFlexibleEarnBalanceAsync()
        {
            try
            {
                long timestamp = GetTimestamp ();
                string query = $"timestamp={timestamp}";
                string signature = CreateSignature (query);
                var request = new HttpRequestMessage (HttpMethod.Get, $"/sapi/v1/simple-earn/flexible/position?{query}&signature={signature}");
                request.Headers.Add ("X-MBX-APIKEY", _apiKey);
                var response = await SendWithRetryAsync (request);
                string jsonString = await response.Content.ReadAsStringAsync ();
                if (response.IsSuccessStatusCode)
                {
                    var token = JToken.Parse (jsonString);
                    if (token is JArray array) return array;
                    if (token is JObject obj)
                    {
                        if (obj["rows"] != null) return (JArray)obj["rows"];
                        if (obj["list"] != null) return (JArray)obj["list"];
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine ($"DEBUG: Исключение при получении Earn: {ex.Message}");
            }
            return new JArray ();
        }

        public async Task<List<string>> GetTopVolumePairsAsync(string quoteAsset = "USDC", int topCount = 20)
        {
            try
            {
                var request = new HttpRequestMessage (HttpMethod.Get, "/api/v3/ticker/24hr");
                var response = await SendWithRetryAsync (request);
                if (!response.IsSuccessStatusCode)
                    return new List<string> ();
                var json = await response.Content.ReadAsStringAsync ();
                var tickers = JArray.Parse (json);
                var filtered = tickers
                    .Where (t => t["symbol"].ToString ().EndsWith (quoteAsset))
                    .Select (t => new
                    {
                        Symbol = t["symbol"].ToString (),
                        Volume = decimal.Parse (t["quoteVolume"].ToString (), CultureInfo.InvariantCulture)
                    })
                    .OrderByDescending (x => x.Volume)
                    .Take (topCount)
                    .Select (x => x.Symbol)
                    .ToList ();
                return filtered;
            }
            catch (Exception ex)
            {
                Debug.WriteLine ($"Ошибка GetTopVolumePairsAsync: {ex.Message}");
                return new List<string> { "BTCUSDC", "ETHUSDC", "BNBUSDC", "SOLUSDC" };
            }
        }

        public async Task<decimal> GetStepSizeAsync(string symbol)
        {
            // Упрощённая версия (кеш)
            var exchangeInfo = await GetExchangeInfoAsync ();
            var symInfo = exchangeInfo["symbols"]?.FirstOrDefault (s => s["symbol"].ToString () == symbol);
            var lotSize = symInfo?["filters"]?.FirstOrDefault (f => f["filterType"]?.ToString () == "LOT_SIZE");
            if (lotSize != null && lotSize["stepSize"] != null)
                return decimal.Parse (lotSize["stepSize"].ToString (), CultureInfo.InvariantCulture);
            return 0.00000001m;
        }

        private JObject _exchangeInfo;
        private async Task<JObject> GetExchangeInfoAsync()
        {
            if (_exchangeInfo != null) return _exchangeInfo;
            var response = await _httpClient.GetAsync ("/api/v3/exchangeInfo");
            if (response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync ();
                _exchangeInfo = JObject.Parse (json);
                return _exchangeInfo;
            }
            return new JObject ();
        }

        // Dust методы
        public async Task<JArray> GetDustAssetsAsync()
        {
            try
            {
                long timestamp = GetTimestamp ();
                string query = $"timestamp={timestamp}";
                string signature = CreateSignature (query);
                var request = new HttpRequestMessage (HttpMethod.Post, $"/sapi/v1/asset/dust?{query}&signature={signature}");
                request.Headers.Add ("X-MBX-APIKEY", _apiKey);
                var response = await SendWithRetryAsync (request);
                string json = await response.Content.ReadAsStringAsync ();
                if (response.IsSuccessStatusCode)
                {
                    var result = JObject.Parse (json);
                    return result["details"] as JArray ?? new JArray ();
                }
                Debug.WriteLine ($"GetDustAssets error: {json}");
            }
            catch (Exception ex) { Debug.WriteLine ($"GetDustAssets exception: {ex.Message}"); }
            return new JArray ();
        }

        public async Task<bool> ConvertDustToBnbAsync(List<string> assetIds)
        {
            if (assetIds == null || assetIds.Count == 0) return false;
            try
            {
                string assets = string.Join (",", assetIds);
                long timestamp = GetTimestamp ();
                string query = $"asset={assets}&timestamp={timestamp}";
                string signature = CreateSignature (query);
                var content = new StringContent ($"{query}&signature={signature}", Encoding.UTF8, "application/x-www-form-urlencoded");
                var request = new HttpRequestMessage (HttpMethod.Post, "/sapi/v1/asset/dust") { Content = content };
                var response = await SendWithRetryAsync (request);
                string json = await response.Content.ReadAsStringAsync ();
                if (response.IsSuccessStatusCode)
                {
                    Debug.WriteLine ($"Dust conversion success: {json}");
                    return true;
                }
                Debug.WriteLine ($"Dust conversion error: {json}");
            }
            catch (Exception ex) { Debug.WriteLine ($"ConvertDustToBnb exception: {ex.Message}"); }
            return false;
        }

        public async Task<JArray> GetFlexibleProductsAsync(string asset)
        {
            try
            {
                long timestamp = GetTimestamp ();
                string query = $"asset={asset}&timestamp={timestamp}";
                string signature = CreateSignature (query);
                var request = new HttpRequestMessage (HttpMethod.Get, $"/sapi/v1/simple-earn/flexible/list?{query}&signature={signature}");
                request.Headers.Add ("X-MBX-APIKEY", _apiKey);
                var response = await SendWithRetryAsync (request);
                string json = await response.Content.ReadAsStringAsync ();
                if (response.IsSuccessStatusCode)
                {
                    var result = JObject.Parse (json);
                    return result["rows"] as JArray ?? new JArray ();
                }
            }
            catch (Exception ex) { Debug.WriteLine ($"GetFlexibleProducts error: {ex.Message}"); }
            return new JArray ();
        }

        public async Task<bool> SubscribeFlexibleEarnAsync(string productId, decimal amount)
        {
            try
            {
                long timestamp = GetTimestamp ();
                string query = $"productId={productId}&amount={amount.ToString (CultureInfo.InvariantCulture)}&timestamp={timestamp}";
                string signature = CreateSignature (query);
                var content = new StringContent ($"{query}&signature={signature}", Encoding.UTF8, "application/x-www-form-urlencoded");
                var request = new HttpRequestMessage (HttpMethod.Post, "/sapi/v1/simple-earn/flexible/subscribe") { Content = content };
                var response = await SendWithRetryAsync (request);
                string json = await response.Content.ReadAsStringAsync ();
                if (response.IsSuccessStatusCode)
                    return true;
                Debug.WriteLine ($"Subscribe error: {json}");
            }
            catch (Exception ex) { Debug.WriteLine ($"Subscribe exception: {ex.Message}"); }
            return false;
        }
    }
}