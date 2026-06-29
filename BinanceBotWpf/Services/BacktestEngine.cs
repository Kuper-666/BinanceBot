using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Двигатель для бэктестирования стратегий на исторических данных
    /// </summary>
    public class BacktestEngine
    {
        private readonly Action<string> _logger;

        public BacktestEngine(Action<string> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Результат бэктеста
        /// </summary>
        public class BacktestResult
        {
            public decimal TotalReturn { get; set; }          // Общая доходность %
            public decimal WinRate { get; set; }              // Процент прибыльных сделок %
            public int TotalTrades { get; set; }              // Всего сделок
            public int WinningTrades { get; set; }            // Прибыльных
            public int LosingTrades { get; set; }             // Убыточных
            public decimal MaxDrawdown { get; set; }          // Макс просадка %
            public decimal SharpeRatio { get; set; }          // Коэффициент Шарпа
            public decimal ProfitFactor { get; set; }         // Фактор прибыли
            public List<decimal> EquityCurve { get; set; }    // Кривая equity
        }

        /// <summary>
        /// Запуск бэктеста на исторических данных
        /// </summary>
        public BacktestResult Run(
            List<BinanceKline> klines,
            int fastSmaPeriod,
            int slowSmaPeriod,
            int rsiPeriod,
            decimal stopLossPercent,
            decimal takeProfitPercent,
            decimal initialCapital = 0m)
        {
            if (klines == null || klines.Count < slowSmaPeriod + 10)
            {
                _logger?.Invoke ("⚠️ Недостаточно данных для бэктеста");
                return null;
            }

            if (initialCapital <= 0) initialCapital = 1000m;

            var closes = klines.Select (k => k.Close).ToList ();
            var highs = klines.Select (k => k.High).ToList ();
            var lows = klines.Select (k => k.Low).ToList ();

            // Предрасчёт индикаторов
            var fastSma = CalculateSmaList (closes, fastSmaPeriod);
            var slowSma = CalculateSmaList (closes, slowSmaPeriod);
            var rsi = CalculateRsiList (closes, rsiPeriod);

            decimal capital = initialCapital;
            decimal position = 0;
            decimal entryPrice = 0;
            decimal peakCapital = initialCapital;
            decimal maxDrawdown = 0;

            int winningTrades = 0;
            int losingTrades = 0;
            decimal totalGrossProfit = 0;
            decimal totalGrossLoss = 0;
            var equityCurve = new List<decimal> { initialCapital };

            for (int i = slowSmaPeriod + 5; i < closes.Count; i++)
            {
                decimal price = closes[i];

                // Генерация сигнала
                bool buySignal = false;
                bool sellSignal = false;

                if (i > 0)
                {
                    bool fastAboveSlow = fastSma[i] > slowSma[i];
                    bool prevFastAboveSlow = fastSma[i - 1] > slowSma[i - 1];

                    // Золотой крест
                    if (!prevFastAboveSlow && fastAboveSlow && rsi[i] < 40)
                    {
                        buySignal = true;
                    }

                    // Смертельный крест
                    if (prevFastAboveSlow && !fastAboveSlow && rsi[i] > 60)
                    {
                        sellSignal = true;
                    }
                }

            // Торговая логика
            if (position == 0 && buySignal)
            {
                // Покупка с комиссией 0.04% (maker)
                decimal cost = capital * 0.0004m;
                position = ( capital - cost ) / price;
                capital = 0;
                entryPrice = price;
            }
            else if (position > 0)
            {
                decimal profitPercent = ( price - entryPrice ) / entryPrice;
                decimal stopPrice = entryPrice * ( 1 - stopLossPercent );
                decimal takePrice = entryPrice * ( 1 + takeProfitPercent );

                bool stopHit = price <= stopPrice;
                bool takeHit = price >= takePrice;

                if (sellSignal || stopHit || takeHit)
                {
                    // Продажа с комиссией 0.04% (maker)
                    decimal proceeds = position * price;
                    decimal fee = proceeds * 0.0004m;
                    capital = proceeds - fee;
                    position = 0;

                    decimal tradePnl = ( price - entryPrice ) / entryPrice;
                    if (tradePnl > 0.0001m)
                    {
                        winningTrades++;
                        totalGrossProfit += capital - ( entryPrice * ( capital / price ) );
                    }
                    else if (tradePnl < -0.0001m)
                    {
                        losingTrades++;
                        totalGrossLoss += Math.Abs (capital - ( entryPrice * ( capital / price ) ));
                    }
                    else
                    {
                        // Break-even — не считаем ни победой, ни поражением
                    }

                    // Обновление просадки
                    if (capital > peakCapital)
                        peakCapital = capital;

                    decimal drawdown = ( peakCapital - capital ) / peakCapital * 100;
                    if (drawdown > maxDrawdown)
                        maxDrawdown = drawdown;
                }

                // Обновление просадки для незакрытых позиций
                decimal unrealizedEquity = position * price;
                if (unrealizedEquity > peakCapital)
                    peakCapital = unrealizedEquity;
                decimal unrealizedDrawdown = ( peakCapital - unrealizedEquity ) / peakCapital * 100;
                if (unrealizedDrawdown > maxDrawdown)
                    maxDrawdown = unrealizedDrawdown;
            }

                equityCurve.Add (position > 0 ? position * price : capital);
            }

            // Закрытие последней позиции
            if (position > 0)
            {
                decimal proceeds = position * closes.Last ();
                decimal fee = proceeds * 0.0004m;
                capital = proceeds - fee;
            }

            decimal totalReturn = ( capital - initialCapital ) / initialCapital * 100;
            int totalTrades = winningTrades + losingTrades;
            decimal winRate = totalTrades > 0 ? (decimal)winningTrades / totalTrades * 100 : 0;

            // Расчёт Sharpe Ratio (годовой коэффициент зависит от таймфрейма)
            decimal sharpeRatio = 0;
            if (equityCurve.Count > 1)
            {
                var returns = new List<decimal> ();
                for (int i = 1; i < equityCurve.Count; i++)
                {
                    returns.Add (( equityCurve[i] - equityCurve[i - 1] ) / equityCurve[i - 1]);
                }
                decimal avgReturn = returns.Average ();
                decimal stdDev = CalculateStdDev (returns);
                // 252 = дни/год для дневных свечей; для 1h: 252*24=6048
                int annualizationFactor = 252;
                sharpeRatio = stdDev > 0 ? avgReturn / stdDev * (decimal)Math.Sqrt (annualizationFactor) : 0;
            }

            // Фактор прибыли (сумма прибылей / сумма убытков)
            decimal profitFactor = 0;
            if (totalGrossLoss > 0 && totalGrossProfit > 0)
            {
                profitFactor = totalGrossProfit / totalGrossLoss;
            }
            else if (totalGrossProfit > 0)
            {
                profitFactor = 999m; // Все сделки прибыльны
            }

            return new BacktestResult
            {
                TotalReturn = totalReturn,
                WinRate = winRate,
                TotalTrades = totalTrades,
                WinningTrades = winningTrades,
                LosingTrades = losingTrades,
                MaxDrawdown = maxDrawdown,
                SharpeRatio = sharpeRatio,
                ProfitFactor = profitFactor,
                EquityCurve = equityCurve
            };
        }

        /// <summary>
        /// Оптимизация параметров (простой перебор)
        /// </summary>
        public Task<Dictionary<string, object>> OptimizeParametersAsync(
            List<BinanceKline> klines,
            List<int> fastPeriods,
            List<int> slowPeriods,
            List<int> rsiPeriods,
            List<decimal> stopLosses,
            List<decimal> takeProfits)
        {
            var bestResult = new BacktestResult { TotalReturn = -999999 };
            var bestParams = new Dictionary<string, object> ();

            int totalCombinations = fastPeriods.Count * slowPeriods.Count * rsiPeriods.Count * stopLosses.Count * takeProfits.Count;
            int current = 0;

            foreach (var fast in fastPeriods)
            {
                foreach (var slow in slowPeriods.Where (s => s > fast))
                {
                    foreach (var rsiP in rsiPeriods)
                    {
                        foreach (var sl in stopLosses)
                        {
                            foreach (var tp in takeProfits.Where (t => t > sl))
                            {
                                current++;

                                var result = Run (klines, fast, slow, rsiP, sl, tp);

                                if (result != null && result.TotalReturn > bestResult.TotalReturn && result.TotalTrades >= 15)
                                {
                                    bestResult = result;
                                    bestParams["FastSma"] = fast;
                                    bestParams["SlowSma"] = slow;
                                    bestParams["RsiPeriod"] = rsiP;
                                    bestParams["StopLossPercent"] = sl;
                                    bestParams["TakeProfitPercent"] = tp;

                                    _logger?.Invoke ($"📊 Новый лучший результат: доходность {result.TotalReturn:F2}%, win rate {result.WinRate:F1}%, сделок {result.TotalTrades}");
                                }

                                if (current % 50 == 0)
                                {
                                    _logger?.Invoke ($"🔄 Оптимизация: {current}/{totalCombinations} ({current * 100 / totalCombinations}%)");
                                }
                            }
                        }
                    }
                }
            }

            _logger?.Invoke ($"✅ Оптимизация завершена. Лучшая доходность: {bestResult.TotalReturn:F2}%");
            return Task.FromResult (bestParams);
        }

        private List<decimal> CalculateSmaList(List<decimal> data, int period)
        {
            var result = new List<decimal> (data.Count);
            decimal sum = 0;
            for (int i = 0; i < data.Count; i++)
            {
                sum += data[i];
                if (i < period - 1)
                {
                    result.Add (0);
                }
                else
                {
                    if (i >= period)
                        sum -= data[i - period];
                    result.Add (sum / period);
                }
            }
            return result;
        }

        private List<decimal> CalculateRsiList(List<decimal> closes, int period)
        {
            var rsiValues = TechnicalAnalysis.RSI (closes, period);
            return rsiValues.Select (v => v ?? 50).ToList ();
        }

        private decimal CalculateStdDev(List<decimal> values)
        {
            if (values.Count == 0) return 0;
            decimal avg = values.Average ();
            decimal sumSq = values.Select (v => ( v - avg ) * ( v - avg )).Sum ();
            return (decimal)Math.Sqrt ((double)( sumSq / values.Count ));
        }
    }
}
