using System.Collections.Generic;
using System.Threading.Tasks;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services
{
    public interface ISignalFilter
    {
        Task<bool> ShouldBuyAsync (
            string symbol, decimal price, decimal rsi,
            decimal fastSma, decimal slowSma, decimal volume,
            decimal avgVolume, decimal macdHistogram,
            decimal prevMacdHistogram, List<decimal> closes,
            List<decimal> highs, List<decimal> lows);
    }
}
