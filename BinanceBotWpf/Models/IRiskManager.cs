namespace BinanceBotWpf.Risk
{
    public interface IRiskManager
    {
        decimal BalanceUSDC { get; }
        decimal MaxDailyLossPercent { get; set; }
        decimal MaxExposurePercent { get; set; }
        int MaxOpenOrders { get; set; }
        bool CanTrade { get; }

        (bool Allowed, string Reason) CanOpenPosition (int currentOpenPositions, decimal orderValueUsdc, decimal tradePnL = 0);
        void RecordTrade (decimal pnlUsdc);
    }
}
