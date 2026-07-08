using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services.Strategies
{
    /// <summary>
    /// Основная торговая стратегия (SMA + RSI + MACD) с мульти-таймфрейм поддержкой
    /// и Золотой архитектурой (3 эшелона ИИ)
    /// </summary>
    public class TradingStrategy : ITradingStrategy
    {
        private readonly StrategyEngine _strategyEngine = new ();
        private readonly Action<string> _logger;

        public int FastSmaPeriod { get; set; } = 9;
        public int SlowSmaPeriod { get; set; } = 21;
        public int RsiPeriod { get; set; } = 14;

        // Мульти-таймфрейм
        public string MainTimeframe { get; set; } = "1h";
        public string EntryTimeframe { get; set; } = "5m";

        // Эшелон 1: AdaptiveAgent (ML.NET волатильность)
        private AdaptiveAgent _adaptiveAgent;
        private bool _adaptiveEnabled = true;

        // Эшелон 2: SignalValidator (ONNX)
        private SignalValidator _signalValidator;
        private bool _signalValidatorEnabled = true;

        // Эшелон 3: NewsSentinel (SQLite)
        private NewsSentinel _newsSentinel;
        private bool _newsSentinelEnabled = true;

        public TradingStrategy(Action<string> logger)
        {
            _logger = logger;
        }

        private MlModelManager _mlManager;

        public void SetMlManager (MlModelManager mlManager)
        {
            _mlManager = mlManager;
        }

        public void SetAdaptiveAgent (AdaptiveAgent agent, bool enabled = true)
        {
            _adaptiveAgent = agent;
            _adaptiveEnabled = enabled;
        }

        public void SetSignalValidator (SignalValidator validator, bool enabled = true)
        {
            _signalValidator = validator;
            _signalValidatorEnabled = enabled;
        }

        public void SetNewsSentinel (NewsSentinel sentinel, bool enabled = true)
        {
            _newsSentinel = sentinel;
            _newsSentinelEnabled = enabled;
        }

        /// <summary>
        /// Проверка новостного фона перед открытием позиции.
        /// Возвращает true, если торговля разрешена.
        /// </summary>
        public bool CheckNewsBeforePosition (string symbol = null)
        {
            if (!_newsSentinelEnabled || _newsSentinel == null) return true;

            bool hasHighImpact = _newsSentinel.IsHighImpactNewsActive (symbol);
            if (hasHighImpact)
            {
                _logger?.Invoke ($"⚠️ {symbol}: обнаружены высокорисковые новости — торговля приостановлена");
            }
            return !hasHighImpact;
        }

        // Публичный метод для установки логгера (если нужно обновить после создания)
        public void SetLogger(Action<string> logger)
        {
            // Поле _logger только для чтения, нельзя изменить после конструктора
            // Поэтому просто игнорируем вызов, логгер уже установлен в конструкторе
        }

        /// <summary>
        /// Анализ пары и генерация сигнала с интеграцией 3 эшелонов ИИ
        /// </summary>
        public Task<(TradeAction Action, string Reason, Dictionary<string, decimal> Indicators)>
            AnalyzeAsync (string symbol, List<BinanceKline> klines)
        {
            var result = (Action: TradeAction.Hold, Reason: "Нет сигнала", Indicators: new Dictionary<string, decimal> ());

            if (klines == null || klines.Count < SlowSmaPeriod + 5)
            {
                result.Reason = "Недостаточно данных";
                return Task.FromResult (result);
            }

            try
            {
                var closes = klines.Select (k => k.Close).ToList ();
                var highs = klines.Select (k => k.High).ToList ();
                var lows = klines.Select (k => k.Low).ToList ();
                var volumes = klines.Select (k => k.Volume).ToList ();

                // ═══════ Эшелон 1: AdaptiveAgent — анализ волатильности ═══════
                decimal adaptiveFactor = 1.0m;
                decimal adaptiveLsmaMultiplier = 1.0m;
                decimal adaptiveSlMultiplier = 1.0m;
                string regime = "Normal";

                if (_adaptiveEnabled && _adaptiveAgent != null)
                {
                    var adaptive = _adaptiveAgent.Calculate (klines);
                    adaptiveFactor = adaptive.Factor;
                    adaptiveLsmaMultiplier = adaptive.LsmaWindowMultiplier;
                    adaptiveSlMultiplier = adaptive.SlMultiplier;
                    regime = adaptive.Regime;

                    result.Indicators["adaptiveFactor"] = adaptiveFactor;
                    result.Indicators["adaptiveRegime"] = regime == "High Volatility" ? 2 : (regime == "Low Volatility" ? 0 : 1);
                }

                // Адаптивные периоды LSMA/SMA
                int adaptiveFastSma = Math.Max (3, (int)Math.Round (FastSmaPeriod * adaptiveLsmaMultiplier));
                int adaptiveSlowSma = Math.Max (adaptiveFastSma + 3, (int)Math.Round (SlowSmaPeriod * adaptiveLsmaMultiplier));

                // Расчёт индикаторов
                decimal currentPrice = closes.Last ();
                decimal fastSma = CalculateSma (closes, adaptiveFastSma);
                decimal slowSma = CalculateSma (closes, adaptiveSlowSma);
                decimal rsi = CalculateRsi (closes, RsiPeriod);
                decimal avgVolume = volumes.TakeLast (20).Average ();
                decimal volumeRatio = volumes.Last () / avgVolume;

                var macd = TechnicalAnalysis.MACD (closes, 12, 26, 9);
                decimal macdHist = macd.Histogram.LastOrDefault () ?? 0;
                decimal prevMacdHist = macd.Histogram.Count > 1 ? macd.Histogram[macd.Histogram.Count - 2] ?? 0 : 0;

                var bb = TechnicalAnalysis.BollingerBands (closes, 20, 2);
                decimal bbUpper = bb.Upper.LastOrDefault () ?? currentPrice;
                decimal bbLower = bb.Lower.LastOrDefault () ?? currentPrice;
                decimal bbWidth = (bbUpper - bbLower) / (currentPrice + 0.0001m);

                // LSMA расчёт
                var lsmaValues = TechnicalAnalysis.LSMA (closes, Math.Max (5, (int)Math.Round (20 * adaptiveLsmaMultiplier)));
                decimal lsma = lsmaValues.LastOrDefault () ?? currentPrice;

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
                result.Indicators["lsma"] = lsma;

                // Дополнительно для ML
                var atrList = TechnicalAnalysis.ATR (highs, lows, closes, 14);
                decimal atr = atrList.LastOrDefault () ?? currentPrice * 0.02m;
                var obvList = TechnicalAnalysis.OBV (klines);
                decimal obv = obvList.LastOrDefault ();
                decimal atrPercent = currentPrice > 0 ? atr / currentPrice : 0;
                result.Indicators["atr"] = atrPercent;

                // Предсказание ML
                if (_mlManager != null)
                {
                    var riskPrediction = _mlManager.PredictRisk (fastSma, slowSma, rsi, volumeRatio, atr, macdHist, bbWidth, obv);
                    result.Indicators["aiProbability"] = (decimal)riskPrediction.Probability;

                    decimal riskVal = riskPrediction.RiskLevel == "Низкий риск" ? 1 : (riskPrediction.RiskLevel == "Средний риск" ? 2 : 3);
                    result.Indicators["aiRiskLevel"] = riskVal;
                }

                // ═══════ Базовый сигнал от SMA ═══════
                var baseSignal = _strategyEngine.AnalyzePairWithWallet (symbol, closes, adaptiveFastSma, adaptiveSlowSma, currentPrice);

                // ═══════ Дополнительные сигналы (если SMA crossover редок) ═══════
                if (baseSignal.Action == TradeAction.Hold)
                {
                    // RSI extremes + LSMA trend alignment (расширенные пороги)
                    if (rsi < 35 && lsma > 0 && currentPrice > lsma)
                    {
                        baseSignal.Action = TradeAction.Buy;
                        baseSignal.Reason = $"RSI перепродан ({rsi:F1}) + LSMA uptrend";
                    }
                    else if (rsi > 65 && lsma > 0 && currentPrice < lsma)
                    {
                        baseSignal.Action = TradeAction.Sell;
                        baseSignal.Reason = $"RSI перекуплен ({rsi:F1}) + LSMA downtrend";
                    }
                    // MACD histogram reversal (расширенные пороги RSI)
                    else if (macdHist > 0 && prevMacdHist <= 0 && rsi < 50)
                    {
                        baseSignal.Action = TradeAction.Buy;
                        baseSignal.Reason = $"MACD пересечение вверх + RSI={rsi:F1}";
                    }
                    else if (macdHist < 0 && prevMacdHist >= 0 && rsi > 50)
                    {
                        baseSignal.Action = TradeAction.Sell;
                        baseSignal.Reason = $"MACD пересечение вниз + RSI={rsi:F1}";
                    }
                    // BB bounce (расширенные пороги)
                    else if (currentPrice <= bbLower * 1.005m && rsi < 40)
                    {
                        baseSignal.Action = TradeAction.Buy;
                        baseSignal.Reason = $"BB отскок от нижней + RSI={rsi:F1}";
                    }
                    else if (currentPrice >= bbUpper * 0.995m && rsi > 60)
                    {
                        baseSignal.Action = TradeAction.Sell;
                        baseSignal.Reason = $"BB отскок от верхней + RSI={rsi:F1}";
                    }
                    // SMA trend direction (без сигнала — только для логов)
                    else if (fastSma > slowSma)
                    {
                        baseSignal.Reason = $"Нет сигнала (SMA uptrend F:{fastSma:F2} > S:{slowSma:F2})";
                    }
                    else if (fastSma < slowSma)
                    {
                        baseSignal.Reason = $"Нет сигнала (SMA downtrend F:{fastSma:F2} < {slowSma:F2})";
                    }
                    else if (fastSma < slowSma)
                    {
                        baseSignal.Reason = $"SMA downtrend F:{fastSma:F2} < S:{slowSma:F2}";
                    }
                }

                // Усиление сигнала от других индикаторов (только для SMA crossover)
                bool isSmaCrossover = baseSignal.Reason.Contains ("SMA");
                if (isSmaCrossover && baseSignal.Action == TradeAction.Buy)
                {
                    bool bbOversold = currentPrice <= bbLower;
                    bool macdBullish = macdHist > 0 && macdHist > prevMacdHist;
                    bool rsiOversold = rsi < 30;

                    if (bbOversold || macdBullish || rsiOversold)
                    {
                        result.Action = TradeAction.Buy;
                        result.Reason = $"SMA Покупка + {(bbOversold ? "BB " : "")}{(macdBullish ? "MACD " : "")}{(rsiOversold ? "RSI" : "")}";
                    }
                    else
                    {
                        result.Action = TradeAction.Hold;
                        result.Reason = "SMA Покупка без подтверждения";
                    }
                }
                else if (isSmaCrossover && baseSignal.Action == TradeAction.Sell)
                {
                    bool bbOverbought = currentPrice >= bbUpper;
                    bool macdBearish = macdHist < 0 && macdHist < prevMacdHist;
                    bool rsiOverbought = rsi > 70;

                    if (bbOverbought || macdBearish || rsiOverbought)
                    {
                        result.Action = TradeAction.Sell;
                        result.Reason = $"SMA Продажа + {(bbOverbought ? "BB " : "")}{(macdBearish ? "MACD " : "")}{(rsiOverbought ? "RSI" : "")}";
                    }
                    else
                    {
                        result.Action = TradeAction.Hold;
                        result.Reason = "SMA Продажа без подтверждения";
                    }
                }
                else if (baseSignal.Action != TradeAction.Hold)
                {
                    // Дополнительные сигналы (RSI, MACD, BB) — уже содержат подтверждение
                    result.Action = baseSignal.Action;
                    result.Reason = baseSignal.Reason;
                }
                else
                {
                    result.Action = TradeAction.Hold;
                    result.Reason = baseSignal.Reason;
                }

                // ═══════ Эшелон 2: SignalValidator — валидация сигнала ═══════
                if (result.Action != TradeAction.Hold && _signalValidatorEnabled && _signalValidator != null)
                {
                    var validation = _signalValidator.Validate (new SignalValidationInput
                    {
                        Price = (float)currentPrice,
                        Rsi = (float)rsi,
                        MacdHistogram = (float)macdHist,
                        BbWidth = (float)bbWidth,
                        AtrPercent = (float)atrPercent,
                        VolumeRatio = (float)volumeRatio,
                        SmaFast = (float)fastSma,
                        SmaSlow = (float)slowSma,
                        SignalDirection = result.Action == TradeAction.Buy ? 1f : -1f
                    });

                    result.Indicators["validationConfidence"] = (decimal)validation.Confidence;
                    result.Indicators["validationRiskFlag"] = validation.RiskFlag ? 1 : 0;

                    if (!validation.IsValid)
                    {
                        result.Action = TradeAction.Hold;
                        result.Reason = $"Заблокировано валидатором (уверенность={validation.Confidence:P0}, риск={validation.RiskFlag})";
                    }
                    else if (validation.RiskFlag)
                    {
                        adaptiveSlMultiplier *= 1.3m;
                        _logger?.Invoke ($"⚠️ {symbol}: валидатор — повышенный риск, SL увеличен на 30%");
                    }
                }

                // Адаптивный стоп-лосс
                if (result.Action != TradeAction.Hold && adaptiveSlMultiplier > 1.0m)
                {
                    result.Indicators["adaptiveSlMultiplier"] = adaptiveSlMultiplier;
                }

                _logger?.Invoke ($"📊 {symbol}: {result.Reason} (RSI={rsi:F1}, MACD={macdHist:F4}, LSMA={lsma:F2}, regime={regime})");
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Ошибка анализа {symbol}: {ex.Message}");
                result.Reason = $"Ошибка: {ex.Message}";
            }

            return Task.FromResult (result);
        }

        /// <summary>
        /// Проверка подтверждения сигнала на мелком таймфрейме.
        /// Вызывается после генерации сигнала на основном TF.
        /// </summary>
        public bool CheckEntryConfirmation(List<BinanceKline> entryKlines, TradeAction signal)
        {
            if (entryKlines == null || entryKlines.Count < 20) return true; // Нет данных — пропускаем проверку

            var closes = entryKlines.Select (k => k.Close).ToList ();
            decimal rsi = CalculateRsi (closes, 14);
            decimal currentPrice = closes.Last ();
            var bb = TechnicalAnalysis.BollingerBands (closes, 20, 2);
            decimal bbLower = bb.Lower.LastOrDefault () ?? currentPrice;
            decimal bbUpper = bb.Upper.LastOrDefault () ?? currentPrice;

            if (signal == TradeAction.Buy)
            {
                return rsi < 70 && currentPrice < bbUpper;
            }
            else if (signal == TradeAction.Sell)
            {
                return rsi > 30 && currentPrice > bbLower;
            }

            return false;
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
