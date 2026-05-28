using System;
using System.Threading.Tasks;
using System.Globalization;

namespace BinanceBotWpf.Models
{
    public class EarnManager
    {
        private readonly object _consoleLock;
        public event Action<string> OnLogGenerated;

        public EarnManager(object consoleLock)
        {
            _consoleLock = consoleLock ?? new object ();
        }

        // Основной метод для обеспечения ликвидности (для любых активов)
        public async Task<bool> EnsureLiquidBalanceAsync(string asset, decimal requiredAmount, BinanceClient client)
        {
            try
            {
                decimal currentSpot = await GetOnlySpotBalanceAsync (client, asset);
                if (currentSpot >= requiredAmount) return true;

                decimal needToRedeem = requiredAmount - currentSpot;
                Log ($"🔄 Выкупаю {needToRedeem} {asset} из Earn...");

                bool success = await client.RedeemFlexibleEarnWithWaitAsync (asset, needToRedeem);
                if (success)
                {
                    Log ($"✅ Выкуп {asset} подтверждён.");
                    return true;
                }
                Log ($"⚠️ Не удалось выкупить {asset}");
                return false;
            }
            catch (Exception ex)
            {
                Log ($"⚠️ Ошибка: {ex.Message}");
                return false;
            }
        }

        // Специальный метод для USDC с более детальным логированием
        public async Task<bool> EnsureLiquidBalanceForUsdcAsync(decimal requiredAmount, BinanceClient client)
        {
            try
            {
                decimal currentSpot = await client.GetAccountBalanceAsync ("USDC");
                if (currentSpot >= requiredAmount) return true;

                decimal needToRedeem = requiredAmount - currentSpot;
                Log ($"🔄 Выкупаю {needToRedeem:F2} USDC из Earn...");

                bool success = await client.RedeemFlexibleEarnWithWaitAsync ("USDC", needToRedeem);
                if (success)
                {
                    await Task.Delay (3000);
                    decimal newSpot = await client.GetAccountBalanceAsync ("USDC");
                    Log ($"✅ Выкуп USDC подтверждён. Баланс на споте: {newSpot:F2}");
                    return true;
                }
                Log ($"⚠️ Не удалось выкупить USDC из Earn");
                return false;
            }
            catch (Exception ex)
            {
                Log ($"⚠️ Ошибка при выкупе USDC: {ex.Message}");
                return false;
            }
        }

        private async Task<decimal> GetOnlySpotBalanceAsync(BinanceClient client, string asset)
        {
            var accountData = await client.GetAccountInfoAsync ();
            if (accountData?["balances"] != null)
            {
                foreach (var b in accountData["balances"])
                {
                    string bAsset = b["asset"]?.ToString ();
                    if (bAsset == asset)
                        return decimal.Parse (b["free"]?.ToString () ?? "0", CultureInfo.InvariantCulture);
                }
            }
            return 0m;
        }

        private void Log(string msg) => OnLogGenerated?.Invoke (msg);
    }
}