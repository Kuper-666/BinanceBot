using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BinanceBotWpf.Exchange
{
    public interface IBinanceRestClient
    {
        bool IsTestnet { get; }
        string LastOrderError { get; }
        event Action<string> OnLogGenerated;

        Task SyncTimeAsync ();
        Task<string> GetServerInfo ();
        Task<List<BinanceKline>> GetKlinesAsync (string symbol, string interval, int limit = 500);
        Task<List<string>> GetTopVolumePairsAsync (string quoteAsset = "USDC", int topCount = 20);
        Task<decimal> GetPriceAsync (string symbol);
        Task<decimal> GetAccountBalanceAsync (string asset);
        Task<JObject> GetAccountInfoAsync ();
        Task<JObject> PlaceOrder (string symbol, string side, string type, decimal quantity);
        Task<JObject> PlaceLimitOrder (string symbol, string side, decimal quantity, decimal price);
        Task<JObject> PlaceOcoOrder (string symbol, decimal quantity, decimal stopPrice, decimal limitPrice);
        Task<bool> CancelOrder (string symbol, long orderId);
        Task<bool> CancelOcoOrder (string symbol, long orderListId);
        Task<List<JObject>> GetAllOrdersAsync (string symbol, long startTime = 0, long endTime = 0, int limit = 500);
        Task<decimal> GetStepSizeAsync (string symbol);
        Task<(decimal stepSize, decimal minQty)> GetLotSizeAsync (string symbol);
        Task<decimal> GetMinNotionalAsync (string symbol);
        Task<Dictionary<string, decimal>> GetAllMinNotionalsAsync ();
        Task<decimal> GetTickSizeAsync (string symbol);
        Task<decimal> GetATRAsync (string symbol, int period = 14, string interval = "1h");
        Task<JArray> GetFlexibleEarnBalanceAsync ();
        Task<JArray> GetFlexibleProductsAsync (string asset);
        Task<bool> SubscribeFlexibleEarnAsync (string productId, decimal amount);
        Task<bool> RedeemFlexibleEarnWithWaitAsync (string asset, decimal amount, int maxWaitSeconds = 60);
        Task<JArray> GetDustAssetsAsync (List<string> assets = null);
        Task<bool> ConvertDustToBnbAsync (List<string> assetIds);
        Task<bool> ConvertDustToUsdcAsync (List<string> assetIds = null);
    }
}
