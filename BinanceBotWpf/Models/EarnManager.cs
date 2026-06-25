using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BinanceBotWpf.Models
{
    /// <summary>Управление Earn: выкуп активов на спот.</summary>
    public class EarnManager
    {
        private readonly HashSet<string> _pendingRedemptions = new ();
        private readonly object _pendingLock = new ();
        private DateTime _lastNoFundsLog = DateTime.MinValue;

        public event Action<string> OnLogGenerated;

        public EarnManager ()
        {
        }

        /// <summary>Обеспечивает наличие requiredAmount актива на споте, выкупая из Earn при необходимости.</summary>
        public async Task<bool> EnsureLiquidBalanceAsync(string asset, decimal requiredAmount, BinanceClient client)
        {
            try
            {
                decimal currentSpot = await GetOnlySpotBalanceAsync (client, asset);
                if (currentSpot >= requiredAmount) return true;

                decimal needToRedeem = requiredAmount - currentSpot;
                decimal minRedeemAmount = asset == "USDC" ? 1.0m : 0.0001m;

                if (needToRedeem < minRedeemAmount)
                {
                    if (asset == "USDC" && DateTime.UtcNow - _lastNoFundsLog > TimeSpan.FromMinutes (1))
                    {
                        Log ($"⚠️ В Earn недостаточно USDC (доступно {await GetEarnBalanceAsync (asset, client):F2}, требуется {needToRedeem:F2})");
                        _lastNoFundsLog = DateTime.UtcNow;
                    }
                    return false;
                }

                lock (_pendingLock)
                {
                    if (_pendingRedemptions.Contains (asset)) return false;
                    _pendingRedemptions.Add (asset);
                }

                try
                {
                    Log ($"🔄 Выкупаю {needToRedeem} {asset} из Earn...");
                    bool success = await client.RedeemFlexibleEarnWithWaitAsync (asset, needToRedeem);
                    if (success)
                    {
                        if (asset != "USDC") Log ($"✅ Выкуп {asset} подтверждён.");
                        return true;
                    }
                    Log ($"⚠️ Не удалось выкупить {asset}");
                    return false;
                }
                finally
                {
                    lock (_pendingLock) _pendingRedemptions.Remove (asset);
                }
            }
            catch (Exception ex)
            {
                Log ($"⚠️ Ошибка: {ex.Message}");
                return false;
            }
        }

        private async Task<decimal> GetEarnBalanceAsync(string asset, BinanceClient client)
        {
            var earnPositions = await client.GetFlexibleEarnBalanceAsync ();
            var earnPos = earnPositions?.FirstOrDefault (p => p["asset"]?.ToString () == asset);
            return earnPos != null ? decimal.Parse (earnPos["totalAmount"]?.ToString () ?? "0", System.Globalization.CultureInfo.InvariantCulture) : 0;
        }

        private async Task<decimal> GetOnlySpotBalanceAsync(BinanceClient client, string asset)
        {
            var accountData = await client.GetAccountInfoAsync ();
            if (accountData?["balances"] != null)
            {
                foreach (var b in accountData["balances"])
                {
                    if (b["asset"]?.ToString () == asset)
                        return decimal.Parse (b["free"]?.ToString () ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            return 0m;
        }

        private void Log(string msg) => OnLogGenerated?.Invoke (msg);
    }
}