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
    }
}
