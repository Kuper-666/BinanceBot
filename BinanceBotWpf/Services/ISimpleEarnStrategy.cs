using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public interface ISimpleEarnStrategy
    {
        decimal MinBalanceForLock { get; set; }
        decimal LockPercent { get; set; }
        decimal ReservePercent { get; set; }
        Task OptimizeEarnAsync ();
    }
}
