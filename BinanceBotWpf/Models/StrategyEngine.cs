using System;
using System.Collections.Generic;

namespace BinanceBotWpf.Models
{
    public class StrategySignal
    {
        public TradeAction Action { get; set; } = TradeAction.Hold;
        public string Reason { get; set; } = "Нет четкого сигнала";
    }

    public class StrategyEngine
    {
        public StrategySignal AnalyzePairWithWallet(string symbol, List<decimal> closes, int fastPeriod, int slowPeriod, decimal currentPrice)
        {
            var signal = new StrategySignal ();
            int required = Math.Max (fastPeriod, slowPeriod) + 2;
            if (closes == null || closes.Count < required)
            {
                signal.Action = TradeAction.Hold;
                signal.Reason = $"Мало данных (нужно {required})";
                return signal;
            }

            try
            {
                decimal fastSmaCurrent = CalculateSMA (closes, fastPeriod);
                decimal fastSmaPrevious = CalculateSMA (closes, fastPeriod, closes.Count - 1);
                decimal slowSmaCurrent = CalculateSMA (closes, slowPeriod);
                decimal slowSmaPrevious = CalculateSMA (closes, slowPeriod, closes.Count - 1);

                // 1. SMA crossover signal
                if (fastSmaPrevious <= slowSmaPrevious && fastSmaCurrent > slowSmaCurrent)
                {
                    signal.Action = TradeAction.Buy;
                    signal.Reason = $"Золотой крест SMA ({fastPeriod}/{slowPeriod})";
                    return signal;
                }
                else if (fastSmaPrevious >= slowSmaPrevious && fastSmaCurrent < slowSmaCurrent)
                {
                    signal.Action = TradeAction.Sell;
                    signal.Reason = $"Смертельный крест SMA ({fastPeriod}/{slowPeriod})";
                    return signal;
                }

                // 2. Mean reversion signal: Bollinger Band bounce + RSI
                int bbPeriod = 20;
                if (closes.Count >= bbPeriod + 2)
                {
                    decimal sma20 = CalculateSMA (closes, bbPeriod);
                    decimal sumSq = 0;
                    for (int i = closes.Count - bbPeriod; i < closes.Count; i++)
                    {
                        decimal diff = closes[i] - sma20;
                        sumSq += diff * diff;
                    }
                    decimal stdDev = (decimal)Math.Sqrt ((double)(sumSq / bbPeriod));
                    decimal upperBand = sma20 + 2 * stdDev;
                    decimal lowerBand = sma20 - 2 * stdDev;

                    // RSI calculation
                    decimal rsi = CalculateRSI (closes, 14);

                    // Buy: price near lower band + RSI oversold
                    if (currentPrice <= lowerBand * 1.005m && rsi < 35)
                    {
                        signal.Action = TradeAction.Buy;
                        signal.Reason = $"Отскок от нижней BB (RSI {rsi:F0})";
                        return signal;
                    }
                    // Sell: price near upper band + RSI overbought
                    if (currentPrice >= upperBand * 0.995m && rsi > 65)
                    {
                        signal.Action = TradeAction.Sell;
                        signal.Reason = $"Отскок от верхней BB (RSI {rsi:F0})";
                        return signal;
                    }
                }

                signal.Action = TradeAction.Hold;
                signal.Reason = $"F:{fastSmaCurrent:F2} / S:{slowSmaCurrent:F2}";
            }
            catch (Exception ex)
            {
                signal.Action = TradeAction.Hold;
                signal.Reason = $"Ошибка: {ex.Message}";
            }
            return signal;
        }

        private decimal CalculateSMA(List<decimal> data, int period, int count = -1)
        {
            int dataCount = count >= 0 ? count : data.Count;
            if (dataCount < period) return 0m;
            decimal sum = 0;
            for (int i = dataCount - period; i < dataCount; i++)
                sum += data[i];
            return sum / period;
        }

        private decimal CalculateRSI(List<decimal> data, int period)
        {
            if (data.Count <= period + 1) return 50m;
            decimal avgGain = 0, avgLoss = 0;
            for (int i = data.Count - period; i < data.Count; i++)
            {
                decimal diff = data[i] - data[i - 1];
                if (diff > 0) avgGain += diff;
                else avgLoss += Math.Abs (diff);
            }
            if (avgLoss == 0) return 100m;
            decimal rs = avgGain / avgLoss;
            return 100m - 100m / (1m + rs);
        }
    }
}