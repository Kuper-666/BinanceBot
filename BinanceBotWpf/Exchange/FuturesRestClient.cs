using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

using BinanceBotWpf.Models;

namespace BinanceBotWpf.Exchange
{
    public class FuturesRestClient : IBinanceFuturesClient, IDisposable
    {
        private readonly string _apiKey;
        private readonly string _apiSecret;
        private readonly HttpClient _httpClient;
        private readonly HttpClient _sapiClient;
        private long _serverTimeOffset = 0;
        private readonly SemaphoreSlim _rateLimiter = new (10, 10);
        private readonly Queue<DateTime> _requestTimes = new ();
        private readonly int _maxRequestsPerSecond = 10;
        private readonly int _maxWeightPerMinute = 1200;
        private int _currentWeight = 0;
        private DateTime _weightResetTime = DateTime.UtcNow;
        private readonly SemaphoreSlim _throttleLock = new (1, 1);
        private readonly SemaphoreSlim _syncLock = new (1, 1);
        private DateTime _lastSyncTime = DateTime.MinValue;
        private readonly ConcurrentDictionary<string, decimal> _stepSizeCache = new ();
        private readonly SemaphoreSlim _exchangeInfoLock = new (1, 1);
        private JObject _exchangeInfoCache;
        private DateTime _exchangeInfoCacheTime = DateTime.MinValue;

        public event Action<string> OnLogGenerated;
        private void Log(string msg) => OnLogGenerated?.Invoke (msg);

        private Uri MakeUri (string path)
        {
            return new Uri (_httpClient.BaseAddress, path);
        }

        private static string NormalizeSymbol (string symbol)
        {
            return symbol?.Trim ().ToUpperInvariant () ?? string.Empty;
        }

        public FuturesRestClient(string apiKey, string apiSecret)
        {
            _apiKey = apiKey;
            _apiSecret = apiSecret;

            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) =>
                {
                    #if DEBUG
                    if (sslPolicyErrors != System.Net.Security.SslPolicyErrors.None)
                    {
                        Log($"SSL Warning (DEBUG): {sslPolicyErrors}");
                    }
                    return true;
                    #else
                    if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
                        return true;
                    Log($"SSL Error: {sslPolicyErrors}");
                    return false;
                    #endif
                },
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };

            _httpClient = new HttpClient (handler);
            _httpClient.Timeout = TimeSpan.FromSeconds (30);
            _httpClient.BaseAddress = new Uri ("https://fapi.binance.com");
            _httpClient.DefaultRequestHeaders.Add ("X-MBX-APIKEY", _apiKey);
            _httpClient.DefaultRequestHeaders.Add ("Accept", "application/json");
            _httpClient.DefaultRequestHeaders.Add ("User-Agent", "BinanceBotWpf/1.0");

