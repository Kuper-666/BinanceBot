using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BinanceBotWpf.Models
{
    public class BinanceClient : IDisposable
    {
        private readonly string _apiKey;
        private readonly string _apiSecret;
        private readonly HttpClient _httpClient;
        private long _serverTimeOffset = 0;
        private readonly bool _useTestnet;
        private JObject _exchangeInfo;
        private readonly Dictionary<string, decimal> _stepSizeCache = new ();
        private readonly SemaphoreSlim _rateLimiter = new SemaphoreSlim (5, 5);
        private DateTime _lastRequestTime = DateTime.UtcNow;
        private readonly TimeSpan _minInterval = TimeSpan.FromMilliseconds (200);

        public event Action<string> OnLogGenerated;
        public string LastOrderError { get; private set; }
        public bool IsTestnet => _useTestnet;

        private void Log(string message) => OnLogGenerated?.Invoke (message);

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

        private async Task<HttpResponseMessage> ThrottledSendAsync(HttpRequestMessage request)
        {
            await _rateLimiter.WaitAsync ();
            try
            {
                var now = DateTime.UtcNow;
                var elapsed = now - _lastRequestTime;
                if (elapsed < _minInterval)
                {
                    await Task.Delay (_minInterval - elapsed);
                }
                _lastRequestTime = DateTime.UtcNow;
                return await _httpClient.SendAsync (request);
            }
            finally
            {
                _rateLimiter.Release ();
            }
        }

        private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, int maxRetries = 3)
        {
            int retryCount = 0;
            int delayMs = 1000;
            while (true)
            {
                var response = await ThrottledSendAsync (request);
                if (response.IsSuccessStatusCode) return response;
                if ((int)response.StatusCode == 418 || (int)response.StatusCode == 429)
                {
                    retryCount++;
                    if (retryCount > maxRetries) throw new Exception ($"Rate limit превышен после {maxRetries} попыток");
                    await Task.Delay (delayMs);
                    delayMs *= 2;
                    continue;
                }
                return response;
            }
        }

        public async Task<JObject> PlaceOrder(string symbol, string side, string type, decimal quantity)
        {
            try
            {
                string query = $"symbol={symbol}&side={side}&type={type}&quantity={quantity.ToString (CultureInfo.InvariantCulture)}&timestamp={GetTimestamp ()}";
                string signature = CreateSignature (query);
                var content = new StringContent ($"{query}&signature={signature}", Encoding.UTF8, "application/x-www-form-urlencoded");
                var request = new HttpRequestMessage (HttpMethod.Post, "/api/v3/order") { Content = content };
                var response = await SendWithRetryAsync (request);
                string body = await response.Content.ReadAsStringAsync ();

                if (response.IsSuccessStatusCode)
                {
                    LastOrderError = null;
                    return JObject.Parse (body);
                }
                else
                {
                    LastOrderError = body;
                    Log ($"PlaceOrder ERROR for {symbol}: {response.StatusCode} - {body}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                LastOrderError = ex.Message;
                Log ($"PlaceOrder EXCEPTION: {ex.Message}");
                return null;
            }
        }

        public async Task<List<JObject>> GetAllOrdersAsync(string symbol, long startTime = 0, long endTime = 0, int limit = 500)
        {
            try
            {
                long timestamp = GetTimestamp ();
                string query = $"symbol={symbol}&timestamp={timestamp}&limit={limit}";
                if (startTime > 0) query += $"&startTime={startTime}";
                if (endTime > 0) query += $"&endTime={endTime}";
                string signature = CreateSignature (query);
                var request = new HttpRequestMessage (HttpMethod.Get, $"/api/v3/allOrders?{query}&signature={signature}");
                var response = await SendWithRetryAsync (request);
                string body = await response.Content.ReadAsStringAsync ();
                if (response.IsSuccessStatusCode)
                {
                    return JArray.Parse (body).ToObject<List<JObject>> ();
                }
                Log ($"GetAllOrders error for {symbol}: {body}");
                return null;
            }
            catch (Exception ex)
            {
                Log ($"GetAllOrders exception: {ex.Message}");
                return null;
            }
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
                    Log ($"Dust conversion success: {json}");
                    return true;
                }
                Log ($"Dust conversion error: {json}");
            }
            catch (Exception ex) { Log ($"ConvertDustToBnb exception: {ex.Message}"); }
            return false;
        }

        public async Task<bool> ConvertDustToUsdcAsync(List<string> assetIds = null)
        {
            try
            {
                if (assetIds == null || assetIds.Count == 0)
                {
                    var dust = await GetDustAssetsAsync ();
                    if (dust == null || dust.Count == 0) return false;
                    assetIds = dust.Select (item => item["assetId"]?.ToString ()).Where (id => !string.IsNullOrEmpty (id)).ToList ();
                    if (assetIds.Count == 0) return false;
                }
                bool dustConverted = await ConvertDustToBnbAsync (assetIds);
                if (!dustConverted) return false;

                decimal bnbBalance = await GetAccountBalanceAsync ("BNB");
                if (bnbBalance < 0.001m) return true;
                var klines = await GetKlinesAsync ("BNBUSDC", "5m", 1);
                if (klines == null || klines.Count == 0) return false;
                decimal price = klines.Last ().Close;
                decimal step = await GetStepSizeAsync ("BNBUSDC");
                decimal qty = Math.Floor (bnbBalance / step) * step;
                if (qty <= 0) return false;
                var order = await PlaceOrder ("BNBUSDC", "SELL", "MARKET", qty);
                if (order != null)
                {
                    Log ($"✅ Sold {qty} BNB for USDC");
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Log ($"ConvertDustToUsdcAsync error: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> ConvertToUsdcAsync(string asset, decimal amount)
        {
            try
            {
                string pair = asset + "USDC";
                var klines = await GetKlinesAsync (pair, "5m", 5);
                if (klines == null || klines.Count == 0) return false;
                decimal price = klines.Last ().Close;
                decimal estimatedValue = amount * price;
                if (estimatedValue < 6.0m) return false;
                decimal stepSize = await GetStepSizeAsync (pair);
                decimal normalizedAmount = Math.Floor (amount / stepSize) * stepSize;
                if (normalizedAmount <= 0) return false;
                var order = await PlaceOrder (pair, "SELL", "MARKET", normalizedAmount);
                return order != null;
            }
            catch (Exception ex)
            {
                Log ($"ConvertToUsdcAsync error: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> RedeemFlexibleEarnWithWaitAsync(string asset, decimal amount, int maxWaitSeconds = 60)
        {
            try
            {
                Log ($"DEBUG: Попытка выкупа {amount} {asset} из Earn");
                var earnPositions = await GetFlexibleEarnBalanceAsync ();
                if (earnPositions == null || earnPositions.Count == 0)
                {
                    Log ($"❌ Нет позиций в Earn для {asset}");
                    return false;
                }
                var targetPosition = earnPositions.FirstOrDefault (p => p["asset"]?.ToString () == asset);
                if (targetPosition == null)
                {
                    Log ($"❌ Не найдена Earn-позиция для {asset}");
                    return false;
                }
                decimal availableInEarn = decimal.Parse (targetPosition["totalAmount"]?.ToString () ?? "0", CultureInfo.InvariantCulture);
                Log ($"DEBUG: Доступно в Earn: {availableInEarn}, требуется: {amount}");
                if (availableInEarn < amount - 0.000001m)
                {
                    Log ($"⚠️ В Earn недостаточно {asset}: доступно {availableInEarn}, требуется {amount}");
                    return false;
                }
                string productId = targetPosition["productId"]?.ToString ();
                if (string.IsNullOrEmpty (productId))
                {
                    Log ($"❌ Не найден productId для {asset}");
                    return false;
                }
                long timestamp = GetTimestamp ();
                string query = $"productId={productId}&amount={amount.ToString (CultureInfo.InvariantCulture)}&destAccount=SPOT&timestamp={timestamp}";
                string signature = CreateSignature (query);
                var content = new StringContent ($"{query}&signature={signature}", Encoding.UTF8, "application/x-www-form-urlencoded");
                var request = new HttpRequestMessage (HttpMethod.Post, "/sapi/v1/simple-earn/flexible/redeem") { Content = content };
                var response = await SendWithRetryAsync (request);
                string json = await response.Content.ReadAsStringAsync ();

                Log ($"DEBUG Redeem response: {json}");
                if (!response.IsSuccessStatusCode)
                {
                    Log ($"❌ Ошибка выкупа {asset}: HTTP {response.StatusCode}, ответ: {json}");
                    return false;
                }
                decimal initialSpot = await GetAccountBalanceAsync (asset);
                for (int i = 0; i < maxWaitSeconds / 2; i++)
                {
                    await Task.Delay (2000);
                    decimal spotBalance = await GetAccountBalanceAsync (asset);
                    if (spotBalance >= initialSpot + amount - 0.000001m)
                    {
                        Log ($"✅ Выкуп {amount} {asset} подтверждён. Баланс на споте: {spotBalance}");
                        return true;
                    }
                    var freshEarn = await GetFlexibleEarnBalanceAsync ();
                    var freshPos = freshEarn?.FirstOrDefault (p => p["asset"]?.ToString () == asset);
                    if (freshPos != null)
                    {
                        decimal remaining = decimal.Parse (freshPos["totalAmount"]?.ToString () ?? "0", CultureInfo.InvariantCulture);
                        if (availableInEarn - remaining >= amount - 0.000001m)
                        {
                            Log ($"✅ Выкуп {amount} {asset} подтверждён (Earn уменьшился с {availableInEarn} до {remaining})");
                            return true;
                        }
                    }
                }
                Log ($"⚠️ Таймаут {maxWaitSeconds} сек: выкуп {amount} {asset} не подтверждён. Спот баланс: {await GetAccountBalanceAsync (asset)}");
                return false;
            }
            catch (Exception ex)
            {
                Log ($"❌ Исключение при выкупе {asset}: {ex.Message}");
                return false;
            }
        }

        public async Task<JObject> PlaceOcoOrder(string symbol, decimal quantity, decimal stopPrice, decimal limitPrice)
        {
            try
            {
                long timestamp = GetTimestamp ();
                decimal tickSize = await GetTickSizeAsync (symbol);
                decimal roundedLimitPrice = Math.Round (limitPrice / tickSize) * tickSize;
                decimal roundedStopPrice = Math.Round (stopPrice / tickSize) * tickSize;
                string query = $"symbol={symbol}&side=SELL&quantity={quantity.ToString (CultureInfo.InvariantCulture)}" +
                               $"&price={roundedLimitPrice.ToString (CultureInfo.InvariantCulture)}" +
                               $"&stopPrice={roundedStopPrice.ToString (CultureInfo.InvariantCulture)}" +
                               $"&timestamp={timestamp}";
                string signature = CreateSignature (query);
                var content = new StringContent ($"{query}&signature={signature}", Encoding.UTF8, "application/x-www-form-urlencoded");
                var request = new HttpRequestMessage (HttpMethod.Post, "/api/v3/order/oco") { Content = content };
                var response = await SendWithRetryAsync (request);
                string body = await response.Content.ReadAsStringAsync ();
                if (response.IsSuccessStatusCode)
                {
                    LastOrderError = null;
                    return JObject.Parse (body);
                }
                else
                {
                    LastOrderError = body;
                    Log ($"PlaceOcoOrder ERROR for {symbol}: {response.StatusCode} - {body}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                LastOrderError = ex.Message;
                Log ($"PlaceOcoOrder EXCEPTION: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> CancelOcoOrder(string symbol, long orderListId)
        {
            try
            {
                long timestamp = GetTimestamp ();
                string query = $"symbol={symbol}&orderListId={orderListId}&timestamp={timestamp}";
                string signature = CreateSignature (query);
                var request = new HttpRequestMessage (HttpMethod.Delete, $"/api/v3/orderList?{query}&signature={signature}");
                var response = await SendWithRetryAsync (request);
                string body = await response.Content.ReadAsStringAsync ();
                if (response.IsSuccessStatusCode)
                {
                    Log ($"OCO order {orderListId} cancelled");
                    return true;
                }
                else
                {
                    Log ($"CancelOcoOrder error: {body}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log ($"CancelOcoOrder exception: {ex.Message}");
                return false;
            }
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
            var sw = Stopwatch.StartNew ();
            try
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
                        Open = decimal.Parse (item[1].ToString (), CultureInfo.InvariantCulture),
                        High = decimal.Parse (item[2].ToString (), CultureInfo.InvariantCulture),
                        Low = decimal.Parse (item[3].ToString (), CultureInfo.InvariantCulture),
                        Close = decimal.Parse (item[4].ToString (), CultureInfo.InvariantCulture),
                        Volume = decimal.Parse (item[5].ToString (), CultureInfo.InvariantCulture),
                        OpenTime = DateTimeOffset.FromUnixTimeMilliseconds ((long)item[0]).DateTime
                    }).ToList ();
                }
                return new List<BinanceKline> ();
            }
            finally
            {
                sw.Stop ();
                if (sw.ElapsedMilliseconds > 500)
                    Log ($"⏱️ GetKlinesAsync {symbol} занял {sw.ElapsedMilliseconds} мс");
            }
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
            var allRows = new JArray ();
            int page = 1;
            int size = 100; // максимум за раз
            bool hasMore = true;

            try
            {
                while (hasMore)
                {
                    long timestamp = GetTimestamp ();
                    string query = $"timestamp={timestamp}&size={size}&page={page}";
                    string signature = CreateSignature (query);
                    var request = new HttpRequestMessage (HttpMethod.Get, $"/sapi/v1/simple-earn/flexible/position?{query}&signature={signature}");
                    request.Headers.Add ("X-MBX-APIKEY", _apiKey);
                    var response = await SendWithRetryAsync (request);
                    string jsonString = await response.Content.ReadAsStringAsync ();

                    if (!response.IsSuccessStatusCode)
                    {
                        Log ($"GetFlexibleEarnBalanceAsync error: {jsonString}");
                        break;
                    }

                    JObject obj = JObject.Parse (jsonString);
                    JArray rows = obj["rows"] as JArray;
                    if (rows == null || rows.Count == 0)
                        break;

                    foreach (var row in rows)
                        allRows.Add (row);

                    int total = obj["total"]?.Value<int> () ?? 0;
                    if (total <= page * size)
                        hasMore = false;
                    else
                        page++;
                }
            }
            catch (Exception ex)
            {
                Log ($"Exception GetFlexibleEarn: {ex.Message}");
            }

            // Не спамим лог (закомментировано)
            // Log($"DEBUG Earn response: {allRows.ToString()}");
            return allRows;
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
                Log ($"GetTopVolumePairsAsync error: {ex.Message}");
                return new List<string> { "BTCUSDC", "ETHUSDC", "SOLUSDC", "XRPUSDC" };
            }
        }

        public async Task<decimal> GetStepSizeAsync(string symbol)
        {
            if (_stepSizeCache.TryGetValue (symbol, out var cached))
                return cached;

            var exchangeInfo = await GetExchangeInfoAsync ();
            var symInfo = exchangeInfo["symbols"]?.FirstOrDefault (s => s["symbol"].ToString () == symbol);
            var lotSize = symInfo?["filters"]?.FirstOrDefault (f => f["filterType"]?.ToString () == "LOT_SIZE");
            if (lotSize != null && lotSize["stepSize"] != null)
            {
                decimal step = decimal.Parse (lotSize["stepSize"].ToString (), CultureInfo.InvariantCulture);
                _stepSizeCache[symbol] = step;
                return step;
            }
            return 0.00000001m;
        }

        public async Task<decimal> GetTickSizeAsync(string symbol)
        {
            var exchangeInfo = await GetExchangeInfoAsync ();
            var symInfo = exchangeInfo["symbols"]?.FirstOrDefault (s => s["symbol"].ToString () == symbol);
            var priceFilter = symInfo?["filters"]?.FirstOrDefault (f => f["filterType"]?.ToString () == "PRICE_FILTER");
            if (priceFilter != null && priceFilter["tickSize"] != null)
            {
                return decimal.Parse (priceFilter["tickSize"].ToString (), CultureInfo.InvariantCulture);
            }
            return 0.0001m;
        }

        public async Task<decimal> GetATRAsync(string symbol, int period = 14)
        {
            try
            {
                var klines = await GetKlinesAsync (symbol, "5m", period + 1);
                if (klines == null || klines.Count < period) return 0;
                decimal atr = 0;
                for (int i = 1; i <= period; i++)
                {
                    decimal tr = Math.Max (klines[i].High - klines[i].Low,
                        Math.Max (Math.Abs (klines[i].High - klines[i - 1].Close),
                                 Math.Abs (klines[i].Low - klines[i - 1].Close)));
                    atr += tr;
                }
                return atr / period;
            }
            catch (Exception ex)
            {
                Log ($"GetATRAsync error: {ex.Message}");
                return 0;
            }
        }

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
                Log ($"GetDustAssets error: {json}");
            }
            catch (Exception ex) { Log ($"GetDustAssets exception: {ex.Message}"); }
            return new JArray ();
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
                Log ($"GetFlexibleProducts error: {json}");
            }
            catch (Exception ex) { Log ($"GetFlexibleProducts exception: {ex.Message}"); }
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
                Log ($"Subscribe error: {json}");
            }
            catch (Exception ex) { Log ($"Subscribe exception: {ex.Message}"); }
            return false;
        }
    }
}