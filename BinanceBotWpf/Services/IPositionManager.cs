using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public interface IPositionManager
    {
        IReadOnlyDictionary<string, OpenPosition> Positions { get; }
        int Count { get; }
        bool TryGet (string symbol, out OpenPosition pos);
        void AddOrUpdate (string symbol, OpenPosition pos);
        bool Remove (string symbol);
        List<string> GetSymbols ();
        Task LoadAsync (IBinanceClient client, Func<string, Task<decimal>> getPrice, Func<decimal, decimal> getStopLossPercent, Func<decimal, decimal> getTakeProfitPercent);
        Task SaveAsync ();
    }
}
