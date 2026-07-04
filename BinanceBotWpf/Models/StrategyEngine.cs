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

                if (fastSmaPrevious <= slowSmaPrevious && fastSmaCurrent > slowSmaCurrent)
                {
                    signal.Action = TradeAction.Buy;
                    signal.Reason = $"Золотой крест SMA ({fastPeriod}/{slowPeriod})";
                }
                else if (fastSmaPrevious >= slowSmaPrevious && fastSmaCurrent < slowSmaCurrent)
                {
                    signal.Action = TradeAction.Sell;
                    signal.Reason = $"Смертельный крест SMA ({fastPeriod}/{slowPeriod})";
                }
                else
                {
                    signal.Action = TradeAction.Hold;
                    signal.Reason = $"F:{fastSmaCurrent:F2} / S:{slowSmaCurrent:F2}";
                }
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
    }
}