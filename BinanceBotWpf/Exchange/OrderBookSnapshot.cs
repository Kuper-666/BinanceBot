using System.Collections.Generic;

namespace BinanceBotWpf.Exchange
{
    public class OrderBookSnapshot
    {
        public string Symbol { get; set; }
        public List<OrderBookLevel> Bids { get; set; } = new ();
        public List<OrderBookLevel> Asks { get; set; } = new ();
        public decimal Spread => ( Bids.Count > 0 && Asks.Count > 0 )
            ? Asks[0].Price - Bids[0].Price
            : 0m;
        public decimal SpreadPercent => ( Bids.Count > 0 && Asks.Count > 0 && Bids[0].Price > 0 )
            ? ( Asks[0].Price - Bids[0].Price ) / Bids[0].Price * 100m
            : 0m;
    }

    public class OrderBookLevel
    {
        public decimal Price { get; set; }
        public decimal Quantity { get; set; }
    }
}
