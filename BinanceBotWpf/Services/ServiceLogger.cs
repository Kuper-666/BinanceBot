using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BinanceBotWpf.Services
{
    public class ServiceLogger : ILoggerFactory
    {
        private Action<string> _rootLogger;
        private readonly ConcurrentDictionary<string, Action<string>> _loggers = new ();

        public static ServiceLogger Instance { get; } = new ();

        public void SetRootLogger (Action<string> logger)
        {
            _rootLogger = logger;
        }

        public ILogger<T> CreateLogger<T> ()
        {
            return new WrappedLogger<T> (_rootLogger);
        }

        public ILogger CreateLogger (string categoryName)
        {
            return new WrappedLogger (categoryName, _rootLogger);
        }

        public void AddProvider (ILoggerProvider provider) { }
        public void Dispose () { }

        private class WrappedLogger : ILogger
        {
            private readonly string _category;
            private readonly Action<string> _log;

            public WrappedLogger (string category, Action<string> log)
            {
                _category = category;
                _log = log;
            }

            public IDisposable BeginScope<TState> (TState state) => null;
            public bool IsEnabled (LogLevel logLevel) => true;

            public void Log<TState> (LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                string level = logLevel switch
                {
                    LogLevel.Trace => "TRACE",
                    LogLevel.Debug => "DEBUG",
                    LogLevel.Information => "INFO",
                    LogLevel.Warning => "WARN",
                    LogLevel.Error => "ERROR",
                    LogLevel.Critical => "CRIT",
                    _ => "LOG"
                };

                string shortCategory = _category;
                int lastDot = _category.LastIndexOf ('.');
                if (lastDot >= 0 && lastDot < _category.Length - 1)
                {
                    shortCategory = _category.Substring (lastDot + 1);
                }

                string message = formatter (state, exception);
                string formatted = $"[{level}] {shortCategory}: {message}";
                if (exception != null)
                {
                    formatted += $"\n  {exception.GetType ().Name}: {exception.Message}";
                }

                _log?.Invoke (formatted);
            }
        }

        private class WrappedLogger<T> : WrappedLogger, ILogger<T>
        {
            public WrappedLogger (Action<string> log) : base (typeof (T).FullName, log) { }
        }
    }
}
