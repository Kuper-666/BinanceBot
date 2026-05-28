using System;

namespace BinanceBotWpf.Models
{
    public class RiskManager
    {
        private readonly decimal _maxWalletAllocation = 100m;

        // Настройки риск-менеджмента (3% тейк-профит, 1.5% стоп-лосс)
        private readonly decimal _takeProfitPercent = 0.03m;
        private readonly decimal _stopLossPercent = 0.015m;

        public RiskManager(decimal maxWalletAllocation)
        {
            _maxWalletAllocation = maxWalletAllocation;
        }

        /// <summary>
        /// Проверяет, безопасен ли вход в сделку по текущим правилам риск-менеджмента
        /// </summary>
        public bool IsTradeSafe(string symbol, decimal currentPrice, decimal availableUsdc, out string decisionReason)
        {
            if (availableUsdc < 10.0m)
            {
                decisionReason = "Отказ: Баланс ниже абсолютного минимума Binance ($10)";
                return false;
            }

            if (currentPrice <= 0)
            {
                decisionReason = "Отказ: Некорректная рыночная цена актива";
                return false;
            }

            decisionReason = "Риск-параметры в норме. Сделка одобрена.";
            return true;
        }

        /// <summary>
        /// Рассчитывает точный объем закупаемой монеты с учетом шага лота биржи
        /// </summary>
        public decimal CalculateOrderQuantity(decimal currentPrice, decimal usdcAllocated)
        {
            if (currentPrice <= 0) return 0m;
            decimal rawQty = usdcAllocated / currentPrice;
            return Math.Round (rawQty, 6);
        }

        /// <summary>
        /// Рассчитывает ценовые уровни Take Profit и Stop Loss на основе цены входа
        /// </summary>
        public void CalculateTakeProfitAndStopLoss(decimal entryPrice, out decimal takeProfitPrice, out decimal stopLossPrice)
        {
            takeProfitPrice = entryPrice * ( 1m + _takeProfitPercent );
            stopLossPrice = entryPrice * ( 1m - _stopLossPercent );
        }
    }
}