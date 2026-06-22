using System;
using System.Collections.Generic;
using System.Linq;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// DCA (Dollar Cost Averaging) стратегия по волатильности:
    /// покупает при отклонении цены от максимума на 2 * ATR.
    /// Отключается при падении > 30% от максимума (ловля падающего ножа).
    /// </summary>
    public class DCAStrategy
    {
        private readonly BinanceClient _client;
        private readonly Action<string> _logger;

        public int LookbackDays { get; set; } = 14;
        public decimal MaxDrawdownPercent { get; set; } = 0.30m; // 30% — отключаем DCA
        public decimal AtrMultiplier { get; set; } = 2.0m;      // 2 * ATR от максимума
        public decimal BuyPercent { get; set; } = 0.10m;        // 10% портфеля

        private DateTime _lastCheckTime = DateTime.MinValue;

        public DCAStrategy(BinanceClient client, Action<string> logger)
        {
            _client = client;
            _logger = logger;
        }

        /// <summary>
        /// Проверяет, нужно ли покупать по DCA.
        /// Возвращает true если условие выполнено.
        /// </summary>
        public bool ShouldBuy(string symbol, List<BinanceKline> klines, decimal balance)
        {
            if (klines == null || klines.Count < LookbackDays + 5) return false;

            // Проверяем интервал (раз в N дней)
            if ((DateTime.UtcNow - _lastCheckTime).TotalDays < 1) return false;

            var closes = klines.Select (k => k.Close).ToList ();
            var highs = klines.Select (k => k.High).ToList ();
            var lows = klines.Select (k => k.Low).ToList ();

            decimal currentPrice = closes.Last ();
            decimal maxPrice = closes.TakeLast (LookbackDays).Max ();
            decimal drawdownFromMax = (maxPrice - currentPrice) / maxPrice;

            // Защита: если упали > 30% — не покупаем (ловля падающего ножа)
            if (drawdownFromMax > MaxDrawdownPercent)
            {
                _logger?.Invoke ($"⚠️ DCA {symbol}: отклонение {drawdownFromMax:P1} > {MaxDrawdownPercent:P0}, покупка отключена");
                return false;
            }

            // Рассчитываем ATR
            var atrList = TechnicalAnalysis.ATR (highs, lows, closes, 14);
            decimal atr = atrList.LastOrDefault () ?? currentPrice * 0.02m;

            // Условие: цена упала на 2 * ATR от максимума
            decimal threshold = maxPrice - atr * AtrMultiplier;

            if (currentPrice <= threshold)
            {
                _lastCheckTime = DateTime.UtcNow;
                _logger?.Invoke ($"📊 DCA {symbol}: цена {currentPrice:F4} <= порог {threshold:F4} (макс {maxPrice:F4} - {AtrMultiplier}x ATR)");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Рассчитывает размер покупки DCA в USDC
        /// </summary>
        public decimal CalculateBuyAmount(decimal balance)
        {
            return balance * BuyPercent;
        }
    }
}
