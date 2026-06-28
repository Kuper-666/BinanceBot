using System;
using System.Threading;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public interface IFearGreedIndexProvider : IDisposable
    {
        Task<FearGreedData?> GetCurrentAsync (CancellationToken ct = default);
        bool IsExtremeGreed ();
        bool IsExtremeFear ();
    }
}
