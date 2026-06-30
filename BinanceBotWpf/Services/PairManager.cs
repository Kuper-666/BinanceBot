using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BinanceBotWpf.Models;
using BinanceBotWpf.ViewModels;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Manages the list of active trading pairs with tier-list logic based on balance.
    /// </summary>
    public class PairManager
    {
        private readonly BinanceClient _client;
        private readonly IWalletManager _wallet;
        private MainWindowViewModel _ui;
        private WebSocketPriceManager _webSocketManager;

        private List<string> _activePairs = new ();
        private readonly object _pairsLock = new ();

        // Tier-list: lower tier = always included, higher tier = requires more balance
        private static readonly string[] _tierLow = new[] { "DOGE", "XRP", "ADA", "SOL" };
        private static readonly string[] _tierMid = new[] { "ETH", "BNB", "LINK", "NEAR", "SUI" };
        private static readonly string[] _tierHigh = new[] { "BTC", "PEPE" };

        public PairManager (BinanceClient client, IWalletManager wallet)
        {
            _client = client;
            _wallet = wallet;
        }

        public void SetViewModel (MainWindowViewModel ui)
        {
            _ui = ui;
        }

        public void SetWebSocketManager (WebSocketPriceManager webSocketManager)
        {
            _webSocketManager = webSocketManager;
        }

        public List<string> GetActivePairs ()
        {
            lock (_pairsLock)
            {
                return new List<string> (_activePairs);
            }
        }

        public static string[] GetWhitelistForBalance (decimal balance)
        {
            List<string> result = new List<string> ();
            result.AddRange (_tierLow);
            if (balance >= 100) result.AddRange (_tierMid);
            if (balance >= 1000) result.AddRange (_tierHigh);
            return result.ToArray ();
        }

        public static int GetMaxPositionsForBalance (decimal balance)
        {
            if (balance >= 1000) return 10;
            if (balance >= 100) return 5;
            return 2;
        }

        public async Task UpdatePairsAsync ()
        {
            try
            {
                if (_webSocketManager == null)
                {
                    _ui?.AddLog ("WebSocket менеджер не инициализирован, пропускаю обновление пар");
                    return;
                }

                decimal balance = _wallet?.GetTotalBalance ("USDC") ?? 0;
                string quoteCurrency = _ui?.QuoteCurrency ?? "USDC";
                string quote = quoteCurrency == "USDT" ? "USDT" : "USDC";

                string[] whitelist = GetWhitelistForBalance (balance);
                int maxPositions = GetMaxPositionsForBalance (balance);

                if (_ui != null) _ui.MaxConcurrentTrades = maxPositions;

                Dictionary<string, decimal> allMinNotionals = await _client.GetAllMinNotionalsAsync ();
                List<string> allPairs = new List<string> ();

                foreach (string asset in whitelist)
                {
                    string pair = asset + quote;
                    if (allMinNotionals.TryGetValue (pair, out decimal minNot))
                    {
                        if (balance >= minNot * 2m)
                            allPairs.Add (pair);
                    }
                    else
                    {
                        allPairs.Add (pair);
                    }
                }

                if (allPairs.Count > 0)
                {
                    lock (_pairsLock) { _activePairs = allPairs; }
                    string[] subscribedSymbols = _webSocketManager.GetSubscribedSymbols ();

                    if (subscribedSymbols == null || subscribedSymbols.Length == 0)
                    {
                        await _webSocketManager.SubscribeToSymbolsAsync (allPairs.ToArray ());
                    }
                    else
                    {
                        string[] newSymbols = allPairs.Except (subscribedSymbols).ToArray ();
                        if (newSymbols.Any ())
                            await _webSocketManager.SubscribeToSymbolsAsync (newSymbols);
                    }

                    string minInfo = string.Join (", ", allPairs.Take (5).Select (p =>
                    {
                        allMinNotionals.TryGetValue (p, out decimal mn);
                        return $"{p}={mn:F0}";
                    }));
                    _ui?.AddLog ($"{allPairs.Count} пар (баланс: {balance:F0} USDC) | minNotional: {minInfo}");
                }
            }
            catch (Exception ex)
            {
                _ui?.AddLog ($"Ошибка обновления пар: {ex.Message}");
                if (ex.InnerException != null)
                    _ui?.AddLog ($"   Детали: {ex.InnerException.Message}");
            }
        }
    }
}
