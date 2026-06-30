namespace BinanceBotWpf.Exchange
{
    public interface IOrderBookProvider
    {
        OrderBookSnapshot GetCurrentSnapshot (string symbol);
    }
}
