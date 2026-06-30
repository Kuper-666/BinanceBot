using BinanceBotWpf.Exchange;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Backward-compatible alias. Use IBinanceRestClient directly in new code.
    /// </summary>
    public interface IBinanceClient : IBinanceRestClient
    {
    }
}
