using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Windows.Input;
using System.Threading;
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
        private string _marketCap = "—";
        private string _sentiment = "⚪ —";
        private string _session = "";

        public string Pair { get; set; }
        public string Price { get => _price; set { _price = value; OnPropertyChanged (); } }
        public string Analysis { get => _analysis; set { _analysis = value; OnPropertyChanged (); } }
        public string MarketCap { get => _marketCap; set { _marketCap = value; OnPropertyChanged (); } }
        public string Sentiment { get => _sentiment; set { _sentiment = value; OnPropertyChanged (); } }
        public string Session { get => _session; set { _session = value; OnPropertyChanged (); } }
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

    public class MainWindowViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly TradingService _tradingService;
        private readonly bool _isTestnet;
        private StockPriceMonitor _stockMonitor;
        private string _walletBalance = "0.00";
        private bool _isRunning = false;
        private CancellationTokenSource _bgCts = new ();

        // Параметры стратегии
        private int _fastSma = 12;
        private int _slowSma = 26;
        private int _rsiBuyThreshold = 25;
        private int _rsiSellThreshold = 75;
        private decimal _stopLossPercent = 0.005m;
        private decimal _takeProfitPercent = 0.008m;
        private decimal _trailingStopPercent = 0.01m;
        private decimal _minBalanceForTrading = 10m;
        private decimal _maxRiskPercent = 0.10m;

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
        private int _maxPositions = 2;
        private string _positionsStatusText = "0/2 нет открытых";
        private string _riskPercentDisplay = "Риск: 0%";

        public decimal PeakBalance => _peakBalance;
        public decimal MaxDrawdown => _maxDrawdown;
        public decimal TotalProfitSum => _totalProfitSum;
        public decimal TotalLossSum => _totalLossSum;
        private TradingSettings _tradingSettings;

        // Обновления
        private bool _isUpdateAvailable = false;
        private string _availableVersion = "";
        private string _updateDownloadUrl = "";
        private string _updateStatusText = "";

        // Логи
        private ObservableCollection<string> _systemLogs = new ();
        private List<string> _allLogs = new ();
        private string _selectedLogLevel = "Все";
        private string _telegramStatus = "⚙️ Настройка...";
        private bool _requestLogsScroll;

        // Fear & Greed
        private int _fearGreedValue = 50;
        private string _fearGreedClassification = "Neutral";

        // График
        private PlotModel _plotModel;
        private string _equityChartStatus = "";
        public string EquityChartStatus { get => _equityChartStatus; set { _equityChartStatus = value; OnPropertyChanged (); } }

        // Коллекции для UI
        public ObservableCollection<PairAnalysisItem> PairsList { get; set; } = new ();
        private Dictionary<string, PairAnalysisItem> _pairDict = new ();
        public ObservableCollection<StockListItem> StocksList { get; set; } = new ();
        private Dictionary<string, StockListItem> _stockDict = new ();

        // Свойства
        public string WalletBalance { get => _walletBalance; set { _walletBalance = value; OnPropertyChanged (); } }
        public bool IsRunning { get => _isRunning; set { _isRunning = value; OnPropertyChanged (); } }

        public int FastSma { get => _fastSma; set { _fastSma = Math.Clamp (value, 3, 50); OnPropertyChanged (); SaveSettings (); } }
        public int SlowSma { get => _slowSma; set { _slowSma = Math.Clamp (value, 10, 200); OnPropertyChanged (); SaveSettings (); } }
        public int RsiBuyThreshold { get => _rsiBuyThreshold; set { _rsiBuyThreshold = Math.Clamp (value, 5, 45); OnPropertyChanged (); SaveSettings (); } }
        public int RsiSellThreshold { get => _rsiSellThreshold; set { _rsiSellThreshold = Math.Clamp (value, 55, 95); OnPropertyChanged (); SaveSettings (); } }
        public decimal StopLossPercent { get => _stopLossPercent; set { _stopLossPercent = Math.Clamp (value, 0.005m, 0.10m); OnPropertyChanged (); SaveSettings (); } }
        public decimal TakeProfitPercent { get => _takeProfitPercent; set { _takeProfitPercent = Math.Clamp (value, 0.006m, 0.20m); OnPropertyChanged (); SaveSettings (); } }
        public decimal TrailingStopPercent { get => _trailingStopPercent; set { _trailingStopPercent = Math.Clamp (value, 0.005m, 0.10m); OnPropertyChanged (); SaveSettings (); } }
        public decimal MinBalanceForTrading { get => _minBalanceForTrading; set { _minBalanceForTrading = Math.Clamp (value, 5m, 100m); OnPropertyChanged (); SaveSettings (); } }
        public decimal MaxRiskPercent { get => _maxRiskPercent; set { _maxRiskPercent = Math.Clamp (value, 0.01m, 0.25m); OnPropertyChanged (); SaveSettings (); } }

        // Свойства обновлений
        public bool IsUpdateAvailable
        {
            get => _isUpdateAvailable;
            set { _isUpdateAvailable = value; OnPropertyChanged (); }
        }

        public string AvailableVersion
        {
            get => _availableVersion;
            set { _availableVersion = value; OnPropertyChanged (); }
        }

        public string UpdateDownloadUrl
        {
            get => _updateDownloadUrl;
            set { _updateDownloadUrl = value; OnPropertyChanged (); }
        }

        public string UpdateStatusText
        {
            get => _updateStatusText;
            set { _updateStatusText = value; OnPropertyChanged (); }
        }

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
        public PlotModel PlotModel { get => _plotModel; set { _plotModel = value; OnPropertyChanged (); } }

        public ObservableCollection<string> SystemLogs { get => _systemLogs; set { _systemLogs = value; OnPropertyChanged (); } }
        public List<string> LogLevels { get; } = new List<string> { "Все", "Ошибки", "Предупреждения", "Инфо", "Торговля" };

        public string SelectedLogLevel
        {
            get => _selectedLogLevel;
            set { _selectedLogLevel = value; OnPropertyChanged (); FilterLogs (); }
        }

        public string TelegramStatus
        {
            get => _telegramStatus;
            set { _telegramStatus = value; OnPropertyChanged (); }
        }

        public int FearGreedValue
        {
            get => _fearGreedValue;
            set { _fearGreedValue = value; OnPropertyChanged (); }
        }

        public string FearGreedClassification
        {
            get => _fearGreedClassification;
            set { _fearGreedClassification = value; OnPropertyChanged (); }
        }

        public bool RequestLogsScroll
        {
            get => _requestLogsScroll;
            set { _requestLogsScroll = value; OnPropertyChanged (); }
        }

        // Команды
        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand EmergencyStopCommand { get; }
        public ICommand ExportDataCommand { get; }
        public ICommand OptimizeStrategyCommand { get; }
        public ICommand ClearLogsCommand { get; }
        public ICommand CopyLogsCommand { get; }
        public ICommand ScrollLogsToEndCommand { get; }
        public ICommand CheckForUpdatesCommand { get; }
        public ICommand UpdateNowCommand { get; }

        private bool _isCheckingForUpdate = false;
        public bool IsCheckingForUpdate { get => _isCheckingForUpdate; set { _isCheckingForUpdate = value; OnPropertyChanged (); } }

        private readonly string _settingsPath;
        private readonly object _settingsLock = new ();
        private bool _isLoadingSettings = false;

        public MainWindowViewModel(TradingService tradingService, TradingSettings tradingSettings, bool isTestnet = false)
        {
            _tradingService = tradingService;
            _isTestnet = isTestnet;
            _settingsPath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Data", "strategy_settings.json");

            LoadSettings ();
            _tradingSettings = tradingSettings;

            StartCommand = new RelayCommand (async _ => await Start (), _ => !IsRunning);
            StopCommand = new RelayCommand (_ => Stop (), _ => IsRunning);
            EmergencyStopCommand = new RelayCommand (async _ => await EmergencyStop (), _ => true);
            ExportDataCommand = new RelayCommand (_ => ExportData (), _ => true);
            OptimizeStrategyCommand = new RelayCommand (async _ => await RunOptimization (), _ => !IsRunning);
            ClearLogsCommand = new RelayCommand (_ => ClearLogs (), _ => true);
            CopyLogsCommand = new RelayCommand (_ => CopyLogs (), _ => true);
            ScrollLogsToEndCommand = new RelayCommand (_ => ScrollLogsToEnd (), _ => true);
            CheckForUpdatesCommand = new RelayCommand (async _ => await CheckForUpdatesAsync (silent: false), _ => !IsCheckingForUpdate);
            UpdateNowCommand = new RelayCommand (async _ => await UpdateNowAsync (), _ => !IsCheckingForUpdate);

            // График
            _plotModel = new PlotModel { Title = "Баланс USDC", Background = OxyColors.Transparent, TextColor = OxyColors.White };
            _plotModel.Axes.Add (new DateTimeAxis { Position = AxisPosition.Bottom, StringFormat = "HH:mm", Title = "Время", TitleColor = OxyColors.White, AxislineColor = OxyColors.White, TicklineColor = OxyColors.White, TextColor = OxyColors.White });
            _plotModel.Axes.Add (new LinearAxis { Position = AxisPosition.Left, Title = "USDC", TitleColor = OxyColors.White, AxislineColor = OxyColors.White, TicklineColor = OxyColors.White, TextColor = OxyColors.White });
            _plotModel.Series.Add (new LineSeries { Color = OxyColors.LimeGreen, MarkerType = MarkerType.Circle, MarkerSize = 3 });

            _stockMonitor = new StockPriceMonitor (AddLog, _isTestnet);
            _ = Task.Run (StocksLoop);
            _ = Task.Run (StartUiUpdateLoop);

            // Обновляем статус Telegram
            UpdateTelegramStatus ();
            Task.Run (async () =>
            {
                while (!_bgCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay (3000, _bgCts.Token);
                        if (Application.Current != null)
                            Application.Current.Dispatcher.Invoke (() => UpdateTelegramStatus ());
                    }
                    catch (OperationCanceledException) { break; }
                }
            });

            // Тихая проверка обновлений при запуске (без диалога, само обновится при наличии новой версии)
            _ = Task.Run (async () =>
            {
                await Task.Delay (5000);
                await CheckForUpdatesAsync (silent: true);
            });
        }

        /// <summary>
        /// Загружает пары для отображения в таблице до старта торговли.
        /// Вызывается после SetLogger(), чтобы ошибки были видны в логах.
        /// </summary>
        public async Task LoadPairsOnStartupAsync ()
        {
            try
            {
                AddLog ("📊 Загрузка пар для отображения...");
                await _tradingService.LoadPairsForDisplayAsync (this);
            }
            catch (Exception ex)
            {
                AddLog ($"⚠️ Не удалось загрузить пары: {ex.Message}");
            }
        }

        public void RefreshTelegramStatus()
        {
            if (Application.Current?.Dispatcher.CheckAccess () == true)
                UpdateTelegramStatus ();
            else
                Application.Current?.Dispatcher.Invoke (UpdateTelegramStatus);
        }

        private void UpdateTelegramStatus()
        {
            try
            {
                bool isEnabled = _tradingService.IsTelegramEnabled ();
                string newStatus = isEnabled ? "✅ Подключён" : "❌ Не настроен";

                if (TelegramStatus != newStatus)
                {
                    TelegramStatus = newStatus;
                }
            }
            catch (Exception ex)
            {
                if (TelegramStatus != "❌ Ошибка")
                {
                    TelegramStatus = "❌ Ошибка";
                    AddLog ($"Ошибка Telegram: {ex.Message}");
                }
            }
        }

        public async Task CheckForUpdatesAsync(bool silent)
        {
            if (IsCheckingForUpdate) return;
            IsCheckingForUpdate = true;
            try
            {
                var httpClient = SharedHttpClient.Instance;
                var checker = new UpdateChecker (httpClient, AddLog);
                checker.OnNewVersionAvailable += (version, url) =>
                {
                    Application.Current.Dispatcher.Invoke (() =>
                    {
                        IsUpdateAvailable = true;
                        AvailableVersion = version;
                        UpdateDownloadUrl = url;
                        UpdateStatusText = $"Версия {version} доступна";
                    });
                };

                await checker.CheckForUpdatesAsync ();

                if (IsUpdateAvailable && !silent)
                {
                    // Сразу скачиваем и устанавливаем
                    UpdateStatusText = "Загрузка обновления...";
                    var updater = new UpdateManager (AddLog);
                    bool updated = await updater.DownloadByUrlAsync (UpdateDownloadUrl, AvailableVersion);
                    if (updated)
                    {
                        UpdateStatusText = "Обновление установлено. Перезапуск...";
                    }
                    else
                    {
                        UpdateStatusText = "Ошибка установки обновления";
                        IsUpdateAvailable = false;
                    }
                }
                else if (!IsUpdateAvailable && !silent)
                {
                    AddLog ("✅ Обновлений не найдено, установлена актуальная версия.");
                }
            }
            catch (Exception ex)
            {
                AddLog ($"❌ Ошибка проверки обновлений: {ex.Message}");
            }
            finally
            {
                IsCheckingForUpdate = false;
            }
        }

        /// <summary>
        /// Скачивать и установить обновление
        /// </summary>
        public async Task UpdateNowAsync()
        {
            IsCheckingForUpdate = true;
            UpdateStatusText = "Загрузка обновления...";

            try
            {
                var updater = new UpdateManager (AddLog);

                if (!string.IsNullOrEmpty (UpdateDownloadUrl))
                {
                    bool updated = await updater.DownloadByUrlAsync (UpdateDownloadUrl, AvailableVersion);
                    if (updated)
                    {
                        UpdateStatusText = "Обновление установлено. Перезапуск...";
                    }
                    else
                    {
                        UpdateStatusText = "Ошибка установки обновления";
                        IsUpdateAvailable = false;
                    }
                }
                else
                {
                    bool updated = await updater.CheckAndUpdateAsync (silent: true);
                    if (!updated)
                    {
                        UpdateStatusText = "Обновлений не найдено или ошибка";
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog ($"❌ Ошибка обновления: {ex.Message}");
                UpdateStatusText = "Ошибка обновления";
            }
            finally
            {
                IsCheckingForUpdate = false;
            }
        }

        private void ScrollLogsToEnd()
        {
            RequestLogsScroll = !RequestLogsScroll;
        }

        public void AddLog(string message)
        {
            string timestamp = DateTime.Now.ToString ("HH:mm:ss");
            string formattedMessage = $"[{timestamp}] {message}";

            if (Application.Current == null) return;
            Application.Current.Dispatcher.Invoke (() =>
            {
                _allLogs.Add (formattedMessage);
                if (_allLogs.Count > 1000) _allLogs.RemoveAt (0);

                bool matchesFilter = false;
                switch (_selectedLogLevel)
                {
                    case "Ошибки": matchesFilter = formattedMessage.Contains ("❌") || formattedMessage.Contains ("Ошибка") || formattedMessage.Contains ("ERROR"); break;
                    case "Предупреждения": matchesFilter = formattedMessage.Contains ("⚠️") || formattedMessage.Contains ("WARNING"); break;
                    case "Инфо": matchesFilter = formattedMessage.Contains ("✅") || formattedMessage.Contains ("ℹ️") || formattedMessage.Contains ("INFO"); break;
                    case "Торговля": matchesFilter = formattedMessage.Contains ("🟢") || formattedMessage.Contains ("🔴") || formattedMessage.Contains ("КУПЛЕНО") || formattedMessage.Contains ("ПРОДАНО"); break;
                    default: matchesFilter = true; break;
                }

                if (matchesFilter)
                {
                    SystemLogs.Add (formattedMessage);
                    if (SystemLogs.Count > 500) SystemLogs.RemoveAt (0);
                    MainWindow.Instance?.AppendLog (formattedMessage);
                }
            });

            SendImportantToTelegram (message);
        }

        private void FilterLogs()
        {
            if (Application.Current == null) return;
            Application.Current.Dispatcher.Invoke (() =>
            {
                SystemLogs.Clear ();
                MainWindow.Instance?.ClearLogs ();

                // Исправленный switch без многоточия
                IEnumerable<string> filtered;
                switch (_selectedLogLevel)
                {
                    case "Ошибки":
                        filtered = _allLogs.Where (l => l.Contains ("❌") || l.Contains ("Ошибка") || l.Contains ("ERROR"));
                        break;
                    case "Предупреждения":
                        filtered = _allLogs.Where (l => l.Contains ("⚠️") || l.Contains ("WARNING"));
                        break;
                    case "Инфо":
                        filtered = _allLogs.Where (l => l.Contains ("✅") || l.Contains ("ℹ️") || l.Contains ("INFO"));
                        break;
                    case "Торговля":
                        filtered = _allLogs.Where (l => l.Contains ("🟢") || l.Contains ("🔴") || l.Contains ("КУПЛЕНО") || l.Contains ("ПРОДАНО"));
                        break;
                    default:
                        filtered = _allLogs;
                        break;
                }

                foreach (var log in filtered.TakeLast (500))
                {
                    SystemLogs.Add (log);
                    MainWindow.Instance?.AppendLog (log);
                }
            });
        }

        public void ClearLogs()
        {
            if (Application.Current == null) return;
            Application.Current.Dispatcher.Invoke (() =>
            {
                _allLogs.Clear ();
                SystemLogs.Clear ();
                MainWindow.Instance?.ClearLogs ();
                AddLog ("🧹 Логи очищены");
            });
        }

        public void CopyLogs()
        {
            try
            {
                var logsText = string.Join (Environment.NewLine, SystemLogs);
                Clipboard.SetText (logsText);
                AddLog ("📋 Логи скопированы в буфер обмена");
            }
            catch (Exception ex)
            {
                AddLog ($"❌ Ошибка копирования логов: {ex.Message}");
            }
        }

        private void SendImportantToTelegram(string message)
        {
            bool isImportant = message.Contains ("❌") ||
                               message.Contains ("✅ КУПЛЕНО") ||
                               message.Contains ("🔒 ЗАКРЫТА") ||
                               message.Contains ("Ошибка") ||
                               message.Contains ("⚠️ Баланс") ||
                               message.Contains ("Ребаланс") ||
                               message.Contains ("Подключение");

            if (isImportant && _tradingService != null)
            {
                _ = Task.Run (async () =>
                {
                    try
                    {
                        var telegramField = typeof (TradingService).GetField ("_telegram",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (telegramField != null)
                        {
                            var telegram = telegramField.GetValue (_tradingService) as TelegramNotifier;
                            if (telegram != null && telegram.IsEnabled)
                            {
                                await telegram.SendMessageAsync (message);
                            }
                        }
                    }
                    catch { }
                });
            }
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

                try
                {
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
                }
                finally
                {
                    _isLoadingSettings = false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine ($"LoadSettings error: {ex.Message}");
            }
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

        private async Task Start()
        {
            IsRunning = true;
            AddLog ("🚀 Запуск торгового бота...");
            await _tradingService.StartTradingAsync (this);
        }

        private void Stop()
        {
            _tradingService.StopTrading ();
            IsRunning = false;
            AddLog ("⏹ Торговля остановлена");
        }

        private async Task EmergencyStop()
        {
            AddLog ("🚨 АВАРИЙНАЯ ОСТАНОВКА: отмена всех ордеров...");

            // 1. Останавливаем торговлю
            _tradingService.StopTrading ();
            IsRunning = false;

            // 2. Отменяем все активные ордера на Binance
            try
            {
                var client = _tradingService.GetBinanceClient ();
                var activePairs = _tradingSettings?.GridSymbol != null
                    ? new List<string> { _tradingSettings.GridSymbol }
                    : new List<string> { "BTCUSDC", "ETHUSDC", "SOLUSDC" };

                foreach (string symbol in activePairs)
                {
                    try
                    {
                        var orders = await client.GetAllOrdersAsync (symbol);
                        if (orders != null)
                        {
                            foreach (var order in orders)
                            {
                                if (order["status"]?.ToString () == "NEW")
                                {
                                    long orderId = Convert.ToInt64 (order["orderId"]);
                                    await client.CancelOrder (symbol, orderId);
                                    AddLog ($"❌ Отменён ордер {orderId} на {symbol}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog ($"⚠️ Ошибка отмены ордеров {symbol}: {ex.Message}");
                    }
                }

                AddLog ("🚨 Аварийная остановка завершена");
            }
            catch (Exception ex)
            {
                AddLog ($"❌ Ошибка аварийной остановки: {ex.Message}");
            }
        }

        private async Task RunOptimization()
        {
            AddLog ("🧠 Запуск автоматической оптимизации стратегии...");
            AddLog ("⏳ Это может занять 2-3 минуты...");

            decimal balance = _tradingService?.GetBinanceClient () != null
                ? await _tradingService.GetBinanceClient ().GetAccountBalanceAsync ("USDC")
                : 0;
            var optimizer = new StrategyOptimizer (_tradingService.GetBinanceClient (), this, AddLog);
            bool success = await optimizer.RunOptimizationAsync (balance);

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

        public void UpdateWalletDisplay(string balance)
        {
            Application.Current.Dispatcher.Invoke (() =>
            {
                WalletBalance = balance;
            });
        }

        public void AddBalancePoint(DateTime time, decimal balance)
        {
            Application.Current.Dispatcher.Invoke (() =>
            {
                if (_plotModel.Series[0] is LineSeries series)
                {
                    series.Points.Add (new DataPoint (DateTimeAxis.ToDouble (time), (double)balance));
                    if (series.Points.Count > 200) series.Points.RemoveAt (0);
                    _plotModel.InvalidatePlot (true);
                }
            });
        }

        public List<Dictionary<string, object>> GetBalanceHistory ()
        {
            var result = new List<Dictionary<string, object>> ();
            Application.Current.Dispatcher.Invoke (() =>
            {
                if (_plotModel?.Series.Count > 0 && _plotModel.Series[0] is LineSeries series)
                {
                    foreach (var pt in series.Points)
                    {
                        result.Add (new Dictionary<string, object>
                        {
                            ["time"] = DateTimeAxis.ToDouble (pt.X),
                            ["balance"] = (decimal)pt.Y
                        });
                    }
                }
            });
            return result;
        }

        public void RestoreBalanceHistory (List<Dictionary<string, object>> points)
        {
            if (points == null || points.Count == 0) return;
            Application.Current.Dispatcher.Invoke (() =>
            {
                if (_plotModel?.Series.Count > 0 && _plotModel.Series[0] is LineSeries series)
                {
                    series.Points.Clear ();
                    foreach (var pt in points)
                    {
                        if (pt.TryGetValue ("time", out object timeObj) && pt.TryGetValue ("balance", out object balObj))
                        {
                            double time = Convert.ToDouble (timeObj);
                            decimal balance = Convert.ToDecimal (balObj);
                            series.Points.Add (new DataPoint (time, (double)balance));
                        }
                    }
                    _plotModel.InvalidatePlot (true);
                }
            });
        }

        public void UpdateMarketTable(string pair, string price, bool hasPosition, TradeAction signal, decimal fastSma, decimal slowSma, decimal? marketCap = null, decimal? sentiment = null, decimal rsi = 50, decimal macdHist = 0, string session = "")
        {
            Application.Current.Dispatcher.Invoke (() =>
            {
                bool rsiConfirmsBuy = signal == TradeAction.Buy && rsi < 50;
                bool rsiConfirmsSell = signal == TradeAction.Sell && rsi > 50;
                bool macdConfirmsBuy = signal == TradeAction.Buy && macdHist > 0;
                bool macdConfirmsSell = signal == TradeAction.Sell && macdHist < 0;
                bool strongBuy = rsiConfirmsBuy || macdConfirmsBuy;
                bool strongSell = rsiConfirmsSell || macdConfirmsSell;

                SolidColorBrush bgBrush;
                SolidColorBrush fgBrush;
                if (hasPosition)
                {
                    bgBrush = new SolidColorBrush (Color.FromRgb (0, 100, 0));
                    fgBrush = new SolidColorBrush (Color.FromRgb (144, 238, 144));
                }
                else if (signal == TradeAction.Buy)
                {
                    bgBrush = strongBuy
                        ? new SolidColorBrush (Color.FromRgb (0, 120, 0))
                        : new SolidColorBrush (Color.FromRgb (0, 50, 120));
                    fgBrush = strongBuy
                        ? new SolidColorBrush (Color.FromRgb (100, 255, 100))
                        : new SolidColorBrush (Color.FromRgb (100, 200, 255));
                }
                else if (signal == TradeAction.Sell)
                {
                    bgBrush = strongSell
                        ? new SolidColorBrush (Color.FromRgb (160, 0, 0))
                        : new SolidColorBrush (Color.FromRgb (130, 50, 0));
                    fgBrush = strongSell
                        ? new SolidColorBrush (Color.FromRgb (255, 120, 120))
                        : new SolidColorBrush (Color.FromRgb (255, 180, 100));
                }
                else
                {
                    bgBrush = new SolidColorBrush (Color.FromRgb (25, 25, 25));
                    fgBrush = new SolidColorBrush (Color.FromRgb (160, 160, 160));
                }

                string mcapStr = "—";
                if (marketCap.HasValue && marketCap.Value > 0)
                {
                    if (marketCap.Value >= 1_000_000_000_000m) mcapStr = $"{marketCap.Value / 1_000_000_000_000m:F1}T";
                    else if (marketCap.Value >= 1_000_000_000m) mcapStr = $"{marketCap.Value / 1_000_000_000m:F1}B";
                    else if (marketCap.Value >= 1_000_000m) mcapStr = $"{marketCap.Value / 1_000_000m:F1}M";
                    else mcapStr = $"{marketCap.Value / 1_000m:F1}K";
                }

                string sentStr = "⚪ —";
                if (sentiment.HasValue)
                {
                    decimal s = sentiment.Value;
                    sentStr = s > 0.3m ? $"🟢 {s:F2}" : s < -0.3m ? $"🔴 {s:F2}" : $"⚪ {s:F2}";
                }

                string rsiTag = strongBuy ? "RSI:BUY" : strongSell ? "RSI:SELL" : "";
                string macdTag = macdConfirmsBuy ? "MACD+" : macdConfirmsSell ? "MACD-" : "";
                string strengthTag = "";
                if (signal == TradeAction.Buy && (strongBuy || strongSell) && !strongSell)
                    strengthTag = "STRONG ";
                else if (signal == TradeAction.Sell && strongSell)
                    strengthTag = "STRONG ";

                string signalLabel = $"{strengthTag}{signal}";
                string rsiStr = rsi < 30 ? $"RSI:{rsi:F0}\u2193" : rsi > 70 ? $"RSI:{rsi:F0}\u2191" : $"RSI:{rsi:F0}";
                string macdStr = macdHist > 0 ? $"MACD:+{macdHist:F4}" : $"MACD:{macdHist:F4}";
                string confirmStr = "";
                if (strongBuy) confirmStr = " \u2705";
                else if (strongSell) confirmStr = " \u274C";
                string analysisText = $"{signalLabel} | F:{fastSma:F2} / S:{slowSma:F2} | {rsiStr} {macdStr}{confirmStr}";

                if (_pairDict.TryGetValue (pair, out var existing))
                {
                    existing.Price = price;
                    existing.Analysis = analysisText;
                    existing.MarketCap = mcapStr;
                    existing.Sentiment = sentStr;
                    existing.Session = session;
                    existing.RowColor = bgBrush;
                    existing.ForegroundBrush = fgBrush;
                }
                else
                {
                    var newItem = new PairAnalysisItem
                    {
                        Pair = pair,
                        Price = price,
                        Analysis = analysisText,
                        MarketCap = mcapStr,
                        Sentiment = sentStr,
                        Session = session,
                        RowColor = bgBrush,
                        ForegroundBrush = fgBrush
                    };
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
                foreach (var item in toRemove)
                {
                    _pairDict.Remove (item.Pair);
                    PairsList.Remove (item);
                }
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
            while (!_bgCts.Token.IsCancellationRequested)
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
                    await Task.Delay (30000, _bgCts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    AddLog ($"❌ Ошибка в StocksLoop: {ex.Message}");
                    await Task.Delay (30000, _bgCts.Token);
                }
            }
        }

        private async Task StartUiUpdateLoop()
        {
            while (!_bgCts.Token.IsCancellationRequested)
            {
                try
                {
                    // Fetch prices on background thread, then update UI
                    var pairsSnapshot = PairsList.ToList ();
                    var prices = new Dictionary<string, decimal> ();
                    foreach (var pairItem in pairsSnapshot)
                    {
                        decimal price = _tradingService.GetCurrentPriceForSymbol (pairItem.Pair);
                        if (price > 0)
                            prices[pairItem.Pair] = price;
                    }
                    await Application.Current.Dispatcher.InvokeAsync (() =>
                    {
                        foreach (var pairItem in PairsList)
                        {
                            if (prices.TryGetValue (pairItem.Pair, out decimal price))
                                pairItem.Price = price.ToString ("F4");
                        }
                    });
                    await Task.Delay (2000, _bgCts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception)
                {
                    await Task.Delay (10000, _bgCts.Token).ContinueWith (_ => { });
                }
            }
        }

        public void ClearTradeHistory ()
        {
            Application.Current.Dispatcher.Invoke (() =>
            {
                TradesHistory.Clear ();
                TotalTrades = 0;
                WinningTrades = 0;
                LosingTrades = 0;
                TotalPnL = 0;
                WinRate = 0;
                BestPnL = 0;
                WorstPnL = 0;
                _totalProfitSum = 0;
                _totalLossSum = 0;
                AvgProfitLossDisplay = "Ср. приб/убыток: 0.00 / 0.00";
            });
        }

        public void AddTradeToHistory(TradeLog trade, bool silent = false)
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

                if (!silent)
                    AddLog ($"📊 Сделка {trade.Symbol}: {trade.Action} PnL={trade.PnL:F2} ({trade.PnLPercent:F2}%)");
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
            OnPropertyChanged (nameof (MaxConcurrentTrades));
        }

        public void UpdateRiskDisplay(decimal riskPercent)
        {
            Application.Current.Dispatcher.Invoke (() =>
                RiskPercentDisplay = $"Риск: {riskPercent * 100:F0}%"
            );
        }

        public int RsiPeriod
        {
            get => _tradingSettings?.RsiPeriod ?? 14;
            set { if (_tradingSettings != null) { _tradingSettings.RsiPeriod = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public decimal MinTradeAmount
        {
            get => _tradingSettings?.MinTradeAmount ?? 10;
            set { if (_tradingSettings != null) { _tradingSettings.MinTradeAmount = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public decimal MaxTradeAmount
        {
            get => _tradingSettings?.MaxTradeAmount ?? 50;
            set { if (_tradingSettings != null) { _tradingSettings.MaxTradeAmount = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public int MaxConcurrentTrades
        {
            get => _tradingSettings?.MaxConcurrentTrades ?? 2;
            set { if (_tradingSettings != null) { _tradingSettings.MaxConcurrentTrades = Math.Clamp (value, 1, 10); OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public decimal RiskPerTradePercent
        {
            get => _tradingSettings?.RiskPerTradePercent ?? 0.01m;
            set
            {
                if (_tradingSettings != null)
                {
                    _tradingSettings.RiskPerTradePercent = Math.Clamp (value, 0.05m, 0.25m);
                    OnPropertyChanged ();
                    SaveTradingSettings ();
                    UpdateRiskDisplay (_tradingSettings.RiskPerTradePercent);
                }
            }
        }

        public decimal RiskRewardRatio
        {
            get => _tradingSettings?.RiskRewardRatio ?? 3.0m;
            set
            {
                if (_tradingSettings != null)
                {
                    _tradingSettings.RiskRewardRatio = Math.Clamp (value, 1.0m, 5.0m);
                    OnPropertyChanged ();
                    SaveTradingSettings ();
                }
            }
        }

        // Grid Bot свойства
        public bool GridBotEnabled
        {
            get => _tradingSettings?.GridBotEnabled ?? false;
            set { if (_tradingSettings != null) { _tradingSettings.GridBotEnabled = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public string GridSymbol
        {
            get => _tradingSettings?.GridSymbol ?? "BTCUSDC";
            set { if (_tradingSettings != null) { _tradingSettings.GridSymbol = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public decimal GridRangePercent
        {
            get => _tradingSettings?.GridRangePercent ?? 0.10m;
            set { if (_tradingSettings != null) { _tradingSettings.GridRangePercent = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public int GridLevels
        {
            get => _tradingSettings?.GridLevels ?? 10;
            set { if (_tradingSettings != null) { _tradingSettings.GridLevels = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public decimal TotalInvestmentPercent
        {
            get => _tradingSettings?.TotalInvestmentPercent ?? 0.20m;
            set { if (_tradingSettings != null) { _tradingSettings.TotalInvestmentPercent = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        // Мульти-таймфрейм свойства
        public string MainTimeframe
        {
            get => _tradingSettings?.MainTimeframe ?? "1h";
            set { if (_tradingSettings != null) { _tradingSettings.MainTimeframe = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public string EntryTimeframe
        {
            get => _tradingSettings?.EntryTimeframe ?? "5m";
            set { if (_tradingSettings != null) { _tradingSettings.EntryTimeframe = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        // Дополнительные стратегии
        public bool VolumeBreakoutEnabled
        {
            get => _tradingSettings?.VolumeBreakoutEnabled ?? false;
            set { if (_tradingSettings != null) { _tradingSettings.VolumeBreakoutEnabled = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public bool DcaEnabled
        {
            get => _tradingSettings?.DcaEnabled ?? false;
            set { if (_tradingSettings != null) { _tradingSettings.DcaEnabled = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        // Фьючерсы
        public bool FuturesEnabled
        {
            get => _tradingSettings?.FuturesEnabled ?? false;
            set { if (_tradingSettings != null) { _tradingSettings.FuturesEnabled = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public int FuturesLeverage
        {
            get => _tradingSettings?.FuturesLeverage ?? 3;
            set { if (_tradingSettings != null) { _tradingSettings.FuturesLeverage = Math.Clamp (value, 1, 20); OnPropertyChanged (); SaveTradingSettings (); } }
        }

        // Эшелоны ИИ (Золотая архитектура)
        public bool AdaptiveAgentEnabled
        {
            get => _tradingSettings?.AdaptiveAgentEnabled ?? true;
            set { if (_tradingSettings != null) { _tradingSettings.AdaptiveAgentEnabled = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public decimal AdaptiveSlMultiplier
        {
            get => _tradingSettings?.AdaptiveSlMultiplier ?? 0.4m;
            set { if (_tradingSettings != null) { _tradingSettings.AdaptiveSlMultiplier = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public decimal AdaptivePeriodMultiplier
        {
            get => _tradingSettings?.AdaptivePeriodMultiplier ?? 0.3m;
            set { if (_tradingSettings != null) { _tradingSettings.AdaptivePeriodMultiplier = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public bool SignalValidatorEnabled
        {
            get => _tradingSettings?.SignalValidatorEnabled ?? true;
            set { if (_tradingSettings != null) { _tradingSettings.SignalValidatorEnabled = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public decimal ValidatorVolumeThreshold
        {
            get => _tradingSettings?.ValidatorVolumeThreshold ?? 8.0m;
            set { if (_tradingSettings != null) { _tradingSettings.ValidatorVolumeThreshold = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public decimal ValidatorAtrThreshold
        {
            get => _tradingSettings?.ValidatorAtrThreshold ?? 0.15m;
            set { if (_tradingSettings != null) { _tradingSettings.ValidatorAtrThreshold = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public int ValidatorRsiLow
        {
            get => _tradingSettings?.ValidatorRsiLow ?? 20;
            set { if (_tradingSettings != null) { _tradingSettings.ValidatorRsiLow = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public int ValidatorRsiHigh
        {
            get => _tradingSettings?.ValidatorRsiHigh ?? 80;
            set { if (_tradingSettings != null) { _tradingSettings.ValidatorRsiHigh = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public bool NewsSentinelEnabled
        {
            get => _tradingSettings?.NewsSentinelEnabled ?? true;
            set { if (_tradingSettings != null) { _tradingSettings.NewsSentinelEnabled = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public int NewsSentinelBlockMinutes
        {
            get => _tradingSettings?.NewsSentinelBlockMinutes ?? 5;
            set { if (_tradingSettings != null) { _tradingSettings.NewsSentinelBlockMinutes = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public bool SessionFilterEnabled
        {
            get => _tradingSettings?.SessionFilterEnabled ?? false;
            set { if (_tradingSettings != null) { _tradingSettings.SessionFilterEnabled = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public bool TradeOnlyEuUs
        {
            get => _tradingSettings?.TradeOnlyEuUs ?? false;
            set { if (_tradingSettings != null) { _tradingSettings.TradeOnlyEuUs = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        public string QuoteCurrency
        {
            get => _tradingSettings?.QuoteCurrency ?? "USDC";
            set { if (_tradingSettings != null) { _tradingSettings.QuoteCurrency = value; OnPropertyChanged (); SaveTradingSettings (); } }
        }

        private async void SaveTradingSettings()
        {
            try
            {
                if (_tradingSettings != null)
                    await _tradingSettings.SaveAsync ();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine ($"SaveTradingSettings error: {ex.Message}");
            }
        }

        private TradingSettings LoadTradingSettingsSync()
        {
            string settingsPath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Data", "trading_settings.json");
            if (!File.Exists (settingsPath))
                return new TradingSettings ();

            try
            {
                string json = File.ReadAllText (settingsPath);
                return System.Text.Json.JsonSerializer.Deserialize<TradingSettings> (json) ?? new TradingSettings ();
            }
            catch
            {
                return new TradingSettings ();
            }
        }

        public void Dispose ()
        {
            try { _bgCts?.Cancel (); } catch (ObjectDisposedException) { }
            try { _bgCts?.Dispose (); } catch (ObjectDisposedException) { }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke (this, new PropertyChangedEventArgs (name));
    }
}