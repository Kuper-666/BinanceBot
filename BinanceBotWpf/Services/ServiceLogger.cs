using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BinanceBotWpf.Services
{
    public class ServiceLogger : ILoggerFactory
    {
        private Action<string> _rootLogger;
        private FileLogger _fileLogger;
        private readonly ConcurrentDictionary<string, Action<string>> _loggers = new ();

        public static ServiceLogger Instance { get; } = new ();

        public void SetRootLogger (Action<string> logger)
        {
            _rootLogger = logger;
        }

        public void SetFileLogger (FileLogger fileLogger)
        {
            _fileLogger = fileLogger;
        }

        public ILogger<T> CreateLogger<T> ()
        {
            return new WrappedLogger<T> (_rootLogger, _fileLogger);
        }

        public ILogger CreateLogger (string categoryName)
        {
            return new WrappedLogger (categoryName, _rootLogger, _fileLogger);
        }

        public void AddProvider (ILoggerProvider provider) { }
        public void Dispose () { }

        private class WrappedLogger : ILogger
        {
            private readonly string _category;
            private readonly Action<string> _log;
            private readonly FileLogger _fileLogger;

            public WrappedLogger (string category, Action<string> log, FileLogger fileLogger = null)
            {
                _category = category;
                _log = log;
                _fileLogger = fileLogger;
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

                if (_fileLogger != null && logLevel >= LogLevel.Error)
                {
                    _fileLogger.Log (level, shortCategory, exception != null ? $"{message}\n{exception}" : message);
                }
            }
        }

        private class WrappedLogger<T> : WrappedLogger, ILogger<T>
        {
            public WrappedLogger (Action<string> log, FileLogger fileLogger = null) : base (typeof (T).FullName, log, fileLogger) { }
        }
    }
}
