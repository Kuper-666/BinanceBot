using System;
using System.IO;
using System.Threading;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services
{
    public class DataLogger
    {
        private readonly string _tradesCsvPath;
        private readonly string _featuresCsvPath;
        private readonly object _tradesLock = new object ();
        private readonly object _featuresLock = new object ();
        private readonly Action<string> _logger;

        public DataLogger(string logsDir, Action<string> logger)
        {
            _logger = logger;
            if (!Directory.Exists (logsDir)) Directory.CreateDirectory (logsDir);
            _tradesCsvPath = Path.Combine (logsDir, $"trades_{DateTime.Now:yyyyMMdd}.csv");
            if (!File.Exists (_tradesCsvPath))
                File.WriteAllText (_tradesCsvPath, "DateTime,Symbol,Action,Price,Quantity,PnL,PnLPercent,EntryPrice,ExitPrice,Reason,Duration\n");
            _featuresCsvPath = Path.Combine (logsDir, "features.csv");
            if (!File.Exists (_featuresCsvPath))
                File.WriteAllText (_featuresCsvPath, "Timestamp,Symbol,Price,FastSma,SlowSma,Rsi,Volume,Volatility\n");
        }

        public void LogTrade(TradeLog trade)
        {
            lock (_tradesLock)
            {
                try
                {
                    string line = $"{trade.CloseTime:yyyy-MM-dd HH:mm:ss},{trade.Symbol},{trade.Action},{trade.ExitPrice:F4},{trade.Quantity:F6},{trade.PnL:F2},{trade.PnLPercent:F2},{trade.EntryPrice:F4},{trade.ExitPrice:F4},{trade.Reason},{trade.Duration}\n";
                    File.AppendAllText (_tradesCsvPath, line);
                    _logger?.Invoke ($"📝 Сделка записана в CSV: {trade.Symbol} {trade.Action} PnL={trade.PnL:F2}");
                }
                catch (Exception ex) { _logger?.Invoke ($"❌ Ошибка записи CSV: {ex.Message}"); }
            }
        }

        public void LogFeatures(string symbol, decimal price, decimal fastSma, decimal slowSma, decimal rsi, decimal volume, decimal volatility)
        {
            lock (_featuresLock)
            {
                try
                {
                    string line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss},{symbol},{price:F4},{fastSma:F4},{slowSma:F4},{rsi:F2},{volume:F4},{volatility:F4}\n";
                    File.AppendAllText (_featuresCsvPath, line);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"Features CSV error: {ex.Message}"); }
            }
        }
    }
}