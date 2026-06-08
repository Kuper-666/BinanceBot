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
            // === 1. Защита от запуска нескольких копий ===
            const string mutexName = "Global\\{B9E8F2A1-5C7D-4A3E-8F2C-9D7E5B4A3C2F}";
            bool createdNew;
            _appMutex = new Mutex (true, mutexName, out createdNew);
            if (!createdNew)
            {
                MessageBox.Show ("Бот уже запущен!", "Предупреждение", MessageBoxButton.OK, MessageBoxImage.Warning);
                Current.Shutdown ();
                return;
            }

            // === 2. Проверка обновлений (тихо, без UI) ===
            //try
            //{
            //    var tempLogger = new Action<string> (msg => System.Diagnostics.Debug.WriteLine (msg));
            //    var updater = new UpdateManager (tempLogger);
            //    bool updated = await updater.CheckAndUpdateAsync (silent: true);
            //    if (updated)
            //    {
            //        // Если обновление найдено и установлено, текущий процесс будет закрыт скриптом
            //        return;
            //    }
            //}
            //catch (Exception ex)
            //{
            //    System.Diagnostics.Debug.WriteLine ($"Ошибка при проверке обновлений: {ex.Message}");
            //}

            // === 3. Основная инициализация бота (ваш существующий код) ===
            base.OnStartup (e);

            string apiKey = "";
            string apiSecret = "";
            bool isTestnet = true;
            decimal minUsdcBalance = 5.50m;
            string telegramBotToken = "";
            string telegramChatId = "";

            string configPath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "config.txt");

            try
            {
                if (File.Exists (configPath))
                {
                    var lines = File.ReadAllLines (configPath);
                    foreach (var line in lines)
                    {
                        if (string.IsNullOrWhiteSpace (line) || !line.Contains ("=")) continue;

                        var parts = line.Split ('=', 2);
                        string key = parts[0].Trim ().ToLower ();
                        string value = parts[1].Trim ();

                        switch (key)
                        {
                            case "apikey":
                                apiKey = value;
                                break;
                            case "apisecret":
                                apiSecret = value;
                                break;
                            case "istestnet":
                                isTestnet = bool.Parse (value);
                                break;
                            case "minusdcbalance":
                                minUsdcBalance = decimal.Parse (value, CultureInfo.InvariantCulture);
                                break;
                            case "telegrambottoken":
                                telegramBotToken = value;
                                break;
                            case "telegramchatid":
                                telegramChatId = value;
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show ($"Ошибка чтения config.txt: {ex.Message}");
            }

            // Создаем клиент
            var binanceClient = new BinanceClient (apiKey, apiSecret, isTestnet);
            await binanceClient.SyncTimeAsync ();

            // Создаем менеджеры
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