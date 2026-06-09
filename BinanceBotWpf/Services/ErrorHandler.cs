using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Polly;
using Polly.Retry;

namespace BinanceBotWpf.Services
{
    public class ErrorHandler
    {
        private readonly Action<string> _logger;
        private Func<string, Task> _telegramNotifier;
        private readonly ConcurrentDictionary<string, DateTime> _lastErrorLog = new ();
        private readonly TimeSpan _errorThrottle = TimeSpan.FromSeconds (30);

        private readonly AsyncRetryPolicy _retryPolicy;
        private readonly AsyncRetryPolicy _exponentialRetryPolicy;

        public ErrorHandler(Action<string> logger, Func<string, Task> telegramNotifier = null)
        {
            _logger = logger;
            _telegramNotifier = telegramNotifier;

            _retryPolicy = Policy
                .Handle<Exception> ()
                .WaitAndRetryAsync (
                    3,
                    attempt => TimeSpan.FromSeconds (Math.Pow (2, attempt - 1)),
                    onRetry: (exception, timeSpan, attempt, context) =>
                    {
                        LogError ($"⚠️ Повторная попытка {attempt}/3 через {timeSpan.TotalSeconds:F0}с: {exception.Message}");
                    });

            _exponentialRetryPolicy = Policy
                .Handle<Exception> ()
                .WaitAndRetryAsync (
                    5,
                    attempt => TimeSpan.FromSeconds (Math.Min (Math.Pow (2, attempt - 1), 60)),
                    onRetry: (exception, timeSpan, attempt, context) =>
                    {
                        LogError ($"⚠️ Критическая операция: повтор {attempt}/5 через {timeSpan.TotalSeconds:F0}с: {exception.Message}");
                    });
        }

        public void SetTelegramNotifier(Func<string, Task> notifier)
        {
            _telegramNotifier = notifier;
        }

        public async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, string operationName = "операция")
        {
            try
            {
                return await _retryPolicy.ExecuteAsync (action);
            }
            catch (Exception ex)
            {
                await NotifyErrorAsync (operationName, ex);
                throw;
            }
        }

        public async Task ExecuteWithRetryAsync(Func<Task> action, string operationName = "операция")
        {
            try
            {
                await _retryPolicy.ExecuteAsync (action);
            }
            catch (Exception ex)
            {
                await NotifyErrorAsync (operationName, ex);
                throw;
            }
        }

        public async Task<T> ExecuteWithExponentialRetryAsync<T>(Func<Task<T>> action, string operationName = "операция")
        {
            try
            {
                return await _exponentialRetryPolicy.ExecuteAsync (action);
            }
            catch (Exception ex)
            {
                await NotifyErrorAsync (operationName, ex);
                throw;
            }
        }

        private void LogError(string message)
        {
            _logger?.Invoke (message);
        }

        private async Task NotifyErrorAsync(string operationName, Exception ex)
        {
            string key = operationName;
            if (_lastErrorLog.TryGetValue (key, out var lastTime) && DateTime.UtcNow - lastTime < _errorThrottle)
                return;

            _lastErrorLog[key] = DateTime.UtcNow;
            string errorMsg = $"❌ Ошибка в {operationName}: {ex.Message}";
            LogError (errorMsg);

            if (_telegramNotifier != null)
                await _telegramNotifier (errorMsg);
        }
    }
}