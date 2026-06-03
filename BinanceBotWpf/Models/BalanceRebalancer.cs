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

        public event Action<string> OnLogGenerated;

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

        public async Task AutoConvertAssetsToUsdcAsync(BinanceClient client, bool isRunning, decimal targetUsdc = 15m)
        {
            try
            {
                decimal currentUsdc = await client.GetAccountBalanceAsync ("USDC");
                if (currentUsdc >= targetUsdc) return;

                // Шаг 1: сначала пробуем конвертировать мелкую пыль в BNB
                Log ("🧹 Пробую конвертировать мелкие остатки (dust) в BNB...");
                bool dustConverted = await client.ConvertDustToBnbAsync (null);
                if (dustConverted)
                {
                    currentUsdc = await client.GetAccountBalanceAsync ("USDC");
                    if (currentUsdc >= targetUsdc) return;
                }

                // Шаг 2: собираем все активы (спот + Earn), исключая чёрный список и USDC
                var allBalances = await GetAllBalancesAsync (client);
                // Оставляем только активы, которые можно продать (стоимость > 6 USDC)
                var sellable = allBalances
                    .Where (kv => kv.Key != "USDC" && !BlacklistedAssets.Contains (kv.Key) && !kv.Key.StartsWith ("LD"))
                    .Select (async kv =>
                    {
                        string asset = kv.Key;
                        decimal totalAmount = kv.Value.TotalAmount;
                        string pair = asset + "USDC";
                        var klines = await client.GetKlinesAsync (pair, "5m", 5);
                        if (klines == null || klines.Count == 0) return (Asset: asset, Value: 0m, Amount: 0m, Price: 0m);
                        decimal price = klines.Last ().Close;
                        decimal estimatedValue = totalAmount * price;
                        return (Asset: asset, Value: estimatedValue, Amount: totalAmount, Price: price);
                    })
                    .Select (t => t.Result)
                    .Where (x => x.Value >= 6.0m)
                    .OrderByDescending (x => x.Value)
                    .ToList ();

                foreach (var item in sellable)
                {
                    if (!isRunning) break;
                    string asset = item.Asset;
                    decimal totalAmount = item.Amount;
                    decimal price = item.Price;
                    string pair = asset + "USDC";

                    decimal stepSize = await client.GetStepSizeAsync (pair);
                    decimal amountToSell = totalAmount;
                    decimal normalizedAmount = Math.Floor (amountToSell / stepSize) * stepSize;
                    if (normalizedAmount <= 0) continue;

                    // Убеждаемся, что на споте достаточно для продажи (при необходимости выкупаем из Earn)
                    decimal spotAmount = await client.GetAccountBalanceAsync (asset);
                    if (spotAmount < normalizedAmount)
                    {
                        decimal needToRedeem = normalizedAmount - spotAmount;
                        Log ($"🔄 Выкупаю {needToRedeem} {asset} из Earn...");
                        bool redeemed = await client.RedeemFlexibleEarnWithWaitAsync (asset, needToRedeem);
                        if (!redeemed)
                        {
                            Log ($"⚠️ Не удалось выкупить {asset}, пробую следующий...");
                            continue;
                        }
                        await Task.Delay (3000);
                    }

                    Log ($"⚖️ Продажа {normalizedAmount} {asset} (шаг {stepSize}) ≈ {normalizedAmount * price:F2} USDC");
                    var order = await client.PlaceOrder (pair, "SELL", "MARKET", normalizedAmount);
                    if (order != null)
                    {
                        Log ($"✅ Успешно! Продано {normalizedAmount} {asset}, USDC пополнен.");
                        currentUsdc = await client.GetAccountBalanceAsync ("USDC");
                        if (currentUsdc >= targetUsdc) return;
                    }
                    else
                    {
                        Log ($"❌ Ошибка при продаже {asset}. Пробую следующий...");
                    }
                }

                Log ("❌ Не удалось продать ни один актив для пополнения USDC (все активы слишком малы или неликвидны).");
            }
            catch (Exception ex) { Log ($"❌ Ошибка ребалансировщика: {ex.Message}"); }
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