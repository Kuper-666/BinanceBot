using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Адаптивный ограничитель частоты запросов для Binance API.
    /// </summary>
    public class RateLimiter
    {
        private readonly ConcurrentDictionary<string, (int Weight, DateTime Expiry)> _rateLimits = new ();
        private readonly SemaphoreSlim _semaphore;
        private readonly int _maxRequestsPerSecond;
        private double _weightMultiplier; // убрано readonly
        private DateTime _lastRequestTime; // убрано readonly
        private readonly object _lock = new ();

        public RateLimiter(int maxRequestsPerSecond = 10, double initialWeightMultiplier = 1.0)
        {
            _maxRequestsPerSecond = maxRequestsPerSecond;
            _weightMultiplier = initialWeightMultiplier;
            _semaphore = new SemaphoreSlim (maxRequestsPerSecond, maxRequestsPerSecond);
            _lastRequestTime = DateTime.UtcNow;
        }

        /// <summary>
        /// Асинхронное ожидание возможности выполнить запрос с указанным весом.
        /// </summary>
        public async Task WaitForSlotAsync(int weight = 1)
        {
            int adjustedWeight = (int)Math.Ceiling (weight * _weightMultiplier);
            for (int i = 0; i < adjustedWeight; i++)
                await _semaphore.WaitAsync ();

            // Контроль времени между запросами (rate limiting по времени)
            TimeSpan delay;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                var elapsed = now - _lastRequestTime;
                var minInterval = TimeSpan.FromMilliseconds (1000.0 / _maxRequestsPerSecond);
                if (elapsed < minInterval)
                {
                    delay = minInterval - elapsed;
                }
                else
                {
                    delay = TimeSpan.Zero;
                }
                _lastRequestTime = DateTime.UtcNow;
            }

            if (delay > TimeSpan.Zero)
                await Task.Delay (delay);
        }

        /// <summary>
        /// Освободить слоты после выполнения запроса.
        /// </summary>
        public void ReleaseSlot(int weight = 1)
        {
            int adjustedWeight = (int)Math.Ceiling (weight * _weightMultiplier);
            for (int i = 0; i < adjustedWeight; i++)
                _semaphore.Release ();
        }

        /// <summary>
        /// Обновить множитель веса на основе ответа сервера (заголовки X-MBX-USED-WEIGHT-1m).
        /// Логирует приближение к лимиту.
        /// </summary>
        public void UpdateWeightMultiplier(int usedWeight, int limitWeight = 1200)
        {
            if (limitWeight <= 0) return;
            double ratio = (double)usedWeight / limitWeight;

            if (ratio > 0.8)
            {
                System.Diagnostics.Debug.WriteLine ($"⚠️ RateLimiter: использовано {usedWeight}/{limitWeight} ({ratio:P0}) — приближение к лимиту!");
            }

            if (ratio > 0.9)
                _weightMultiplier = Math.Min (3.0, _weightMultiplier * 1.2);
            else if (ratio < 0.5)
                _weightMultiplier = Math.Max (0.5, _weightMultiplier * 0.9);
        }
    }
}