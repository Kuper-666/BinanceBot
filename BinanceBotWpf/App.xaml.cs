#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using BinanceBotWpf.Models;
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
            services.AddSingleton<IEventBus, EventBus> ();
            services.AddSingleton<IBinanceClient> (sp => new BinanceClient (apiKey, apiSecret, isTestnet));
            services.AddSingleton<IWalletManager> (sp =>
            {
                var client = sp.GetRequiredService<IBinanceClient> ();
                var wallet = new WalletManager ((BinanceClient)client);
                wallet.OnLogGenerated += msg => _fileLogger?.Info ("Wallet", msg);
                return wallet;
            });
            services.AddSingleton (sp => new EarnManager ());
            services.AddSingleton (sp => new BalanceRebalancer ());
            services.AddSingleton<IPositionManager> (sp => new PositionManager (
                Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Data", "open_positions.json"),
                msg => _fileLogger?.Info ("Position", msg)));
            services.AddSingleton (sp => new TradingService (
                (BinanceClient)sp.GetRequiredService<IBinanceClient> (),
                (WalletManager)sp.GetRequiredService<IWalletManager> (),
                sp.GetRequiredService<EarnManager> (),
                sp.GetRequiredService<BalanceRebalancer> (),
                minUsdcBalance, telegramBotToken, telegramChatId));
            services.AddSingleton (sp => new MainWindowViewModel (
                sp.GetRequiredService<TradingService> (), isTestnet));
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
