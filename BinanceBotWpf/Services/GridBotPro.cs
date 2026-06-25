using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Продвинутый спотовый сеточный бот.
    /// Рекомендуемые настройки для 50 USDC:
    ///   - Пара: DOGEUSDC / PEPEUSDC / WIFUSDC (мин. ордер 1 USDC)
    ///   - Диапазон: ±15-20%
    ///   - Сеток: 10-15
    ///   - Инвестиция: 80-90% баланса (оставить на комиссии)
    ///   - Take Profit на сетку: 0.3-0.5%
    ///   - Stop Loss: не использовать (управление через диапазон)
    /// </summary>
    public class GridBotPro : IDisposable
    {
        private readonly IBinanceClient _client;
        private readonly Action<string> _logger;

        private CancellationTokenSource _cts;
        private bool _isRunning;
        private string _symbol;
        private decimal _centerPrice;
        private decimal _rangePercent;
        private int _gridLevels;
        private decimal _totalInvestment;
        private decimal _stepPercent;
        private decimal _profitPerGrid;

        private readonly Dictionary<decimal, string> _activeBuyOrders = new ();
        private readonly Dictionary<decimal, string> _activeSellOrders = new ();
        private readonly Dictionary<string, decimal> _filledBuyPrices = new ();
        private readonly object _lock = new ();

        private decimal _totalProfit;
        private int _totalCycles;
        private decimal _stepSize;
        private decimal _tickSize;
        private decimal _minNotional;

        public bool IsRunning => _isRunning;
        public string Symbol => _symbol;
        public decimal CenterPrice => _centerPrice;
        public int ActiveOrdersCount => _activeBuyOrders.Count + _activeSellOrders.Count;
        public decimal TotalProfit => _totalProfit;
        public int TotalCycles => _totalCycles;

        public GridBotPro (IBinanceClient client, Action<string> logger)
        {
            _client = client;
            _logger = logger;
        }

        /// <summary>
        /// Запуск сетки с автоматическим расчётом параметров
        /// </summary>
        public async Task StartAsync (string symbol, decimal currentPrice, decimal balanceUsdc,
            decimal rangePercent = 0.175m, int gridLevels = 12, decimal investmentPercent = 0.85m)
        {
            if (_isRunning)
            {
                _logger?.Invoke ($"⚠️ GridBotPro уже запущен для {_symbol}");
                return;
            }

            _symbol = symbol;
            _centerPrice = currentPrice;
            _rangePercent = rangePercent;
            _gridLevels = gridLevels;
            _totalInvestment = balanceUsdc * investmentPercent;
            _isRunning = true;
            _cts = new CancellationTokenSource ();
            _totalProfit = 0;
            _totalCycles = 0;

            _stepSize = await _client.GetStepSizeAsync (symbol);
            _tickSize = await _client.GetTickSizeAsync (symbol);
            _minNotional = await _client.GetMinNotionalAsync (symbol);

            _stepPercent = rangePercent / gridLevels;
            _profitPerGrid = _stepPercent * 0.97m;

            _logger?.Invoke ($"🔲 GridBotPro: {_symbol}");
            _logger?.Invoke ($"   Центр: {currentPrice:F6} | Диапазон: ±{rangePercent * 100:F1}% | Шаг: {_stepPercent * 100:F2}%");
            _logger?.Invoke ($"   Сеток: {gridLevels} | Инвестиция: {_totalInvestment:F2} USDC | Профит/сетку: ~{_profitPerGrid * 100:F2}%");

            decimal[] buyLevels = new decimal[gridLevels];
            decimal[] sellLevels = new decimal[gridLevels];

            for (int i = 0; i < gridLevels; i++)
            {
                buyLevels[i] = AlignToTick (currentPrice * (1 - _stepPercent * (i + 1)));
                sellLevels[i] = AlignToTick (currentPrice * (1 + _stepPercent * (i + 1)));
            }

            _logger?.Invoke ($"   Buy:  {string.Join (" | ", buyLevels.Select (x => x.ToString ("F6")))}");
            _logger?.Invoke ($"   Sell: {string.Join (" | ", sellLevels.Select (x => x.ToString ("F6")))}");

            decimal perLevelUsdc = _totalInvestment / (gridLevels * 2);
            if (perLevelUsdc < _minNotional)
            {
                int maxLevels = (int)Math.Floor (_totalInvestment / (_minNotional * 2));
                if (maxLevels < 2)
                {
                    _logger?.Invoke ($"❌ Недостаточно средств для сетки. Нужно минимум {_minNotional * 4:F2} USDC");
                    _isRunning = false;
                    return;
                }
                gridLevels = maxLevels;
                _gridLevels = gridLevels;
                buyLevels = new decimal[gridLevels];
                sellLevels = new decimal[gridLevels];
                _stepPercent = rangePercent / gridLevels;
                _profitPerGrid = _stepPercent * 0.97m;
                perLevelUsdc = _totalInvestment / (gridLevels * 2);

                for (int i = 0; i < gridLevels; i++)
                {
                    buyLevels[i] = AlignToTick (currentPrice * (1 - _stepPercent * (i + 1)));
                    sellLevels[i] = AlignToTick (currentPrice * (1 + _stepPercent * (i + 1)));
                }
                _logger?.Invoke ($"🔧 Автоподстройка: {gridLevels} уровней, {perLevelUsdc:F2} USDC/уровень");
            }

            int placed = 0;
            for (int i = 0; i < gridLevels; i++)
            {
                if (_cts.Token.IsCancellationRequested) break;

                decimal buyQty = AlignToStep (perLevelUsdc / buyLevels[i]);
                if (buyQty > 0 && buyQty * buyLevels[i] >= _minNotional)
                {
                    var order = await _client.PlaceLimitOrder (symbol, "BUY", buyQty, buyLevels[i]);
                    if (order != null)
                    {
                        string orderId = order["orderId"]?.ToString () ?? "";
                        lock (_lock) { _activeBuyOrders[buyLevels[i]] = orderId; }
                        placed++;
                    }
                }

                decimal sellQty = AlignToStep (perLevelUsdc / sellLevels[i]);
                if (sellQty > 0 && sellQty * sellLevels[i] >= _minNotional)
                {
                    var order = await _client.PlaceLimitOrder (symbol, "SELL", sellQty, sellLevels[i]);
                    if (order != null)
                    {
                        string orderId = order["orderId"]?.ToString () ?? "";
                        lock (_lock) { _activeSellOrders[sellLevels[i]] = orderId; }
                        placed++;
                    }
                }

                await Task.Delay (200);
            }

            _logger?.Invoke ($"✅ GridBotPro: {placed} ордеров выставлено");
            _ = Task.Run (() => MonitorLoop (_cts.Token));
        }

        /// <summary>
        /// Мониторинг исполнения ордеров + выставление встречных + трекинг профита
        /// </summary>
        private async Task MonitorLoop (CancellationToken token)
        {
            while (!token.IsCancellationRequested && _isRunning)
            {
                try
                {
                    await Task.Delay (3000, token);

                    var openOrders = await _client.GetAllOrdersAsync (_symbol, limit: 50);
                    if (openOrders == null) continue;

                    var filledIds = openOrders
                        .Where (o => o["status"]?.ToString () == "FILLED")
                        .ToList ();

                    foreach (var filled in filledIds)
                    {
                        if (token.IsCancellationRequested) break;

                        string orderId = filled["orderId"]?.ToString ();
                        string side = filled["side"]?.ToString ();
                        decimal price = decimal.Parse (filled["price"]?.ToString () ?? "0");
                        decimal qty = decimal.Parse (filled["executedQty"]?.ToString () ?? "0");
                        decimal quoteQty = decimal.Parse (filled["cummulativeQuoteQty"]?.ToString () ?? "0");
                        decimal fillPrice = quoteQty > 0 && qty > 0 ? quoteQty / qty : price;

                        if (side == "BUY")
                        {
                            lock (_lock)
                            {
                                foreach (var kvp in _activeBuyOrders.Where (k => k.Value == orderId).ToList ())
                                    _activeBuyOrders.Remove (kvp.Key);
                                _filledBuyPrices[orderId] = fillPrice;
                            }

                            decimal targetSell = AlignToTick (fillPrice * (1 + _stepPercent));
                            decimal sellQty = AlignToStep (qty);
                            if (sellQty > 0 && sellQty * targetSell >= _minNotional)
                            {
                                var sellOrder = await _client.PlaceLimitOrder (_symbol, "SELL", sellQty, targetSell);
                                if (sellOrder != null)
                                {
                                    string newId = sellOrder["orderId"]?.ToString () ?? "";
                                    lock (_lock) { _activeSellOrders[targetSell] = newId; }
                                    _logger?.Invoke ($"📗 Buy исполнен @ {fillPrice:F6} → встречный sell {sellQty} @ {targetSell:F6}");
                                }
                            }
                        }
                        else if (side == "SELL")
                        {
                            lock (_lock)
                            {
                                foreach (var kvp in _activeSellOrders.Where (k => k.Value == orderId).ToList ())
                                    _activeSellOrders.Remove (kvp.Key);
                            }

                            if (_filledBuyPrices.TryGetValue (orderId, out decimal buyPrice))
                            {
                                decimal profit = (fillPrice - buyPrice) * qty;
                                _totalProfit += profit;
                                _totalCycles++;
                                _filledBuyPrices.Remove (orderId);
                                _logger?.Invoke ($"📕 Sell исполнен @ {fillPrice:F6} | Профит: +{profit:F4} USDC (всего: {_totalProfit:F4}, циклов: {_totalCycles})");
                            }

                            decimal targetBuy = AlignToTick (fillPrice * (1 - _stepPercent));
                            decimal buyQty = AlignToStep (qty);
                            if (buyQty > 0 && buyQty * targetBuy >= _minNotional)
                            {
                                var buyOrder = await _client.PlaceLimitOrder (_symbol, "BUY", buyQty, targetBuy);
                                if (buyOrder != null)
                                {
                                    string newId = buyOrder["orderId"]?.ToString () ?? "";
                                    lock (_lock) { _activeBuyOrders[targetBuy] = newId; }
                                    _logger?.Invoke ($"📕 Sell исполнен → встречный buy {buyQty} @ {targetBuy:F6}");
                                }
                            }
                        }
                    }

                    await CancelStaleOrders (token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger?.Invoke ($"❌ GridBotPro ошибка: {ex.Message}");
                    await Task.Delay (10000);
                }
            }
        }

        private async Task CancelStaleOrders (CancellationToken token)
        {
            if (token.IsCancellationRequested) return;

            decimal currentPrice = await _client.GetPriceAsync (_symbol);
            if (currentPrice <= 0) return;

            decimal upperBound = _centerPrice * (1 + _rangePercent * 1.1m);
            decimal lowerBound = _centerPrice * (1 - _rangePercent * 1.1m);

            List<decimal> staleBuys;
            List<decimal> staleSells;
            lock (_lock)
            {
                staleBuys = _activeBuyOrders.Keys.Where (k => k < lowerBound || k > upperBound).ToList ();
                staleSells = _activeSellOrders.Keys.Where (k => k < lowerBound || k > upperBound).ToList ();
            }

            foreach (decimal price in staleBuys)
            {
                lock (_lock)
                {
                    if (_activeBuyOrders.TryGetValue (price, out string id))
                    {
                        _activeBuyOrders.Remove (price);
                        _ = _client.CancelOrder (_symbol, long.Parse (id));
                    }
                }
            }

            foreach (decimal price in staleSells)
            {
                lock (_lock)
                {
                    if (_activeSellOrders.TryGetValue (price, out string id))
                    {
                        _activeSellOrders.Remove (price);
                        _ = _client.CancelOrder (_symbol, long.Parse (id));
                    }
                }
            }
        }

        private decimal AlignToTick (decimal price) => Math.Round (price / _tickSize) * _tickSize;
        private decimal AlignToStep (decimal qty) => Math.Floor (qty / _stepSize) * _stepSize;

        public async Task StopAsync ()
        {
            if (!_isRunning) return;

            _cts?.Cancel ();
            _isRunning = false;

            lock (_lock)
            {
                foreach (var kvp in _activeBuyOrders)
                    _ = _client.CancelOrder (_symbol, long.Parse (kvp.Value));
                foreach (var kvp in _activeSellOrders)
                    _ = _client.CancelOrder (_symbol, long.Parse (kvp.Value));
                _activeBuyOrders.Clear ();
                _activeSellOrders.Clear ();
            }

            _logger?.Invoke ($"⏹️ GridBotPro остановлен | Итого профит: {_totalProfit:F4} USDC ({_totalCycles} циклов)");
            await Task.CompletedTask;
        }

        public void Dispose ()
        {
            _cts?.Cancel ();
            _cts?.Dispose ();
        }
    }
}
