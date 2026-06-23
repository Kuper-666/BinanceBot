#nullable enable
using System;
using System.IO;
using System.Globalization;
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

        protected override async void OnStartup(StartupEventArgs e)
        {
            // Глобальный обработчик неотслеживаемых исключений Task (WebSocket обрывы и т.д.)
            TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                System.Diagnostics.Debug.WriteLine ($"[UnobservedTask] {args.Exception?.InnerException?.Message ?? args.Exception?.Message}");
                args.SetObserved ();
            };
            // Защита от запуска нескольких копий
            const string mutexName = "Global\\{B9E8F2A1-5C7D-4A3E-8F2C-9D7E5B4A3C2F}";
            bool createdNew;
            _appMutex = new Mutex (true, mutexName, out createdNew);
            if (!createdNew)
            {
                MessageBox.Show ("Бот уже запущен!", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                Current.Shutdown ();
                return;
            }

            base.OnStartup (e);

            // Загрузка конфигурации: новый зашифрованный config.json,
            // либо миграция со старого открытого config.txt при первом запуске после обновления
            BotConfig config;
            try
            {
                config = BotConfig.LoadOrMigrate (out bool wasMigrated);
                if (wasMigrated)
                {
                    MessageBox.Show (
                        "Конфигурация перенесена из config.txt в зашифрованный config.json.\n\n" +
                        "Старый файл переименован в config.txt.bak — он больше не используется ботом " +
                        "и хранит ключи в открытом виде. Рекомендуем удалить его вручную после проверки, " +
                        "что бот запускается корректно.",
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

            // Создание config.json при первом запуске (ни config.json, ни config.txt не найдены)
            if (config == null)
            {
                try
                {
                    BotConfig.CreateDefault ();
                    MessageBox.Show ("Создан файл config.json.\n\nПожалуйста, заполните свои API-ключи и перезапустите бота.\n\nКлючи будут автоматически зашифрованы при сохранении через интерфейс или при первом успешном запуске после ручного редактирования.",
                                    "Настройка", MessageBoxButton.OK, MessageBoxImage.Information);
                    Current.Shutdown ();
                    return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show ($"Не удалось создать config.json: {ex.Message}");
                    Current.Shutdown ();
                    return;
                }
            }

            string apiKey = config.ApiKey;
            string apiSecret = config.ApiSecret;
            bool isTestnet = config.IsTestnet;
            decimal minUsdcBalance = config.MinUsdcBalance;
            string telegramBotToken = config.TelegramBotToken;
            string telegramChatId = config.TelegramChatId;

            // Проверка наличия API ключей
            if (string.IsNullOrEmpty (apiKey) || string.IsNullOrEmpty (apiSecret))
            {
                MessageBox.Show ("В config.json не указаны ApiKey или ApiSecret.\n\nЗаполните их и перезапустите бота.",
                                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                Current.Shutdown ();
                return;
            }

            if (apiKey == "YOUR_API_KEY_HERE" || apiSecret == "YOUR_API_SECRET_HERE")
            {
                MessageBox.Show ("Пожалуйста, замените YOUR_API_KEY_HERE и YOUR_API_SECRET_HERE на реальные API ключи в config.json",
                                "Настройка", MessageBoxButton.OK, MessageBoxImage.Warning);
                Current.Shutdown ();
                return;
            }

            // Инициализация бота
            try
            {
                // Создаём клиент Binance
                var binanceClient = new BinanceClient (apiKey, apiSecret, isTestnet);

                // Синхронизируем время с сервером Binance
                await binanceClient.SyncTimeAsync ();

                // Проверяем подключение (опционально, но полезно)
                string serverInfo = await binanceClient.GetServerInfo ();
                System.Diagnostics.Debug.WriteLine ($"Сервер: {serverInfo}");

                // Инициализация сервисов
                object consoleLock = new object ();
                var walletManager = new WalletManager (binanceClient);
                var earnManager = new EarnManager (consoleLock);
                var rebalancer = new BalanceRebalancer (consoleLock, 0.1m);

                // Подписываемся на события логирования (вывод в Debug)
                walletManager.OnLogGenerated += (msg) => System.Diagnostics.Debug.WriteLine ($"[Wallet] {msg}");
                earnManager.OnLogGenerated += (msg) => System.Diagnostics.Debug.WriteLine ($"[Earn] {msg}");
                rebalancer.OnLogGenerated += (msg) => System.Diagnostics.Debug.WriteLine ($"[Rebalancer] {msg}");
                binanceClient.OnLogGenerated += (msg) => System.Diagnostics.Debug.WriteLine ($"[Binance] {msg}");

                // Создаём TradingService
                var tradingService = new TradingService (
                    binanceClient,
                    walletManager,
                    earnManager,
                    rebalancer,
                    minUsdcBalance,
                    telegramBotToken,
                    telegramChatId);

                // Создаём ViewModel
                var viewModel = new MainWindowViewModel (tradingService, isTestnet);
                
                // Сразу устанавливаем логгер, чтобы инициализировать Telegram
                tradingService.SetLogger(viewModel.AddLog);

                // Создаём и показываем главное окно
                var mainWindow = new MainWindow (viewModel);

                // Устанавливаем иконку окна
                try
                {
                    string iconPath = System.IO.Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "binance_bot.ico");
                    if (File.Exists (iconPath))
                        mainWindow.Icon = new BitmapImage (new Uri (iconPath));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine ($"Не удалось загрузить иконку: {ex.Message}");
                }

                mainWindow.Title = $"Торговый помощник v{AppConstants.AppVersion}";
                mainWindow.Show ();

                // Логируем запуск
                viewModel.AddLog ($"🚀 Бот запущен в режиме: {( isTestnet ? "ТЕСТОВАЯ СЕТЬ" : "РЕАЛЬНАЯ СЕТЬ" )}");
                viewModel.AddLog ($"🔌 API Key: {apiKey.Substring (0, Math.Min (8, apiKey.Length))}...");
                viewModel.AddLog ($"📡 Статус подключения: {serverInfo}");

                // Загружаем пары для отображения в таблице (после SetLogger)
                _ = Task.Run (async () =>
                {
                    await Task.Delay (1000);
                    await viewModel.LoadPairsOnStartupAsync ();
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show (
                    $"Ошибка инициализации бота:\n\n" +
                    $"Сообщение: {ex.Message}\n" +
                    $"Тип: {ex.GetType ().Name}\n" +
                    $"Стек вызовов: {ex.StackTrace}\n\n" +
                    $"Проверьте подключение к интернету и правильность API ключей.",
                    "Критическая ошибка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Current.Shutdown ();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _appMutex?.ReleaseMutex ();
            _appMutex?.Dispose ();
            base.OnExit (e);
        }
    }
}