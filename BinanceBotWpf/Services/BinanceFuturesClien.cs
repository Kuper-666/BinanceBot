using System;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;

namespace BinanceBotWpf.Services
{
    public class BinanceFuturesClient : IDisposable
    {
        private readonly string _apiKey;
        private readonly string _apiSecret;
        private readonly HttpClient _httpClient;
        private long _serverTimeOffset = 0;
        public event Action<string> OnLogGenerated;
        private void Log(string msg) => OnLogGenerated?.Invoke (msg);

        public BinanceFuturesClient(string apiKey, string apiSecret)
        {
            _apiKey = apiKey;
            _apiSecret = apiSecret;
            _httpClient = new HttpClient ();
            _httpClient.BaseAddress = new Uri ("https://fapi.binance.com");
            _httpClient.DefaultRequestHeaders.Add ("X-MBX-APIKEY", _apiKey);
        }

        private long GetTimestamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds () + _serverTimeOffset;
        private string CreateSignature(string query) => BitConverter.ToString (new HMACSHA256 (Encoding.UTF8.GetBytes (_apiSecret)).ComputeHash (Encoding.UTF8.GetBytes (query))).Replace ("-", "").ToLower ();

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

        public async Task<decimal> GetAccountBalanceAsync(string asset = "USDT")
        {
            long timestamp = GetTimestamp ();
            string query = $"timestamp={timestamp}";
            string signature = CreateSignature (query);
            var request = new HttpRequestMessage (HttpMethod.Get, $"/fapi/v2/account?{query}&signature={signature}");
            var response = await _httpClient.SendAsync (request);
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
            long timestamp = GetTimestamp ();
            string query = $"symbol={symbol}&leverage={leverage}&timestamp={timestamp}";
            string signature = CreateSignature (query);
            var content = new StringContent ($"{query}&signature={signature}", Encoding.UTF8, "application/x-www-form-urlencoded");
            var request = new HttpRequestMessage (HttpMethod.Post, "/fapi/v1/leverage") { Content = content };
            var response = await _httpClient.SendAsync (request);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync ();
                Log ($"Ошибка установки плеча для {symbol}: {body}");
            }
        }

        public async Task<JObject> PlaceMarketOrder(string symbol, string side, decimal quantity)
        {
            long timestamp = GetTimestamp ();
            string query = $"symbol={symbol}&side={side}&type=MARKET&quantity={quantity.ToString (CultureInfo.InvariantCulture)}&timestamp={timestamp}";
            string signature = CreateSignature (query);
            var content = new StringContent ($"{query}&signature={signature}", Encoding.UTF8, "application/x-www-form-urlencoded");
            var request = new HttpRequestMessage (HttpMethod.Post, "/fapi/v1/order") { Content = content };
            var response = await _httpClient.SendAsync (request);
            string body = await response.Content.ReadAsStringAsync ();
            if (response.IsSuccessStatusCode)
                return JObject.Parse (body);
            else
                Log ($"PlaceOrder Futures ERROR: {body}");
            return null;
        }

        // ==================== ДОБАВЛЕННЫЕ МЕТОДЫ ====================
        public async Task<List<JObject>> GetPositionsAsync()
        {
            long timestamp = GetTimestamp ();
            string query = $"timestamp={timestamp}";
            string signature = CreateSignature (query);
            var request = new HttpRequestMessage (HttpMethod.Get, $"/fapi/v2/positionRisk?{query}&signature={signature}");
            var response = await _httpClient.SendAsync (request);
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
            string url = $"/fapi/v1/klines?symbol={symbol}&interval={interval}&limit={limit}";
            var response = await _httpClient.GetAsync (url);
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
        // ============================================================

        public void Dispose() => _httpClient.Dispose ();
    }
}