using Binance.Net.Clients;
using Binance.Net.Objects;
using CryptoExchange.Net.Objects.Sockets;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public class WebSocketPriceManager : IDisposable
    {
        private readonly BinanceSocketClient _socketClient;
        private readonly Action<string> _logger;
        private readonly ConcurrentDictionary<string, decimal> _currentPrices = new ();
        private readonly ConcurrentDictionary<string, UpdateSubscription> _subscriptions = new ();

        public WebSocketPriceManager(Action<string> logger)
        {
            _logger = logger;
            _socketClient = new BinanceSocketClient ();
        }

        public async Task SubscribeToSymbolsAsync(string[] symbols)
        {
            foreach (var symbol in symbols)
            {
                if (_subscriptions.ContainsKey (symbol)) continue;
                var result = await _socketClient.SpotApi.ExchangeData.SubscribeToTickerUpdatesAsync (symbol, update =>
                {
                    var price = update.Data.LastPrice;
                    _currentPrices.AddOrUpdate (symbol, price, (k, v) => price);
                });
                if (result.Success)
                {
                    _subscriptions[symbol] = result.Data;
                    _logger?.Invoke ($"✅ WebSocket: подписка на {symbol}");
                }
                else
                {
                    _logger?.Invoke ($"❌ WebSocket: ошибка подписки на {symbol}. {result.Error?.Message}");
                }
            }
        }

        public decimal GetCurrentPrice(string symbol) => _currentPrices.TryGetValue (symbol, out var price) ? price : 0;
        public string[] GetSubscribedSymbols() => _subscriptions.Keys.ToArray ();

        public void Dispose()
        {
            foreach (var sub in _subscriptions.Values)
            {
                // В старой версии Binance.Net нет UnsubscribeAsync, но есть Unsubscribe
                // Если нет ни того, ни другого, просто выходим
                if (sub is IDisposable disposable)
                    disposable.Dispose ();
            }
            _socketClient?.Dispose ();
        }
    }
}