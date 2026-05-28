using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Globalization;

namespace BinanceBotWpf.Models
{
    public class BalanceRebalancer
    {
        private readonly object _consoleLock;
        private readonly decimal _tradePercent;
        private readonly decimal _maxTradePercent = 0.30m;
        private readonly decimal _minTradePercent = 0.05m;
        private decimal _targetUsdcBalance = 5.50m;

        public event Action<string> OnLogGenerated; // <-- изменено

        private static readonly HashSet<string> BlacklistedAssets = new HashSet<string>
        {
            "RDNT", "NTRN", "LDBNB", "LDAIGENSYN", "BETH", "WBETH"
        };

        public BalanceRebalancer(object consoleLock, decimal tradePercent, decimal targetUsdcBalance = 5.50m)
        {
            _consoleLock = consoleLock;
            _tradePercent = tradePercent;
            _targetUsdcBalance = Math.Max (targetUsdcBalance, 5.10m);
        }

        public void SetTargetUsdcBalance(decimal newTarget)
        {
            _targetUsdcBalance = Math.Max (newTarget, 5.10m);
            Log ($"🎯 Целевой баланс USDC изменён на {_targetUsdcBalance:F2}");
        }

        private decimal CalculateDynamicPercent(decimal currentUsdc)
        {
            if (currentUsdc >= _targetUsdcBalance)
                return _minTradePercent;

            decimal deficit = _targetUsdcBalance - currentUsdc;
            decimal ratio = Math.Min (1.0m, deficit / _targetUsdcBalance);
            decimal dynamicPercent = _minTradePercent + ( _maxTradePercent - _minTradePercent ) * ratio;
            return Math.Min (_maxTradePercent, dynamicPercent);
        }

        public async Task AutoConvertAssetsToUsdcAsync(BinanceClient client, bool isRunning)
        {
            try
            {
                decimal currentUsdc = await client.GetAccountBalanceAsync ("USDC");
                if (currentUsdc >= _targetUsdcBalance) return;

                decimal dynamicPercent = CalculateDynamicPercent (currentUsdc);
                Log ($"⚠️ Критический баланс USDC (${currentUsdc:F2} < {_targetUsdcBalance:F2}). Использую процент продажи = {dynamicPercent * 100:F1}%");

                var allBalances = await GetAllBalancesAsync (client);
                if (allBalances.Count == 0)
                {
                    Log ("❌ Нет активов для продажи.");
                    return;
                }

                var sorted = allBalances.OrderByDescending (x => x.Value.TotalAmount).ToList ();

                foreach (var assetEntry in sorted)
                {
                    if (!isRunning) break;
                    string asset = assetEntry.Key;
                    decimal totalAmount = assetEntry.Value.TotalAmount;
                    if (totalAmount <= 0) continue;

                    if (asset == "USDC" || BlacklistedAssets.Contains (asset) || asset.StartsWith ("LD"))
                        continue;

                    string pair = asset + "USDC";
                    var klines = await client.GetKlinesAsync (pair, "5m", 5);
                    if (klines == null || klines.Count == 0)
                        continue;
                    decimal price = klines.Last ().Close;
                    decimal estimatedValue = totalAmount * price;
                    if (estimatedValue < 6.0m) continue;

                    decimal amountToSell = totalAmount * dynamicPercent;
                    if (amountToSell * price < 5.50m)
                        amountToSell = totalAmount;

                    decimal stepSize = await client.GetStepSizeAsync (pair);
                    decimal normalizedAmount = Math.Floor (amountToSell / stepSize) * stepSize;
                    if (normalizedAmount <= 0) continue;

                    decimal spotAmount = await client.GetAccountBalanceAsync (asset);
                    if (spotAmount < normalizedAmount)
                    {
                        decimal needToRedeem = normalizedAmount - spotAmount;
                        Log ($"🔄 Выкупаю {needToRedeem} {asset} из Earn...");
                        bool redeemed = await client.RedeemFlexibleEarnWithWaitAsync (asset, needToRedeem);
                        if (!redeemed)
                        {
                            Log ($"❌ Не удалось выкупить {asset}. Пробую следующий...");
                            continue;
                        }
                        await Task.Delay (3000);
                        spotAmount = await client.GetAccountBalanceAsync (asset);
                        if (spotAmount < normalizedAmount)
                        {
                            Log ($"⚠️ После выкупа баланс {asset} на споте ({spotAmount}) меньше требуемого ({normalizedAmount})");
                            continue;
                        }
                    }

                    Log ($"⚖️ Продажа {normalizedAmount} {asset} (шаг {stepSize}) ≈ {normalizedAmount * price:F2} USDC");
                    var order = await client.PlaceOrder (pair, "SELL", "MARKET", normalizedAmount);
                    if (order != null)
                    {
                        Log ($"✅ Успешно! Продано {normalizedAmount} {asset}, USDC пополнен.");
                        return;
                    }
                    else
                    {
                        Log ($"❌ Ошибка при продаже {asset}. Пробую следующий актив...");
                    }
                }

                Log ("❌ Не удалось продать ни один актив для пополнения USDC.");
            }
            catch (Exception ex)
            {
                Log ($"❌ Ошибка ребалансировщика: {ex.Message}");
            }
        }

        private int GetDecimalPlaces(decimal value)
        {
            string s = value.ToString (CultureInfo.InvariantCulture);
            int idx = s.IndexOf ('.');
            return idx == -1 ? 0 : s.Length - idx - 1;
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
                        if (free > 0)
                            result[asset] = (free, 0);
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
            catch (Exception ex)
            {
                Log ($"Ошибка получения балансов: {ex.Message}");
            }
            return result;
        }

        private void Log(string msg) => OnLogGenerated?.Invoke (msg);
    }
}