using System;
using System.Collections.Generic;

namespace BinanceBotWpf.Services
{
    public interface IPriceAlertManager : IDisposable
    {
        event Action<PriceAlert>? OnAlertTriggered;
        string AddAlert (string symbol, decimal targetPrice, PriceAlertDirection direction);
        bool RemoveAlert (string id);
        List<PriceAlert> GetAllAlerts ();
    }
}
