using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BinanceBotWpf.Models;
using BinanceBotWpf.Services.Strategies;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Двигатель для бэктестирования стратегий на исторических данных
    /// Синхронизирован с реальным исполнением: комиссии, проскальзывание, округление,
    /// трейлинг-стоп, частичная фиксация, кулдауны.
    /// </summary>
    public class BacktestEngine
    {
        private readonly Action<string> _logger;

        // ─── Параметры, соответствующие реальной торговле ───
        private const decimal FeePercent = 0.001m;           // 0.1% taker (стандарт Binance)
        private const decimal SlippagePercent = 0.002m;       // 0.2% проскальзывание
        private const decimal RiskPerTradePercent = 0.02m;    // 2% баланса на сделку (соответствует RiskCalculator.Clamp 0.5%-2%)
        private const decimal TrailingStopPercent = 0.02m;    // 2% трейлинг-стоп
        private const decimal PartialCloseProfitPercent = 0.05m; // 5% профита → частичная фиксация
        private const decimal PartialCloseQtyPercent = 0.5m;   // закрываем 50%
        private const int MaxHoldTimeHours = 24;               // максимальное удержание 24ч
        private const int BuyCooldownMinutes = 15;             // кулдаун 15мин/символ
        private const int MaxTradesPerHour = 3;                // глобальный лимит
        private const int SharpeAnnualization = 6048;          // 252*24 для 1h свечей

        public BacktestEngine(Action<string> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Результат бэктеста
        /// </summary>
        public class BacktestResult
        {
            public decimal TotalReturn { get; set; }
            public decimal WinRate { get; set; }
            public int TotalTrades { get; set; }
            public int WinningTrades { get; set; }
            public int LosingTrades { get; set; }
            public int BreakEvenTrades { get; set; }
            public decimal MaxDrawdown { get; set; }
            public decimal SharpeRatio { get; set; }
            public decimal ProfitFactor { get; set; }
            public List<decimal> EquityCurve { get; set; }
        }

        // ─── Внутренняя модель открытой позиции ───
        private class SimPosition
        {
            public string Symbol;
            public decimal EntryPrice;
            public decimal Quantity;
            public decimal InitialQuantity;
            public decimal HighestPriceSinceOpen;
            public DateTime OpenTime;
            public bool PartialClosed;
        }

        /// <summary>
        /// Запуск бэктеста с простой SMA стратегией (как было), но с реальным исполнением
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

            var fastSma = CalculateSmaList (closes, fastSmaPeriod);
            var slowSma = CalculateSmaList (closes, slowSmaPeriod);
            var rsi = CalculateRsiList (closes, rsiPeriod);

            decimal capital = initialCapital;
            SimPosition pos = null;
            decimal peakCapital = initialCapital;
            decimal maxDrawdown = 0;

            int winningTrades = 0;
            int losingTrades = 0;
            int breakEvenTrades = 0;
            decimal totalGrossProfit = 0;
            decimal totalGrossLoss = 0;
            var equityCurve = new List<decimal> { initialCapital };
            var lastTradeTimes = new Dictionary<string, DateTime> ();
            var recentTradeTimes = new List<DateTime> ();

            for (int i = slowSmaPeriod + 5; i < closes.Count; i++)
            {
                DateTime candleTime = klines[i].OpenTime;
                decimal candleHigh = highs[i];
                decimal candleLow = lows[i];
                decimal candleClose = closes[i];

                // ─── Генерация сигнала (SMA cross + RSI, как раньше) ───
                bool buySignal = false;
                bool sellSignal = false;

                if (i > 0)
                {
                    bool fastAboveSlow = fastSma[i] > slowSma[i];
                    bool prevFastAboveSlow = fastSma[i - 1] > slowSma[i - 1];

                    if (!prevFastAboveSlow && fastAboveSlow && rsi[i] < 40)
                        buySignal = true;
                    if (prevFastAboveSlow && !fastAboveSlow && rsi[i] > 60)
                        sellSignal = true;
                }

                // ─── Обработка открытой позиции ───
                if (pos != null)
                {
                    // Обновляем HighestPriceSinceOpen (для трейлинга)
                    if (candleHigh > pos.HighestPriceSinceOpen)
                        pos.HighestPriceSinceOpen = candleHigh;

                    // Trailing stop: SL растёт с ценой
                    decimal trailingSL = pos.HighestPriceSinceOpen * (1 - TrailingStopPercent);
                    decimal currentSL = Math.Max (stopLossPercent > 0
                        ? pos.EntryPrice * (1 - stopLossPercent)
                        : 0, trailingSL);

                    // Partial close: при +5% профита закрываем 50%, SL в безубыток
                    if (!pos.PartialClosed && candleHigh >= pos.EntryPrice * (1 + PartialCloseProfitPercent))
                    {
                        decimal closeQty = RoundToStep (pos.Quantity * PartialCloseQtyPercent, 0.00001m);
                        if (closeQty > 0 && closeQty < pos.Quantity)
                        {
                            decimal exitPrice = ApplySlippage (pos.EntryPrice * (1 + PartialCloseProfitPercent), isBuy: false);
                            decimal partialProceeds = closeQty * exitPrice;
                            decimal partialFee = partialProceeds * FeePercent;
                            capital += partialProceeds - partialFee;

                            decimal pnl = (exitPrice - pos.EntryPrice) / pos.EntryPrice;
                            if (pnl > 0.0001m) { winningTrades++; totalGrossProfit += partialProceeds * pnl; }
                            else if (pnl < -0.0001m) { losingTrades++; totalGrossLoss += Math.Abs (partialProceeds * pnl); }
                            else breakEvenTrades++;

                            pos.Quantity -= closeQty;
                            pos.PartialClosed = true;
                            currentSL = pos.EntryPrice; // SL в безубыток
                        }
                    }

                    // Проверка закрытия по SL/TP/Sell signal/MaxHoldTime
                    bool stopHit = candleLow <= currentSL;
                    bool takeHit = candleHigh >= pos.EntryPrice * (1 + takeProfitPercent);
                    bool maxHold = (candleTime - pos.OpenTime).TotalHours >= MaxHoldTimeHours;

                    if (sellSignal || stopHit || takeHit || maxHold)
                    {
                        decimal exitPrice;
                        if (stopHit && !takeHit)
                            exitPrice = ApplySlippage (currentSL, isBuy: false);
                        else if (takeHit)
                            exitPrice = ApplySlippage (pos.EntryPrice * (1 + takeProfitPercent), isBuy: false);
                        else
                            exitPrice = ApplySlippage (candleClose, isBuy: false);

                        decimal proceeds = pos.Quantity * exitPrice;
                        decimal fee = proceeds * FeePercent;
                        capital += proceeds - fee;

                        decimal tradePnl = (exitPrice - pos.EntryPrice) / pos.EntryPrice;
                        if (tradePnl > 0.0001m)
                        {
                            winningTrades++;
                            totalGrossProfit += proceeds * tradePnl;
                        }
                        else if (tradePnl < -0.0001m)
                        {
                            losingTrades++;
                            totalGrossLoss += Math.Abs (proceeds * tradePnl);
                        }
                        else
                        {
                            breakEvenTrades++;
                        }

                        TrackCooldown ("SYMBOL", candleTime, lastTradeTimes, recentTradeTimes);
                        pos = null;
                    }
                    else
                    {
                        // Обновляем текущий SL для незакрытой позиции
                    }
                }

                // ─── Вход в позицию ───
                if (pos == null && buySignal)
                {
                    if (CanTrade ("SYMBOL", candleTime, lastTradeTimes, recentTradeTimes))
                    {
                        decimal entryPrice = ApplySlippage (candleClose, isBuy: true);
                        decimal riskAmount = capital * RiskPerTradePercent;
                        decimal qty = RoundToStep (riskAmount / entryPrice, 0.00001m);

                        if (qty > 0 && qty * entryPrice <= capital && qty * entryPrice >= 5m)
                        {
                            decimal cost = qty * entryPrice;
                            decimal fee = cost * FeePercent;
                            capital -= (cost + fee);

                            pos = new SimPosition
                            {
                                Symbol = "SYMBOL",
                                EntryPrice = entryPrice,
                                Quantity = qty,
                                InitialQuantity = qty,
                                HighestPriceSinceOpen = candleHigh,
                                OpenTime = candleTime,
                                PartialClosed = false
                            };
                        }
                    }
                }

                // ─── Просадка ───
                decimal currentEquity = pos != null
                    ? capital + pos.Quantity * candleClose
                    : capital;
                if (currentEquity > peakCapital) peakCapital = currentEquity;
                decimal dd = (peakCapital - currentEquity) / peakCapital * 100;
                if (dd > maxDrawdown) maxDrawdown = dd;

                equityCurve.Add (currentEquity);
            }

            // Закрытие последней позиции
            if (pos != null)
            {
                decimal exitPrice = ApplySlippage (closes.Last (), isBuy: false);
                decimal proceeds = pos.Quantity * exitPrice;
                decimal fee = proceeds * FeePercent;
                capital += proceeds - fee;

                decimal tradePnl = (exitPrice - pos.EntryPrice) / pos.EntryPrice;
                if (tradePnl > 0.0001m) { winningTrades++; totalGrossProfit += proceeds * tradePnl; }
                else if (tradePnl < -0.0001m) { losingTrades++; totalGrossLoss += Math.Abs (proceeds * tradePnl); }
                else breakEvenTrades++;
            }

            decimal totalReturn = (capital - initialCapital) / initialCapital * 100;
            int totalTrades = winningTrades + losingTrades + breakEvenTrades;
            decimal winRate = totalTrades > 0 ? (decimal)winningTrades / totalTrades * 100 : 0;

            decimal sharpeRatio = CalculateSharpe (equityCurve, SharpeAnnualization);
            decimal profitFactor = totalGrossLoss > 0 && totalGrossProfit > 0
                ? totalGrossProfit / totalGrossLoss
                : (totalGrossProfit > 0 ? 999m : 0);

            return new BacktestResult
            {
                TotalReturn = totalReturn,
                WinRate = winRate,
                TotalTrades = totalTrades,
                WinningTrades = winningTrades,
                LosingTrades = losingTrades,
                BreakEvenTrades = breakEvenTrades,
                MaxDrawdown = maxDrawdown,
                SharpeRatio = sharpeRatio,
                ProfitFactor = profitFactor,
                EquityCurve = equityCurve
            };
        }

        /// <summary>
        /// Запуск бэктеста с реальной TradingStrategy
        /// </summary>
        public async Task<BacktestResult> RunWithStrategyAsync(
            List<BinanceKline> klines,
            TradingStrategy strategy,
            decimal stopLossPercent,
            decimal takeProfitPercent,
            decimal initialCapital = 0m)
        {
            if (klines == null || klines.Count < strategy.SlowSmaPeriod + 10)
            {
                _logger?.Invoke ("⚠️ Недостаточно данных для бэктеста");
                return null;
            }

            if (initialCapital <= 0) initialCapital = 1000m;

            decimal capital = initialCapital;
            SimPosition pos = null;
            decimal peakCapital = initialCapital;
            decimal maxDrawdown = 0;

            int winningTrades = 0;
            int losingTrades = 0;
            int breakEvenTrades = 0;
            decimal totalGrossProfit = 0;
            decimal totalGrossLoss = 0;
            var equityCurve = new List<decimal> { initialCapital };
            var lastTradeTimes = new Dictionary<string, DateTime> ();
            var recentTradeTimes = new List<DateTime> ();

            int warmup = strategy.SlowSmaPeriod + 10;

            for (int i = warmup; i < klines.Count; i++)
            {
                var window = klines.GetRange (0, i + 1);
                var analysis = await strategy.AnalyzeAsync ("BACKTEST", window);

                DateTime candleTime = klines[i].OpenTime;
                decimal candleHigh = klines[i].High;
                decimal candleLow = klines[i].Low;
                decimal candleClose = klines[i].Close;

                // ─── Обработка открытой позиции ───
                if (pos != null)
                {
                    if (candleHigh > pos.HighestPriceSinceOpen)
                        pos.HighestPriceSinceOpen = candleHigh;

                    decimal trailingSL = pos.HighestPriceSinceOpen * (1 - TrailingStopPercent);
                    decimal currentSL = Math.Max (
                        stopLossPercent > 0 ? pos.EntryPrice * (1 - stopLossPercent) : 0,
                        trailingSL);

                    // Partial close
                    if (!pos.PartialClosed && candleHigh >= pos.EntryPrice * (1 + PartialCloseProfitPercent))
                    {
                        decimal closeQty = RoundToStep (pos.Quantity * PartialCloseQtyPercent, 0.00001m);
                        if (closeQty > 0 && closeQty < pos.Quantity)
                        {
                            decimal exitPrice = ApplySlippage (pos.EntryPrice * (1 + PartialCloseProfitPercent), isBuy: false);
                            decimal partialProceeds = closeQty * exitPrice;
                            decimal partialFee = partialProceeds * FeePercent;
                            capital += partialProceeds - partialFee;

                            decimal pnl = (exitPrice - pos.EntryPrice) / pos.EntryPrice;
                            if (pnl > 0.0001m) { winningTrades++; totalGrossProfit += partialProceeds * pnl; }
                            else if (pnl < -0.0001m) { losingTrades++; totalGrossLoss += Math.Abs (partialProceeds * pnl); }
                            else breakEvenTrades++;

                            pos.Quantity -= closeQty;
                            pos.PartialClosed = true;
                            currentSL = pos.EntryPrice;
                        }
                    }

                    bool stopHit = candleLow <= currentSL;
                    bool takeHit = candleHigh >= pos.EntryPrice * (1 + takeProfitPercent);
                    bool maxHold = (candleTime - pos.OpenTime).TotalHours >= MaxHoldTimeHours;

                    if (analysis.Action == TradeAction.Sell || stopHit || takeHit || maxHold)
                    {
                        decimal exitPrice;
                        if (stopHit && !takeHit)
                            exitPrice = ApplySlippage (currentSL, isBuy: false);
                        else if (takeHit)
                            exitPrice = ApplySlippage (pos.EntryPrice * (1 + takeProfitPercent), isBuy: false);
                        else
                            exitPrice = ApplySlippage (candleClose, isBuy: false);

                        decimal proceeds = pos.Quantity * exitPrice;
                        decimal fee = proceeds * FeePercent;
                        capital += proceeds - fee;

                        decimal tradePnl = (exitPrice - pos.EntryPrice) / pos.EntryPrice;
                        if (tradePnl > 0.0001m) { winningTrades++; totalGrossProfit += proceeds * tradePnl; }
                        else if (tradePnl < -0.0001m) { losingTrades++; totalGrossLoss += Math.Abs (proceeds * tradePnl); }
                        else breakEvenTrades++;

                        TrackCooldown (pos.Symbol, candleTime, lastTradeTimes, recentTradeTimes);
                        pos = null;
                    }
                }

                // ─── Вход ───
                if (pos == null && analysis.Action == TradeAction.Buy)
                {
                    if (CanTrade ("BACKTEST", candleTime, lastTradeTimes, recentTradeTimes))
                    {
                        decimal entryPrice = ApplySlippage (candleClose, isBuy: true);
                        decimal riskAmount = capital * RiskPerTradePercent;
                        decimal qty = RoundToStep (riskAmount / entryPrice, 0.00001m);

                        if (qty > 0 && qty * entryPrice <= capital && qty * entryPrice >= 5m)
                        {
                            decimal cost = qty * entryPrice;
                            decimal fee = cost * FeePercent;
                            capital -= (cost + fee);

                            pos = new SimPosition
                            {
                                Symbol = "BACKTEST",
                                EntryPrice = entryPrice,
                                Quantity = qty,
                                InitialQuantity = qty,
                                HighestPriceSinceOpen = candleHigh,
                                OpenTime = candleTime,
                                PartialClosed = false
                            };
                        }
                    }
                }

                // ─── Просадка ───
                decimal currentEquity = pos != null
                    ? capital + pos.Quantity * candleClose
                    : capital;
                if (currentEquity > peakCapital) peakCapital = currentEquity;
                decimal dd = (peakCapital - currentEquity) / peakCapital * 100;
                if (dd > maxDrawdown) maxDrawdown = dd;

                equityCurve.Add (currentEquity);
            }

            // Закрытие последней позиции
            if (pos != null)
            {
                decimal exitPrice = ApplySlippage (klines.Last ().Close, isBuy: false);
                decimal proceeds = pos.Quantity * exitPrice;
                decimal fee = proceeds * FeePercent;
                capital += proceeds - fee;

                decimal tradePnl = (exitPrice - pos.EntryPrice) / pos.EntryPrice;
                if (tradePnl > 0.0001m) { winningTrades++; totalGrossProfit += proceeds * tradePnl; }
                else if (tradePnl < -0.0001m) { losingTrades++; totalGrossLoss += Math.Abs (proceeds * tradePnl); }
                else breakEvenTrades++;
            }

            decimal totalReturn = (capital - initialCapital) / initialCapital * 100;
            int totalTrades = winningTrades + losingTrades + breakEvenTrades;
            decimal winRate = totalTrades > 0 ? (decimal)winningTrades / totalTrades * 100 : 0;

            decimal sharpeRatio = CalculateSharpe (equityCurve, SharpeAnnualization);
            decimal profitFactor = totalGrossLoss > 0 && totalGrossProfit > 0
                ? totalGrossProfit / totalGrossLoss
                : (totalGrossProfit > 0 ? 999m : 0);

            return new BacktestResult
            {
                TotalReturn = totalReturn,
                WinRate = winRate,
                TotalTrades = totalTrades,
                WinningTrades = winningTrades,
                LosingTrades = losingTrades,
                BreakEvenTrades = breakEvenTrades,
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

                                    _logger?.Invoke ($"📊 Новый лучший: доходность {result.TotalReturn:F2}%, винрейт {result.WinRate:F1}%, сделок {result.TotalTrades}");
                                }

                                if (current % 50 == 0)
                                    _logger?.Invoke ($"🔄 Оптимизация: {current}/{totalCombinations} ({current * 100 / totalCombinations}%)");
                            }
                        }
                    }
                }
            }

            _logger?.Invoke ($"✅ Оптимизация завершена. Лучшая доходность: {bestResult.TotalReturn:F2}%");
            return Task.FromResult (bestParams);
        }

        // ─── Вспомогательные методы ───

        private static decimal ApplySlippage (decimal price, bool isBuy)
        {
            return isBuy ? price * (1 + SlippagePercent) : price * (1 - SlippagePercent);
        }

        private static decimal RoundToStep (decimal qty, decimal stepSize)
        {
            if (stepSize <= 0) return qty;
            return Math.Floor (qty / stepSize) * stepSize;
        }

        private static bool CanTrade (
            string symbol, DateTime candleTime,
            Dictionary<string, DateTime> lastTradeTimes,
            List<DateTime> recentTradeTimes)
        {
            // Кулдаун по символу: 15 минут
            if (lastTradeTimes.TryGetValue (symbol, out DateTime lastTime)
                && candleTime - lastTime < TimeSpan.FromMinutes (BuyCooldownMinutes))
                return false;

            // Глобальный лимит: 3 сделки в час
            recentTradeTimes.RemoveAll (t => candleTime - t > TimeSpan.FromHours (1));
            if (recentTradeTimes.Count >= MaxTradesPerHour)
                return false;

            return true;
        }

        private static void TrackCooldown (
            string symbol, DateTime candleTime,
            Dictionary<string, DateTime> lastTradeTimes,
            List<DateTime> recentTradeTimes)
        {
            lastTradeTimes[symbol] = candleTime;
            recentTradeTimes.Add (candleTime);
        }

        private static decimal CalculateSharpe (List<decimal> equityCurve, int annualizationFactor)
        {
            if (equityCurve.Count < 2) return 0;
            var returns = new List<decimal> ();
            for (int i = 1; i < equityCurve.Count; i++)
            {
                if (equityCurve[i - 1] != 0)
                    returns.Add ((equityCurve[i] - equityCurve[i - 1]) / equityCurve[i - 1]);
            }
            if (returns.Count == 0) return 0;
            decimal avgReturn = returns.Average ();
            decimal sumSq = returns.Select (v => (v - avgReturn) * (v - avgReturn)).Sum ();
            decimal stdDev = (decimal)Math.Sqrt ((double)(sumSq / returns.Count));
            return stdDev > 0 ? avgReturn / stdDev * (decimal)Math.Sqrt (annualizationFactor) : 0;
        }

        private List<decimal> CalculateSmaList (List<decimal> data, int period)
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

        private List<decimal> CalculateRsiList (List<decimal> closes, int period)
        {
            var rsiValues = TechnicalAnalysis.RSI (closes, period);
            return rsiValues.Select (v => v ?? 50).ToList ();
        }
    }
}
