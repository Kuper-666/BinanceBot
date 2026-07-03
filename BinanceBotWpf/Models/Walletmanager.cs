using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using BinanceBotWpf.Exchange;
using BinanceBotWpf.Services;
using Newtonsoft.Json.Linq;

namespace BinanceBotWpf.Models
{
    public class AssetBalance
    {
        public decimal Spot { get; set; }
        public decimal Earn { get; set; }
        public decimal Futures { get; set; }
        public decimal Total => Spot + Earn + Futures;
    }

    public class WalletManager : IWalletManager
    {
        private readonly BinanceClient _client;
        private IBinanceFuturesClient _futuresClient;
        private readonly Dictionary<string, AssetBalance> _balances;
        public event Action<string> OnLogGenerated;

        public WalletManager(BinanceClient client)
        {
            _client = client ?? throw new ArgumentNullException (nameof (client));
            _balances = new Dictionary<string, AssetBalance> ();
        }

        public void SetFuturesClient (IBinanceFuturesClient futuresClient)
        {
            _futuresClient = futuresClient;
        }

        public async Task UpdateBalance()
        {
            try
            {
                var account = await _client.GetAccountInfoAsync ();
                var earnData = await _client.GetFlexibleEarnBalanceAsync ();

                lock (_balances)
                {
                    _balances.Clear ();
                    if (account?["balances"] != null)
                    {
                        foreach (var b in account["balances"])
                        {
                            string asset = b["asset"]?.ToString ();
                            if (string.IsNullOrEmpty (asset)) continue;
                            decimal free = decimal.Parse (b["free"]?.ToString () ?? "0", CultureInfo.InvariantCulture);
                            if (!_balances.ContainsKey (asset))
                                _balances[asset] = new AssetBalance ();
                            _balances[asset].Spot = free;
                        }
                    }
                    if (earnData != null)
                    {
                        foreach (var item in earnData)
                        {
                            string asset = item["asset"]?.ToString ();
                            if (string.IsNullOrEmpty (asset)) continue;
                            decimal amount = decimal.Parse (item["totalAmount"]?.ToString () ?? "0", CultureInfo.InvariantCulture);
                            if (!_balances.ContainsKey (asset))
                                _balances[asset] = new AssetBalance ();
                            _balances[asset].Earn = amount;
                        }
                    }
                }

                // Добавляем фьючерсный баланс
                if (_futuresClient != null)
                {
                    try
                    {
                        foreach (string asset in new[] { "USDT", "USDC" })
                        {
                            decimal futuresBalance = await _futuresClient.GetAccountBalanceAsync (asset);
                            if (futuresBalance > 0)
                            {
                                lock (_balances)
                                {
                                    if (!_balances.ContainsKey (asset))
                                        _balances[asset] = new AssetBalance ();
                                    _balances[asset].Futures = futuresBalance;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log ($"Ошибка обновления балансов: {ex.Message}"); }
        }

        public Dictionary<string, AssetBalance> GetActiveBalances()
        {
            lock (_balances) { return new Dictionary<string, AssetBalance> (_balances); }
        }

        public decimal GetTotalBalance(string asset)
        {
            lock (_balances)
            {
                if (_balances.TryGetValue (asset, out var balance))
                    return balance.Total;
                return 0m;
            }
        }

        public async Task<List<string>> DetermineAvailablePairs(string quoteAsset = "USDC")
        {
            await Task.CompletedTask;
            return new List<string> { "BTCUSDC", "ETHUSDC", "BNBUSDC", "SOLUSDC" };
        }

        private void Log(string msg) => OnLogGenerated?.Invoke (msg);
    }
}