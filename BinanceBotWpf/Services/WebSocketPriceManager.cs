using Binance.Net.Clients;
using Binance.Net.Objects;
using CryptoExchange.Net.Objects;
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
                if (_subscriptions.ContainsKey (symbol))
                    continue;

                var subscriptionResult = await _socketClient.SpotApi.ExchangeData.SubscribeToTickerUpdatesAsync (symbol, (update) =>
                {
                    var price = update.Data.LastPrice;
                    _currentPrices.AddOrUpdate (symbol, price, (k, v) => price);
                });

                if (subscriptionResult.Success)
                {
                    _subscriptions[symbol] = subscriptionResult.Data;
                    _logger?.Invoke ($"✅ WebSocket: подписка на {symbol}");
                }
                else
                {
                    _logger?.Invoke ($"❌ WebSocket: ошибка подписки на {symbol}: {subscriptionResult.Error?.Message}");
                }
            }
        }

        public decimal GetCurrentPrice(string symbol)
        {
            return _currentPrices.TryGetValue (symbol, out var price) ? price : 0;
        }

        public string[] GetSubscribedSymbols()
        {
            return _subscriptions.Keys.ToArray ();
        }

        public void Dispose()
        {
            // Отписываемся от всех подписок, вызывая UnsubscribeAsync (если метод существует)
            foreach (var sub in _subscriptions.Values)
            {
                try
                {
                    // Проверяем наличие метода UnsubscribeAsync через reflection (или просто игнорируем, так как при уничтожении клиента всё закроется)
                    var method = sub.GetType ().GetMethod ("UnsubscribeAsync");
                    if (method != null)
                    {
                        var task = (Task)method.Invoke (sub, null);
                        task?.Wait (1000);
                    }
                }
                catch { }
            }
            _socketClient?.Dispose ();
        }
    }
}