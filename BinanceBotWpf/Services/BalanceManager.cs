using BinanceBotWpf.Models;
using System;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public class BalanceManager
    {
        private readonly BinanceClient _client;
        private readonly EarnManager _earn;
        private readonly BalanceRebalancer _rebalancer;
        private readonly Action<string> _logger;

        public BalanceManager(BinanceClient client, EarnManager earn, BalanceRebalancer rebalancer, Action<string> logger)
        {
            _client = client;
            _earn = earn;
            _rebalancer = rebalancer;
            _logger = logger;
        }

        public async Task<decimal> EnsureTradingBalanceAsync(decimal required, bool silent = false)
        {
            decimal current = await _client.GetAccountBalanceAsync ("USDC");
            if (current >= required) return current;

            decimal need = required - current;
            if (need < 1.0m)
            {
                if (!silent) _logger?.Invoke ($"⚠️ Сумма выкупа {need:F2} USDC меньше 1, пропускаем.");
                return current;
            }

            _logger?.Invoke ($"🔄 Выкупаю {need:F2} USDC из Earn...");
            bool success = await _earn.EnsureLiquidBalanceAsync ("USDC", need, _client);
            if (success)
            {
                current = await _client.GetAccountBalanceAsync ("USDC");
                _logger?.Invoke ($"✅ Выкуп подтверждён. Баланс USDC: {current:F2}");
                return current;
            }
            _logger?.Invoke ($"⚠️ Не удалось выкупить USDC");
            return current;
        }

        public async Task AutoRebalanceAsync()
        {
            await _rebalancer.AutoConvertAssetsToUsdcAsync (_client, true);
        }
    }
}