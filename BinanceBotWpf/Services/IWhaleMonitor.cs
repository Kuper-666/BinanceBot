using System;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public interface IWhaleMonitor : IDisposable
    {
        event Action<WhaleAlert> OnWhaleDetected;
        Task StartAsync (string[] symbols);
    }
}
