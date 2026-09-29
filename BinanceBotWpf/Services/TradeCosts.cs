namespace BinanceBotWpf.Services
{
    public static class TradeCosts
    {
        public const decimal FeeRate = 0.001m;

        public static decimal Fees(decimal entryPrice, decimal exitPrice, decimal quantity)
        {
            if (quantity <= 0 || entryPrice <= 0 || exitPrice <= 0) return 0;
            return ( entryPrice + exitPrice ) * quantity * FeeRate;
        }

        public static decimal NetPnL(decimal entryPrice, decimal exitPrice, decimal quantity)
        {
            if (quantity <= 0) return 0;
            return ( exitPrice - entryPrice ) * quantity - Fees (entryPrice, exitPrice, quantity);
        }

        public static decimal NetPnLPercent(decimal entryPrice, decimal exitPrice, decimal quantity)
        {
            decimal cost = entryPrice * quantity;
            if (cost <= 0) return 0;
            return NetPnL (entryPrice, exitPrice, quantity) / cost * 100;
        }
    }
}
