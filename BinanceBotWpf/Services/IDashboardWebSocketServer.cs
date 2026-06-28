using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public interface IDashboardWebSocketServer
    {
        bool IsRunning { get; }
        int ClientCount { get; }
        Func<string, Dictionary<string, object>, Task> OnCommand { get; set; }

        Task StartAsync (int port = 8765);
        void Stop ();
        void BroadcastPrices (List<Dictionary<string, object>> pairsData);
        void BroadcastPositions (List<Dictionary<string, object>> positions);
        void BroadcastTrades (List<Dictionary<string, object>> trades);
        void BroadcastLogs (string logs);
        void BroadcastEchelons (Dictionary<string, object> echelons);
        void BroadcastEquity (List<Dictionary<string, object>> equityPoints);
        void BroadcastPnl (List<Dictionary<string, object>> pnlPoints);
        void BroadcastStats (Dictionary<string, object> stats);
        void BroadcastFearGreed (Dictionary<string, object> data);
        void BroadcastPriceAlerts (List<Dictionary<string, object>> alerts);
    }
}
