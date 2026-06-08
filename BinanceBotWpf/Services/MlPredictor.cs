using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public class MlPredictor
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiUrl = "http://localhost:8000/predict";
        private bool _isServerAvailable = true;

        public MlPredictor()
        {
            _httpClient = new HttpClient ();
            _httpClient.Timeout = TimeSpan.FromSeconds (2); // таймаут 2 секунды
        }

        /// <summary>
        /// Запрос к Python-модели о прибыльности сделки.
        /// </summary>
        public async Task<bool> IsProfitableAsync(
            decimal fastSma, decimal slowSma, decimal rsi, decimal volumeRatio,
            decimal atr, decimal macdHist, decimal bbWidth, decimal obv)
        {
            if (!_isServerAvailable) return true; // если сервер не отвечает, разрешаем сделку

            try
            {
                var request = new
                {
                    fast_sma = (float)fastSma,
                    slow_sma = (float)slowSma,
                    rsi = (float)rsi,
                    volume_ratio = (float)volumeRatio,
                    atr = (float)atr,
                    macd_hist = (float)macdHist,
                    bb_width = (float)bbWidth,
                    obv = (float)obv
                };
                var json = JsonSerializer.Serialize (request);
                var content = new StringContent (json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync (_apiUrl, content);
                if (response.IsSuccessStatusCode)
                {
                    var resultJson = await response.Content.ReadAsStringAsync ();
                    var result = JsonSerializer.Deserialize<Dictionary<string, object>> (resultJson);
                    if (result != null && result.TryGetValue ("profitable", out var profitable))
                    {
                        return Convert.ToBoolean (profitable);
                    }
                }
                return true;
            }
            catch (HttpRequestException)
            {
                _isServerAvailable = false;
                // Логируем один раз, чтобы не спамить
                System.Diagnostics.Debug.WriteLine ("⚠️ Python ML сервер недоступен, используем стандартную ML.NET");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine ($"MlPredictor error: {ex.Message}");
                return true;
            }
        }
    }
}