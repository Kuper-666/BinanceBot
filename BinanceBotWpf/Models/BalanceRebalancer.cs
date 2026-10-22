using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace BinanceBotWpf.Models
{
    /// <summary>Автоматическая конвертация активов в USDC для поддержания баланса.</summary>
    public class BalanceRebalancer : IBalanceRebalancer
    {
        private decimal _targetUsdcBalance = 5.50m;
        public event Action<string> OnLogGenerated;
        private static readonly HashSet<string> BlacklistedAssets = new () { "RDNT", "NTRN", "LDBNB", "LDAIGENSYN", "BETH", "WBETH", "FDUSD" };

        public BalanceRebalancer(decimal targetUsdcBalance = 5.50m)
        {
            _targetUsdcBalance = Math.Max (targetUsdcBalance, 5.10m);
        }

        private bool _isRebalancing = false;
        // Backoff: после безрезультатного цикла повторяем через 30 мин, далее 1ч, 2ч, 4ч.
        // Уведомление отправляем только один раз — при входе в состояние "конвертировать нечего".
        private DateTime _nextAttemptUtc = DateTime.MinValue;
        private int _consecutiveFailures = 0;
        private static readonly TimeSpan MinBackoff = TimeSpan.FromMinutes (30);
        private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours (4);

        public async Task AutoConvertAssetsToUsdcAsync(BinanceClient client, bool isRunning, HashSet<string> openPositionSymbols, decimal targetUsdc = 15m)
        {
            // Пока действует backoff — тихо выходим, не дергаем API и не пишем в лог/Telegram
            if (DateTime.UtcNow < _nextAttemptUtc) return;

            if (_isRebalancing)
            {
                Log ("⚠️ Ребаланс уже выполняется, пропускаем.");
                return;
            }
            _isRebalancing = true;
            bool convertedAnything = false;
            try
            {
                decimal currentUsdc = await client.GetAccountBalanceAsync ("USDC");
                if (currentUsdc >= targetUsdc)
                {
                    ResetBackoff ();
                    return;
                }

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
                            if (currentUsdc >= targetUsdc)
                            {
                                ResetBackoff ();
                                return;
                            }
                            Log ($"✅ Выкуп USDC подтверждён. Новый спот баланс: {currentUsdc:F2}");
                            convertedAnything = true;
                        }
                        else
                        {
                            Log ($"⚠️ Не удалось выкупить USDC из Earn. Пробую продать другие активы.");
                        }
                    }
                    else if (earnUsdc > 0.01m)
                    {
                        Log ($"🔄 Выкупаю {earnUsdc:F2} USDC из Earn (все доступные)...");
                        bool redeemed = await client.RedeemFlexibleEarnWithWaitAsync ("USDC", earnUsdc);
                        if (redeemed)
                        {
                            convertedAnything = true;
                            currentUsdc = await client.GetAccountBalanceAsync ("USDC");
                            if (currentUsdc >= targetUsdc)
                            {
                                ResetBackoff ();
                                return;
                            }
                        }
                    }
                }

                var allBalances = await GetAllBalancesAsync (client);
                var sorted = allBalances.OrderByDescending (x => x.Value.TotalAmount).ToList ();

                // Пыль конвертируем не больше одного раза за цикл
                bool dustAttempted = false;

                foreach (var assetEntry in sorted)
                {
                    if (!isRunning) break;
                    string asset = assetEntry.Key;
                    decimal totalAmount = assetEntry.Value.TotalAmount;
                    if (totalAmount <= 0) continue;
                    if (asset == "USDC" || BlacklistedAssets.Contains (asset) || asset.StartsWith ("LD", StringComparison.Ordinal)) continue;

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

                    if (estimatedValue < 1.0m)
                    {
                        continue;
                    }

                    if (estimatedValue < 10.0m)
                    {
                        if (!dustAttempted)
                        {
                            dustAttempted = true;
                            Log ($"⏭️ {asset}: стоимость {estimatedValue:F2} USDC ниже 10, пробую пыль → USDC");
                            bool dustConverted = await client.ConvertDustToUsdcAsync (null);
                            if (dustConverted)
                            {
                                convertedAnything = true;
                                currentUsdc = await client.GetAccountBalanceAsync ("USDC");
                                if (currentUsdc >= targetUsdc)
                                {
                                    ResetBackoff ();
                                    return;
                                }
                            }
                        }
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
                            decimal earnBalance = await GetEarnBalanceAsync (asset, client);
                            if (earnBalance <= 0.0001m)
                            {
                                Log ($"⏭️ {asset}: нет средств в Earn для выкупа, пропускаем");
                                continue;
                            }
                            if (needToRedeem > earnBalance)
                            {
                                Log ($"⚠️ {asset}: нужно {needToRedeem}, доступно в Earn {earnBalance} — выкупаем все");
                                needToRedeem = earnBalance;
                            }
                            bool redeemed = await client.RedeemFlexibleEarnWithWaitAsync (asset, needToRedeem);
                            if (!redeemed) continue;
                            await Task.Delay (3000);
                        }
                    }

                    Log ($"⚖️ Продажа {normalizedAmount} {asset} ≈ {normalizedAmount * price:F2} USDC");
                    var order = await client.PlaceOrder (pair, "SELL", "MARKET", normalizedAmount);
                    if (order != null)
                    {
                        convertedAnything = true;
                        Log ($"✅ Продано {normalizedAmount} {asset}");
                        currentUsdc = await client.GetAccountBalanceAsync ("USDC");
                        if (currentUsdc >= targetUsdc)
                        {
                            ResetBackoff ();
                            return;
                        }
                    }
                    else
                    {
                        Log ($"❌ Ошибка при продаже {asset}. Пробую следующий...");
                    }
                }

                currentUsdc = await client.GetAccountBalanceAsync ("USDC");
                if (currentUsdc >= targetUsdc)
                {
                    ResetBackoff ();
                    Log ($"✅ USDC баланс достаточный: {currentUsdc:F2}");
                }
                else
                {
                    // Ничего не удалось конвертировать (или конвертировали, но цели не достигли):
                    // уходим в тихий backoff, уведомление — только при входе в это состояние
                    _consecutiveFailures++;
                    TimeSpan delay = GetBackoffDelay (_consecutiveFailures);
                    _nextAttemptUtc = DateTime.UtcNow + delay;

                    if (_consecutiveFailures == 1)
                    {
                        if (convertedAnything)
                        {
                            Log ($"⚠️ Конвертация выполнена, но USDC {currentUsdc:F2} < цели {targetUsdc:F2}. Следующая попытка через {delay.TotalMinutes:F0} мин");
                        }
                        else
                        {
                            Log ($"❌ Нет ликвидных активов для конвертации в USDC (текущий: {currentUsdc:F2}, цель: {targetUsdc:F2}). Следующая попытка через {delay.TotalMinutes:F0} мин");
                        }
                    }
                }
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

        /// <summary>Пауза перед следующей попыткой: 30 мин → 1 ч → 2 ч → 4 ч (максимум).</summary>
        private static TimeSpan GetBackoffDelay(int failures)
        {
            double minutes = MinBackoff.TotalMinutes * Math.Pow (2, Math.Min (failures - 1, 8));
            return TimeSpan.FromMinutes (Math.Min (minutes, MaxBackoff.TotalMinutes));
        }

        private void ResetBackoff()
        {
            _consecutiveFailures = 0;
            _nextAttemptUtc = DateTime.MinValue;
        }

        private void Log(string msg) => OnLogGenerated?.Invoke (msg);
    }
}