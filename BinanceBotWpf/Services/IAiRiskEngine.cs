using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public interface IAiRiskEngine
    {
        Task<AiRiskResult> CalculateRiskAsync (string symbol, decimal balance,
            decimal price, decimal fastSma, decimal slowSma, decimal rsi,
            decimal volumeRatio, decimal macdHist, decimal bbWidth, decimal obv);
    }
}
