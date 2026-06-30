using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BinanceBotWpf.Models;
using BinanceBotWpf.Services.Strategies;
using BinanceBotWpf.ViewModels;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Manages all secondary background loops: auto-optimize, update check, daily report, whale monitor, earn optimize, fear-greed.
    /// </summary>
    public class BackgroundLoopManager
    {
        private readonly BinanceClient _client;
        private readonly IWalletManager _wallet;
        private readonly ISimpleEarnStrategy _earnStrategy;
        private readonly IFearGreedIndexProvider _fearGreedProvider;
        private TradingSettings _tradingSettings;

        private MainWindowViewModel _ui;
        private UpdateChecker _updateChecker;
        private WhaleMonitor _whaleMonitor;
        private TelegramNotifier _telegram;
        private PairManager _pairManager;

        private volatile bool _isRunning;
        private CancellationTokenSource _shutdownCts;

        public BackgroundLoopManager (
            BinanceClient client,
            IWalletManager wallet,
            ISimpleEarnStrategy earnStrategy,
            IFearGreedIndexProvider fearGreedProvider)
        {
            _client = client;
            _wallet = wallet;
            _earnStrategy = earnStrategy;
            _fearGreedProvider = fearGreedProvider;
        }

        public void Configure (MainWindowViewModel ui, bool isRunning, CancellationTokenSource shutdownCts,
            TradingSettings tradingSettings, UpdateChecker updateChecker, TelegramNotifier telegram, PairManager pairManager)
        {
            _ui = ui;
            _isRunning = isRunning;
            _shutdownCts = shutdownCts;
            _tradingSettings = tradingSettings;
            _updateChecker = updateChecker;
            _telegram = telegram;
            _pairManager = pairManager;
        }

        /// <summary>
        /// Reconfigure running state after start/stop.
        /// </summary>
        public void UpdateRunningState (bool isRunning, CancellationTokenSource shutdownCts)
        {
            _isRunning = isRunning;
            _shutdownCts = shutdownCts;
        }

        public async Task AutoOptimizeLoop ()
        {
            int lastTradeCount = 0;
            while (_isRunning)
            {
                try
                {
                    await Task.Delay (TimeSpan.FromHours (24), _shutdownCts?.Token ?? CancellationToken.None);

                    if (!_isRunning) break;

                    int currentTradeCount = _ui?.TotalTrades ?? 0;
                    int newTrades = currentTradeCount - lastTradeCount;

                    if (newTrades < 10)
                    {
                        _ui?.AddLog ($"Оптимизация пропущена: только {newTrades} новых сделок (нужно минимум 10)");
                        lastTradeCount = currentTradeCount;
                        continue;
                    }

                    _ui?.AddLog ($"Запуск оптимизации ({newTrades} новых сделок)...");

                    decimal balance = _wallet?.GetTotalBalance ("USDC") ?? 0;
                    StrategyOptimizer optimizer = new StrategyOptimizer (_client, _ui, _ui.AddLog);
                    bool success = await optimizer.RunOptimizationAsync (balance);

                    if (success)
                    {
                        _ui?.AddLog ("Оптимизация завершена");
                    }
                    else
                    {
                        _ui?.AddLog ("Оптимизация не дала результатов");
                    }

                    lastTradeCount = currentTradeCount;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _ui?.AddLog ($"Ошибка оптимизации: {ex.Message}");
                    try { await Task.Delay (60000, _shutdownCts?.Token ?? CancellationToken.None); } catch { }
                }
            }
        }

        public async Task PeriodicUpdateCheckLoop ()
        {
            await Task.Delay (TimeSpan.FromMinutes (5), _shutdownCts?.Token ?? CancellationToken.None);

            while (_isRunning)
            {
                try
                {
                    if (!_isRunning) break;

                    if (_updateChecker != null)
                    {
                        await _updateChecker.CheckForUpdatesAsync ();
                    }
                }
                catch { }

                await Task.Delay (TimeSpan.FromMinutes (30), _shutdownCts?.Token ?? CancellationToken.None);
            }
        }

        public async Task DailyReportLoop ()
        {
            while (_isRunning)
            {
                try
                {
                    DateTime now = DateTime.UtcNow;
                    DateTime nextRun = now.Date.AddDays (1).AddHours (9);
                    TimeSpan delay = nextRun - now;
                    if (delay.TotalMilliseconds > 0)
                        await Task.Delay (delay, _shutdownCts?.Token ?? CancellationToken.None);

                    if (!_isRunning || _telegram == null) continue;

                    decimal totalPnL = _ui?.TotalPnL ?? 0;
                    decimal winRate = _ui?.WinRate ?? 0;
                    int totalTrades = _ui?.TotalTrades ?? 0;
                    int winningTrades = _ui?.WinningTrades ?? 0;
                    int losingTrades = _ui?.LosingTrades ?? 0;

                    await _telegram.SendDailyReport (totalPnL, winRate, totalTrades, winningTrades, losingTrades);
                }
                catch { }
            }
        }

        public async Task WhaleLoop ()
        {
            HashSet<string> stablecoinPairs = new HashSet<string> (StringComparer.OrdinalIgnoreCase)
            {
                "USDCUSDT", "USDTUSDC", "BUSDUSDT", "FDUSDUSDT", "TUSDUSDT", "USDCUSDC", "DAIUSDT"
            };

            bool started = false;
            while (_isRunning)
            {
                try
                {
                    if (started) { await Task.Delay (60000, _shutdownCts?.Token ?? CancellationToken.None); continue; }

                    List<string> pairs = _pairManager?.GetActivePairs () ?? new List<string> ();
                    pairs = pairs.Where (p => !stablecoinPairs.Contains (p)).ToList ();

                    if (pairs.Count > 0)
                    {
                        Action<string> log = (msg) => _ui?.AddLog (msg);
                        _whaleMonitor = new WhaleMonitor (log, 100000);
                        _whaleMonitor.OnWhaleDetected += whale =>
                        {
                            _ui?.AddLog ($"🐋 WHALE {whale.Side} {whale.Symbol}: ${whale.ValueUsdc:N0}");
                        };
                        await _whaleMonitor.StartAsync (pairs.ToArray ());
                        _ui?.AddLog ($"🐋 Whale monitor запущен для {pairs.Count} пар (порог: $100k, стейблкоины исключены)");
                        started = true;
                    }
                    await Task.Delay (10000, _shutdownCts?.Token ?? CancellationToken.None);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _ui?.AddLog ($"Whale monitor ошибка: {ex.Message}");
                    try { await Task.Delay (30000, _shutdownCts?.Token ?? CancellationToken.None); } catch { }
                }
            }
        }

        public async Task EarnOptimizeLoop ()
        {
            while (_isRunning)
            {
                try
                {
                    await Task.Delay (TimeSpan.FromHours (6), _shutdownCts?.Token ?? CancellationToken.None);
                    if (!_isRunning) break;
                    await _earnStrategy.OptimizeEarnAsync ();
                }
                catch { }
            }
        }

        public async Task FearGreedLoop ()
        {
            while (_isRunning)
            {
                try
                {
                    if (_fearGreedProvider != null)
                    {
                        FearGreedData data = await _fearGreedProvider.GetCurrentAsync ();
                        if (data != null && _ui != null)
                        {
                            _ui.FearGreedValue = data.Value;
                            _ui.FearGreedClassification = data.Classification;
                        }
                    }
                    await Task.Delay (TimeSpan.FromMinutes (15), _shutdownCts?.Token ?? CancellationToken.None);
                }
                catch (OperationCanceledException) { break; }
                catch { try { await Task.Delay (TimeSpan.FromMinutes (15), _shutdownCts?.Token ?? CancellationToken.None); } catch { } }
            }
        }

        public void DisposeWhaleMonitor ()
        {
            try { _whaleMonitor?.Dispose (); }
            catch { }
        }
    }
}
