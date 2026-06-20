using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Защита открытых позиций: трейлинг-стоп, частичная фиксация, таймауты
    /// </summary>
    public class PositionProtector
    {
        private readonly BinanceClient _client;
        private readonly PositionManager _positionManager;
        private readonly Action<string> _logger;

        public bool EnableDynamicTrailingStop { get; set; } = true;
        public decimal ActivationProfitPercent { get; set; } = 0.02m; // Активация при +2%
        public decimal TrailingStepPercent { get; set; } = 0.005m;    // Шаг трейлинга 0.5%
        private readonly Dictionary<string, decimal> _lastTrailingPrice = new ();

        public decimal TrailingStopPercent { get; set; } = 0.02m;
        public decimal PartialClosePercent { get; set; } = 0.05m; // 5% профита -> частичная фиксация
        public TimeSpan MaxHoldTime { get; set; } = TimeSpan.FromHours (2);
        public decimal PartialCloseQtyPercent { get; set; } = 0.5m; // Закрываем 50% при частичной фиксации

        public PositionProtector(BinanceClient client, PositionManager positionManager, Action<string> logger)
        {
            _client = client;
            _positionManager = positionManager;
            _logger = logger;
        }

        // Публичный метод для установки логгера
        public void SetLogger(Action<string> logger)
        {
            // Поле _logger только для чтения, нельзя изменить после конструктора
            // Этот метод оставлен для совместимости, но ничего не делает
        }

        /// <summary>
        /// Проверка и обновление всех защит
        /// </summary>
        public async Task<List<string>> CheckAndProtectAsync(Func<string, decimal> getCurrentPrice)
        {
            var toClose = new List<string> ();

            foreach (var sym in _positionManager.GetSymbols ())
            {
                if (!_positionManager.TryGet (sym, out var pos)) continue;

                decimal price = getCurrentPrice (sym);
                if (price <= 0) continue;

                // Если позиция осталась без OCO-защиты на бирже после прошлого сбоя — пробуем восстановить в первую очередь
                if (pos.IsUnprotected)
                {
                    _logger?.Invoke ($"🔁 Повторная попытка восстановить защиту для {sym} (позиция без OCO на бирже)");
                    await UpdateOcoOrder (sym, pos);
                }

                // Динамический трейлинг-стоп
                await UpdateDynamicTrailingStopAsync (sym, pos, price);

                // Стандартный трейлинг-стоп
                await UpdateTrailingStopAsync (sym, pos, price);

                // Частичная фиксация
                await CheckPartialCloseAsync (sym, pos, price);

                // Проверка условий закрытия
                bool shouldClose = ShouldClosePosition (pos, price);
                if (shouldClose)
                {
                    toClose.Add (sym);
                    _lastTrailingPrice.Remove (sym);
                }
            }

            return toClose;
        }

        private async Task UpdateTrailingStopAsync(string symbol, OpenPosition pos, decimal currentPrice)
        {
            // Обновляем максимальную цену
            if (currentPrice > pos.HighestPriceSinceOpen)
            {
                pos.HighestPriceSinceOpen = currentPrice;

                // Расчёт нового трейлинг-стопа
                decimal newStopLoss = pos.HighestPriceSinceOpen * ( 1 - TrailingStopPercent );

                if (newStopLoss > pos.StopLossPrice)
                {
                    pos.StopLossPrice = newStopLoss;
                    _logger?.Invoke ($"📈 Трейлинг-стоп {symbol}: SL повышен до {newStopLoss:F4}");

                    // Обновляем OCO ордер
                    await UpdateOcoOrder (symbol, pos);
                }
            }

            // Трейлинг тейк-профит (при росте)
            if (currentPrice > pos.HighestPrice)
            {
                pos.HighestPrice = currentPrice;
                decimal newTakeProfit = currentPrice * ( 1 + TrailingStopPercent );
                if (newTakeProfit > pos.TakeProfitPrice)
                {
                    pos.TakeProfitPrice = newTakeProfit;
                    _logger?.Invoke ($"📈 Трейлинг TP {symbol}: повышен до {newTakeProfit:F4}");
                    await UpdateOcoOrder (symbol, pos);
                }
            }
        }

        private async Task CheckPartialCloseAsync(string symbol, OpenPosition pos, decimal currentPrice)
        {
            decimal profitPercent = ( currentPrice - pos.EntryPrice ) / pos.EntryPrice;

            if (profitPercent >= PartialClosePercent && pos.Quantity > 0)
            {
                decimal stepSize = await _client.GetStepSizeAsync (symbol);
                decimal closeQty = Math.Floor (pos.Quantity * PartialCloseQtyPercent / stepSize) * stepSize;

                if (closeQty > 0.000001m && closeQty < pos.Quantity)
                {
                    var order = await _client.PlaceOrder (symbol, "SELL", "MARKET", closeQty);
                    if (order != null)
                    {
                        decimal pnl = ( currentPrice - pos.EntryPrice ) * closeQty;
                        _logger?.Invoke ($"🎯 Частичная фиксация {symbol}: продано {closeQty} по {currentPrice:F4}, PnL {pnl:F2}");

                        pos.Quantity -= closeQty;

                        if (pos.Quantity <= 0.000001m)
                        {
                            _positionManager.Remove (symbol);
                        }
                        else
                        {
                            // Перемещаем стоп-лосс в безубыток
                            pos.StopLossPrice = pos.EntryPrice;
                            _logger?.Invoke ($"🛡️ Стоп-лосс {symbol} перемещён в безубыток: {pos.StopLossPrice:F4}");
                            await UpdateOcoOrder (symbol, pos);
                        }
                    }
                }
            }
        }

        private bool ShouldClosePosition(OpenPosition pos, decimal currentPrice)
        {
            // Стоп-лосс
            if (currentPrice <= pos.StopLossPrice)
            {
                _logger?.Invoke ($"🔴 Закрытие по стоп-лоссу: {pos.Symbol} {currentPrice:F4} <= {pos.StopLossPrice:F4}");
                return true;
            }

            // Тейк-профит
            if (currentPrice >= pos.TakeProfitPrice)
            {
                _logger?.Invoke ($"🟢 Закрытие по тейк-профиту: {pos.Symbol} {currentPrice:F4} >= {pos.TakeProfitPrice:F4}");
                return true;
            }

            // Максимальное время удержания
            if (DateTime.UtcNow - pos.OpenTime > MaxHoldTime)
            {
                _logger?.Invoke ($"⏰ Закрытие по таймауту: {pos.Symbol} удержание {DateTime.UtcNow - pos.OpenTime}");
                return true;
            }

            return false;
        }

        // ✅ Счётчики ошибок добавлены как поля класса:
         private readonly Dictionary<string, int> _ocoFailCount = new();
         private readonly Dictionary<string, DateTime> _ocoNextRetry = new();

        private async Task UpdateOcoOrder(string symbol, OpenPosition pos)
        {
            // ✅ Кулдаун: пропускаем если ещё не время
            if (_ocoNextRetry.TryGetValue (symbol, out var nextRetry) && DateTime.UtcNow < nextRetry)
            {
                _logger?.Invoke ($"⏳ {symbol}: OCO в кулдауне, следующая попытка в {nextRetry:HH:mm:ss}");
                return;
            }

            try
            {
                if (pos.OcoOrderListId != 0)
                {
                    bool cancelled = await _client.CancelOcoOrder (symbol, pos.OcoOrderListId);
                    if (!cancelled)
                        _logger?.Invoke ($"⚠️ Не удалось отменить старый OCO {pos.OcoOrderListId} для {symbol}");
                    pos.OcoOrderListId = 0;
                }

                // ✅ Проверяем реальный свободный баланс АКТИВА (не USDC!)
                string baseAsset = symbol.Replace ("USDC", "").Replace ("USDT", "");
                decimal freeAsset = await _client.GetAccountBalanceAsync (baseAsset);

                if (freeAsset < pos.Quantity * 0.99m)
                {
                    _logger?.Invoke ($"⚠️ {symbol}: Баланс {baseAsset}={freeAsset:F6} < нужного {pos.Quantity:F6}." +
                                    $" Актив ещё заблокирован в ордере. OCO отложен.");
                    pos.IsUnprotected = true;
                    return;
                }

                // ✅ Exponential Backoff: 3 попытки (2с, 4с)
                _ocoFailCount.TryGetValue (symbol, out int failCount);

                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    var newOco = await _client.PlaceOcoOrder (
                        symbol, pos.Quantity, pos.StopLossPrice, pos.TakeProfitPrice);

                    if (newOco != null)
                    {
                        pos.OcoOrderListId = (long)newOco["orderListId"];
                        pos.IsUnprotected = false;
                        _ocoFailCount[symbol] = 0;   // ✅ Сброс счётчика при успехе
                        _ocoNextRetry.Remove (symbol);
                        _logger?.Invoke ($"✅ OCO ордер {symbol}: SL={pos.StopLossPrice:F4}, TP={pos.TakeProfitPrice:F4}");
                        return;
                    }

                    if (attempt < 3)
                    {
                        int delaySec = (int)Math.Pow (2, attempt); // 2с, 4с
                        _logger?.Invoke ($"⚠️ {symbol}: Попытка OCO {attempt}/3 не удалась, повтор через {delaySec}с...");
                        await Task.Delay (delaySec * 1000);
                    }
                }

                // ✅ Кулдаун: после 3 неудач ждём 2..10 минут
                _ocoFailCount[symbol] = ++failCount;
                int cooldownMin = Math.Min (failCount * 2, 10);
                _ocoNextRetry[symbol] = DateTime.UtcNow.AddMinutes (cooldownMin);

                pos.IsUnprotected = true;
                _logger?.Invoke ($"🚨 {symbol}: OCO провалился 3/3. Следующая попытка через {cooldownMin}мин.");
            }
            catch (Exception ex)
            {
                pos.OcoOrderListId = 0;
                pos.IsUnprotected = true;
                _logger?.Invoke ($"⚠️ Исключение OCO {symbol}: {ex.Message}");
            }
        }

        /// <summary>
        /// Динамический трейлинг-стоп (может как повышать, так и понижать SL)
        /// </summary>
        private async Task UpdateDynamicTrailingStopAsync(string symbol, OpenPosition pos, decimal currentPrice)
        {
            if (!EnableDynamicTrailingStop) return;

            decimal profitPercent = ( currentPrice - pos.EntryPrice ) / pos.EntryPrice;

            // Активация только после достижения порога прибыли
            if (profitPercent < ActivationProfitPercent) return;

            // Получаем последнюю зафиксированную цену
            if (!_lastTrailingPrice.TryGetValue (symbol, out decimal lastPrice))
                lastPrice = pos.EntryPrice;

            // Проверяем, нужно ли обновить трейлинг-стоп
            decimal priceIncrease = ( currentPrice - lastPrice ) / lastPrice;

            if (priceIncrease >= TrailingStepPercent)
            {
                // Цена выросла на шаг трейлинга -> повышаем стоп-лосс
                decimal newStopLoss = currentPrice * ( 1 - TrailingStopPercent );

                if (newStopLoss > pos.StopLossPrice)
                {
                    pos.StopLossPrice = newStopLoss;
                    _lastTrailingPrice[symbol] = currentPrice;

                    _logger?.Invoke ($"📈 Динамический трейлинг {symbol}: SL повышен до {newStopLoss:F4} (прибыль {profitPercent:P1})");
                    await UpdateOcoOrder (symbol, pos);
                }
            }
            else if (priceIncrease < -TrailingStepPercent)
            {
                // Цена упала на шаг трейлинга -> фиксируем часть прибыли
                decimal partialQty = pos.Quantity * 0.25m; // Закрываем 25%
                decimal stepSize = await _client.GetStepSizeAsync (symbol);
                decimal closeQty = Math.Floor (partialQty / stepSize) * stepSize;

                if (closeQty > 0.000001m && closeQty < pos.Quantity)
                {
                    var order = await _client.PlaceOrder (symbol, "SELL", "MARKET", closeQty);
                    if (order != null)
                    {
                        decimal pnl = ( currentPrice - pos.EntryPrice ) * closeQty;
                        _logger?.Invoke ($"🎯 Частичная фиксация {symbol}: продано {closeQty} по {currentPrice:F4}, PnL {pnl:F2}");

                        pos.Quantity -= closeQty;
                        _lastTrailingPrice[symbol] = currentPrice;

                        if (pos.Quantity <= 0.000001m)
                        {
                            _positionManager.Remove (symbol);
                        }
                    }
                }
            }
        }
    }
}