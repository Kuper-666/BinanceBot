using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BinanceBotWpf.Exchange
{
    public interface IBinanceFuturesClient
    {
        event Action<string> OnLogGenerated;

        Task SyncTimeAsync ();
        Task<decimal> GetAccountBalanceAsync (string asset = "USDT");
        Task SetLeverageAsync (string symbol, int leverage);
        Task<JObject> PlaceMarketOrder (string symbol, string side, decimal quantity);
        Task<List<JObject>> GetPositionsAsync ();
        Task<List<BinanceKline>> GetKlinesAsync (string symbol, string interval, int limit = 500);
        Task SetMarginTypeAsync (string symbol, string marginType);
        Task SetPositionModeAsync (bool hedgeMode);
        Task<JObject> PlaceTrailingStopMarketAsync (string symbol, string side, decimal quantity, decimal callbackRate);
        Task<JObject> PlaceStopMarketAsync (string symbol, string side, decimal quantity, decimal stopPrice);
        Task<decimal> GetPriceAsync (string symbol);
        Task<decimal> GetStepSizeAsync (string symbol);
        Task<(decimal stepSize, decimal minQty)> GetLotSizeAsync (string symbol);
        Task<decimal> GetTickSizeAsync (string symbol);
        Task<decimal> GetMinNotionalAsync (string symbol);
        Task<JObject> PlaceLimitOrder (string symbol, string side, decimal quantity, decimal price);
        Task<bool> CancelOrder (string symbol, long orderId);
        Task<List<JObject>> GetAllOrdersAsync (string symbol, int limit = 50);
        Task<decimal> GetATRAsync (string symbol, int period = 14, string interval = "1h");
    }
}
