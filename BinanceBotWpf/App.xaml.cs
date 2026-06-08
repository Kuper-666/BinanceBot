#nullable enable
using System;
using System.IO;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BinanceBotWpf.Models;
using BinanceBotWpf.Services;
using BinanceBotWpf.ViewModels;
using System.Collections.Generic;
using System.Linq;

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

            string apiKey = "";
            string apiSecret = "";
            bool isTestnet = true;
            decimal minUsdcBalance = 5.50m;
            string telegramBotToken = "";
            string telegramChatId = "";
            string futuresApiKey = "";
            string futuresApiSecret = "";
            int futuresLeverage = 5;
            decimal futuresMaxRiskPercent = 0.10m;

            string configPath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "config.txt");
            var lines = new List<string> ();

            try
            {
                if (File.Exists (configPath))
                {
                    lines = File.ReadAllLines (configPath).ToList ();
                    bool needRewrite = false;

                    for (int i = 0; i < lines.Count; i++)
                    {
                        var line = lines[i];
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
                                string decrypted = SecureStringHelper.Decrypt (value);
                                apiSecret = decrypted;
                                if (!value.StartsWith ("ENC:"))
                                {
                                    lines[i] = $"ApiSecret={SecureStringHelper.Encrypt (apiSecret)}";
                                    needRewrite = true;
                                }
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
                            case "futuresapikey":
                                futuresApiKey = value;
                                break;
                            case "futuresapisecret":
                                string futuresDecrypted = SecureStringHelper.Decrypt (value);
                                futuresApiSecret = futuresDecrypted;
                                if (!value.StartsWith ("ENC:"))
                                {
                                    lines[i] = $"futuresApiSecret={SecureStringHelper.Encrypt (futuresApiSecret)}";
                                    needRewrite = true;
                                }
                                break;
                            case "futuresleverage":
                                futuresLeverage = int.Parse (value);
                                break;
                            case "futuresmaxriskpercent":
                                futuresMaxRiskPercent = decimal.Parse (value, CultureInfo.InvariantCulture);
                                break;
                        }
                    }

                    if (needRewrite)
                    {
                        File.WriteAllLines (configPath, lines);
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

        protected override void OnExit(ExitEventArgs e)
        {
            _appMutex?.ReleaseMutex ();
            _appMutex?.Dispose ();
            base.OnExit (e);
        }
    }
}