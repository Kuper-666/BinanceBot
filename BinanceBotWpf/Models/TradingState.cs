using System;
using System.Collections.Generic;

namespace BinanceBotWpf.Models
{
    /// <summary>
    /// Trading state snapshot for persistence across restarts.
    /// </summary>
    public class TradingState
    {
        public DateTime SavedAt { get; set; } = DateTime.UtcNow;

        // Session
        public decimal SessionStartBalance { get; set; }
        public bool SessionStartBalanceCaptured { get; set; }

        // Trade cooldowns
        public Dictionary<string, DateTime> LastBuyTime { get; set; } = new ();
        public List<DateTime> RecentTradeTimes { get; set; } = new ();

        // Trade history (last N trades for stats reconstruction)
        public List<TradeLog> TradesHistory { get; set; } = new ();

        // Derived stats (avoid recalculating from full history)
        public decimal TotalPnL { get; set; }
        public decimal WinRate { get; set; }
        public int TotalTrades { get; set; }
        public int WinningTrades { get; set; }
        public int LosingTrades { get; set; }
        public decimal BestPnL { get; set; }
        public decimal WorstPnL { get; set; }
        public decimal PeakBalance { get; set; }
        public decimal MaxDrawdown { get; set; }
        public decimal TotalProfitSum { get; set; }
        public decimal TotalLossSum { get; set; }

        // Dashboard histories
        public List<Dictionary<string, object>> EquityHistory { get; set; } = new ();
        public List<Dictionary<string, object>> PnlHistory { get; set; } = new ();
    }
}
