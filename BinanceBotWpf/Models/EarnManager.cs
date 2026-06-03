using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BinanceBotWpf.Models
{
    public class EarnManager
    {
        private readonly object _consoleLock;
        private readonly HashSet<string> _pendingRedemptions = new HashSet<string> ();
        private readonly object _pendingLock = new object ();

        private DateTime _lastNoFundsLog = DateTime.MinValue;
        private DateTime _lastSuccessLog = DateTime.MinValue;

        public event Action<string> OnLogGenerated;

        public EarnManager(object consoleLock)
        {
            _consoleLock = consoleLock ?? new object ();
        }

        /// <summary>
        /// Обеспечивает наличие requiredAmount актива на споте, при необходимости выкупая из Earn.
        /// </summary>
        public async Task<bool> EnsureLiquidBalanceAsync(string asset, decimal requiredAmount, BinanceClient client)
        {
            try
            {
                decimal currentSpot = await GetOnlySpotBalanceAsync (client, asset);
                if (currentSpot >= requiredAmount)
                    return true;

                decimal needToRedeem = requiredAmount - currentSpot;
                decimal minRedeemAmount = asset == "USDC" ? 1.0m : 0.0001m;

                if (needToRedeem < minRedeemAmount)
                {
                    // Для USDC логируем не чаще раза в минуту
                    if (asset == "USDC")
                    {
                        if (DateTime.UtcNow - _lastNoFundsLog > TimeSpan.FromMinutes (1))
                        {
                            decimal earnBalance = await GetEarnBalanceAsync (asset, client);
                            Log ($"⚠️ В Earn недостаточно {asset}: доступно {earnBalance:F8}, требуется {needToRedeem:F8}");
                            _lastNoFundsLog = DateTime.UtcNow;
                        }
                    }
                    else
                    {
                        Log ($"⚠️ Сумма выкупа {needToRedeem} {asset} меньше минимальной {minRedeemAmount}. Пропускаем.");
                    }
                    return false;
                }

                // Блокировка повторного выкупа
                lock (_pendingLock)
                {
                    if (_pendingRedemptions.Contains (asset))
                    {
                        Log ($"⏳ Выкуп {asset} уже запущен, ждём...");
                        return false;
                    }
                    _pendingRedemptions.Add (asset);
                }

                try
                {
                    Log ($"🔄 Выкупаю {needToRedeem} {asset} из Earn (таймаут 60 сек)...");
                    bool success = await client.RedeemFlexibleEarnWithWaitAsync (asset, needToRedeem, maxWaitSeconds: 60);
                    if (success)
                    {
                        if (asset == "USDC")
                        {
                            if (DateTime.UtcNow - _lastSuccessLog > TimeSpan.FromMinutes (5))
                            {
                                Log ($"✅ Выкуп {asset} подтверждён.");
                                _lastSuccessLog = DateTime.UtcNow;
                            }
                        }
                        else
                        {
                            Log ($"✅ Выкуп {asset} подтверждён.");
                        }
                        return true;
                    }
                    Log ($"⚠️ Не удалось выкупить {asset} (таймаут или ошибка API).");
                    return false;
                }
                finally
                {
                    lock (_pendingLock) _pendingRedemptions.Remove (asset);
                }
            }
            catch (Exception ex)
            {
                Log ($"⚠️ Ошибка в EnsureLiquidBalanceAsync: {ex.Message}");
                return false;
            }
        }

        // Вспомогательный метод для получения баланса в Earn
        private async Task<decimal> GetEarnBalanceAsync(string asset, BinanceClient client)
        {
            var earnPositions = await client.GetFlexibleEarnBalanceAsync ();
            var earnPos = earnPositions?.FirstOrDefault (p => p["asset"]?.ToString () == asset);
            return earnPos != null ? decimal.Parse (earnPos["totalAmount"]?.ToString () ?? "0", System.Globalization.CultureInfo.InvariantCulture) : 0;
        }

        /// <summary>
        /// Специальный метод для USDC с теми же улучшениями и подавлением микровыкупов.
        /// </summary>
        public async Task<bool> EnsureLiquidBalanceForUsdcAsync(decimal requiredAmount, BinanceClient client)
        {
            try
            {
                decimal currentSpot = await client.GetAccountBalanceAsync ("USDC");
                if (currentSpot >= requiredAmount)
                    return true;

                decimal needToRedeem = requiredAmount - currentSpot;
                if (needToRedeem < 1.0m) // Минимум 1 USDC
                {
                    // Совсем не логируем мелочь для USDC
                    return false;
                }

                lock (_pendingLock)
                {
                    if (_pendingRedemptions.Contains ("USDC"))
                    {
                        Log ("⏳ Выкуп USDC уже запущен, ждём...");
                        return false;
                    }
                    _pendingRedemptions.Add ("USDC");
                }

                try
                {
                    Log ($"🔄 Выкупаю {needToRedeem:F2} USDC из Earn (таймаут 60 сек)...");
                    bool success = await client.RedeemFlexibleEarnWithWaitAsync ("USDC", needToRedeem, maxWaitSeconds: 60);
                    if (success)
                    {
                        await Task.Delay (3000);
                        decimal newSpot = await client.GetAccountBalanceAsync ("USDC");
                        Log ($"✅ Выкуп USDC подтверждён. Баланс на споте: {newSpot:F2}");
                        return true;
                    }
                    Log ("⚠️ Не удалось выкупить USDC из Earn");
                    return false;
                }
                finally
                {
                    lock (_pendingLock)
                        _pendingRedemptions.Remove ("USDC");
                }
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
                        return decimal.Parse (b["free"]?.ToString () ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            return 0m;
        }

        private void Log(string msg) => OnLogGenerated?.Invoke (msg);
    }
}