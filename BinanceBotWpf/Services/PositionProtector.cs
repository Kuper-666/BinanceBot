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

        private async Task UpdateOcoOrder(string symbol, OpenPosition pos)
        {
            try
            {
                if (pos.OcoOrderListId != 0)
                {
                    await _client.CancelOcoOrder (symbol, pos.OcoOrderListId);
                }

                var newOco = await _client.PlaceOcoOrder (symbol, pos.Quantity, pos.StopLossPrice, pos.TakeProfitPrice);
                if (newOco != null)
                {
                    pos.OcoOrderListId = (long)newOco["orderListId"];
                    _logger?.Invoke ($"🔄 OCO ордер {symbol} обновлён: SL={pos.StopLossPrice:F4}, TP={pos.TakeProfitPrice:F4}");
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"⚠️ Ошибка обновления OCO {symbol}: {ex.Message}");
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