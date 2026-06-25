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

namespace BinanceBotWpf
{
    public partial class App : Application
    {
        private static Mutex? _appMutex;
        private static string LogDir => Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Logs");
        private static ServiceProvider? _serviceProvider;
        private static FileLogger? _fileLogger;

        protected override async void OnStartup (StartupEventArgs e)
        {
            base.OnStartup (e);

            _fileLogger = new FileLogger ();
            _fileLogger.Info ("App", "Запуск приложения");

            // #27: Глобальный обработчик исключений UI
            DispatcherUnhandledException += (sender, args) =>
            {
                string msg = $"[UI Exception] {args.Exception}";
                _fileLogger?.Error ("UI", msg);
                WriteCrashLog (msg);
                args.Handled = true;
            };

            // #1: Глобальный обработчик неотслеживаемых исключений Task
            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                string msg = $"[UnobservedTask] {args.Exception?.InnerException?.Message ?? args.Exception?.Message}";
                _fileLogger?.Error ("Task", msg);
                WriteCrashLog (msg);
                args.SetObserved ();
            };

            // #3: Защита от запуска нескольких копий + гарантированное освобождение Mutex
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
                // #7: Логируем полный стек исключения
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
            // #2: Загрузка конфигурации
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

            // #6: Валидация Telegram-токена и ChatId
            bool telegramConfigured = !string.IsNullOrEmpty (telegramBotToken) && !string.IsNullOrEmpty (telegramChatId);

            // Валидация API ключей
            if (string.IsNullOrEmpty (apiKey) || string.IsNullOrEmpty (apiSecret) ||
                apiKey == "YOUR_API_KEY_HERE" || apiSecret == "YOUR_API_SECRET_HERE")
            {
                MessageBox.Show (
                    "В config.json не указаны или не заменены ApiKey/ApiSecret.\n\nЗаполните их и перезапустите бота.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                Current.Shutdown ();
                return;
            }

            // Инициализация бота
            var binanceClient = new BinanceClient (apiKey, apiSecret, isTestnet);
            await binanceClient.SyncTimeAsync ();

            // #8: Проверка serverInfo
            string serverInfo;
            try
            {
                serverInfo = await binanceClient.GetServerInfo ();
            }
            catch
            {
                serverInfo = "Не удалось подключиться к серверу";
            }

            // Создаём сервисы
            var walletManager = new WalletManager (binanceClient);
            var earnManager = new EarnManager ();
            var rebalancer = new BalanceRebalancer (0.1m);

            walletManager.OnLogGenerated += (msg) => _fileLogger?.Info ("Wallet", msg);
            earnManager.OnLogGenerated += (msg) => _fileLogger?.Info ("Earn", msg);
            rebalancer.OnLogGenerated += (msg) => _fileLogger?.Info ("Rebalancer", msg);
            binanceClient.OnLogGenerated += (msg) => _fileLogger?.Info ("Binance", msg);

            var tradingService = new TradingService (
                binanceClient, walletManager, earnManager, rebalancer,
                minUsdcBalance, telegramBotToken, telegramChatId);

            // #22: Регистрация сервисов через DI
            _serviceProvider = new ServiceProvider ();
            _serviceProvider.Register (binanceClient);
            _serviceProvider.Register<ITradingService> (tradingService);
            _serviceProvider.Register (walletManager);
            _serviceProvider.Register (earnManager);
            _serviceProvider.Register (rebalancer);

            var viewModel = new MainWindowViewModel (tradingService, isTestnet);
            tradingService.SetLogger (viewModel.AddLog);

            var mainWindow = new MainWindow (viewModel);

            // #17: Использование встроенного ресурса для иконки
            try
            {
                var iconUri = new Uri ("pack://application:,,,/Resources/favicon.ico", UriKind.Absolute);
                mainWindow.Icon = BitmapFrame.Create (iconUri);
            }
            catch
            {
                // Fallback: ищем на диске
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

            // #5: Маскировка API ключа в логе
            string maskedKey = apiKey.Length > 4
                ? new string ('*', apiKey.Length - 4) + apiKey[^4..]
                : "****";
            viewModel.AddLog ($"🚀 Бот запущен: {(isTestnet ? "ТЕСТОВАЯ СЕТЬ" : "РЕАЛЬНАЯ СЕТЬ")}");
            viewModel.AddLog ($"🔌 API Key: {maskedKey}");
            viewModel.AddLog ($"📡 Сервер: {serverInfo}");
            if (!telegramConfigured)
            {
                viewModel.AddLog ("⚠️ Telegram не настроен — уведомления отключены");
            }

            // #4: Task.Run с обработкой ошибок
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

        protected override void OnExit (ExitEventArgs e)
        {
            _fileLogger?.Info ("App", "Завершение приложения");
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
