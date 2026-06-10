using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Windows.Input;
using System.Threading.Tasks;
using System.Windows;
using System.IO;
using System.Globalization;
using System.Windows.Media;
using BinanceBotWpf.Services;
using BinanceBotWpf.Models;
using System.Collections.Generic;
using OxyPlot;
using OxyPlot.Series;
using OxyPlot.Axes;

namespace BinanceBotWpf.ViewModels
{
    public class PairAnalysisItem : INotifyPropertyChanged
    {
        private string _price;
        private string _analysis;
        private SolidColorBrush _rowColor = Brushes.Transparent;
        private SolidColorBrush _foregroundBrush = Brushes.White;

        public string Pair { get; set; }
        public string Price { get => _price; set { _price = value; OnPropertyChanged (); } }
        public string Analysis { get => _analysis; set { _analysis = value; OnPropertyChanged (); } }
        public SolidColorBrush RowColor { get => _rowColor; set { _rowColor = value; OnPropertyChanged (); } }
        public SolidColorBrush ForegroundBrush { get => _foregroundBrush; set { _foregroundBrush = value; OnPropertyChanged (); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke (this, new PropertyChangedEventArgs (name));
    }

    public class StockListItem : INotifyPropertyChanged
    {
        private decimal _price;
        private decimal _changePercent;
        private decimal _volume;
        private SolidColorBrush _rowColor = Brushes.Transparent;
        private SolidColorBrush _foregroundBrush = Brushes.White;

        public string Symbol { get; set; }
        public decimal Price { get => _price; set { _price = value; OnPropertyChanged (); } }
        public decimal ChangePercent { get => _changePercent; set { _changePercent = value; OnPropertyChanged (); } }
        public decimal Volume { get => _volume; set { _volume = value; OnPropertyChanged (); } }
        public SolidColorBrush RowColor { get => _rowColor; set { _rowColor = value; OnPropertyChanged (); } }
        public SolidColorBrush ForegroundBrush { get => _foregroundBrush; set { _foregroundBrush = value; OnPropertyChanged (); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke (this, new PropertyChangedEventArgs (name));
    }

    public class MainWindowViewModel : INotifyPropertyChanged
    {
        private readonly TradingService _tradingService;
        private readonly bool _isTestnet;
        private StockPriceMonitor _stockMonitor;
        private string _systemLogs = "";
        private string _walletBalance = "0.00";
        private bool _isRunning = false;

        // Параметры стратегии
        private int _fastSma = 9;
        private int _slowSma = 21;
        private int _rsiBuyThreshold = 30;
        private int _rsiSellThreshold = 70;
        private decimal _stopLossPercent = 0.02m;
        private decimal _takeProfitPercent = 0.04m;
        private decimal _trailingStopPercent = 0.02m;
        private decimal _minBalanceForTrading = 20m;
        private decimal _maxRiskPercent = 0.25m;

        // История и статистика
        private ObservableCollection<TradeLog> _tradesHistory = new ();
        private decimal _totalPnL = 0;
        private decimal _winRate = 0;
        private int _totalTrades = 0;
        private int _winningTrades = 0;
        private int _losingTrades = 0;
        private decimal _bestPnL = 0;
        private decimal _worstPnL = 0;
        private decimal _peakBalance = 0;
        private decimal _maxDrawdown = 0;
        private string _maxDrawdownDisplay = "Просадка: 0%";
        private decimal _totalProfitSum = 0;
        private decimal _totalLossSum = 0;
        private string _avgProfitLossDisplay = "Ср. приб/убыток: 0 / 0";
        private int _currentPositionsCount = 0;
        private int _maxPositions = 3;
        private string _positionsStatusText = "0/3 нет открытых";
        private string _riskPercentDisplay = "Риск: 0%";

        // График
        private PlotModel _plotModel;
        public PlotModel PlotModel
        {
            get => _plotModel;
            set { _plotModel = value; OnPropertyChanged (); }
        }

        // Команды
        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand ExportDataCommand { get; }
        public ICommand OptimizeStrategyCommand { get; }

        public ObservableCollection<PairAnalysisItem> PairsList { get; set; } = new ();
        private Dictionary<string, PairAnalysisItem> _pairDict = new ();
        public ObservableCollection<StockListItem> StocksList { get; set; } = new ();
        private Dictionary<string, StockListItem> _stockDict = new ();

        public string SystemLogs { get => _systemLogs; set { _systemLogs = value; OnPropertyChanged (); } }
        public string WalletBalance { get => _walletBalance; set { _walletBalance = value; OnPropertyChanged (); } }
        public bool IsRunning { get => _isRunning; set { _isRunning = value; OnPropertyChanged (); } }

        public int FastSma { get => _fastSma; set { if (_fastSma != value) { _fastSma = value; OnPropertyChanged (); SaveSettings (); } } }
        public int SlowSma { get => _slowSma; set { if (_slowSma != value) { _slowSma = value; OnPropertyChanged (); SaveSettings (); } } }
        public int RsiBuyThreshold { get => _rsiBuyThreshold; set { if (_rsiBuyThreshold != value) { _rsiBuyThreshold = value; OnPropertyChanged (); SaveSettings (); } } }
        public int RsiSellThreshold { get => _rsiSellThreshold; set { if (_rsiSellThreshold != value) { _rsiSellThreshold = value; OnPropertyChanged (); SaveSettings (); } } }
        public decimal StopLossPercent { get => _stopLossPercent; set { if (_stopLossPercent != value) { _stopLossPercent = value; OnPropertyChanged (); SaveSettings (); } } }
        public decimal TakeProfitPercent { get => _takeProfitPercent; set { if (_takeProfitPercent != value) { _takeProfitPercent = value; OnPropertyChanged (); SaveSettings (); } } }
        public decimal TrailingStopPercent { get => _trailingStopPercent; set { if (_trailingStopPercent != value) { _trailingStopPercent = value; OnPropertyChanged (); SaveSettings (); } } }
        public decimal MinBalanceForTrading { get => _minBalanceForTrading; set { if (_minBalanceForTrading != value) { _minBalanceForTrading = value; OnPropertyChanged (); SaveSettings (); } } }
        public decimal MaxRiskPercent { get => _maxRiskPercent; set { if (_maxRiskPercent != value) { _maxRiskPercent = value; OnPropertyChanged (); SaveSettings (); } } }

        public ObservableCollection<TradeLog> TradesHistory { get => _tradesHistory; set { _tradesHistory = value; OnPropertyChanged (); } }
        public decimal TotalPnL { get => _totalPnL; set { _totalPnL = value; OnPropertyChanged (); } }
        public decimal WinRate { get => _winRate; set { _winRate = value; OnPropertyChanged (); } }
        public int TotalTrades { get => _totalTrades; set { _totalTrades = value; OnPropertyChanged (); } }
        public int WinningTrades { get => _winningTrades; set { _winningTrades = value; OnPropertyChanged (); } }
        public int LosingTrades { get => _losingTrades; set { _losingTrades = value; OnPropertyChanged (); } }
        public decimal BestPnL { get => _bestPnL; set { _bestPnL = value; OnPropertyChanged (); } }
        public decimal WorstPnL { get => _worstPnL; set { _worstPnL = value; OnPropertyChanged (); } }
        public string MaxDrawdownDisplay { get => _maxDrawdownDisplay; set { _maxDrawdownDisplay = value; OnPropertyChanged (); } }
        public string AvgProfitLossDisplay { get => _avgProfitLossDisplay; set { _avgProfitLossDisplay = value; OnPropertyChanged (); } }
        public string PositionsStatusText { get => _positionsStatusText; set { _positionsStatusText = value; OnPropertyChanged (); } }
        public string RiskPercentDisplay { get => _riskPercentDisplay; set { _riskPercentDisplay = value; OnPropertyChanged (); } }
        public int CurrentPositionsCount { get => _currentPositionsCount; set { _currentPositionsCount = value; OnPropertyChanged (); } }
        public int MaxPositions { get => _maxPositions; set { _maxPositions = value; OnPropertyChanged (); } }

        private readonly string _settingsPath;
        private readonly object _settingsLock = new ();
        private bool _isLoadingSettings = false;

        public MainWindowViewModel(TradingService tradingService, bool isTestnet = false)
        {
            _tradingService = tradingService;
            _isTestnet = isTestnet;
            _settingsPath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Data", "strategy_settings.json");
            LoadSettings ();

            StartCommand = new RelayCommand (async _ => await Start (), _ => !IsRunning);
            StopCommand = new RelayCommand (_ => Stop (), _ => IsRunning);
            ExportDataCommand = new RelayCommand (_ => ExportData (), _ => true);
            OptimizeStrategyCommand = new RelayCommand (async _ => await RunOptimization (), _ => !IsRunning);

            // График
            _plotModel = new PlotModel { Title = "Баланс USDC", Background = OxyColors.Transparent, TextColor = OxyColors.White };
            _plotModel.Axes.Add (new DateTimeAxis { Position = AxisPosition.Bottom, StringFormat = "HH:mm", Title = "Время", TitleColor = OxyColors.White, AxislineColor = OxyColors.White, TicklineColor = OxyColors.White, TextColor = OxyColors.White });
            _plotModel.Axes.Add (new LinearAxis { Position = AxisPosition.Left, Title = "USDC", TitleColor = OxyColors.White, AxislineColor = OxyColors.White, TicklineColor = OxyColors.White, TextColor = OxyColors.White });
            _plotModel.Series.Add (new LineSeries { Color = OxyColors.LimeGreen, MarkerType = MarkerType.Circle, MarkerSize = 3 });

            // Мониторинг акций
            _stockMonitor = new StockPriceMonitor (AddLog, _isTestnet);
            _ = Task.Run (StocksLoop);

            // Запускаем цикл обновления UI через WebSocket
            _ = Task.Run (StartUiUpdateLoop);
        }

        private void LoadSettings()
        {
            try
            {
                string dir = Path.GetDirectoryName (_settingsPath);
                if (!Directory.Exists (dir)) Directory.CreateDirectory (dir);
                if (!File.Exists (_settingsPath)) return;

                string json = File.ReadAllText (_settingsPath);
                var settings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>> (json);
                if (settings == null) return;

                _isLoadingSettings = true;
                if (settings.TryGetValue ("FastSma", out var fs)) FastSma = Convert.ToInt32 (fs);
                if (settings.TryGetValue ("SlowSma", out var ss)) SlowSma = Convert.ToInt32 (ss);
                if (settings.TryGetValue ("RsiBuyThreshold", out var rb)) RsiBuyThreshold = Convert.ToInt32 (rb);
                if (settings.TryGetValue ("RsiSellThreshold", out var rs)) RsiSellThreshold = Convert.ToInt32 (rs);
                if (settings.TryGetValue ("StopLossPercent", out var sl)) StopLossPercent = Convert.ToDecimal (sl, CultureInfo.InvariantCulture);
                if (settings.TryGetValue ("TakeProfitPercent", out var tp)) TakeProfitPercent = Convert.ToDecimal (tp, CultureInfo.InvariantCulture);
                if (settings.TryGetValue ("TrailingStopPercent", out var tr)) TrailingStopPercent = Convert.ToDecimal (tr, CultureInfo.InvariantCulture);
                if (settings.TryGetValue ("MinBalanceForTrading", out var mb)) MinBalanceForTrading = Convert.ToDecimal (mb, CultureInfo.InvariantCulture);
                if (settings.TryGetValue ("MaxRiskPercent", out var mr)) MaxRiskPercent = Convert.ToDecimal (mr, CultureInfo.InvariantCulture);
                _isLoadingSettings = false;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"LoadSettings error: {ex.Message}"); _isLoadingSettings = false; }
        }

        public void SaveSettings()
        {
            if (_isLoadingSettings) return;
            lock (_settingsLock)
            {
                try
                {
                    var settings = new Dictionary<string, object>
                    {
                        ["FastSma"] = FastSma,
                        ["SlowSma"] = SlowSma,
                        ["RsiBuyThreshold"] = RsiBuyThreshold,
                        ["RsiSellThreshold"] = RsiSellThreshold,
                        ["StopLossPercent"] = StopLossPercent,
                        ["TakeProfitPercent"] = TakeProfitPercent,
                        ["TrailingStopPercent"] = TrailingStopPercent,
                        ["MinBalanceForTrading"] = MinBalanceForTrading,
                        ["MaxRiskPercent"] = MaxRiskPercent
                    };
                    string dir = Path.GetDirectoryName (_settingsPath);
                    if (!Directory.Exists (dir)) Directory.CreateDirectory (dir);
                    string json = System.Text.Json.JsonSerializer.Serialize (settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText (_settingsPath, json);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"SaveSettings error: {ex.Message}"); }
            }
        }

        private async Task Start() { IsRunning = true; await _tradingService.StartTradingAsync (this); }
        private void Stop() { _tradingService.StopTrading (); IsRunning = false; }

        private async Task RunOptimization()
        {
            AddLog ("🧠 Запуск автоматической оптимизации стратегии...");
            AddLog ("⏳ Это может занять 2-3 минуты...");

            var optimizer = new StrategyOptimizer (_tradingService.GetBinanceClient (), this, AddLog);
            bool success = await optimizer.RunOptimizationAsync ();

            if (success)
            {
                AddLog ("✅ Оптимизация завершена успешно!");
                AddLog ($"📊 Новые параметры: SMA {FastSma}/{SlowSma}, RSI {RsiBuyThreshold}, SL={StopLossPercent:P0}, TP={TakeProfitPercent:P0}");
            }
            else
            {
                AddLog ("❌ Оптимизация не удалась. Проверьте подключение к интернету и наличие исторических данных.");
            }
        }

        public void ExportData()
        {
            try
            {
                string sourceDir = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Logs");
                string destDir = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Export");
                if (!Directory.Exists (sourceDir)) { AddLog ("Нет папки Logs для экспорта."); return; }
                Directory.CreateDirectory (destDir);
                foreach (var file in Directory.GetFiles (sourceDir))
                {
                    string fileName = Path.GetFileName (file);
                    File.Copy (file, Path.Combine (destDir, fileName), true);
                }
                AddLog ($"✅ Данные экспортированы в папку: {destDir}");
            }
            catch (Exception ex) { AddLog ($"Ошибка экспорта: {ex.Message}"); }
        }

        public void UpdateWalletDisplay(string balance) => Application.Current.Dispatcher.Invoke (() => WalletBalance = balance);

        public void AddBalancePoint(DateTime time, decimal balance)
        {
            Application.Current.Dispatcher.Invoke (() =>
            {
                if (_plotModel.Series[0] is LineSeries series)
                {
                    series.Points.Add (new DataPoint (DateTimeAxis.ToDouble (time), (double)balance));
                    if (series.Points.Count > 200) series.Points.RemoveAt (0);
                    if (series.Points.Count % 10 == 0)
                        _plotModel.InvalidatePlot (true);
                }
            });
        }

        public void UpdateMarketTable(string pair, string price, bool hasPosition, TradeAction signal, decimal fastSma, decimal slowSma)
        {
            Application.Current.Dispatcher.Invoke (() =>
            {
                SolidColorBrush bgBrush = Brushes.Transparent;
                SolidColorBrush fgBrush = Brushes.White;
                if (hasPosition) bgBrush = new SolidColorBrush (Color.FromRgb (0, 80, 0));
                else if (signal == TradeAction.Buy) bgBrush = new SolidColorBrush (Color.FromRgb (0, 70, 150));
                else if (signal == TradeAction.Sell) bgBrush = new SolidColorBrush (Color.FromRgb (150, 40, 40));
                else fgBrush = new SolidColorBrush (Color.FromRgb (200, 200, 200));

                if (_pairDict.TryGetValue (pair, out var existing))
                {
                    existing.Price = price;
                    existing.Analysis = $"F:{fastSma:F2} / S:{slowSma:F2}";
                    existing.RowColor = bgBrush;
                    existing.ForegroundBrush = fgBrush;
                }
                else
                {
                    var newItem = new PairAnalysisItem { Pair = pair, Price = price, Analysis = $"F:{fastSma:F2} / S:{slowSma:F2}", RowColor = bgBrush, ForegroundBrush = fgBrush };
                    _pairDict[pair] = newItem;
                    PairsList.Add (newItem);
                }
            });
        }

        public void RemoveMissingPairs(List<string> activePairs)
        {
            Application.Current.Dispatcher.Invoke (() =>
            {
                var toRemove = PairsList.Where (p => !activePairs.Contains (p.Pair)).ToList ();
                foreach (var item in toRemove) { _pairDict.Remove (item.Pair); PairsList.Remove (item); }
            });
        }

        private TradeAction GetStockSignal(string symbol, decimal priceChangePercent)
        {
            if (priceChangePercent > 0.5m) return TradeAction.Buy;
            if (priceChangePercent < -0.5m) return TradeAction.Sell;
            return TradeAction.Hold;
        }

        private async Task StocksLoop()
        {
            while (true)
            {
                try
                {
                    var stocksData = await _stockMonitor.FetchAllTrackedStocksAsync ();
                    await Application.Current.Dispatcher.InvokeAsync (() =>
                    {
                        foreach (var stock in stocksData)
                        {
                            var signal = GetStockSignal (stock.Symbol, stock.PriceChangePercent);
                            SolidColorBrush bgBrush = Brushes.Transparent;
                            SolidColorBrush fgBrush = Brushes.White;
                            if (signal == TradeAction.Buy) bgBrush = new SolidColorBrush (Color.FromRgb (0, 70, 150));
                            else if (signal == TradeAction.Sell) bgBrush = new SolidColorBrush (Color.FromRgb (150, 40, 40));
                            else fgBrush = new SolidColorBrush (Color.FromRgb (200, 200, 200));

                            if (_stockDict.TryGetValue (stock.Symbol, out var existing))
                            {
                                existing.Price = stock.Price;
                                existing.ChangePercent = stock.PriceChangePercent;
                                existing.Volume = stock.Volume;
                                existing.RowColor = bgBrush;
                                existing.ForegroundBrush = fgBrush;
                            }
                            else
                            {
                                var newItem = new StockListItem
                                {
                                    Symbol = stock.Symbol,
                                    Price = stock.Price,
                                    ChangePercent = stock.PriceChangePercent,
                                    Volume = stock.Volume,
                                    RowColor = bgBrush,
                                    ForegroundBrush = fgBrush
                                };
                                _stockDict[stock.Symbol] = newItem;
                                StocksList.Add (newItem);
                            }
                        }
                    });
                    await Task.Delay (30000);
                }
                catch (Exception ex)
                {
                    AddLog ($"❌ Ошибка в StocksLoop: {ex.Message}");
                    await Task.Delay (30000);
                }
            }
        }

        private async Task StartUiUpdateLoop()
        {
            while (true)
            {
                try
                {
                    await Application.Current.Dispatcher.InvokeAsync (() =>
                    {
                        foreach (var pairItem in PairsList)
                        {
                            decimal price = _tradingService.GetCurrentPriceForSymbol (pairItem.Pair);
                            if (price > 0)
                                pairItem.Price = price.ToString ("F4");
                        }
                    });
                    await Task.Delay (1000);
                }
                catch (Exception ex)
                {
                    AddLog ($"❌ Ошибка обновления UI: {ex.Message}");
                    await Task.Delay (5000);
                }
            }
        }

        public void AddLog(string message)
        {
            Application.Current.Dispatcher.Invoke (() =>
            {
                SystemLogs += $"{DateTime.Now:HH:mm:ss} - {message}\n";
                if (SystemLogs.Length > 50000)
                {
                    var idx = SystemLogs.IndexOf ('\n', SystemLogs.Length / 2);
                    if (idx > 0) SystemLogs = SystemLogs.Substring (idx + 1);
                }
            });
        }

        public void AddTradeToHistory(TradeLog trade)
        {
            Application.Current.Dispatcher.Invoke (() =>
            {
                TradesHistory.Insert (0, trade);
                TotalTrades = TradesHistory.Count;
                WinningTrades = TradesHistory.Count (t => t.PnL > 0);
                LosingTrades = TradesHistory.Count (t => t.PnL < 0);
                TotalPnL = TradesHistory.Sum (t => t.PnL);
                WinRate = TotalTrades > 0 ? (decimal)WinningTrades / TotalTrades * 100 : 0;
                BestPnL = TotalTrades > 0 ? TradesHistory.Max (t => t.PnL) : 0;
                WorstPnL = TotalTrades > 0 ? TradesHistory.Min (t => t.PnL) : 0;
                if (trade.PnL > 0) _totalProfitSum += trade.PnL;
                else _totalLossSum += trade.PnL;
                decimal avgP = WinningTrades > 0 ? _totalProfitSum / WinningTrades : 0;
                decimal avgL = LosingTrades > 0 ? Math.Abs (_totalLossSum / LosingTrades) : 0;
                AvgProfitLossDisplay = $"Ср. приб/убыток: {avgP:F2} / {avgL:F2}";
            });
        }

        public void UpdateDrawdown(decimal currentBalance)
        {
            Application.Current.Dispatcher.Invoke (() =>
            {
                if (currentBalance > _peakBalance) _peakBalance = currentBalance;
                if (_peakBalance > 0)
                {
                    decimal dd = ( _peakBalance - currentBalance ) / _peakBalance * 100;
                    if (dd > _maxDrawdown) _maxDrawdown = dd;
                    MaxDrawdownDisplay = $"Просадка: {_maxDrawdown:F1}%";
                }
            });
        }

        public void UpdatePositionsStatus(int current, int max, List<string> symbols)
        {
            Application.Current.Dispatcher.Invoke (() =>
            {
                CurrentPositionsCount = current;
                MaxPositions = max;
                PositionsStatusText = current == 0 ? $"{current}/{max} нет открытых" : $"{current}/{max} торгует: {string.Join (", ", symbols)}";
            });
        }

        public void ReloadSettings()
        {
            LoadSettings ();
            OnPropertyChanged (nameof (FastSma));
            OnPropertyChanged (nameof (SlowSma));
            OnPropertyChanged (nameof (RsiBuyThreshold));
            OnPropertyChanged (nameof (RsiSellThreshold));
            OnPropertyChanged (nameof (StopLossPercent));
            OnPropertyChanged (nameof (TakeProfitPercent));
            OnPropertyChanged (nameof (TrailingStopPercent));
            OnPropertyChanged (nameof (MinBalanceForTrading));
            OnPropertyChanged (nameof (MaxRiskPercent));
        }

        public void UpdateRiskDisplay(decimal riskPercent) => Application.Current.Dispatcher.Invoke (() => RiskPercentDisplay = $"Риск: {riskPercent * 100:F0}%");

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) => PropertyChanged?.Invoke (this, new PropertyChangedEventArgs (name));
    }
}