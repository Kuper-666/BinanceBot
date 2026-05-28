using BinanceBotWpf.Models;
using BinanceBotWpf.Services;
using BinanceBotWpf.ViewModels;
using System;
using System.IO;
using System.Globalization;
using System.Windows;

namespace BinanceBotWpf
{
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
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
            var rebalancer = new BalanceRebalancer (consoleLock, 0.1m); // только 2 параметра

            // Создаем TradingService (без третьего параметра для ребалансировщика)
            var tradingService = new TradingService (binanceClient, walletManager, earnManager, rebalancer, minUsdcBalance, telegramBotToken, telegramChatId);

            var viewModel = new MainWindowViewModel (tradingService);
            var mainWindow = new MainWindow (viewModel);
            mainWindow.Show ();
        }
    }
}