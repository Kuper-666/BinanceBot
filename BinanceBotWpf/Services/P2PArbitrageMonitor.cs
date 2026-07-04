using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public class P2PArbitrageOpportunity
    {
        public string Asset { get; set; } = string.Empty;
        public decimal SpotPrice { get; set; }
        public decimal P2PSellPrice { get; set; }
        public decimal SpreadPercent { get; set; }
        public string Direction { get; set; } = string.Empty;
    }

    public class P2PArbitrageMonitor : IDisposable
    {
        private readonly HttpClient _http;
        private readonly Action<string> _logger;
        private readonly decimal _minSpreadPercent;

        public List<P2PArbitrageOpportunity> Opportunities { get; } = new ();

        public P2PArbitrageMonitor(Action<string> logger, decimal minSpreadPercent = 1.0m)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds (10) };
            _logger = logger;
            _minSpreadPercent = minSpreadPercent;
        }

        public void Dispose ()
        {
            _http.Dispose ();
        }

        public async Task<List<P2PArbitrageOpportunity>> CheckOpportunitiesAsync()
        {
            Opportunities.Clear ();
            try
            {
                string[] assets = { "USDC", "BTC", "ETH", "SOL", "BNB" };
                foreach (string asset in assets)
                {
                    try
                    {
                        decimal spotPrice = await GetSpotPriceAsync (asset + "USDT");
                        if (spotPrice <= 0) continue;

                        decimal p2pSell = await GetP2PAveragePriceAsync (asset, "SELL");
                        if (p2pSell <= 0) continue;

                        decimal spread = ((p2pSell - spotPrice) / spotPrice) * 100;
                        if (Math.Abs (spread) >= _minSpreadPercent)
                        {
                            var opp = new P2PArbitrageOpportunity
                            {
                                Asset = asset,
                                SpotPrice = spotPrice,
                                P2PSellPrice = p2pSell,
                                SpreadPercent = spread,
                                Direction = spread > 0 ? "P2P→Spot" : "Spot→P2P"
                            };
                            Opportunities.Add (opp);
                            _logger?.Invoke ($"💱 P2P арбитраж {asset}: спот={spotPrice:F2}, P2P={p2pSell:F2}, спред={spread:F1}% ({opp.Direction})");
                        }
                    }
                    catch { }
                }

                if (Opportunities.Count == 0)
                    _logger?.Invoke ($"ℹ️ P2P: арбитражных возможностей с спредом >{_minSpreadPercent}% не найдено");
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ P2P мониторинг ошибка: {ex.Message}");
            }

            return Opportunities;
        }

        private async Task<decimal> GetSpotPriceAsync(string symbol)
        {
            try
            {
                string url = $"https://api.binance.com/api/v3/ticker/price?symbol={symbol}";
                var response = await _http.GetAsync (url);
                if (!response.IsSuccessStatusCode) return 0;
                var json = await response.Content.ReadAsStringAsync ();
                var doc = JsonDocument.Parse (json);
                return decimal.Parse (doc.RootElement.GetProperty ("price").GetString (),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return 0; }
        }

        private async Task<decimal> GetP2PAveragePriceAsync(string asset, string tradeType)
        {
            try
            {
                string url = "https://p2p.binance.com/bapi/c2c/v2/friendly/c2c/adv/search";
                var body = $"{{\"fiat\":\"USD\",\"page\":1,\"rows\":10,\"tradeType\":\"{tradeType}\",\"asset\":\"{asset}\",\"payTypes\":[]}}";
                var content = new StringContent (body, System.Text.Encoding.UTF8, "application/json");
                var response = await _http.PostAsync (url, content);
                if (!response.IsSuccessStatusCode) return 0;
                var json = await response.Content.ReadAsStringAsync ();
                var doc = JsonDocument.Parse (json);
                var data = doc.RootElement.GetProperty ("data");

                decimal total = 0;
                int count = 0;
                foreach (var item in data.EnumerateArray ())
                {
                    if (item.TryGetProperty ("adv", out var adv) && adv.TryGetProperty ("price", out var price))
                    {
                        total += decimal.Parse (price.GetString (),
                            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
                        count++;
                    }
                }
                return count > 0 ? total / count : 0;
            }
            catch { return 0; }
        }
    }
}
