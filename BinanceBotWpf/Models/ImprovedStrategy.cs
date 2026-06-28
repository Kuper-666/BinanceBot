using System;
using System.Collections.Generic;
using System.Linq;

namespace BinanceBotWpf.Models
{
    public class ImprovedStrategy
    {
        /// <summary>
        /// Комбинированный сигнал на основе RSI, Bollinger Bands, MACD и фильтра тренда.
        /// </summary>
        public static TradeAction GetSignal(
            List<decimal> closes,
            List<decimal> highs,
            List<decimal> lows,
            List<decimal> volumes,
            decimal currentPrice,
            out string reason)
        {
            reason = "Нет сигнала";
            if (closes.Count < 50 || highs.Count < 50 || lows.Count < 50 || volumes.Count < 20) return TradeAction.Hold;

            // 1. Долгосрочный тренд (50-период SMA) – фильтр направления
            decimal longSma = closes.Skip (closes.Count - 50).Average ();
            bool isUptrend = currentPrice > longSma;

            // 2. RSI (14)
            decimal rsi = TechnicalAnalysis.RSI (closes, 14).LastOrDefault () ?? 50;

            // 3. Bollinger Bands (20,2)
            var bb = TechnicalAnalysis.BollingerBands (closes, 20, 2);
            decimal? bbUpper = bb.Upper.LastOrDefault ();
            decimal? bbLower = bb.Lower.LastOrDefault ();
            decimal? bbMiddle = bb.Middle.LastOrDefault ();
            if (!bbUpper.HasValue || !bbLower.HasValue) return TradeAction.Hold;

            // 4. MACD (12,26,9)
            var macd = TechnicalAnalysis.MACD (closes, 12, 26, 9);
            decimal? macdHist = macd.Histogram.LastOrDefault ();
            decimal? macdPrev = macd.Histogram.Count > 1 ? macd.Histogram[^2] : 0;

            // 5. Объём (должен быть выше среднего)
            decimal avgVolume = volumes.Skip (volumes.Count - 20).Average ();
            decimal currentVolume = volumes.Last ();
            bool highVolume = currentVolume > avgVolume * 1.2m;

            // 6. ATR для динамического стопа (не используется для сигнала, но для риска)
            // Условия для BUY (лонг)
            bool buySignal = false;
            bool sellSignal = false;

            // Покупка: oversold + цена у нижней полосы + MACD начинает расти + uptrend (или нейтральный)
            if (rsi < 30 && currentPrice <= bbLower.Value && macdHist > -0.001m && macdPrev <= macdHist && highVolume)
            {
                buySignal = true;
                reason = $"RSI={rsi:F0} ниже 30, цена у нижней BB, MACD растёт, объём высокий";
            }
            // Продажа: overbought + цена у верхней полосы + MACD падает + downtrend
            else if (rsi > 70 && currentPrice >= bbUpper.Value && macdHist < 0.001m && macdPrev >= macdHist && highVolume && !isUptrend)
            {
                sellSignal = true;
                reason = $"RSI={rsi:F0} выше 70, цена у верхней BB, MACD падает, объём высокий, не аптренд";
            }

            if (buySignal) return TradeAction.Buy;
            if (sellSignal) return TradeAction.Sell;
            return TradeAction.Hold;
        }
    }
}