            var sapiHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) =>
                {
                    #if DEBUG
                    return true;
                    #else
                    return sslPolicyErrors == System.Net.Security.SslPolicyErrors.None;
                    #endif
                },
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            _sapiClient = new HttpClient (sapiHandler);
            _sapiClient.Timeout = TimeSpan.FromSeconds (30);
            _sapiClient.BaseAddress = new Uri ("https://api.binance.com");
            _sapiClient.DefaultRequestHeaders.Add ("X-MBX-APIKEY", _apiKey);
            _sapiClient.DefaultRequestHeaders.Add ("Accept", "application/json");
            _sapiClient.DefaultRequestHeaders.Add ("User-Agent", "BinanceBotWpf/1.0");
        }

        private long GetTimestamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds () + _serverTimeOffset;
        private string CreateSignature(string query)
        {
            using (var hmac = new HMACSHA256 (Encoding.UTF8.GetBytes (_apiSecret)))
            {
                return BitConverter.ToString (hmac.ComputeHash (Encoding.UTF8.GetBytes (query))).Replace ("-", "").ToLower ();
            }
        }

        public async Task SyncTimeAsync()
        {
            try
            {
                var response = await _httpClient.GetStringAsync ("/fapi/v1/time");
                long serverTime = JObject.Parse (response)["serverTime"].Value<long> ();
                _serverTimeOffset = serverTime - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ();
                Log ("Синхронизация времени для фьючерсов выполнена.");
            }
            catch { _serverTimeOffset = 0; }
        }

        private async Task EnsureTimeSyncedAsync ()
        {
            if (( DateTime.UtcNow - _lastSyncTime ).TotalMinutes < 5) return;
            await _syncLock.WaitAsync ();
            try
            {
                if (( DateTime.UtcNow - _lastSyncTime ).TotalMinutes < 5) return;
                await SyncTimeAsync ();
                _lastSyncTime = DateTime.UtcNow;
            }
            finally { _syncLock.Release (); }
        }

        private async Task ResyncAndRetryAsync ()
        {
            Log ("Пересинхронизация времени из-за -1021 ошибки...");
            await _syncLock.WaitAsync ();
            try
            {
                _serverTimeOffset = 0;
                await SyncTimeAsync ();
                _lastSyncTime = DateTime.UtcNow;
            }
            finally { _syncLock.Release (); }
        }

        private async Task ThrottleAsync(int estimatedWeight = 1)
        {
            await _throttleLock.WaitAsync ();
            try
            {
                var now = DateTime.UtcNow;

                if (now - _weightResetTime > TimeSpan.FromMinutes (1))
                {
                    _currentWeight = 0;
                    _weightResetTime = now;
                }

                if (_currentWeight + estimatedWeight > _maxWeightPerMinute)
                {
                    int waitMs = (int)( _weightResetTime.AddMinutes (1) - now ).TotalMilliseconds + 100;
                    Debug.WriteLine ($"Rate limit: waiting {waitMs}ms (weight {_currentWeight}/{_maxWeightPerMinute})");
                    DateTime resetTimeBeforeWait = _weightResetTime;
                    _throttleLock.Release ();
                    await Task.Delay (Math.Max (100, waitMs));
                    await _throttleLock.WaitAsync ();
                    if (_weightResetTime == resetTimeBeforeWait)
                    {
                        _currentWeight = 0;
                        _weightResetTime = DateTime.UtcNow;
                    }
                }

                while (_requestTimes.Count > 0 && _requestTimes.Peek () < now.AddSeconds (-1))
                    _requestTimes.Dequeue ();

                if (_requestTimes.Count >= _maxRequestsPerSecond)
                {
                    int delayMs = 1000 - (int)( now - _requestTimes.Peek () ).TotalMilliseconds;
                    if (delayMs > 0 && delayMs < 5000)
                    {
                        Debug.WriteLine ($"Throttle: waiting {delayMs}ms (requests {_requestTimes.Count}/{_maxRequestsPerSecond})");
                        _throttleLock.Release ();
                        await Task.Delay (delayMs);
                        await _throttleLock.WaitAsync ();
                    }
                }

                _requestTimes.Enqueue (DateTime.UtcNow);
                _currentWeight += estimatedWeight;
            }
            finally
            {
                _throttleLock.Release ();
            }

            await _rateLimiter.WaitAsync ();
            try
            {
                await Task.Delay (50);
            }
            finally
            {
                _rateLimiter.Release ();
            }
        }

        private HttpRequestMessage CloneRequest (HttpRequestMessage original, string body)
        {
            var clone = new HttpRequestMessage (original.Method, original.RequestUri);
            if (body != null)
                clone.Content = new StringContent (body, Encoding.UTF8,
                    original.Content?.Headers.ContentType?.MediaType ?? "application/x-www-form-urlencoded");
            clone.Version = original.Version;
            foreach (var header in original.Headers)
                clone.Headers.Add (header.Key, header.Value);
            return clone;
        }

        private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, int maxRetries = 3)
        {
            int retryCount = 0;
            int delayMs = 1000;
            string originalBody = null;

            if (request.Content != null)
                originalBody = await request.Content.ReadAsStringAsync ();

            while (true)
            {
                try
                {
                    await ThrottleAsync ();
                    await EnsureTimeSyncedAsync ();
                    var response = await _httpClient.SendAsync (request);
                    if (response.IsSuccessStatusCode) return response;

                    if ((int)response.StatusCode == 418 || (int)response.StatusCode == 429)
                    {
                        retryCount++;
                        if (retryCount > maxRetries)
                        {
                            Log ($"Rate limit exceeded after {maxRetries} retries");
                            throw new Exception ($"Rate limit exceeded after {maxRetries} retries");
                        }
                        Log ($"Rate limit (status {response.StatusCode}). Retrying in {delayMs}ms (attempt {retryCount}/{maxRetries})");
                        await Task.Delay (delayMs);
                        delayMs = Math.Min (delayMs * 2, 32000);

                        request = CloneRequest (request, originalBody);
                        continue;
                    }

                    string body = await response.Content.ReadAsStringAsync ();
                    if (body.Contains ("-1021") && retryCount < maxRetries)
                    {
                        retryCount++;
                        Log ($"Timestamp -1021. Resync and retry ({retryCount}/{maxRetries})");
                        await ResyncAndRetryAsync ();
                        delayMs = Math.Min (delayMs * 2, 32000);

                        request = CloneRequest (request, originalBody);
                        continue;
                    }

                    return response;
                }
                catch (HttpRequestException ex)
                {
                    retryCount++;
                    if (retryCount > maxRetries)
                    {
                        Log ($"HTTP error after {maxRetries} retries: {ex.Message}");
                        throw new Exception ($"HTTP request failed after {maxRetries} retries", ex);
                    }
                    Log ($"HTTP error (network). Retrying in {delayMs}ms (attempt {retryCount}/{maxRetries})");
                    await Task.Delay (delayMs);
                    delayMs = Math.Min (delayMs * 2, 32000);
                    request = CloneRequest (request, originalBody);
                }
                catch (TaskCanceledException ex)
                {
                    retryCount++;
                    if (retryCount > maxRetries)
                    {
                        Log ($"Timeout after {maxRetries} retries");
                        throw new Exception ($"Request timeout after {maxRetries} retries", ex);
                    }
                    Log ($"Request timeout. Retrying in {delayMs}ms (attempt {retryCount}/{maxRetries})");
                    await Task.Delay (delayMs);
                    delayMs = Math.Min (delayMs * 2, 32000);
                    request = CloneRequest (request, originalBody);
                }
            }
        }

        public async Task<decimal> GetAccountBalanceAsync(string asset = "USDT")
        {
            long timestamp = GetTimestamp ();
            string query = $"timestamp={timestamp}";
            string signature = CreateSignature (query);
            var request = new HttpRequestMessage (HttpMethod.Get, MakeUri ($"/fapi/v2/account?{query}&signature={signature}"));
            var response = await SendWithRetryAsync (request);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync ();
                var account = JObject.Parse (json);
                foreach (var assetBal in account["assets"])
                {
                    if (assetBal["asset"].ToString () == asset)
                        return decimal.Parse (assetBal["walletBalance"].ToString (), CultureInfo.InvariantCulture);
                }
            }
            return 0;
        }

        public async Task SetLeverageAsync(string symbol, int leverage)
        {
            string sym = NormalizeSymbol (symbol);
            long timestamp = GetTimestamp ();
            string query = $"symbol={sym}&leverage={leverage}&timestamp={timestamp}";
            string signature = CreateSignature (query);
            var content = new StringContent ($"{query}&signature={signature}", Encoding.UTF8, "application/x-www-form-urlencoded");
            var request = new HttpRequestMessage (HttpMethod.Post, "/fapi/v1/leverage") { Content = content };
            var response = await SendWithRetryAsync (request);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync ();
                Log ($"Ошибка установки плеча для {symbol}: {body}");
            }
        }

        public async Task<JObject> PlaceMarketOrder(string symbol, string side, decimal quantity)
        {
            string sym = NormalizeSymbol (symbol);
            long timestamp = GetTimestamp ();
            string query = $"symbol={sym}&side={side}&type=MARKET&quantity={quantity.ToString (CultureInfo.InvariantCulture)}&timestamp={timestamp}";
            string signature = CreateSignature (query);
            var content = new StringContent ($"{query}&signature={signature}", Encoding.UTF8, "application/x-www-form-urlencoded");
            var request = new HttpRequestMessage (HttpMethod.Post, "/fapi/v1/order") { Content = content };
            var response = await SendWithRetryAsync (request);
            string body = await response.Content.ReadAsStringAsync ();
            if (response.IsSuccessStatusCode)
                return JObject.Parse (body);
            else
                Log ($"PlaceOrder Futures ERROR: {body}");
            return null;
        }

        public async Task<List<JObject>> GetPositionsAsync()
        {
            long timestamp = GetTimestamp ();
            string query = $"timestamp={timestamp}";
            string signature = CreateSignature (query);
            var request = new HttpRequestMessage (HttpMethod.Get, MakeUri ($"/fapi/v2/positionRisk?{query}&signature={signature}"));
            var response = await SendWithRetryAsync (request);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync ();
                return JArray.Parse (json).ToObject<List<JObject>> ();
            }
            Log ($"GetPositions error: {response.StatusCode}");
            return new List<JObject> ();
        }

        public async Task<List<BinanceKline>> GetKlinesAsync(string symbol, string interval, int limit = 500)
        {
            string url = $"/fapi/v1/klines?symbol={symbol.Trim ().ToUpperInvariant ()}&interval={interval}&limit={limit}";
            var request = new HttpRequestMessage (HttpMethod.Get, MakeUri (url));
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
                    Volume = decimal.Parse (item[5].ToString (), CultureInfo.InvariantCulture)
                }).ToList ();
            }
            return new List<BinanceKline> ();
        }

        public async Task SetMarginTypeAsync(string symbol, string marginType)
        {
            string sym = NormalizeSymbol (symbol);
            long timestamp = GetTimestamp ();
            string query = $"symbol={sym}&marginType={marginType}&timestamp={timestamp}";
            string signature = CreateSignature (query);
            var content = new StringContent ($"{query}&signature={signature}", Encoding.UTF8, "application/x-www-form-urlencoded");
            var request = new HttpRequestMessage (HttpMethod.Post, "/fapi/v1/marginType") { Content = content };
            var response = await SendWithRetryAsync (request);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync ();
                Log ($"SetMarginType error for {symbol}: {body}");
            }
        }

        public async Task SetPositionModeAsync(bool hedgeMode)
        {
            long timestamp = GetTimestamp ();
            string dualSidePosition = hedgeMode ? "true" : "false";
            string query = $"dualSidePosition={dualSidePosition}&timestamp={timestamp}";
            string signature = CreateSignature (query);
            var content = new StringContent ($"{query}&signature={signature}", Encoding.UTF8, "application/x-www-form-urlencoded");
            var request = new HttpRequestMessage (HttpMethod.Post, "/fapi/v1/positionSide/dual") { Content = content };
            var response = await SendWithRetryAsync (request);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync ();
                Log ($"SetPositionMode error: {body}");
            }
        }

        public async Task<JObject> PlaceTrailingStopMarketAsync(string symbol, string side, decimal quantity, decimal callbackRate)
        {
            string sym = NormalizeSymbol (symbol);
            long timestamp = GetTimestamp ();
            string query = $"symbol={sym}&side={side}&type=TRAILING_STOP_MARKET&quantity={quantity.ToString (CultureInfo.InvariantCulture)}&callbackRate={callbackRate.ToString (CultureInfo.InvariantCulture)}&timestamp={timestamp}";
            string signature = CreateSignature (query);
            var content = new StringContent ($"{query}&signature={signature}", Encoding.UTF8, "application/x-www-form-urlencoded");
            var request = new HttpRequestMessage (HttpMethod.Post, "/fapi/v1/order") { Content = content };
            var response = await SendWithRetryAsync (request);
            string body = await response.Content.ReadAsStringAsync ();
            if (response.IsSuccessStatusCode)
                return JObject.Parse (body);
            else
                Log ($"PlaceTrailingStopMarket ERROR: {body}");
            return null;
        }

        public async Task<JObject> PlaceStopMarketAsync(string symbol, string side, decimal quantity, decimal stopPrice)
        {
            string sym = NormalizeSymbol (symbol);
            long timestamp = GetTimestamp ();
            string query = $"symbol={sym}&side={side}&type=STOP_MARKET&quantity={quantity.ToString (CultureInfo.InvariantCulture)}&stopPrice={stopPrice.ToString (CultureInfo.InvariantCulture)}&timestamp={timestamp}";
            string signature = CreateSignature (query);
            var content = new StringContent ($"{query}&signature={signature}", Encoding.UTF8, "application/x-www-form-urlencoded");
            var request = new HttpRequestMessage (HttpMethod.Post, "/fapi/v1/order") { Content = content };
            var response = await SendWithRetryAsync (request);
            string body = await response.Content.ReadAsStringAsync ();
            if (response.IsSuccessStatusCode)
                return JObject.Parse (body);
            else
                Log ($"PlaceStopMarket ERROR: {body}");
            return null;
        }

        public async Task<decimal> GetPriceAsync(string symbol)
        {
            try
            {
                var request = new HttpRequestMessage (HttpMethod.Get, MakeUri ($"/fapi/v1/ticker/price?symbol={symbol.Trim ().ToUpperInvariant ()}"));
                var response = await SendWithRetryAsync (request);
                if (response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync ();
                    var json = JObject.Parse (body);
                    return decimal.Parse (json["price"].ToString (), CultureInfo.InvariantCulture);
                }
            }
            catch { }
            return 0;
        }

        private async Task<JObject> GetExchangeInfoAsync()
        {
            if (_exchangeInfoCache != null && (DateTime.UtcNow - _exchangeInfoCacheTime).TotalHours < 24)
                return _exchangeInfoCache;

            await _exchangeInfoLock.WaitAsync ();
            try
            {
                if (_exchangeInfoCache != null && (DateTime.UtcNow - _exchangeInfoCacheTime).TotalHours < 24)
                    return _exchangeInfoCache;

                var request = new HttpRequestMessage (HttpMethod.Get, MakeUri ("/fapi/v1/exchangeInfo"));
                var response = await SendWithRetryAsync (request);
                if (response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync ();
                    _exchangeInfoCache = JObject.Parse (body);
                    _exchangeInfoCacheTime = DateTime.UtcNow;
                    var symbols = _exchangeInfoCache ["symbols"];
                    Log ($"📊 Futures exchangeInfo: {symbols?.Count () ?? 0} символов загружено");
                    return _exchangeInfoCache;
                }
                Log ($"⚠️ Futures exchangeInfo: HTTP {(int)response.StatusCode}");
            }
            catch (Exception ex) { Log ($"GetExchangeInfoAsync error: {ex.Message}"); }
            finally { _exchangeInfoLock.Release (); }
            return new JObject ();
        }

        public async Task<decimal> GetStepSizeAsync(string symbol)
        {
            if (_stepSizeCache.TryGetValue(symbol, out var cached))
                return cached;

            var exchangeInfo = await GetExchangeInfoAsync ();
            var symInfo = exchangeInfo?["symbols"]?.FirstOrDefault(s => s["symbol"]?.ToString().Trim() == symbol);
            if (symInfo == null)
            {
                var symbolNames = exchangeInfo?["symbols"]?.Select (s => s ["symbol"]?.ToString ())?.Where (s => s != null)?.Take (10)?.ToList ();
                Log ($"⚠️ GetStepSizeAsync: символ {symbol} не найден в exchangeInfo (всего {exchangeInfo?["symbols"]?.Count () ?? 0}, первые: {string.Join (", ", symbolNames ?? new List<string> ())}), используется fallback stepSize=1");
                _stepSizeCache[symbol] = 1m;
                return 1m;
            }
            var lotSize = symInfo?["filters"]?.FirstOrDefault(f => f["filterType"]?.ToString() == "LOT_SIZE");
            if (lotSize != null && lotSize["stepSize"] != null)
            {
                decimal step = decimal.Parse(lotSize["stepSize"].ToString(), CultureInfo.InvariantCulture);
                _stepSizeCache[symbol] = step;
                return step;
            }
            Log ($"⚠️ GetStepSizeAsync: LOT_SIZE фильтр не найден для {symbol}, используется fallback stepSize=1");
            _stepSizeCache[symbol] = 1m;
            return 1m;
        }

        public async Task<(decimal stepSize, decimal minQty)> GetLotSizeAsync(string symbol)
        {
            var exchangeInfo = await GetExchangeInfoAsync ();
            var symInfo = exchangeInfo?["symbols"]?.FirstOrDefault(s => s["symbol"]?.ToString().Trim() == symbol);
            if (symInfo == null)
            {
                Log ($"⚠️ GetLotSizeAsync: символ {symbol} не найден в exchangeInfo, используется fallback stepSize=1");
                return (1m, 0m);
            }
            var lotSize = symInfo?["filters"]?.FirstOrDefault(f => f["filterType"]?.ToString() == "LOT_SIZE");
            if (lotSize != null)
            {
                decimal step = lotSize["stepSize"] != null
                    ? decimal.Parse(lotSize["stepSize"].ToString(), CultureInfo.InvariantCulture) : 1m;
                decimal minQ = lotSize["minQty"] != null
                    ? decimal.Parse(lotSize["minQty"].ToString(), CultureInfo.InvariantCulture) : 0m;
                if (!_stepSizeCache.ContainsKey(symbol)) _stepSizeCache[symbol] = step;
                return (step, minQ);
            }
            Log ($"⚠️ GetLotSizeAsync: LOT_SIZE фильтр не найден для {symbol}, используется fallback stepSize=1");
            return (1m, 0m);
        }

        public async Task<decimal> GetTickSizeAsync(string symbol)
        {
            var exchangeInfo = await GetExchangeInfoAsync ();
            var symInfo = exchangeInfo?["symbols"]?.FirstOrDefault(s => s["symbol"]?.ToString().Trim() == symbol);
            var priceFilter = symInfo?["filters"]?.FirstOrDefault(f => f["filterType"]?.ToString() == "PRICE_FILTER");
            if (priceFilter != null && priceFilter["tickSize"] != null)
            {
                return decimal.Parse(priceFilter["tickSize"].ToString(), CultureInfo.InvariantCulture);
            }
            return 0.0001m;
        }

        public async Task<decimal> GetMinNotionalAsync(string symbol)
        {
            var exchangeInfo = await GetExchangeInfoAsync ();
            var symInfo = exchangeInfo?["symbols"]?.FirstOrDefault(s => s["symbol"]?.ToString().Trim() == symbol);
            var notionalFilter = symInfo?["filters"]?.FirstOrDefault(
                f => f["filterType"]?.ToString() == "NOTIONAL" || f["filterType"]?.ToString() == "MIN_NOTIONAL");
            decimal minNotional = 5m;
            if (notionalFilter != null)
            {
                if (notionalFilter["minNotional"] != null)
                    minNotional = decimal.Parse(notionalFilter["minNotional"].ToString(), CultureInfo.InvariantCulture);
                else if (notionalFilter["notional"] != null)
                    minNotional = decimal.Parse(notionalFilter["notional"].ToString(), CultureInfo.InvariantCulture);
            }
            return minNotional;
        }

        public async Task<JObject> PlaceLimitOrder(string symbol, string side, decimal quantity, decimal price)
        {
            string sym = NormalizeSymbol(symbol);
            try
            {
                (decimal stepSize, decimal minQty) = await GetLotSizeAsync(sym);
                if (stepSize > 0)
                    quantity = Math.Floor(quantity / stepSize) * stepSize;
                if (quantity < minQty)
                    quantity = minQty;
                if (quantity <= 0)
                {
                    Log($"PlaceLimitOrder SKIP {sym}: quantity=0 (stepSize={stepSize}, minQty={minQty})");
                    return null;
                }
                long timestamp = GetTimestamp();
                string query = $"symbol={sym}&side={side}&type=LIMIT&timeInForce=GTC&quantity={quantity.ToString(CultureInfo.InvariantCulture)}&price={price.ToString(CultureInfo.InvariantCulture)}&timestamp={timestamp}";
                string signature = CreateSignature(query);
                var content = new StringContent($"{query}&signature={signature}", Encoding.UTF8, "application/x-www-form-urlencoded");
                var request = new HttpRequestMessage(HttpMethod.Post, "/fapi/v1/order") { Content = content };
                var response = await SendWithRetryAsync(request);
                string body = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                    return JObject.Parse(body);
                Log($"PlaceLimitOrder Futures ERROR for {symbol}: {body}");
                return null;
            }
            catch (Exception ex)
            {
                Log($"PlaceLimitOrder Futures EXCEPTION: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> CancelOrder(string symbol, long orderId)
        {
            string sym = NormalizeSymbol(symbol);
            try
            {
                long timestamp = GetTimestamp();
                string query = $"symbol={sym}&orderId={orderId}&timestamp={timestamp}";
                string signature = CreateSignature(query);
                var request = new HttpRequestMessage(HttpMethod.Delete, MakeUri($"/fapi/v1/order?{query}&signature={signature}"));
                var response = await SendWithRetryAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Log($"CancelOrder Futures exception: {ex.Message}");
                return false;
            }
        }

        public async Task<List<JObject>> GetAllOrdersAsync(string symbol, int limit = 50)
        {
            string sym = NormalizeSymbol(symbol);
            try
            {
                long timestamp = GetTimestamp();
                string query = $"symbol={sym}&timestamp={timestamp}&limit={limit}";
                string signature = CreateSignature(query);
                var request = new HttpRequestMessage(HttpMethod.Get, MakeUri($"/fapi/v1/allOrders?{query}&signature={signature}"));
                var response = await SendWithRetryAsync(request);
                string body = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                    return JArray.Parse(body).ToObject<List<JObject>>();
                Log($"GetAllOrders Futures error for {symbol}: {body}");
                return null;
            }
            catch (Exception ex)
            {
                Log($"GetAllOrders Futures exception: {ex.Message}");
                return null;
            }
        }

        public async Task<decimal> GetATRAsync(string symbol, int period = 14, string interval = "1h")
        {
            try
            {
                var klines = await GetKlinesAsync(symbol, interval, period + 1);
                if (klines == null || klines.Count < period) return 0;
                decimal atr = 0;
                for (int i = 1; i <= period; i++)
                {
                    decimal tr = Math.Max(klines[i].High - klines[i].Low,
                        Math.Max(Math.Abs(klines[i].High - klines[i - 1].Close),
                                 Math.Abs(klines[i].Low - klines[i - 1].Close)));
                    atr += tr;
                }
                return atr / period;
            }
            catch (Exception ex)
            {
                Log($"GetATRAsync Futures error: {ex.Message}");
                return 0;
            }
        }

        public async Task<JObject> TransferToFuturesAsync(string asset, decimal amount)
        {
            try
            {
                long timestamp = GetTimestamp();
                string type = "MAIN_UMFUTURE";
                string query = $"type={type}&asset={asset}&amount={amount.ToString(CultureInfo.InvariantCulture)}&timestamp={timestamp}";
                string signature = CreateSignature(query);
                var content = new StringContent($"{query}&signature={signature}", Encoding.UTF8, "application/x-www-form-urlencoded");
                var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_sapiClient.BaseAddress, $"/sapi/v1/asset/transfer")) { Content = content };
                var response = await _sapiClient.SendAsync(request);
                string body = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    Log($"✅ Перевод {amount} {asset} из спота в фьючерсы: {body}");
                    return JObject.Parse(body);
                }
                Log($"⚠️ Ошибка перевода на фьючерсы: {body}");
                return null;
            }
            catch (Exception ex)
            {
                Log($"❌ TransferToFutures ошибка: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            _httpClient.Dispose();
            _sapiClient?.Dispose();
        }
    }
}
