using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Спотовый сеточный бот: автоматическая расстановка лимитных ордеров выше и ниже текущей цены.
    /// При исполнении buy-ордера выставляется встречный sell-ордера на уровень выше (и наоборот).
    /// </summary>
    public class GridBot : IDisposable
    {
        private readonly BinanceClient _client;
        private readonly PositionManager _positionManager;
        private readonly Action<string> _logger;

        private CancellationTokenSource _cts;
        private bool _isRunning;
        private string _symbol;
        private decimal _centerPrice;
        private decimal[] _buyLevels;
        private decimal[] _sellLevels;
        private readonly Dictionary<decimal, string> _activeOrderIds = new ();
        private readonly object _lock = new ();
        private decimal _lastBuyPrice;

        public bool IsRunning => _isRunning;
        public string Symbol => _symbol;
        public decimal CenterPrice => _centerPrice;
        public int ActiveOrdersCount => _activeOrderIds.Count;
        public event Action<TradeLog> OnTrade;

        public GridBot(BinanceClient client, PositionManager positionManager, Action<string> logger)
        {
            _client = client;
            _positionManager = positionManager;
            _logger = logger;
        }

        /// <summary>
        /// Запуск сетки: рассчитывает уровни и выставляет лимитные ордера
        /// </summary>
        public async Task StartAsync(string symbol, decimal centerPrice, decimal gridRangePercent, int gridLevels, decimal totalInvestmentUsdc, bool useDynamicStep = false)
        {
            if (_isRunning)
            {
                _logger?.Invoke ($"⚠️ GridBot уже запущен для {_symbol}");
                return;
            }

            _symbol = symbol;
            _centerPrice = centerPrice;
            _isRunning = true;
            _cts = new CancellationTokenSource ();

            decimal rangeMultiplier = gridRangePercent;
            decimal stepPercent;
            if (useDynamicStep)
            {
                decimal atr = 0;
                try { atr = await _client.GetATRAsync (symbol, 14); } catch { }
                stepPercent = atr > 0 ? (atr / centerPrice) : (rangeMultiplier / gridLevels);
            }
            else
            {
                stepPercent = rangeMultiplier / gridLevels;
            }

            // Рассчитываем уровни покупок (ниже цены) и продаж (выше цены)
            _buyLevels = new decimal[gridLevels];
            _sellLevels = new decimal[gridLevels];

            for (int i = 0; i < gridLevels; i++)
            {
                _buyLevels[i] = centerPrice * (1 - stepPercent * (i + 1));
                _sellLevels[i] = centerPrice * (1 + stepPercent * (i + 1));
            }

            _logger?.Invoke ($"🔲 GridBot: {_symbol} | Центр={centerPrice:F4} | Шаг={stepPercent * 100:F2}% | {gridLevels} уровней в каждую сторону");
            _logger?.Invoke ($"   Buy уровни: {string.Join(", ", _buyLevels.Select (x => x.ToString ("F4")))}");
            _logger?.Invoke ($"   Sell уровни: {string.Join(", ", _sellLevels.Select (x => x.ToString ("F4")))}");

            // Выставляем лимитные ордера на каждый уровень
            decimal perLevelUsdc = totalInvestmentUsdc / (gridLevels * 2);
            decimal stepSize = await _client.GetStepSizeAsync (symbol);
            decimal tickSize = await _client.GetTickSizeAsync (symbol);
            decimal minNotional = await _client.GetMinNotionalAsync (symbol);

            // Автоподстройка: уменьшаем уровни пока каждый ордер не будет >= minNotional
            while (perLevelUsdc < minNotional && gridLevels > 1)
            {
                gridLevels--;
                perLevelUsdc = totalInvestmentUsdc / (gridLevels * 2);
            }

            // Если даже 1 уровень не покрывает минимум — увеличиваем инвестиции
            if (perLevelUsdc < minNotional)
            {
                perLevelUsdc = minNotional;
                totalInvestmentUsdc = perLevelUsdc * 2; // 1 buy + 1 sell
                _logger?.Invoke ($"⚠️ Автоподстройка: инвестиции увеличены до {totalInvestmentUsdc:F2} USDC для покрытия минимума");
            }

            if (gridLevels != _buyLevels.Length)
            {
                _logger?.Invoke ($"🔧 Автоподстройка: уровней {gridLevels} (было {_buyLevels.Length}), на уровень {perLevelUsdc:F2} USDC");
                _buyLevels = new decimal[gridLevels];
                _sellLevels = new decimal[gridLevels];
                for (int i = 0; i < gridLevels; i++)
                {
                    _buyLevels[i] = _centerPrice * (1 - stepPercent * (i + 1));
                    _sellLevels[i] = _centerPrice * (1 + stepPercent * (i + 1));
                }
            }

            // Округляем уровни по tickSize
            for (int i = 0; i < gridLevels; i++)
            {
                _buyLevels[i] = Math.Round (_buyLevels[i] / tickSize) * tickSize;
                _sellLevels[i] = Math.Round (_sellLevels[i] / tickSize) * tickSize;
            }

            for (int i = 0; i < gridLevels; i++)
            {
                if (_cts.Token.IsCancellationRequested) break;

                // Buy ордер на уровне ниже
                decimal buyQty = Math.Floor (perLevelUsdc / _buyLevels[i] / stepSize) * stepSize;
                decimal buyNotional = buyQty * _buyLevels[i];
                if (buyQty > 0 && buyNotional >= minNotional)
                {
                    var buyOrder = await _client.PlaceLimitOrder (symbol, "BUY", buyQty, _buyLevels[i]);
                    if (buyOrder != null)
                    {
                        string orderId = buyOrder["orderId"]?.ToString () ?? "";
                        lock (_lock) { _activeOrderIds[_buyLevels[i]] = orderId; }
                        _logger?.Invoke ($"   📗 Buy лимит: {buyQty} @ {_buyLevels[i]:F4}");
                    }
                }

                // Sell ордер на уровне выше
                decimal sellQty = Math.Floor (perLevelUsdc / _sellLevels[i] / stepSize) * stepSize;
                decimal sellNotional = sellQty * _sellLevels[i];
                if (sellQty > 0 && sellNotional >= minNotional)
                {
                    var sellOrder = await _client.PlaceLimitOrder (symbol, "SELL", sellQty, _sellLevels[i]);
                    if (sellOrder != null)
                    {
                        string orderId = sellOrder["orderId"]?.ToString () ?? "";
                        lock (_lock) { _activeOrderIds[_sellLevels[i]] = orderId; }
                        _logger?.Invoke ($"   📕 Sell лимит: {sellQty} @ {_sellLevels[i]:F4}");
                    }
                }

                await Task.Delay (200);
            }

            _logger?.Invoke ($"✅ GridBot запущен: {_activeOrderIds.Count} ордеров выставлено");

            // Запускаем мониторинг исполнения
            _ = Task.Run (() => MonitorLoop (_cts.Token));
        }

        /// <summary>
        /// Остановка сетки: отменяет все ордера
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning) return;

            _cts?.Cancel ();
            _isRunning = false;

            // Отменяем все активные ордера
            lock (_lock)
            {
                foreach (var kvp in _activeOrderIds)
                {
                    if (long.TryParse (kvp.Value, out long orderId))
                        _ = _client.CancelOrder (_symbol, orderId);
                }
                _activeOrderIds.Clear ();
            }

            _logger?.Invoke ($"⏹️ GridBot остановлен для {_symbol}");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Продажа всех активов по рыночной цене (при выходе за верхнюю границу)
        /// </summary>
        public async Task EmergencySellAllAsync(decimal currentPrice)
        {
            _logger?.Invoke ($"🚨 GridBot: экстренная продажа {_symbol} по {currentPrice:F4}");

            string baseAsset = _symbol.Replace ("USDC", "").Replace ("USDT", "");
            decimal balance = await _client.GetAccountBalanceAsync (baseAsset);
            if (balance > 0.000001m)
            {
                decimal stepSize = await _client.GetStepSizeAsync (_symbol);
                decimal qty = Math.Floor (balance / stepSize) * stepSize;
                if (qty > 0)
                {
                    await _client.PlaceOrder (_symbol, "SELL", "MARKET", qty);
                    _logger?.Invoke ($"   🔴 Продано {qty} {baseAsset} по рыночной цене");
                }
            }

            await StopAsync ();
        }

        /// <summary>
        /// Пересчёт сетки при значительном изменении цены
        /// </summary>
        public async Task UpdateGridAsync(decimal newCenterPrice, decimal gridRangePercent, int gridLevels, decimal totalInvestmentUsdc)
        {
            if (!_isRunning) return;

            decimal priceChange = Math.Abs (newCenterPrice - _centerPrice) / _centerPrice;
            if (priceChange < 0.05m) return; // Пересчитываем только при изменении > 5%

            _logger?.Invoke ($"🔄 GridBot: пересчёт сетки (цена изменилась на {priceChange:P1})");
            await StopAsync ();
            await StartAsync (_symbol, newCenterPrice, gridRangePercent, gridLevels, totalInvestmentUsdc);
        }

        /// <summary>
        /// Мониторинг исполнения ордеров и выставление встречных
        /// </summary>
        private async Task MonitorLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _isRunning)
            {
                try
                {
                    await Task.Delay (5000, token);

                    // Проверяем исполненные ордера через позиции
                    if (_positionManager.TryGet (_symbol, out var pos) && pos.Quantity > 0)
                    {
                        decimal currentPrice = await _client.GetPriceAsync (_symbol);
                        if (currentPrice <= 0) continue;

                        decimal closestBuyLevel = _buyLevels?
                            .OrderBy (l => Math.Abs (l - pos.EntryPrice))
                            .FirstOrDefault () ?? 0;

                        if (closestBuyLevel > 0)
                        {
                            decimal targetSellPrice = pos.EntryPrice * (1 + Math.Abs (_buyLevels[0] - _sellLevels[0]) / _centerPrice);
                            decimal stepSize = await _client.GetStepSizeAsync (_symbol);
                            decimal sellQty = Math.Floor (pos.Quantity / stepSize) * stepSize;

                            if (sellQty > 0.000001m)
                            {
                                var sellOrder = await _client.PlaceLimitOrder (_symbol, "SELL", sellQty, targetSellPrice);
                                if (sellOrder != null)
                                {
                                    string orderId = sellOrder["orderId"]?.ToString () ?? "";
                                    lock (_lock) { _activeOrderIds[targetSellPrice] = orderId; }
                                    _logger?.Invoke ($"🔲 GridBot: встречный sell {sellQty} @ {targetSellPrice:F4}");
                                }
                            }

                            _lastBuyPrice = pos.EntryPrice;
                        }
                    }

                    // Проверяем исполненные sell-ордера через активные ордера
                    var openOrders = await _client.GetAllOrdersAsync (_symbol, limit: 50);
                    if (openOrders != null)
                    {
                        var filledSells = openOrders
                            .Where (o => o["status"]?.ToString () == "FILLED" && o["side"]?.ToString () == "SELL")
                            .ToList ();

                        foreach (var sell in filledSells)
                        {
                            decimal sellPrice = decimal.Parse (sell["price"]?.ToString () ?? "0");
                            decimal sellQty = decimal.Parse (sell["executedQty"]?.ToString () ?? "0");

                            if (_lastBuyPrice > 0 && sellPrice > 0 && sellQty > 0)
                            {
                                decimal profit = (sellPrice - _lastBuyPrice) * sellQty;
                                decimal profitPct = (sellPrice / _lastBuyPrice - 1) * 100;
                                OnTrade?.Invoke (new TradeLog
                                {
                                    Symbol = _symbol,
                                    EntryPrice = _lastBuyPrice,
                                    ExitPrice = sellPrice,
                                    Quantity = sellQty,
                                    PnL = profit,
                                    PnLPercent = profitPct,
                                    OpenTime = DateTime.UtcNow,
                                    CloseTime = DateTime.UtcNow,
                                    Reason = "Grid Cycle",
                                    Action = "GRID_SELL"
                                });
                                _lastBuyPrice = 0;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger?.Invoke ($"❌ GridBot ошибка: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            _cts?.Cancel ();
            _cts?.Dispose ();
        }
    }
}
