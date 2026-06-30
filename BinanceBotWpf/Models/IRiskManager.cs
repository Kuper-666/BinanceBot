namespace BinanceBotWpf.Risk
{
    public interface IRiskManager
    {
        decimal BalanceUSDC { get; set; }
        int MaxOpenOrders { get; set; }
        decimal MaxDailyLossPercent { get; set; }
        decimal MaxExposurePercent { get; set; }
        bool CanTrade { get; }

        (bool Allowed, string Reason) CanOpenPosition (int currentOpenPositions, decimal orderValueUsdc, decimal currentTotalExposure = 0, decimal tradePnL = 0);
        void RecordTrade (decimal pnlUsdc);
    }
}
