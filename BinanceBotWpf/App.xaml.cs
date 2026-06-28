#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using BinanceBotWpf.Models;
using BinanceBotWpf.Risk;
using BinanceBotWpf.Services;
using BinanceBotWpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace BinanceBotWpf
{
    public partial class App : Application
    {
        private static Mutex? _appMutex;
        private static string LogDir => Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Logs");
        private static IServiceProvider? _serviceProvider;
        private static FileLogger? _fileLogger;

        protected override async void OnStartup (StartupEventArgs e)
        {
            base.OnStartup (e);

            _fileLogger = new FileLogger ();
            ServiceLogger.Instance.SetFileLogger (_fileLogger);
            _fileLogger.Info ("App", "Запуск приложения");

            DispatcherUnhandledException += (sender, args) =>
            {
                string msg = $"[UI Exception] {args.Exception}";
                _fileLogger?.Error ("UI", msg);
                WriteCrashLog (msg);
                args.Handled = true;
            };

            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                string msg = $"[UnobservedTask] {args.Exception?.InnerException?.Message ?? args.Exception?.Message}";
                _fileLogger?.Error ("Task", msg);
                WriteCrashLog (msg);
                args.SetObserved ();
            };

            const string mutexName = "Global\\{B9E8F2A1-5C7D-4A3E-8F2C-9D7E5B4A3C2F}";
            bool createdNew;
            _appMutex = new Mutex (true, mutexName, out createdNew);
            if (!createdNew)
            {
                MessageBox.Show ("Бот уже запущен!", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                Current.Shutdown ();
                return;
            }

            try
            {
                await StartBotAsync ();
            }
            catch (Exception ex)
            {
                string fullError = $"[FATAL] {ex}";
                System.Diagnostics.Debug.WriteLine (fullError);
                WriteCrashLog (fullError);

                MessageBox.Show (
                    $"Критическая ошибка:\n\n{ex.Message}\n\nПодробности в Logs/crash.log",
                    "Критическая ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                Current.Shutdown ();
            }
        }

        private async Task StartBotAsync ()
        {
            BotConfig? config;
            try
            {
                config = BotConfig.LoadOrMigrate (out bool wasMigrated);
                if (wasMigrated)
                {
                    MessageBox.Show (
                        "Конфигурация перенесена из config.txt в зашифрованный config.json.\n\n" +
                        "Старый файл переименован в config.txt.bak. Рекомендуем удалить его вручную.",
                        "Миграция конфигурации", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show ($"Ошибка чтения конфигурации: {ex.Message}\n\nПроверьте формат файла config.json.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                Current.Shutdown ();
                return;
            }

            if (config == null)
            {
                try
                {
                    BotConfig.CreateDefault ();
                    MessageBox.Show (
                        "Создан файл config.json.\n\nЗаполните API-ключи и перезапустите бота.",
                        "Настройка", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show ($"Не удалось создать config.json: {ex.Message}");
                }
                Current.Shutdown ();
                return;
            }

            string apiKey = config.ApiKey;
            string apiSecret = config.ApiSecret;
            bool isTestnet = config.IsTestnet;
            decimal minUsdcBalance = config.MinUsdcBalance;
            string telegramBotToken = config.TelegramBotToken;
            string telegramChatId = config.TelegramChatId;

            bool telegramConfigured = !string.IsNullOrEmpty (telegramBotToken) && !string.IsNullOrEmpty (telegramChatId);

            if (string.IsNullOrEmpty (apiKey) || string.IsNullOrEmpty (apiSecret) ||
                apiKey == "YOUR_API_KEY_HERE" || apiSecret == "YOUR_API_SECRET_HERE")
            {
                MessageBox.Show (
                    "В config.json не указаны или не заменены ApiKey/ApiSecret.\n\nЗаполните их и перезапустите бота.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                Current.Shutdown ();
                return;
            }

            var services = new ServiceCollection ();
            ConfigureServices (services, apiKey, apiSecret, isTestnet, minUsdcBalance, telegramBotToken, telegramChatId);
            _serviceProvider = services.BuildServiceProvider ();

            var binanceClient = _serviceProvider.GetRequiredService<IBinanceClient> ();
            await binanceClient.SyncTimeAsync ();

            string serverInfo;
            try
            {
                serverInfo = await ((BinanceClient)binanceClient).GetServerInfo ();
            }
            catch
            {
                serverInfo = "Не удалось подключиться к серверу";
            }

            var viewModel = _serviceProvider.GetRequiredService<MainWindowViewModel> ();
            var tradingService = _serviceProvider.GetRequiredService<TradingService> ();
            tradingService.SetLogger (viewModel.AddLog);

            var mainWindow = new MainWindow (viewModel);

            try
            {
                var iconUri = new Uri ("pack://application:,,,/Resources/favicon.ico", UriKind.Absolute);
                mainWindow.Icon = BitmapFrame.Create (iconUri);
            }
            catch
            {
                try
                {
                    string iconPath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "binance_bot.ico");
                    if (File.Exists (iconPath))
                        mainWindow.Icon = new BitmapImage (new Uri (iconPath));
                }
                catch { }
            }

            mainWindow.Title = $"Торговый помощник v{AppConstants.AppVersion}";
            mainWindow.Show ();

            viewModel.AddLog ($"🚀 Бот запущен: {(isTestnet ? "ТЕСТОВАЯ СЕТЬ" : "РЕАЛЬНАЯ СЕТЬ")}");
            viewModel.AddLog ($"📡 Сервер: {serverInfo}");
            if (!telegramConfigured)
            {
                viewModel.AddLog ("⚠️ Telegram не настроен — уведомления отключены");
            }

            _ = Task.Run (async () =>
            {
                try
                {
                    await Task.Delay (1000);
                    await viewModel.LoadPairsOnStartupAsync ();
                }
                catch (Exception ex)
                {
                    viewModel.AddLog ($"❌ Ошибка загрузки пар: {ex.Message}");
                }
            });
        }

        private static void ConfigureServices (ServiceCollection services,
            string apiKey, string apiSecret, bool isTestnet,
            decimal minUsdcBalance, string telegramBotToken, string telegramChatId)
        {
            Action<string> fileLogger = msg => _fileLogger?.Info ("App", msg);
            string dataDir = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Data");
            string modelPath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "trading_model.zip");
            string positionsPath = Path.Combine (dataDir, "open_positions.json");
            var sharedHttpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds (30) };

            // BotConfig
            services.AddSingleton<BotConfig> (sp => BotConfig.LoadOrMigrate (out _));

            // Core services
            services.AddSingleton<IEventBus, EventBus> ();
            services.AddSingleton<IBinanceClient> (sp => new BinanceClient (apiKey, apiSecret, isTestnet));
            services.AddSingleton<IWalletManager> (sp =>
            {
                var client = (BinanceClient)sp.GetRequiredService<IBinanceClient> ();
                var wallet = new WalletManager (client);
                wallet.OnLogGenerated += fileLogger;
                return wallet;
            });
            services.AddSingleton<IEarnManager> (sp =>
            {
                var earn = new EarnManager ();
                earn.OnLogGenerated += fileLogger;
                return earn;
            });
            services.AddSingleton<IBalanceRebalancer> (sp =>
            {
                var rebalancer = new BalanceRebalancer ();
                rebalancer.OnLogGenerated += fileLogger;
                return rebalancer;
            });
            services.AddSingleton<IPositionManager> (sp => new PositionManager (positionsPath, fileLogger));

            // ML and strategy
            services.AddSingleton<IMlModelManager> (sp => new MlModelManager (modelPath, fileLogger));
            services.AddSingleton<ITradingStrategy> (sp => new TradingStrategy (fileLogger));
            services.AddSingleton<ISignalFilter> (sp => new SignalFilter (fileLogger));

            // Protection and strategies
            services.AddSingleton<IPositionProtector> (sp =>
                new PositionProtector (
                    (BinanceClient)sp.GetRequiredService<IBinanceClient> (),
                    (PositionManager)sp.GetRequiredService<IPositionManager> (),
                    fileLogger));
            services.AddSingleton<IVolumeBreakoutStrategy> (sp =>
                new VolumeBreakoutStrategy (
                    (BinanceClient)sp.GetRequiredService<IBinanceClient> (), fileLogger));
            services.AddSingleton<IDcaStrategy> (sp =>
                new DCAStrategy (
                    (BinanceClient)sp.GetRequiredService<IBinanceClient> (), fileLogger));

            // Providers
            services.AddSingleton<INewsProvider> (sp => new NewsProvider (sharedHttpClient, fileLogger));
            services.AddSingleton<IMacroCalendarProvider> (sp => new MacroCalendarProvider (sharedHttpClient, fileLogger));
            services.AddSingleton<IFearGreedIndexProvider> (sp => new FearGreedIndexProvider (fileLogger));
            services.AddSingleton<IPriceAlertManager> (sp =>
            {
                var mgr = new PriceAlertManager (
                    (Func<string, decimal>)(sym => 0),
                    null,
                    fileLogger);
                return mgr;
            });

            // Infrastructure
            services.AddSingleton<TradingSettings> (sp => TradingSettings.LoadAsync ().GetAwaiter ().GetResult ());
            services.AddSingleton<IBackupService> (sp => new BackupService (fileLogger));
            services.AddSingleton<IRiskManager> (sp => new Risk.RiskManager ());
            services.AddSingleton<IAiRiskEngine> (sp =>
                new AiRiskEngine (
                    (MlModelManager)sp.GetRequiredService<IMlModelManager> (),
                    (BinanceClient)sp.GetRequiredService<IBinanceClient> (),
                    fileLogger));
            services.AddSingleton<IDashboardWebSocketServer> (sp =>
                new DashboardWebSocketServer (
                    ServiceLogger.Instance.CreateLogger<DashboardWebSocketServer> ()));
            services.AddSingleton<WebSocketPriceManager> (sp =>
                new WebSocketPriceManager (fileLogger));
            services.AddSingleton<ISimpleEarnStrategy> (sp =>
                new SimpleEarnStrategy (
                    (BinanceClient)sp.GetRequiredService<IBinanceClient> (), fileLogger));

            // Main services
            services.AddSingleton<TradingService> (sp => new TradingService (
                sp.GetRequiredService<IBinanceClient> (),
                sp.GetRequiredService<IWalletManager> (),
                sp.GetRequiredService<IEarnManager> (),
                sp.GetRequiredService<IBalanceRebalancer> (),
                sp.GetRequiredService<IPositionManager> (),
                sp.GetRequiredService<IMlModelManager> (),
                sp.GetRequiredService<ITradingStrategy> (),
                sp.GetRequiredService<ISignalFilter> (),
                sp.GetRequiredService<IPositionProtector> (),
                sp.GetRequiredService<IVolumeBreakoutStrategy> (),
                sp.GetRequiredService<IDcaStrategy> (),
                sp.GetRequiredService<INewsProvider> (),
                sp.GetRequiredService<IMacroCalendarProvider> (),
                sp.GetRequiredService<TradingSettings> (),
                sp.GetRequiredService<IBackupService> (),
                sp.GetRequiredService<IAiRiskEngine> (),
                sp.GetRequiredService<IDashboardWebSocketServer> (),
                sp.GetRequiredService<IFearGreedIndexProvider> (),
                sp.GetRequiredService<IPriceAlertManager> (),
                sp.GetRequiredService<IRiskManager> (),
                sp.GetRequiredService<WebSocketPriceManager> (),
                sp.GetRequiredService<ISimpleEarnStrategy> (),
                sp.GetRequiredService<BotConfig> ()));
            services.AddSingleton<MainWindowViewModel> (sp => new MainWindowViewModel (
                sp.GetRequiredService<TradingService> (),
                sp.GetRequiredService<TradingSettings> (),
                isTestnet));
        }

        protected override void OnExit (ExitEventArgs e)
        {
            _fileLogger?.Info ("App", "Завершение приложения");

            if (_serviceProvider is IDisposable disposable)
                disposable.Dispose ();

            _fileLogger?.Dispose ();
            _fileLogger = null;

            try
            {
                _appMutex?.ReleaseMutex ();
            }
            catch { }
            finally
            {
                _appMutex?.Dispose ();
                _appMutex = null;
            }
            base.OnExit (e);
        }

        private static void WriteCrashLog (string message)
        {
            try
            {
                if (!Directory.Exists (LogDir))
                    Directory.CreateDirectory (LogDir);
                string path = Path.Combine (LogDir, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                File.WriteAllText (path, $"{DateTime.UtcNow:O}\n{message}\n");
            }
            catch { }
        }
    }
}
