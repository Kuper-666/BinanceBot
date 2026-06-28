using System.Collections.Generic;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services
{
    public interface IVolumeBreakoutStrategy
    {
        decimal VolumeMultiplier { get; set; }
        decimal StopLossPercent { get; set; }
        bool CheckVolumeBreakout (List<BinanceKline> klines);
    }
}
