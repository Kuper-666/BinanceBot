using Binance.Net.Clients;
using CryptoExchange.Net.Objects;
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
        private readonly int _maxReconnectAttempts = 10;
        private readonly TimeSpan _initialReconnectDelay = TimeSpan.FromSeconds (2);
        private readonly TimeSpan _maxReconnectDelay = TimeSpan.FromSeconds (30);

        public WebSocketPriceManager(Action<string> logger)
        {
            _logger = logger;
            // Используем клиент с настройками по умолчанию
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
            int attempt = 0;
            TimeSpan delay = _initialReconnectDelay;

            while (!cancellationToken.IsCancellationRequested && attempt < _maxReconnectAttempts)
            {
                try
                {
                    var result = await _socketClient.SpotApi.ExchangeData.SubscribeToTickerUpdatesAsync (symbol, update =>
                    {
                        if (update?.Data?.LastPrice != null)
                        {
                            decimal price = update.Data.LastPrice;
                            _currentPrices.AddOrUpdate (symbol, price, (_, _) => price);
                        }
                    }, cancellationToken);

                    if (result.Success && result.Data != null)
                    {
                        _subscriptions[symbol] = result.Data;
                        _logger?.Invoke ($"✅ WebSocket: подписка на {symbol} успешна");
                        return;
                    }
                    else
                    {
                        _logger?.Invoke ($"⚠️ WebSocket: ошибка подписки на {symbol}: {result.Error?.Message}. Попытка {attempt + 1}");
                        attempt++;
                        delay = TimeSpan.FromTicks (Math.Min ((long)( delay.Ticks * 2 ), _maxReconnectDelay.Ticks));
                        await Task.Delay (delay, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.Invoke ($"❌ WebSocket: исключение при подписке {symbol}: {ex.Message}. Попытка {attempt + 1}");
                    attempt++;
                    delay = TimeSpan.FromTicks (Math.Min ((long)( delay.Ticks * 2 ), _maxReconnectDelay.Ticks));
                    await Task.Delay (delay, cancellationToken);
                }
            }

            _logger?.Invoke ($"❌ WebSocket: не удалось подписаться на {symbol} после {_maxReconnectAttempts} попыток");
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