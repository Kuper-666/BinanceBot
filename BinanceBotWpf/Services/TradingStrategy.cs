using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Основная торговая стратегия (SMA + RSI + MACD)
    /// </summary>
    public class TradingStrategy
    {
        private readonly StrategyEngine _strategyEngine = new ();
        private readonly Action<string> _logger;

        public int FastSmaPeriod { get; set; } = 9;
        public int SlowSmaPeriod { get; set; } = 21;
        public int RsiPeriod { get; set; } = 14;

        public TradingStrategy(Action<string> logger)
        {
            _logger = logger;
        }

        private MlModelManager _mlManager;

        public void SetMlManager(MlModelManager mlManager)
        {
            _mlManager = mlManager;
        }

        // Публичный метод для установки логгера (если нужно обновить после создания)
        public void SetLogger(Action<string> logger)
        {
            // Поле _logger только для чтения, нельзя изменить после конструктора
            // Поэтому просто игнорируем вызов, логгер уже установлен в конструкторе
        }

        /// <summary>
        /// Анализ пары и генерация сигнала
        /// </summary>
        public async Task<(TradeAction Action, string Reason, Dictionary<string, decimal> Indicators)>
            AnalyzeAsync(string symbol, List<BinanceKline> klines)
        {
            var result = (Action: TradeAction.Hold, Reason: "Нет сигнала", Indicators: new Dictionary<string, decimal> ());

            if (klines == null || klines.Count < SlowSmaPeriod + 5)
            {
                result.Reason = "Недостаточно данных";
                return result;
            }

            try
            {
                var closes = klines.Select (k => k.Close).ToList ();
                var highs = klines.Select (k => k.High).ToList ();
                var lows = klines.Select (k => k.Low).ToList ();
                var volumes = klines.Select (k => k.Volume).ToList ();

                // Расчёт индикаторов
                decimal currentPrice = closes.Last ();
                decimal fastSma = CalculateSma (closes, FastSmaPeriod);
                decimal slowSma = CalculateSma (closes, SlowSmaPeriod);
                decimal rsi = CalculateRsi (closes, RsiPeriod);
                decimal avgVolume = volumes.TakeLast (20).Average ();
                decimal volumeRatio = volumes.Last () / avgVolume;

                var macd = TechnicalAnalysis.MACD (closes, 12, 26, 9);
                decimal macdHist = macd.Histogram.LastOrDefault () ?? 0;
                decimal prevMacdHist = macd.Histogram.Count > 1 ? macd.Histogram[macd.Histogram.Count - 2] ?? 0 : 0;

                var bb = TechnicalAnalysis.BollingerBands (closes, 20, 2);
                decimal bbUpper = bb.Upper.LastOrDefault () ?? currentPrice;
                decimal bbLower = bb.Lower.LastOrDefault () ?? currentPrice;
                decimal bbWidth = ( bbUpper - bbLower ) / ( currentPrice + 0.0001m );

                // Сохраняем индикаторы
                result.Indicators["price"] = currentPrice;
                result.Indicators["fastSma"] = fastSma;
                result.Indicators["slowSma"] = slowSma;
                result.Indicators["rsi"] = rsi;
                result.Indicators["volumeRatio"] = volumeRatio;
                result.Indicators["macdHist"] = macdHist;
                result.Indicators["bbWidth"] = bbWidth;
                result.Indicators["prevMacdHist"] = prevMacdHist;
                result.Indicators["bbUpper"] = bbUpper;
                result.Indicators["bbLower"] = bbLower;

                // Дополнительно для ML
                var atrList = TechnicalAnalysis.ATR(highs, lows, closes, 14);
                decimal atr = atrList.LastOrDefault() ?? currentPrice * 0.02m;
                var obvList = TechnicalAnalysis.OBV(klines);
                decimal obv = obvList.LastOrDefault();
                
                // Предсказание ИИ
                if (_mlManager != null)
                {
                    var riskPrediction = _mlManager.PredictRisk(fastSma, slowSma, rsi, volumeRatio, atr, macdHist, bbWidth, obv);
                    result.Indicators["aiProbability"] = (decimal)riskPrediction.Probability;
                    
                    // Сохраняем RiskLevel как число (например: Low=1, Medium=2, High=3) или просто логгируем, 
                    // так как в Dictionary<string, decimal> нельзя сохранить строку.
                    decimal riskVal = riskPrediction.RiskLevel == "Low Risk" ? 1 : (riskPrediction.RiskLevel == "Medium Risk" ? 2 : 3);
                    result.Indicators["aiRiskLevel"] = riskVal;
                }

                // Базовый сигнал от SMA
                var baseSignal = _strategyEngine.AnalyzePairWithWallet (symbol, closes, FastSmaPeriod, SlowSmaPeriod, currentPrice);

                // Усиление сигнала от других индикаторов
                if (baseSignal.Action == TradeAction.Buy)
                {
                    bool bbOversold = currentPrice <= bbLower;
                    bool macdBullish = macdHist > 0 && macdHist > prevMacdHist;
                    bool rsiOversold = rsi < 30;

                    if (bbOversold || macdBullish || rsiOversold)
                    {
                        result.Action = TradeAction.Buy;
                        result.Reason = $"SMA Buy + {( bbOversold ? "BB " : "" )}{( macdBullish ? "MACD " : "" )}{( rsiOversold ? "RSI" : "" )}";
                    }
                    else
                    {
                        result.Action = TradeAction.Hold;
                        result.Reason = "SMA Buy but no confirmation";
                    }
                }
                else if (baseSignal.Action == TradeAction.Sell)
                {
                    bool bbOverbought = currentPrice >= bbUpper;
                    bool macdBearish = macdHist < 0 && macdHist < prevMacdHist;
                    bool rsiOverbought = rsi > 70;

                    if (bbOverbought || macdBearish || rsiOverbought)
                    {
                        result.Action = TradeAction.Sell;
                        result.Reason = $"SMA Sell + {( bbOverbought ? "BB " : "" )}{( macdBearish ? "MACD " : "" )}{( rsiOverbought ? "RSI" : "" )}";
                    }
                    else
                    {
                        result.Action = TradeAction.Hold;
                        result.Reason = "SMA Sell but no confirmation";
                    }
                }
                else
                {
                    result.Action = TradeAction.Hold;
                    result.Reason = baseSignal.Reason;
                }

                _logger?.Invoke ($"📊 {symbol}: {result.Reason} (RSI={rsi:F1}, MACD={macdHist:F4})");
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Ошибка анализа {symbol}: {ex.Message}");
                result.Reason = $"Ошибка: {ex.Message}";
            }

            return result;
        }

        private decimal CalculateSma(List<decimal> data, int period)
        {
            if (data.Count < period) return 0;
            return data.Skip (data.Count - period).Average ();
        }

        private decimal CalculateRsi(List<decimal> closes, int period)
        {
            var rsiValues = TechnicalAnalysis.RSI (closes, period);
            return rsiValues.LastOrDefault () ?? 50;
        }
    }
}