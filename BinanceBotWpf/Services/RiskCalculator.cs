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

            // 1. Сначала проверяем, хватит ли баланса хотя бы на минимальный нотионал
            if (currentBalance < minNotional)
                return (0, QuantityResult.InsufficientBalanceForMinNotional);

            // 2. Расчёт количества с учётом шага и минимального количества
            decimal qty = Math.Floor (( riskAmount / price ) / stepSize) * stepSize;

            if (qty < minQty)
            {
                qty = Math.Ceiling (minQty / stepSize) * stepSize;
            }

            // 3. Если сумма меньше минимального нотионала — поднимаем до него
            if (qty * price < minNotional)
            {
                decimal minQtyByNotional = minNotional / price;
                qty = Math.Ceiling (minQtyByNotional / stepSize) * stepSize;
            }

            if (qty <= 0)
                return (0, QuantityResult.ZeroQuantityAfterRounding);

            // 4. Финальная проверка: не превышает ли сумма доступный баланс
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

        /// <summary>
        /// Единый расчёт SL/TP: сначала пытаемся через ATR, затем fallback на фиксированный процент.
        /// Возвращает (StopLossPrice, TakeProfitPrice, StopLossPercent).
        /// </summary>
        public async Task<(decimal SlPrice, decimal TpPrice, decimal SlPercent)> CalculateStopLossAndTakeProfitAsync(
            string symbol, decimal entryPrice, decimal riskRewardRatio, decimal fallbackSlPercent)
        {
            decimal atr = 0;
            try { atr = await CalculateAtrAsync (symbol); } catch { }

            decimal slDistance;
            if (atr > 0 && atr / entryPrice < 0.15m)
            {
                slDistance = atr * 1.5m;
            }
            else
            {
                slDistance = entryPrice * fallbackSlPercent;
            }

            decimal slPrice = entryPrice - slDistance;
            decimal tpPrice = entryPrice + slDistance * riskRewardRatio;
            decimal slPct = slDistance / entryPrice;

            return (slPrice, tpPrice, slPct);
        }

        /// <summary>
        /// Расчёт суммы риска: Balance * RiskPerTradePercent (жёсткий лимит 1%)
        /// </summary>
        public static decimal CalculateRiskAmount(decimal balance, decimal riskPerTradePercent)
        {
            return balance * Math.Clamp (riskPerTradePercent, 0.005m, 0.02m);
        }
    }
}
