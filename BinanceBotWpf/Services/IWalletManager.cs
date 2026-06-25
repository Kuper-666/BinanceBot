using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services
{
    public interface IWalletManager
    {
        event Action<string> OnLogGenerated;
        Task UpdateBalance ();
        decimal GetTotalBalance (string asset);
        Dictionary<string, AssetBalance> GetActiveBalances ();
        Task<List<string>> DetermineAvailablePairs (string quoteAsset = "USDC");
    }
}
