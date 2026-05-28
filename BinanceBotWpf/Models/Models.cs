using System;

namespace BinanceBotWpf.Models
{
    public enum TradeAction
    {
        Hold,
        Buy,
        Sell
    }

    public class Position
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal EntryPrice { get; set; }
        public decimal Quantity { get; set; }
        public decimal StopLoss { get; set; }
        public decimal TakeProfit { get; set; }
        public DateTime OpenTime { get; set; }
        public bool IsLong { get; set; }
        public decimal BreakEvenPrice { get; set; }

        public decimal GetCurrentPnL(decimal currentPrice)
        {
            return IsLong ? ( currentPrice - EntryPrice ) * Quantity : ( EntryPrice - currentPrice ) * Quantity;
        }

        public decimal GetCurrentPnLPercent(decimal currentPrice)
        {
            if (EntryPrice == 0) return 0;
            return IsLong ? ( currentPrice - EntryPrice ) / EntryPrice * 100 : ( EntryPrice - currentPrice ) / EntryPrice * 100;
        }
    }

    public class TradeLog
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal EntryPrice { get; set; }
        public decimal ExitPrice { get; set; }
        public decimal Quantity { get; set; }
        public bool IsLong { get; set; }
        public decimal PnL { get; set; }
        public decimal PnLPercent { get; set; }
        public DateTime OpenTime { get; set; }
        public DateTime CloseTime { get; set; }
        public string Reason { get; set; } = string.Empty;
        public TimeSpan Duration { get; set; }
        public string Action { get; set; } = "CLOSE"; // BUY_OPEN, SELL_CLOSE
    }

    public class TradeSignal
    {
        public string Symbol { get; set; } = string.Empty;
        public TradeAction Action { get; set; }
        public string Reason { get; set; } = string.Empty;
        public int BuySignals { get; set; }
        public int SellSignals { get; set; }
        public int TrendStrength { get; set; }
        public int Confidence { get; set; }
        public decimal StopLoss { get; set; }
        public decimal TakeProfit { get; set; }
    }

    public class Statistics
    {
        public int TotalTrades { get; set; }
        public int WinningTrades { get; set; }
        public int LosingTrades { get; set; }
        public decimal WinRate { get; set; }
        public decimal GrossProfit { get; set; }
        public decimal GrossLoss { get; set; }
        public decimal NetProfit { get; set; }
        public decimal AverageWin { get; set; }
        public decimal AverageLoss { get; set; }
        public decimal ProfitFactor { get; set; }
        public TimeSpan AverageTradeDuration { get; set; }
        public int MaxConsecutiveWins { get; set; }
        public int MaxConsecutiveLosses { get; set; }
    }
}