using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Globalization;

namespace BinanceBotWpf.Models
{
    /// <summary>Автоматическая конвертация активов в USDC для поддержания баланса.</summary>
    public class BalanceRebalancer : IBalanceRebalancer
    {
        private decimal _targetUsdcBalance = 5.50m;
        public event Action<string> OnLogGenerated;
        private static readonly HashSet<string> BlacklistedAssets = new () { "RDNT", "NTRN", "LDBNB", "LDAIGENSYN", "BETH", "WBETH", "FDUSD" };

        public BalanceRebalancer (decimal targetUsdcBalance = 5.50m)
        {
            _targetUsdcBalance = Math.Max (targetUsdcBalance, 5.10m);
        }

        private bool _isRebalancing = false;

        public async Task AutoConvertAssetsToUsdcAsync(BinanceClient client, bool isRunning, HashSet<string> openPositionSymbols, decimal targetUsdc = 15m)
        {
            if (_isRebalancing)
            {
                Log ("⚠️ Ребаланс уже выполняется, пропускаем.");
                return;
            }
            _isRebalancing = true;
            try
            {
                decimal currentUsdc = await client.GetAccountBalanceAsync ("USDC");
                if (currentUsdc >= targetUsdc) return;

                decimal need = targetUsdc - currentUsdc;
                if (need >= 0.5m)
                {
                    decimal earnUsdc = await GetEarnBalanceAsync ("USDC", client);
                    if (earnUsdc >= need - 0.01m)
                    {
                        Log ($"🔄 Выкупаю {need:F2} USDC из Earn...");
                        bool redeemed = await client.RedeemFlexibleEarnWithWaitAsync ("USDC", need);
                        if (redeemed)
                        {
                            currentUsdc = await client.GetAccountBalanceAsync ("USDC");
                            if (currentUsdc >= targetUsdc) return;
                            Log ($"✅ Выкуп USDC подтверждён. Новый спот баланс: {currentUsdc:F2}");
                        }
                        else
                        {
                            Log ($"⚠️ Не удалось выкупить USDC из Earn. Пробую продать другие активы.");
                        }
                    }
                    else
                    {
                        Log ($"⚠️ В Earn недостаточно USDC: доступно {earnUsdc:F2}, требуется {need:F2}");
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
                    if (openPositionSymbols != null && openPositionSymbols.Contains (pair))
                    {
                        Log ($"⏸️ {asset} пропущен: есть открытая позиция ({pair})");
                        continue;
                    }

                    var klines = await client.GetKlinesAsync (pair, "5m", 5);
                    if (klines == null || klines.Count == 0) continue;
                    decimal price = klines.Last ().Close;
                    decimal estimatedValue = totalAmount * price;

                    // ИСПРАВЛЕНО: минимальная сумма продажи 10 USDC (было 6)
                    if (estimatedValue < 10.0m)
                    {
                        Log ($"⏭️ {asset}: стоимость {estimatedValue:F2} USDC ниже минимальной 10.0, пробую конвертировать пыль в USDC");
                        // Пробуем сконвертировать через dust
                        await client.ConvertDustToUsdcAsync (null);
                        continue;
                    }

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
                    else
                    {
                        Log ($"❌ Ошибка при продаже {asset}. Пробую следующий...");
                    }
                }
                Log ("❌ Не удалось пополнить USDC: нет ликвидных активов (или все активы с открытыми позициями).");
            }
            catch (Exception ex) { Log ($"❌ Ошибка ребалансировщика: {ex.Message}"); }
            finally
            {
                _isRebalancing = false;
            }
        }

        private async Task<Dictionary<string, (decimal TotalAmount, decimal TotalValue)>> GetAllBalancesAsync(BinanceClient client)
        {
            var result = new Dictionary<string, (decimal TotalAmount, decimal TotalValue)> ();
            try
            {
                var account = await client.GetAccountInfoAsync ();
                var earnData = await client.GetFlexibleEarnBalanceAsync (); // Теперь поддерживает пагинацию

                if (account?["balances"] != null)
                {
                    foreach (var b in account["balances"])
                    {
                        string asset = b["asset"]?.ToString ();
                        if (string.IsNullOrEmpty (asset)) continue;
                        decimal free = decimal.Parse (b["free"]?.ToString () ?? "0", CultureInfo.InvariantCulture);
                        decimal locked = decimal.Parse (b["locked"]?.ToString () ?? "0", CultureInfo.InvariantCulture);
                        decimal total = free + locked;
                        if (total > 0)
                            result[asset] = (total, 0);
                    }
                }

                if (earnData != null)
                {
                    foreach (var item in earnData)
                    {
                        string asset = item["asset"]?.ToString ();
                        if (string.IsNullOrEmpty (asset)) continue;

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

                Log ($"📊 Получено {result.Count} активов с балансом");
            }
            catch (Exception ex) { Log ($"Ошибка получения балансов: {ex.Message}"); }
            return result;
        }

        private async Task<decimal> GetEarnBalanceAsync(string asset, BinanceClient client)
        {
            var earnPositions = await client.GetFlexibleEarnBalanceAsync ();
            var earnPos = earnPositions?.FirstOrDefault (p => p["asset"]?.ToString () == asset);
            return earnPos != null ? decimal.Parse (earnPos["totalAmount"]?.ToString () ?? "0", CultureInfo.InvariantCulture) : 0;
        }

        private void Log(string msg) => OnLogGenerated?.Invoke (msg);
    }
}