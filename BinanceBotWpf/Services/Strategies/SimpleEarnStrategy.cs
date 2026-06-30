using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services.Strategies
{
    public class SimpleEarnStrategy : ISimpleEarnStrategy
    {
        private readonly BinanceClient _client;
        private readonly Action<string> _logger;

        public decimal MinBalanceForLock { get; set; } = 100m;
        public decimal LockPercent { get; set; } = 0.7m;
        public decimal ReservePercent { get; set; } = 0.3m;

        public SimpleEarnStrategy(BinanceClient client, Action<string> logger)
        {
            _client = client;
            _logger = logger;
        }

        public async Task OptimizeEarnAsync()
        {
            try
            {
                var earnPositions = await _client.GetFlexibleEarnBalanceAsync ();
                decimal totalEarn = 0;
                decimal totalSpot = await _client.GetAccountBalanceAsync ("USDC");

                if (earnPositions != null)
                {
                    foreach (var pos in earnPositions)
                    {
                        if (pos["asset"]?.ToString () == "USDC")
                        {
                            totalEarn = decimal.Parse (pos["totalAmount"]?.ToString () ?? "0",
                                System.Globalization.CultureInfo.InvariantCulture);
                            break;
                        }
                    }
                }

                decimal total = totalSpot + totalEarn;
                _logger?.Invoke ($"💰 Simple Earn: Spot={totalSpot:F2}, Earn={totalEarn:F2}, Total={total:F2} USDC");

                if (total < MinBalanceForLock)
                {
                    _logger?.Invoke ($"ℹ️ Баланс {total:F2} USDC < минимума {MinBalanceForLock}, Earn оптимизация пропущена");
                    return;
                }

                decimal targetEarn = total * LockPercent;
                decimal diff = targetEarn - totalEarn;

                if (diff > 5m)
                {
                    _logger?.Invoke ($"📈 Переводим {diff:F2} USDC в Simple Earn (flexible)");
                }
                else if (diff < -10m && totalEarn > MinBalanceForLock)
                {
                    decimal redeem = Math.Min (-diff, totalEarn - MinBalanceForLock);
                    _logger?.Invoke ($"📉 Выводим {redeem:F2} USDC из Simple Earn на спот");
                }
                else
                {
                    _logger?.Invoke ($"✅ Simple Earn баланс оптимален ({totalEarn:F2} USDC)");
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Ошибка Simple Earn оптимизации: {ex.Message}");
            }
        }
    }
}
