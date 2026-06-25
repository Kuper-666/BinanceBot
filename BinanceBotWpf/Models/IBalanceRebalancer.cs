using System;
using System.Threading.Tasks;

namespace BinanceBotWpf.Models
{
    public interface IBalanceRebalancer
    {
        event Action<string> OnLogGenerated;
        Task AutoConvertAssetsToUsdcAsync (BinanceClient client, bool isRunning, System.Collections.Generic.HashSet<string> openPositionSymbols, decimal targetUsdc = 15m);
    }
}
