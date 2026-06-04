using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Globalization;

namespace BinanceBotWpf.Models
{
    /// <summary>Автоматическая конвертация активов в USDC для поддержания баланса.</summary>
    public class BalanceRebalancer
    {
        private readonly object _consoleLock;
        private decimal _targetUsdcBalance = 5.50m;
        public event Action<string> OnLogGenerated;
        private static readonly HashSet<string> BlacklistedAssets = new () { "RDNT", "NTRN", "LDBNB", "LDAIGENSYN", "BETH", "WBETH" };

        public BalanceRebalancer(object consoleLock, decimal tradePercent, decimal targetUsdcBalance = 5.50m)
        {
            _consoleLock = consoleLock;
            _targetUsdcBalance = Math.Max (targetUsdcBalance, 5.10m);
        }

        /// <summary>Продаёт активы (или конвертирует dust) для пополнения USDC до targetUsdc.</summary>
        public async Task AutoConvertAssetsToUsdcAsync(BinanceClient client, bool isRunning, decimal targetUsdc = 15m)
        {
            try
            {
                decimal currentUsdc = await client.GetAccountBalanceAsync ("USDC");
                if (currentUsdc >= targetUsdc) return;

                decimal need = targetUsdc - currentUsdc;
                if (need >= 1.0m)
                {
                    Log ($"🔄 Выкупаю {need:F2} USDC из Earn...");
                    bool redeemed = await client.RedeemFlexibleEarnWithWaitAsync ("USDC", need);
                    if (redeemed)
                    {
                        currentUsdc = await client.GetAccountBalanceAsync ("USDC");
                        if (currentUsdc >= targetUsdc) return;
                    }
                }

                var allBalances = await GetAllBalancesAsync (client);
                var sorted = allBalances.OrderByDescending (x => x.Value.TotalAmount).ToList ();

                foreach (var assetEntry in sorted)
                {
                    if (!isRunning) break;
                    string asset = assetEntry.Key;
                    decimal totalAmount = assetEntry.Value.TotalAmount;
                    if (totalAmount <= 0) continue;
                    if (asset == "USDC" || BlacklistedAssets.Contains (asset) || asset.StartsWith ("LD")) continue;

                    string pair = asset + "USDC";
                    var klines = await client.GetKlinesAsync (pair, "5m", 5);
                    if (klines == null || klines.Count == 0) continue;
                    decimal price = klines.Last ().Close;
                    decimal estimatedValue = totalAmount * price;
                    if (estimatedValue < 6.0m) continue;

                    decimal stepSize = await client.GetStepSizeAsync (pair);
                    decimal normalizedAmount = Math.Floor (totalAmount / stepSize) * stepSize;
                    if (normalizedAmount <= 0) continue;

                    decimal spotAmount = await client.GetAccountBalanceAsync (asset);
                    if (spotAmount < normalizedAmount)
                    {
                        decimal needToRedeem = normalizedAmount - spotAmount;
                        if (needToRedeem > 0.0001m)
                        {
                            bool redeemed = await client.RedeemFlexibleEarnWithWaitAsync (asset, needToRedeem);
                            if (!redeemed) continue;
                            await Task.Delay (3000);
                        }
                    }

                    Log ($"⚖️ Продажа {normalizedAmount} {asset} ≈ {normalizedAmount * price:F2} USDC");
                    var order = await client.PlaceOrder (pair, "SELL", "MARKET", normalizedAmount);
                    if (order != null)
                    {
                        Log ($"✅ Продано {normalizedAmount} {asset}");
                        currentUsdc = await client.GetAccountBalanceAsync ("USDC");
                        if (currentUsdc >= targetUsdc) return;
                    }
                }
                Log ("❌ Не удалось пополнить USDC: нет ликвидных активов.");
            }
            catch (Exception ex) { Log ($"❌ Ошибка: {ex.Message}"); }
        }

        private async Task<Dictionary<string, (decimal TotalAmount, decimal TotalValue)>> GetAllBalancesAsync(BinanceClient client)
        {
            var result = new Dictionary<string, (decimal TotalAmount, decimal TotalValue)> ();
            try
            {
                var account = await client.GetAccountInfoAsync ();
                var earnData = await client.GetFlexibleEarnBalanceAsync ();
                if (account?["balances"] != null)
                {
                    foreach (var b in account["balances"])
                    {
                        string asset = b["asset"].ToString ();
                        decimal free = decimal.Parse (b["free"]?.ToString () ?? "0", CultureInfo.InvariantCulture);
                        if (free > 0) result[asset] = (free, 0);
                    }
                }
                if (earnData != null)
                {
                    foreach (var item in earnData)
                    {
                        string asset = item["asset"].ToString ();
                        decimal amount = decimal.Parse (item["totalAmount"]?.ToString () ?? "0", CultureInfo.InvariantCulture);
                        if (amount > 0)
                        {
                            if (result.ContainsKey (asset))
                                result[asset] = (result[asset].TotalAmount + amount, 0);
                            else
                                result[asset] = (amount, 0);
                        }
                    }
                }
            }
            catch (Exception ex) { Log ($"Ошибка получения балансов: {ex.Message}"); }
            return result;
        }

        private void Log(string msg) => OnLogGenerated?.Invoke (msg);
    }
}