using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BinanceBotWpf.Exchange;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services
{
    public interface IWalletManager
    {
        event Action<string> OnLogGenerated;
        void SetFuturesClient (IBinanceFuturesClient futuresClient);
        Task UpdateBalance ();
        decimal GetTotalBalance (string asset);
        Dictionary<string, AssetBalance> GetActiveBalances ();
        Task<List<string>> DetermineAvailablePairs (string quoteAsset = "USDC");
    }
}
