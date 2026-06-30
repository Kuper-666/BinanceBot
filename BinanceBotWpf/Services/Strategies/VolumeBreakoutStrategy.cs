using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services.Strategies
{
    /// <summary>
    /// Стратегия пробоя объёма: если объём за последнюю свечу превышает средний
    /// за 20 свечей в N раз, и цена закрывается выше верхней полосы Боллинджера —
    /// отправляется STOP_MARKET на покупку.
    /// </summary>
    public class VolumeBreakoutStrategy : IVolumeBreakoutStrategy
    {
        private readonly BinanceClient _client;
        private readonly Action<string> _logger;

        public decimal VolumeMultiplier { get; set; } = 2.0m;
        public decimal StopLossPercent { get; set; } = 0.01m; // 1% стоп

        public VolumeBreakoutStrategy(BinanceClient client, Action<string> logger)
        {
            _client = client;
            _logger = logger;
        }

        /// <summary>
        /// Проверка условия пробоя объёма на свечах.
        /// Возвращает true если сигнал на покупку.
        /// </summary>
        public bool CheckVolumeBreakout(List<BinanceKline> klines)
        {
            if (klines == null || klines.Count < 25) return false;

            var closes = klines.Select (k => k.Close).ToList ();
            var volumes = klines.Select (k => k.Volume).ToList ();

            decimal lastVolume = volumes.Last ();
            decimal avgVolume = volumes.TakeLast (20).Average ();

            if (avgVolume <= 0) return false;

            decimal volumeRatio = lastVolume / avgVolume;
            decimal currentPrice = closes.Last ();

            var bb = TechnicalAnalysis.BollingerBands (closes, 20, 2);
            decimal bbUpper = bb.Upper.LastOrDefault () ?? currentPrice;

            bool isVolumeSpike = volumeRatio >= VolumeMultiplier;
            bool isAboveBollinger = currentPrice > bbUpper;

            if (isVolumeSpike && isAboveBollinger)
            {
                _logger?.Invoke ($"📊 Volume Breakout: объём {volumeRatio:F1}x среднего, цена {currentPrice:F4} > BB Upper {bbUpper:F4}");
                return true;
            }

            return false;
        }
    }
}
