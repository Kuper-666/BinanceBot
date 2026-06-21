using BinanceBotWpf.Models;
using BinanceBotWpf.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public class RiskCalculator
    {
        private readonly BinanceClient _client;
        private readonly MainWindowViewModel _ui;
        private readonly Action<string> _logger;

        public RiskCalculator(BinanceClient client, MainWindowViewModel ui, Action<string> logger)
        {
            _client = client;
            _ui = ui;
            _logger = logger;
        }

        public Task<decimal> CalculateDynamicRiskAsync(decimal totalBalance, decimal baseRisk, decimal volatility, int aiRiskLevel = 2)
        {
            volatility = Math.Clamp (volatility, 0.005m, 0.30m);
            decimal riskMultiplier = Math.Max (0.2m, 1 - ( volatility - 0.02m ) * 10);
            
            // Учитываем ИИ уровень риска (1 = Low, 2 = Medium, 3 = High)
            if (aiRiskLevel == 1) riskMultiplier *= 1.2m; // Повышаем риск
            else if (aiRiskLevel == 3) riskMultiplier *= 0.5m; // Снижаем риск

            decimal adjustedRisk = Math.Clamp (baseRisk * riskMultiplier, 0.02m, 0.25m);
            _logger?.Invoke ($"📊 Волатильность: {volatility:P2}, ИИ Риск: {(aiRiskLevel == 1 ? "Низкий" : (aiRiskLevel == 3 ? "Высокий" : "Средний"))}, скорректированный риск: {adjustedRisk:P2}");
            return Task.FromResult (totalBalance * adjustedRisk);
        }

        public async Task<decimal> CalculateAtrAsync(string symbol, int period = 14)
        {
            return await _client.GetATRAsync (symbol, period);
        }

        public enum QuantityResult
        {
            Ok,
            InsufficientBalanceForMinNotional,
            ZeroQuantityAfterRounding,
            ExceedsAvailableBalance
        }

        /// <summary>
        /// Чистая, легко тестируемая функция расчёта количества актива для покупки
        /// с учётом минимального notional биржи и шага лота. Вынесена отдельно от
        /// ExecuteBuy, чтобы её можно было покрыть юнит-тестами без мока биржевого клиента.
        /// </summary>
        public static (decimal Quantity, QuantityResult Result) CalculatePositionQuantity(
    decimal riskAmount, decimal price, decimal stepSize, decimal minQty, decimal currentBalance, decimal minNotional = 6m)
        {
            if (price <= 0 || stepSize <= 0 || minQty <= 0)
                return (0, QuantityResult.ZeroQuantityAfterRounding);

            decimal qty = Math.Floor (( riskAmount / price ) / stepSize) * stepSize;

            if (qty < minQty)
            {
                // Минимальное количество
                qty = Math.Ceiling (minQty / stepSize) * stepSize;
            }

            if (qty * price < minNotional)
            {
                if (currentBalance < minNotional)
                    return (0, QuantityResult.InsufficientBalanceForMinNotional);
                decimal minQtyByNotional = minNotional / price;
                qty = Math.Ceiling (minQtyByNotional / stepSize) * stepSize;
            }

            if (qty <= 0)
                return (0, QuantityResult.ZeroQuantityAfterRounding);

            if (qty * price > currentBalance)
                return (0, QuantityResult.ExceedsAvailableBalance);

            return (qty, QuantityResult.Ok);
        }

        public async Task<decimal> CalculatePositionSizeAsync(string symbol, decimal riskCapital, decimal price)
        {
            decimal atr = await CalculateAtrAsync (symbol);
            if (atr <= 0) atr = price * 0.02m;
            decimal positionSize = riskCapital / atr; // размер в единицах актива
            decimal rawQty = positionSize / price;
            var (stepSize, minQty) = await _client.GetLotSizeAsync (symbol);
            decimal qty = Math.Floor (rawQty / stepSize) * stepSize;
            qty = Math.Round (qty, 8);
            return qty;
        }
    }
}
