using System.Collections.Generic;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services
{
    public interface IDcaStrategy
    {
        int LookbackDays { get; set; }
        decimal MaxDrawdownPercent { get; set; }
        decimal AtrMultiplier { get; set; }
        decimal BuyPercent { get; set; }

        bool ShouldBuy (string symbol, List<BinanceKline> klines, decimal balance);
        decimal CalculateBuyAmount (decimal balance);
    }
}
