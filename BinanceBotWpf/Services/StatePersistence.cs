using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Saves and loads trading state to/from JSON file.
    /// Thread-safe via lock for concurrent save requests.
    /// </summary>
    public class StatePersistence : IDisposable
    {
        private readonly string _filePath;
        private readonly object _lock = new ();
        private Timer _autoSaveTimer;
        private Func<TradingState> _stateProvider;
        private Action<TradingState> _stateRestorer;
        private Action<string> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new ()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public StatePersistence(string dataDir, Action<string> logger = null)
        {
            _logger = logger;
            _filePath = Path.Combine(dataDir, "trading_state.json");
        }

        /// <summary>
        /// Register callbacks for auto-save and restore.
        /// </summary>
        public void Register(Func<TradingState>stateProvider, Action<TradingState> stateRestorer)
        {
            _stateProvider = stateProvider;
            _stateRestorer = stateRestorer;
        }

        /// <summary>
        /// Start periodic auto-save every 5 minutes.
        /// </summary>
        public void StartAutoSave()
        {
            _autoSaveTimer?.Dispose();
            _autoSaveTimer = new Timer(_ => Save(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        /// <summary>
        /// Save current state to file.
        /// </summary>
        public void Save()
        {
            if (_stateProvider == null) return;

            try
            {
                TradingState state = _stateProvider();
                state.SavedAt = DateTime.UtcNow;

                // Prune old cooldowns (older than 1 hour)
                DateTime cutoff = DateTime.UtcNow.AddHours(-1);
                state.RecentTradeTimes.RemoveAll(t => t < cutoff);
                foreach (var kvp in state.LastBuyTime)
                {
                    if (kvp.Value < cutoff)
                        state.LastBuyTime.Remove(kvp.Key);
                }

                // Limit history sizes
                if (state.TradesHistory.Count > 500)
                    state.TradesHistory = state.TradesHistory.GetRange(state.TradesHistory.Count - 500, 500);
                if (state.EquityHistory.Count > 200)
                    state.EquityHistory = state.EquityHistory.GetRange(state.EquityHistory.Count - 200, 200);
                if (state.PnlHistory.Count > 200)
                    state.PnlHistory = state.PnlHistory.GetRange(state.PnlHistory.Count - 200, 200);

                string json = JsonSerializer.Serialize(state, JsonOptions);

                lock (_lock)
                {
                    string dir = Path.GetDirectoryName(_filePath);
                    if (!Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    // Write to temp file first, then rename (atomic)
                    string tempPath = _filePath + ".tmp";
                    File.WriteAllText(tempPath, json);
                    if (File.Exists(_filePath))
                        File.Delete(_filePath);
                    File.Move(tempPath, _filePath);
                }

                _logger?.Invoke($"💾 Trading state saved ({state.TradesHistory.Count} trades, {state.EquityHistory.Count} equity points)");
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"❌ Failed to save trading state: {ex.Message}");
            }
        }

        /// <summary>
        /// Load state from file. Returns null if file doesn't exist or is invalid.
        /// </summary>
        public TradingState Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    _logger?.Invoke("ℹ️ No saved trading state found, starting fresh");
                    return null;
                }

                string json;
                lock (_lock)
                {
                    json = File.ReadAllText(_filePath);
                }

                TradingState state = JsonSerializer.Deserialize<TradingState>(json, JsonOptions);
                if (state != null)
                {
                    _logger?.Invoke($"📂 Trading state loaded: saved {state.SavedAt:yyyy-MM-dd HH:mm}, {state.TradesHistory.Count} trades");
                }
                return state;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"⚠️ Failed to load trading state: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Restore state into the runtime via registered callbacks.
        /// </summary>
        public void Restore()
        {
            if (_stateRestorer == null) return;

            TradingState state = Load();
            if (state != null)
            {
                _stateRestorer(state);
            }
        }

        public void StopAutoSave()
        {
            _autoSaveTimer?.Dispose();
            _autoSaveTimer = null;
        }

        public void Dispose()
        {
            StopAutoSave();
            Save(); // Final save on dispose
        }
    }
}
