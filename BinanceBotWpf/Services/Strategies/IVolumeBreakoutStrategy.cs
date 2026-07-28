using BinanceBotWpf.Models;
using System.Collections.Generic;

namespace BinanceBotWpf.Services.Strategies
{
    public interface IVolumeBreakoutStrategy
    {
        decimal VolumeMultiplier { get; set; }
        decimal StopLossPercent { get; set; }
        bool CheckVolumeBreakout(List<BinanceKline> klines);
    }
}