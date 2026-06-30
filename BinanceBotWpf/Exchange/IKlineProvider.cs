using System.Collections.Generic;
using System.Threading.Tasks;

namespace BinanceBotWpf.Exchange
{
    public interface IKlineProvider
    {
        Task<List<BinanceKline>> GetKlinesAsync (string symbol, string interval, int limit);
        bool HasRealTimeData (string symbol, string interval);
    }
}
