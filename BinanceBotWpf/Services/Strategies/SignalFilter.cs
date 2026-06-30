using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services.Strategies
{
    /// <summary>
    /// Фильтрация торговых сигналов с помощью дополнительных индикаторов
    /// </summary>
    public class SignalFilter : ISignalFilter
    {
        private readonly Action<string> _logger;

        // Параметры фильтрации
        public int MinRsiForBuy { get; set; } = 30;
        public int MaxRsiForSell { get; set; } = 70;
        public decimal MinVolumeRatio { get; set; } = 0.8m;
        public decimal MinAdxForTrend { get; set; } = 25;
        public bool RequireTrendConfirm { get; set; } = true;
        public bool RequireMacdConfirm { get; set; } = true;

        public SignalFilter(Action<string> logger)
        {
            _logger = logger;
        }

        // Публичный метод для установки логгера
        public void SetLogger(Action<string> logger)
        {
            // Поле _logger только для чтения, нельзя изменить после конструктора
            // Этот метод оставлен для совместимости, но ничего не делает
        }

        /// <summary>
        /// Проверяет, стоит ли покупать по всем фильтрам
        /// </summary>
        public Task<bool> ShouldBuyAsync(
            string symbol,
            decimal price,
            decimal rsi,
            decimal fastSma,
            decimal slowSma,
            decimal volume,
            decimal avgVolume,
            decimal macdHistogram,
            decimal prevMacdHistogram,
            List<decimal> closes,
            List<decimal> highs,
            List<decimal> lows)
        {
            // 1. Объёмный фильтр
            bool volumeOk = volume > avgVolume * MinVolumeRatio;
            if (!volumeOk)
            {
                _logger?.Invoke ($"📊 {symbol}: объём {volume:F0} < {avgVolume * MinVolumeRatio:F0} (мин. {MinVolumeRatio:P0})");
                return Task.FromResult (false);
            }

            // 2. RSI фильтр — зона покупки: от перепроданности до перекупленности
            bool rsiOk = rsi >= MinRsiForBuy && rsi <= MaxRsiForSell;
            if (!rsiOk)
            {
                _logger?.Invoke ($"📊 {symbol}: RSI {rsi:F1} вне диапазона [{MinRsiForBuy}..{MaxRsiForSell}]");
                return Task.FromResult (false);
            }

            // 3. Трендовый фильтр (цена выше SMA50)
            bool trendOk = true;
            if (RequireTrendConfirm && closes.Count >= 50)
            {
                decimal sma50 = closes.Skip (closes.Count - 50).Average ();
                trendOk = price > sma50;
                if (!trendOk)
                {
                    _logger?.Invoke ($"📊 {symbol}: цена ниже SMA50, тренд нисходящий");
                }
            }

            // 4. MACD фильтр (гистограмма растёт)
            bool macdOk = true;
            if (RequireMacdConfirm)
            {
                macdOk = macdHistogram > prevMacdHistogram && macdHistogram > 0;
                if (!macdOk)
                {
                    _logger?.Invoke ($"📊 {symbol}: MACD гистограмма не растёт ({macdHistogram:F4})");
                }
            }

            // 5. SMA сигнал (быстрый выше медленного)
            bool smaOk = fastSma > slowSma;
            if (!smaOk)
            {
                _logger?.Invoke ($"📊 {symbol}: SMA {fastSma:F2} < {slowSma:F2}");
            }

            bool result = volumeOk && rsiOk && trendOk && macdOk && smaOk;

            if (result)
                _logger?.Invoke ($"✅ {symbol}: все фильтры пройдены");

            return Task.FromResult (result);
        }

        /// <summary>
        /// Проверяет, стоит ли продавать
        /// </summary>
        public bool ShouldSell(
            string symbol,
            decimal price,
            decimal entryPrice,
            decimal rsi,
            decimal fastSma,
            decimal slowSma,
            decimal macdHistogram,
            decimal prevMacdHistogram,
            DateTime openTime,
            TimeSpan maxHoldTime)
        {
            // 1. Стоп-лосс (защита)
            decimal stopLoss = entryPrice * 0.98m; // -2%
            if (price <= stopLoss)
            {
                _logger?.Invoke ($"🔴 {symbol}: стоп-лосс {price:F4} <= {stopLoss:F4}");
                return true;
            }

            // 2. RSI перекупленность
            if (rsi > MaxRsiForSell)
            {
                _logger?.Invoke ($"📊 {symbol}: RSI {rsi:F1} > {MaxRsiForSell} (перекуплен)");
                return true;
            }

            // 3. Смертельный крест SMA
            if (fastSma < slowSma)
            {
                _logger?.Invoke ($"📊 {symbol}: смертельный крест SMA {fastSma:F2} < {slowSma:F2}");
                return true;
            }

            // 4. MACD разворот вниз
            if (macdHistogram < prevMacdHistogram && macdHistogram < 0)
            {
                _logger?.Invoke ($"📊 {symbol}: MACD разворот вниз");
                return true;
            }

            // 5. Максимальное время удержания
            if (DateTime.UtcNow - openTime > maxHoldTime)
            {
                _logger?.Invoke ($"⏰ {symbol}: превышено время удержания ({maxHoldTime.TotalHours}ч)");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Расчёт динамического размера позиции на основе волатильности
        /// </summary>
        public decimal CalculateDynamicPositionSize(decimal totalBalance, decimal price, decimal atr, decimal maxRiskPercent = 0.02m)
        {
            if (price <= 0 || atr <= 0) return 10m; // базовый размер

            // Риск на сделку: 2% от баланса
            decimal riskAmount = totalBalance * maxRiskPercent;

            // Размер позиции на основе ATR (чем выше волатильность, тем меньше позиция)
            decimal atrPercent = atr / price;
            decimal volatilityMultiplier = Math.Max (0.5m, Math.Min (1.5m, 0.02m / atrPercent));

            decimal positionUsdc = riskAmount * 100 * volatilityMultiplier;
            positionUsdc = Math.Min (positionUsdc, totalBalance * 0.1m); // не более 10% баланса

            decimal quantity = positionUsdc / price;
            return quantity;
        }

        /// <summary>
        /// Проверка, разрешена ли торговля в текущее время
        /// </summary>
        public bool IsTradingTime(TradingSettings settings)
        {
            return settings.CanTradeNow ();
        }

        /// <summary>
        /// Проверка новостного фона (заглушка - можно расширить)
        /// </summary>
        public bool IsNewsImpactLow()
        {
            // TODO: добавить проверку экономического календаря
            return true;
        }
    }
}
