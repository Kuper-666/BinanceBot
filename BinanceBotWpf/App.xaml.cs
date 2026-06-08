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

            // Создание config.txt, если отсутствует
            if (!File.Exists (configPath))
            {
                string sample = @"# Binance Bot Configuration
ApiKey=YOUR_API_KEY_HERE
ApiSecret=YOUR_API_SECRET_HERE
isTestnet=true

# Telegram notifications (optional)
telegramBotToken=
telegramChatId=

# Trading parameters
minUsdcBalance=5.50
";
                try
                {
                    File.WriteAllText (configPath, sample);
                    MessageBox.Show ("Создан файл config.txt. Пожалуйста, заполните свои API-ключи и перезапустите бота.",
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
            }
            catch (Exception ex)
            {
                MessageBox.Show ($"Ошибка чтения config.txt: {ex.Message}");
                Current.Shutdown ();
                return;
            }

            if (string.IsNullOrEmpty (apiKey) || string.IsNullOrEmpty (apiSecret))
            {
                MessageBox.Show ("В config.txt не указаны ApiKey или ApiSecret.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                Current.Shutdown ();
                return;
            }

            var binanceClient = new BinanceClient (apiKey, apiSecret, isTestnet);
            await binanceClient.SyncTimeAsync ();

            object consoleLock = new object ();
            var walletManager = new WalletManager (binanceClient);
            var earnManager = new EarnManager (consoleLock);
            var rebalancer = new BalanceRebalancer (consoleLock, 0.1m);

            var tradingService = new TradingService (binanceClient, walletManager, earnManager, rebalancer, minUsdcBalance, telegramBotToken, telegramChatId);
            var viewModel = new MainWindowViewModel (tradingService, isTestnet);
            var mainWindow = new MainWindow (viewModel);
            mainWindow.Show ();
        }
    }
}