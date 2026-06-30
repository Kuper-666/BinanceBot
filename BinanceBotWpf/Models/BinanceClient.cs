using System;
using BinanceBotWpf.Exchange;

namespace BinanceBotWpf.Models
{
    /// <summary>
    /// Backward-compatible alias. Use SpotRestClient or IBinanceRestClient directly in new code.
    /// </summary>
    public class BinanceClient : SpotRestClient
    {
        public BinanceClient (string apiKey, string apiSecret, bool useTestnet = false)
            : base (apiKey, apiSecret, useTestnet)
        {
        }
    }
}
