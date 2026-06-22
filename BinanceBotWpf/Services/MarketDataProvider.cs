using BinanceBotWpf.Models;
using BinanceBotWpf.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Получение и кэширование рыночных данных, анализ пар с индикаторами.
    /// </summary>
    public class MarketDataProvider
    {
        private readonly BinanceClient _client;
        private readonly MainWindowViewModel _ui;
        private readonly Action<string> _logger;
        private readonly Dictionary<string, (List<BinanceKline> Klines, DateTime Expiry)> _klinesCache = new ();
        private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds (60);
        private readonly StrategyEngine _strategy = new ();

        public MarketDataProvider(BinanceClient client, MainWindowViewModel ui, Action<string> logger)
        {
            _client = client;
            _ui = ui;
            _logger = logger;
        }

        public async Task<List<BinanceKline>> GetKlinesCachedAsync(string symbol, string interval, int limit)
        {
            lock (_klinesCache)
            {
                if (_klinesCache.TryGetValue (symbol, out var cached) && DateTime.UtcNow < cached.Expiry)
                    return cached.Klines;
            }
            var klines = await _client.GetKlinesAsync (symbol, interval, limit);
            lock (_klinesCache) { _klinesCache[symbol] = (klines, DateTime.UtcNow + _cacheDuration); }
            return klines;
        }

        /// <summary>Анализ пар: SMA, RSI, MACD, Bollinger Bands, фильтр объёма.</summary>
        public async Task<List<(string Symbol, TradeAction Action, decimal Price, decimal Rsi, decimal FastSma, decimal SlowSma, decimal Volatility, decimal Volume, decimal AvgVolume, decimal MacdHistogram, decimal BbWidth)>> AnalyzePairsAsync(List<string> pairs, int fastSmaPeriod, int slowSmaPeriod)
        {
            var results = new List<(string, TradeAction, decimal, decimal, decimal, decimal, decimal, decimal, decimal, decimal, decimal)> ();
            foreach (var sym in pairs)
            {
                try
                {
                    var klines = await GetKlinesCachedAsync (sym, "1h", 100); // 1h: стандартный интервал анализа
                    if (klines?.Count < Math.Max (fastSmaPeriod, slowSmaPeriod) + 2) continue;
                    var closes = klines.Select (k => k.Close).ToList ();
                    var volumes = klines.Select (k => k.Volume).ToList ();
                    decimal price = closes.Last ();
                    decimal volume = volumes.Last ();
                    decimal avgVolume = volumes.TakeLast (20).Average ();
                    if (volume < avgVolume * 0.8m) continue;

                    var signal = _strategy.AnalyzePairWithWallet (sym, closes, fastSmaPeriod, slowSmaPeriod, price);
                    decimal rsi = CalculateRsi (closes);
                    decimal fastSma = CalculateSma (closes, fastSmaPeriod);
                    decimal slowSma = CalculateSma (closes, slowSmaPeriod);
                    decimal volatility = CalculateVolatility (closes, 20);
                    var macd = TechnicalAnalysis.MACD (closes, 12, 26, 9);
                    decimal macdHist = macd.Histogram.LastOrDefault () ?? 0;
                    var bb = TechnicalAnalysis.BollingerBands (closes, 20, 2);
                    decimal bbUpper = bb.Upper.LastOrDefault () ?? price;
                    decimal bbLower = bb.Lower.LastOrDefault () ?? price;
                    decimal bbMiddle = bb.Middle.LastOrDefault () ?? price;
                    decimal bbWidth = ( bbUpper - bbLower ) / ( bbMiddle + 0.0001m );

                    if (signal.Action == TradeAction.Buy && ( price <= bbLower || macdHist > 0 ))
                        signal.Action = TradeAction.Buy;
                    else if (signal.Action == TradeAction.Sell && ( price >= bbUpper || macdHist < 0 ))
                        signal.Action = TradeAction.Sell;
                    else
                        signal.Action = TradeAction.Hold;

                    _ui.UpdateMarketTable (sym, price.ToString ("F4"), false, signal.Action, fastSma, slowSma);
                    results.Add ((sym, signal.Action, price, rsi, fastSma, slowSma, volatility, volume, avgVolume, macdHist, bbWidth));
                }
                catch (Exception ex) { _logger?.Invoke ($"❌ Ошибка анализа {sym}: {ex.Message}"); }
            }
            return results;
        }

        private decimal CalculateSma(List<decimal> data, int period) => data.Skip (data.Count - period).Average ();
        private decimal CalculateRsi(List<decimal> closes) => TechnicalAnalysis.RSI (closes, 14).LastOrDefault () ?? 50;
        private decimal CalculateVolatility(List<decimal> data, int period)
        {
            if (data == null || data.Count < period || period <= 0) return 0.02m;
            var last = data.TakeLast (period).ToList ();
            decimal avg = last.Average ();
            if (avg == 0 || avg > 1_000_000m) return 0.02m;
            decimal sumSq = last.Select (x => ( x - avg ) * ( x - avg )).Sum ();
            decimal stdDev = (decimal)Math.Sqrt ((double)( sumSq / period ));
            decimal volatility = stdDev / avg;
            if (volatility > 1.0m || volatility < 0.001m) return 0.02m;
            return Math.Min (0.30m, Math.Max (0.005m, volatility));
        }
    }
}