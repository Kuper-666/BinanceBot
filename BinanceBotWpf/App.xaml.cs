using System;
using System.IO;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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

            string configPath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "config.txt");

            // Создание config.txt при первом запуске
            if (!File.Exists (configPath))
            {
                string sample = @"# Binance Bot Configuration
# Получите API ключи на https://www.binance.com/ru/support/faq/how-to-create-api-keys-on-binance-360002502072

ApiKey=YOUR_API_KEY_HERE
ApiSecret=YOUR_API_SECRET_HERE
isTestnet=true

# Telegram notifications (optional)
# Получите токен у @BotFather в Telegram
telegramBotToken=
telegramChatId=

# Trading parameters
minUsdcBalance=5.50
";
                try
                {
                    File.WriteAllText (configPath, sample);
                    MessageBox.Show ("Создан файл config.txt.\n\nПожалуйста, заполните свои API-ключи и перезапустите бота.\n\nДля тестирования оставьте isTestnet=true",
                                    "Настройка", MessageBoxButton.OK, MessageBoxImage.Information);
                    Current.Shutdown ();
                    return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show ($"Не удалось создать config.txt: {ex.Message}");
                    Current.Shutdown ();
                    return;
                }
            }

            // Чтение конфигурации
            string apiKey = "";
            string apiSecret = "";
            bool isTestnet = true;
            decimal minUsdcBalance = 5.50m;
            string telegramBotToken = "";
            string telegramChatId = "";

            try
            {
                var lines = File.ReadAllLines (configPath);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace (line) || line.StartsWith ("#") || !line.Contains ("=")) continue;
                    var parts = line.Split ('=', 2);
                    string key = parts[0].Trim ().ToLower ();
                    string value = parts[1].Trim ();

                    // Убираем кавычки если есть
                    value = value.Trim ('"', '\'');

                    switch (key)
                    {
                        case "apikey": apiKey = value; break;
                        case "apisecret": apiSecret = value; break;
                        case "istestnet": isTestnet = bool.Parse (value); break;
                        case "minusdcbalance": minUsdcBalance = decimal.Parse (value, CultureInfo.InvariantCulture); break;
                        case "telegrambottoken": telegramBotToken = value; break;
                        case "telegramchatid": telegramChatId = value; break;
                    }
                }

                // Логируем для отладки
                System.Diagnostics.Debug.WriteLine ($"Telegram Token: {( string.IsNullOrEmpty (telegramBotToken) ? "НЕ НАЙДЕН" : "НАЙДЕН" )}");
                System.Diagnostics.Debug.WriteLine ($"Telegram ChatId: {( string.IsNullOrEmpty (telegramChatId) ? "НЕ НАЙДЕН" : "НАЙДЕН" )}");
            }
            catch (Exception ex)
            {
                MessageBox.Show ($"Ошибка чтения config.txt: {ex.Message}\n\nПроверьте формат файла.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                Current.Shutdown ();
                return;
            }

            // Проверка наличия API ключей
            if (string.IsNullOrEmpty (apiKey) || string.IsNullOrEmpty (apiSecret))
            {
                MessageBox.Show ("В config.txt не указаны ApiKey или ApiSecret.\n\nЗаполните их и перезапустите бота.",
                                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                Current.Shutdown ();
                return;
            }

            if (apiKey == "YOUR_API_KEY_HERE" || apiSecret == "YOUR_API_SECRET_HERE")
            {
                MessageBox.Show ("Пожалуйста, замените YOUR_API_KEY_HERE и YOUR_API_SECRET_HERE на реальные API ключи в config.txt",
                                "Настройка", MessageBoxButton.OK, MessageBoxImage.Warning);
                Current.Shutdown ();
                return;
            }

            // Инициализация бота
            try
            {
                // Создаем клиент Binance
                var binanceClient = new BinanceClient (apiKey, apiSecret, isTestnet);

                // Синхронизируем время с сервером Binance (важно для подписей)
                await binanceClient.SyncTimeAsync ();

                // Проверяем подключение к API
                string serverInfo = await binanceClient.GetServerInfo ();

                // Показываем статус подключения
                string connectionStatus = isTestnet ? "ТЕСТОВАЯ СЕТЬ" : "РЕАЛЬНАЯ СЕТЬ";
                MessageBox.Show (
                    $"Подключение к Binance: {serverInfo}\n" +
                    $"Режим: {connectionStatus}\n" +
                    $"API Key: {apiKey.Substring (0, Math.Min (8, apiKey.Length))}...\n\n" +
                    $"Если подключение успешно, бот будет запущен.",
                    "Статус подключения",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                // Проверяем баланс (тестовый запрос)
                try
                {
                    decimal testBalance = await binanceClient.GetAccountBalanceAsync ("USDC");
                    System.Diagnostics.Debug.WriteLine ($"Тестовый баланс USDC: {testBalance}");
                }
                catch (Exception balanceEx)
                {
                    MessageBox.Show (
                        $"Предупреждение: Не удалось получить баланс.\n" +
                        $"Ошибка: {balanceEx.Message}\n\n" +
                        $"Проверьте API ключи и права доступа.",
                        "Предупреждение",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                // Инициализация сервисов
                object consoleLock = new object ();
                var walletManager = new WalletManager (binanceClient);
                var earnManager = new EarnManager (consoleLock);
                var rebalancer = new BalanceRebalancer (consoleLock, 0.1m);

                // Подписываемся на события логирования
                walletManager.OnLogGenerated += (msg) => System.Diagnostics.Debug.WriteLine ($"[Wallet] {msg}");
                earnManager.OnLogGenerated += (msg) => System.Diagnostics.Debug.WriteLine ($"[Earn] {msg}");
                rebalancer.OnLogGenerated += (msg) => System.Diagnostics.Debug.WriteLine ($"[Rebalancer] {msg}");
                binanceClient.OnLogGenerated += (msg) => System.Diagnostics.Debug.WriteLine ($"[Binance] {msg}");

                // Создаем TradingService
                var tradingService = new TradingService (
                    binanceClient,
                    walletManager,
                    earnManager,
                    rebalancer,
                    minUsdcBalance,
                    telegramBotToken,
                    telegramChatId);

                // Создаем ViewModel
                var viewModel = new MainWindowViewModel (tradingService, isTestnet);

                // Создаем и показываем главное окно
                var mainWindow = new MainWindow (viewModel);
                mainWindow.Show ();

                // Добавляем сообщение о запуске в лог
                viewModel.AddLog ($"🚀 Бот запущен в режиме: {( isTestnet ? "ТЕСТОВАЯ СЕТЬ" : "РЕАЛЬНАЯ СЕТЬ" )}");
                viewModel.AddLog ($"🔌 API Key: {apiKey.Substring (0, Math.Min (8, apiKey.Length))}...");
                viewModel.AddLog ($"📡 Статус подключения: {serverInfo}");
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
            // Освобождаем мьютекс при выходе
            _appMutex?.ReleaseMutex ();
            _appMutex?.Dispose ();
            base.OnExit (e);
        }
    }
}