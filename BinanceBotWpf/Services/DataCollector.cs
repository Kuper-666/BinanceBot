using BinanceBotWpf.Models;
using BinanceBotWpf.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public class DataCollector
    {
        private readonly BinanceClient _client;
        private readonly MlModelManager _mlManager;
        private readonly Action<string> _logger;
        private readonly MainWindowViewModel _ui;
        private readonly MarketIntelligenceService _marketIntel;

        public DataCollector(BinanceClient client, MlModelManager mlManager, Action<string> logger, MainWindowViewModel ui, MarketIntelligenceService marketIntel = null)
        {
            _client = client;
            _mlManager = mlManager;
            _logger = logger;
            _ui = ui;
            _marketIntel = marketIntel;
        }

        public async Task FetchAndRetrainFromOrderHistoryAsync(List<string> activePairs, int fastSma, int slowSma)
        {
            try
            {
                _logger?.Invoke ("📥 Сбор истории ордеров для переобучения ML...");

                var allClosedTrades = new List<(DateTime CloseTime, string Symbol, decimal EntryPrice, decimal ExitPrice, decimal Quantity, bool IsProfitable)> ();
                foreach (var sym in activePairs)
                {
                    var orders = await _client.GetAllOrdersAsync (sym, limit: 100);
                    if (orders == null || orders.Count == 0) continue;

                    var buys = orders.Where (o => o["side"].ToString () == "BUY" && o["status"].ToString () == "FILLED")
                                     .OrderBy (o => (long)o["time"]).ToList ();
                    var sells = orders.Where (o => o["side"].ToString () == "SELL" && o["status"].ToString () == "FILLED")
                                      .OrderBy (o => (long)o["time"]).ToList ();

                    int buyIdx = 0, sellIdx = 0;
                    while (buyIdx < buys.Count && sellIdx < sells.Count)
                    {
                        var buy = buys[buyIdx];
                        var sell = sells[sellIdx];
                        if ((long)sell["time"] < (long)buy["time"]) { sellIdx++; continue; }

                        decimal buyQty = decimal.Parse (buy["executedQty"].ToString (), System.Globalization.CultureInfo.InvariantCulture);
                        decimal sellQty = decimal.Parse (sell["executedQty"].ToString (), System.Globalization.CultureInfo.InvariantCulture);
                        decimal qty = Math.Min (buyQty, sellQty);
                        if (qty > 0)
                        {
                            decimal entryPrice = decimal.Parse (buy["price"].ToString (), System.Globalization.CultureInfo.InvariantCulture);
                            decimal exitPrice = decimal.Parse (sell["price"].ToString (), System.Globalization.CultureInfo.InvariantCulture);
                            bool profitable = exitPrice > entryPrice;
                            allClosedTrades.Add ((DateTimeOffset.FromUnixTimeMilliseconds ((long)sell["time"]).DateTime, sym, entryPrice, exitPrice, qty, profitable));
                        }
                        if (buyQty <= sellQty) buyIdx++;
                        if (sellQty <= buyQty) sellIdx++;
                    }
                }

                _logger?.Invoke ($"📊 Найдено {allClosedTrades.Count} закрытых позиций");
                if (allClosedTrades.Count < 30)
                {
                    _logger?.Invoke ($"⚠️ Недостаточно сделок ({allClosedTrades.Count}) для обучения, требуется 30.");
                    return;
                }

                var features = new List<(decimal FastSma, decimal SlowSma, decimal Rsi, decimal VolumeRatio, decimal Atr, decimal MacdHistogram, decimal BbWidth, decimal Obv, float MarketCapRank, float SentimentScore, float GalaxyScore, bool IsProfitable)> ();
                foreach (var trade in allClosedTrades)
                {
                    try
                    {
                        var klines = await _client.GetKlinesAsync (trade.Symbol, "5m", 50);
                        if (klines == null || klines.Count < Math.Max (fastSma, slowSma) + 2) continue;
                        var closes = klines.Select (k => k.Close).ToList ();
                        var volumes = klines.Select (k => k.Volume).ToList ();
                        decimal fastSmaVal = closes.Skip (closes.Count - fastSma).Average ();
                        decimal slowSmaVal = closes.Skip (closes.Count - slowSma).Average ();
                        decimal rsi = TechnicalAnalysis.RSI (closes, 14).LastOrDefault () ?? 50;
                        decimal avgVolume = volumes.TakeLast (20).Average ();
                        decimal volumeRatio = volumes.Last () / avgVolume;
                        decimal atr = await _client.GetATRAsync (trade.Symbol, 14);
                        var macd = TechnicalAnalysis.MACD (closes, 12, 26, 9);
                        decimal macdHist = macd.Histogram.LastOrDefault () ?? 0;
                        var bb = TechnicalAnalysis.BollingerBands (closes, 20, 2);
                        decimal bbUpper = bb.Upper.LastOrDefault () ?? closes.Last ();
                        decimal bbLower = bb.Lower.LastOrDefault () ?? closes.Last ();
                        decimal bbMiddle = bb.Middle.LastOrDefault () ?? closes.Last ();
                        decimal bbWidth = ( bbUpper - bbLower ) / ( bbMiddle + 0.0001m );

                        // OBV
                        var obvValues = TechnicalAnalysis.OBV (klines);
                        decimal obvLast = obvValues.Last ();
                        decimal obvNormalized = (decimal)Math.Log10 (Math.Abs ((double)obvLast) + 1);

                        // Фундаментальные / соц. фичи из MarketIntelligence
                        string baseAsset = MarketIntelligenceService.ToBaseAsset (trade.Symbol);
                        var assetData = _marketIntel?.GetAssetData (baseAsset);
                        float marketCapRank = assetData?.CoinGeckoRank ?? -1;
                        float sentimentScore = assetData != null ? (float)assetData.CompositeSentimentScore : 0f;
                        float galaxyScore = assetData != null && assetData.GalaxyScore.HasValue ? (float)assetData.GalaxyScore.Value : 0f;

                        features.Add ((fastSmaVal, slowSmaVal, rsi, volumeRatio, atr, macdHist, bbWidth, obvNormalized, marketCapRank, sentimentScore, galaxyScore, trade.IsProfitable));
                    }
                    catch (Exception ex) { _logger?.Invoke ($"Ошибка обработки {trade.Symbol}: {ex.Message}"); }
                }

                if (features.Count < 20)
                {
                    _logger?.Invoke ($"⚠️ Недостаточно признаков для обучения ({features.Count})");
                    return;
                }

                await _mlManager.RetrainFromFeaturesAsync (features, _logger);
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Ошибка сбора истории ордеров: {ex.Message}");
            }
        }
    }
}