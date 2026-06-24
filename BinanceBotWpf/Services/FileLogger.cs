using System;
using System.IO;
using System.Threading;

namespace BinanceBotWpf.Services
{
    public class FileLogger : IDisposable
    {
        private readonly string _logDir;
        private readonly object _lock = new ();
        private StreamWriter _writer;
        private DateTime _currentFileDate;
        private bool _disposed;

        public FileLogger (string logDir = null)
        {
            _logDir = logDir ?? Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Logs");
            if (!Directory.Exists (_logDir))
                Directory.CreateDirectory (_logDir);
            RotateIfNeeded ();
        }

        public void Log (string level, string source, string message)
        {
            if (_disposed) return;
            lock (_lock)
            {
                try
                {
                    RotateIfNeeded ();
                    string timestamp = DateTime.Now.ToString ("yyyy-MM-dd HH:mm:ss.fff");
                    _writer?.WriteLine ($"[{timestamp}] [{level}] [{source}] {message}");
                    _writer?.Flush ();
                }
                catch { }
            }
        }

        public void Info (string source, string message) => Log ("INFO", source, message);
        public void Warn (string source, string message) => Log ("WARN", source, message);
        public void Error (string source, string message) => Log ("ERROR", source, message);

        private void RotateIfNeeded ()
        {
            DateTime today = DateTime.UtcNow.Date;
            if (_writer != null && _currentFileDate == today) return;

            try { _writer?.Flush (); _writer?.Dispose (); } catch { }

            _currentFileDate = today;
            string path = Path.Combine (_logDir, $"bot_{today:yyyyMMdd}.log");
            _writer = new StreamWriter (path, append: true) { AutoFlush = true };

            CleanupOldLogs ();
        }

        private void CleanupOldLogs ()
        {
            try
            {
                foreach (string file in Directory.GetFiles (_logDir, "bot_*.log"))
                {
                    FileInfo fi = new FileInfo (file);
                    if (fi.LastWriteTimeUtc < DateTime.UtcNow.AddDays (-30))
                    {
                        fi.Delete ();
                    }
                }
            }
            catch { }
        }

        public void Dispose ()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_lock)
            {
                try { _writer?.Flush (); _writer?.Dispose (); } catch { }
            }
        }
    }
}
