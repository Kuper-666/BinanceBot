using System.Threading.Tasks;
using System.Collections.Generic;

namespace BinanceBotWpf.Services
{
    public interface IMlModelManager
    {
        bool IsLoaded { get; }
        (bool IsProfitable, float Probability, string RiskLevel) PredictRisk (
            decimal fastSma, decimal slowSma, decimal rsi, decimal volumeRatio,
            decimal atr, decimal macdHist, decimal bbWidth, decimal obv,
            float marketCapRank = -1f, float sentimentScore = 0f, float galaxyScore = 0f);
    }
}
