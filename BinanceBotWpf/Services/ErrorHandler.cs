using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Централизованный обработчик ошибок с политиками повторных попыток
    /// </summary>
    public class ErrorHandler
    {
        private readonly Action<string> _logger;
        private readonly List<ErrorRecord> _errorHistory = new ();
        private readonly object _lock = new ();
        private readonly Dictionary<string, int> _errorCounts = new ();

        // Настройки повторных попыток
        private readonly int _maxRetries = 3;
        private readonly int _initialDelayMs = 1000;
        private readonly int _maxDelayMs = 10000;

        public ErrorHandler(Action<string> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Выполняет действие с автоматическими повторными попытками при ошибке
        /// </summary>
        public async Task<T> ExecuteWithRetryAsync<T>(
            Func<Task<T>> action,
            string operationName,
            int? maxRetries = null,
            bool throwOnFinalFailure = true)
        {
            int retries = maxRetries ?? _maxRetries;
            int attempt = 0;
            int delayMs = _initialDelayMs;
            Exception lastException = null;

            while (attempt <= retries)
            {
                try
                {
                    return await action ();
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    attempt++;

                    // Логируем ошибку
                    LogError (operationName, ex, attempt);

                    // Проверяем, стоит ли повторять
                    if (attempt > retries || !ShouldRetry (ex))
                    {
                        if (throwOnFinalFailure)
                            throw;
                        return default;
                    }

                    // Экспоненциальная задержка с джиттером
                    int jitter = new Random ().Next (0, 100);
                    int waitMs = Math.Min (delayMs + jitter, _maxDelayMs);

                    _logger?.Invoke ($"🔄 Повторная попытка {attempt}/{retries} через {waitMs}мс: {operationName}");
                    await Task.Delay (waitMs);
                    delayMs *= 2;
                }
            }

            if (throwOnFinalFailure && lastException != null)
                throw lastException;

            return default;
        }

        /// <summary>
        /// Выполняет действие без возврата значения с повторными попытками
        /// </summary>
        public async Task ExecuteWithRetryAsync(
            Func<Task> action,
            string operationName,
            int? maxRetries = null)
        {
            await ExecuteWithRetryAsync (async () =>
            {
                await action ();
                return true;
            }, operationName, maxRetries);
        }

        /// <summary>
        /// Определяет, стоит ли повторять попытку при данной ошибке
        /// </summary>
        private bool ShouldRetry(Exception ex)
        {
            string message = ex.Message.ToLower ();

            // Повторяем при временных ошибках
            if (message.Contains ("timeout") ||
                message.Contains ("connection") ||
                message.Contains ("network") ||
                message.Contains ("rate limit") ||
                message.Contains ("-1021") || // Timestamp
                message.Contains ("-1003") || // Rate limit
                message.Contains ("-2015"))    // Invalid API key
            {
                return true;
            }

            // Не повторяем при ошибках авторизации или неверных параметрах
            if (message.Contains ("-1022") || // Invalid signature
                message.Contains ("-2014") || // Invalid symbol
                message.Contains ("-1102"))   // Mandatory parameter
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Логирует ошибку в историю
        /// </summary>
        private void LogError(string operation, Exception ex, int attempt)
        {
            string errorType = ex.GetType ().Name;
            string errorMsg = $"[{attempt}] {operation}: {errorType} - {ex.Message}";

            lock (_lock)
            {
                // Увеличиваем счётчик ошибок
                if (_errorCounts.ContainsKey (operation))
                    _errorCounts[operation]++;
                else
                    _errorCounts[operation] = 1;

                // Добавляем в историю
                _errorHistory.Insert (0, new ErrorRecord
                {
                    Timestamp = DateTime.Now,
                    Operation = operation,
                    Message = ex.Message,
                    Attempt = attempt,
                    IsResolved = false
                });

                // Ограничиваем историю 200 записями
                if (_errorHistory.Count > 200)
                    _errorHistory.RemoveAt (_errorHistory.Count - 1);
            }

            _logger?.Invoke ($"❌ {errorMsg}");
        }

        /// <summary>
        /// Отмечает операцию как исправленную
        /// </summary>
        public void MarkResolved(string operation)
        {
            lock (_lock)
            {
                if (_errorCounts.ContainsKey (operation))
                    _errorCounts[operation] = 0;

                var unresolved = _errorHistory.Where (e => e.Operation == operation && !e.IsResolved).ToList ();
                foreach (var error in unresolved)
                    error.IsResolved = true;
            }
        }

        /// <summary>
        /// Возвращает статистику ошибок
        /// </summary>
        public Dictionary<string, int> GetErrorStatistics()
        {
            lock (_lock)
            {
                return new Dictionary<string, int> (_errorCounts);
            }
        }

        /// <summary>
        /// Возвращает последние N ошибок
        /// </summary>
        public List<ErrorRecord> GetRecentErrors(int count = 20)
        {
            lock (_lock)
            {
                return _errorHistory.Take (count).ToList ();
            }
        }

        /// <summary>
        /// Очищает историю ошибок
        /// </summary>
        public void ClearHistory()
        {
            lock (_lock)
            {
                _errorHistory.Clear ();
                _errorCounts.Clear ();
            }
        }

        /// <summary>
        /// Запись об ошибке
        /// </summary>
        public class ErrorRecord
        {
            public DateTime Timestamp { get; set; }
            public string Operation { get; set; }
            public string Message { get; set; }
            public int Attempt { get; set; }
            public bool IsResolved { get; set; }

            public override string ToString()
            {
                return $"[{Timestamp:HH:mm:ss}] {Operation}: {Message} (попытка {Attempt}){( IsResolved ? " ✅" : "" )}";
            }
        }
    }
}