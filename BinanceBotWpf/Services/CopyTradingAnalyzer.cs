using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public class TopTraderProfile
    {
        public string TraderId { get; set; } = string.Empty;
        public string Nickname { get; set; } = string.Empty;
        public decimal RoiPercent { get; set; }
        public decimal WinRate { get; set; }
        public int TotalTrades { get; set; }
        public decimal Aum { get; set; }
        public int Followers { get; set; }
        public string[] PnL7d { get; set; } = Array.Empty<string> ();
    }

    public class CopyTradingAnalyzer
    {
        private readonly HttpClient _http;
        private readonly Action<string> _logger;

        public List<TopTraderProfile> TopTraders { get; } = new ();

        public CopyTradingAnalyzer(Action<string> logger)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds (15) };
            _logger = logger;
        }

        public async Task<List<TopTraderProfile>> AnalyzeTopTradersAsync(int maxTraders = 10)
        {
            TopTraders.Clear ();
            try
            {
                string url = $"https://www.binance.com/bapi/futures/v1/public/future/copy-trade/lead-portfolio?pageNo=1&pageSize={maxTraders}&timeRange=THIRTY_DAY&sortBy=ROI";
                var response = await _http.GetAsync (url);
                if (!response.IsSuccessStatusCode)
                {
                    _logger?.Invoke ($"⚠️ Copy-trading API: HTTP {(int)response.StatusCode}");
                    return TopTraders;
                }

                var json = await response.Content.ReadAsStringAsync ();
                var doc = JsonDocument.Parse (json);

                if (doc.RootElement.TryGetProperty ("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray ())
                    {
                        var trader = new TopTraderProfile
                        {
                            TraderId = item.TryGetProperty ("portfolioId", out var pid) ? pid.ToString () : "",
                            Nickname = item.TryGetProperty ("nickname", out var nick) ? nick.GetString () ?? "Unknown" : "Unknown",
                            RoiPercent = item.TryGetProperty ("roi", out var roi) ? ParseDecimal (roi) : 0,
                            WinRate = item.TryGetProperty ("winRate", out var wr) ? ParseDecimal (wr) : 0,
                            TotalTrades = item.TryGetProperty ("totalTrades", out var tt) ? tt.GetInt32 () : 0,
                            Aum = item.TryGetProperty ("aum", out var aum) ? ParseDecimal (aum) : 0,
                            Followers = item.TryGetProperty ("followerCount", out var fc) ? fc.GetInt32 () : 0,
                        };
                        TopTraders.Add (trader);
                    }
                }

                if (TopTraders.Count > 0)
                {
                    _logger?.Invoke ($"📊 Copy-trading: найдено {TopTraders.Count} топ-трейдеров");
                    foreach (var t in TopTraders.Take (3))
                    {
                        _logger?.Invoke ($"   🏆 {t.Nickname}: ROI={t.RoiPercent:F1}%, WR={t.WinRate:F0}%, trades={t.TotalTrades}, AUM=${t.Aum:N0}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Copy-trading анализ ошибка: {ex.Message}");
            }

            return TopTraders;
        }

        private static decimal ParseDecimal(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.String)
                return decimal.Parse (el.GetString () ?? "0", System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture);
            if (el.ValueKind == JsonValueKind.Number)
                return el.GetDecimal ();
            return 0;
        }
    }
}
