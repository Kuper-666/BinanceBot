using Binance.Net.Clients;
using CryptoExchange.Net.Objects.Sockets;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public class WebSocketPriceManager : IDisposable
    {
        private readonly BinanceSocketClient _socketClient;
        private readonly Action<string> _logger;
        private readonly ConcurrentDictionary<string, decimal> _currentPrices = new ();
        private readonly ConcurrentDictionary<string, UpdateSubscription> _subscriptions = new ();
        private readonly SemaphoreSlim _subscribeSemaphore = new (1, 1);
        private CancellationTokenSource _reconnectCts = new ();
        private bool _disposed;

        public WebSocketPriceManager(Action<string> logger)
        {
            _logger = logger;
            _socketClient = new BinanceSocketClient ();
        }

        public async Task SubscribeToSymbolsAsync(string[] symbols)
        {
            if (_disposed) throw new ObjectDisposedException (nameof (WebSocketPriceManager));

            await _subscribeSemaphore.WaitAsync ();
            try
            {
                foreach (var symbol in symbols)
                {
                    if (_subscriptions.ContainsKey (symbol)) continue;
                    _ = SubscribeWithRetry (symbol, _reconnectCts.Token);
                }
            }
            finally
            {
                _subscribeSemaphore.Release ();
            }
        }

        private async Task SubscribeWithRetry(string symbol, CancellationToken cancellationToken)
        {
            int retryCount = 0;
            int maxRetries = 10;
            int baseDelay = 2000;

            while (!cancellationToken.IsCancellationRequested && retryCount < maxRetries)
            {
                try
                {
                    var result = await _socketClient.SpotApi.ExchangeData.SubscribeToTickerUpdatesAsync (symbol, update =>
                    {
                        var price = update.Data.LastPrice;
                        _currentPrices.AddOrUpdate (symbol, price, (k, v) => price);
                    }, cancellationToken);

                    if (result.Success)
                    {
                        _subscriptions[symbol] = result.Data;
                        _logger?.Invoke ($"✅ WebSocket: подписка на {symbol} успешна");
                        return;
                    }
                    else
                    {
                        _logger?.Invoke ($"⚠️ WebSocket: ошибка подписки на {symbol}: {result.Error?.Message}. Попытка {retryCount + 1}");
                        retryCount++;
                        int delay = baseDelay * (int)Math.Pow (2, retryCount);
                        await Task.Delay (delay, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.Invoke ($"❌ WebSocket: исключение при подписке {symbol}: {ex.Message}. Попытка {retryCount + 1}");
                    retryCount++;
                    int delay = baseDelay * (int)Math.Pow (2, retryCount);
                    await Task.Delay (delay, cancellationToken);
                }
            }

            _logger?.Invoke ($"❌ WebSocket: не удалось подписаться на {symbol} после {maxRetries} попыток");
        }

        public decimal GetCurrentPrice(string symbol) => _currentPrices.TryGetValue (symbol, out var price) ? price : 0;
        public string[] GetSubscribedSymbols() => _subscriptions.Keys.ToArray ();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _reconnectCts.Cancel ();
            _reconnectCts.Dispose ();

            foreach (var sub in _subscriptions.Values)
            {
                if (sub is IDisposable disposable)
                    disposable.Dispose ();
            }
            _socketClient?.Dispose ();
            _subscribeSemaphore?.Dispose ();
        }
    }
}