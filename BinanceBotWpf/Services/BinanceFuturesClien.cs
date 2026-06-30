using System;
using BinanceBotWpf.Exchange;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Backward-compatible alias. Use FuturesRestClient or IBinanceFuturesClient directly in new code.
    /// </summary>
    public class BinanceFuturesClient : FuturesRestClient
    {
        public BinanceFuturesClient (string apiKey, string apiSecret)
            : base (apiKey, apiSecret)
        {
        }
    }
}
