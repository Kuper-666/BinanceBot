using System.Collections.Generic;
using System.Threading.Tasks;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services.Strategies
{
    public interface ITradingStrategy
    {
        int FastSmaPeriod { get; set; }
        int SlowSmaPeriod { get; set; }
        int RsiPeriod { get; set; }
        string MainTimeframe { get; set; }
        string EntryTimeframe { get; set; }

        void SetMlManager (MlModelManager mlManager);
        void SetAdaptiveAgent (AdaptiveAgent agent, bool enabled);
        void SetSignalValidator (SignalValidator validator, bool enabled);
        void SetNewsSentinel (NewsSentinel sentinel, bool enabled);

        Task<(TradeAction Action, string Reason, Dictionary<string, decimal> Indicators)>
            AnalyzeAsync (string symbol, List<BinanceKline> klines);
        bool CheckEntryConfirmation (List<BinanceKline> entryKlines, TradeAction action);
        bool CheckNewsBeforePosition (string symbol);
    }
}
