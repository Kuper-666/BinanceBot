using BinanceBotWpf.Models;
using BinanceBotWpf.ViewModels;
using System;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public class OrderExecutor
    {
        private readonly BinanceClient _client;
        private readonly PositionManager _positionManager;
        private readonly DataLogger _dataLogger;
        private readonly Action<string> _logger;
        private readonly MainWindowViewModel _ui;

        public OrderExecutor(BinanceClient client, PositionManager positionManager, DataLogger dataLogger, Action<string> logger, MainWindowViewModel ui)
        {
            _client = client;
            _positionManager = positionManager;
            _dataLogger = dataLogger;
            _logger = logger;
            _ui = ui;
        }

        public async Task<decimal> ExecuteBuyAsync(
    string symbol, decimal price, decimal qty, decimal currentSpotBalance,
    decimal stopLossPercent, decimal takeProfitPercent,
    Func<decimal, decimal, Task> updateWalletAndPositions)
        {
            decimal required = qty * price;
            if (required > currentSpotBalance) return currentSpotBalance;

            _logger?.Invoke ($"💵 Попытка купить {qty} {symbol} по {price:F4}, сумма ~{required:F2} USDC (доступно {currentSpotBalance:F2})");

            var order = await _client.PlaceOrder (symbol, "BUY", "MARKET", qty);
            if (order == null)
            {
                await LogError ($"ExecuteBuy {symbol}: {_client.LastOrderError}");
                return currentSpotBalance;
            }

            // ожидание появления монет на споте
            string asset = symbol.Replace ("USDC", "");
            decimal balance = 0;
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay (1000);
                balance = await _client.GetAccountBalanceAsync (asset);
                if (balance >= qty - 0.000001m) break;
            }

            if (balance < qty - 0.000001m)
            {
                _logger?.Invoke ($"⚠️ Баланс {asset} не зачислен ({balance:F6} < {qty:F6}), OCO отложен");
                var pos = new OpenPosition
                {
                    Symbol = symbol,
                    Quantity = qty,
                    EntryPrice = price,
                    OpenTime = DateTime.UtcNow,
                    StopLossPrice = price * ( 1 - stopLossPercent ),
                    TakeProfitPrice = price * ( 1 + takeProfitPercent ),
                    HighestPrice = price,
                    HighestPriceSinceOpen = price,
                    OcoOrderListId = 0,
                    IsUnprotected = true
                };
                _positionManager.AddOrUpdate (symbol, pos);
                decimal newBalance = currentSpotBalance - required;
                _logger?.Invoke ($"✅ КУПЛЕНО: {qty} {symbol} по {price:F4} | Остаток USDC на споте: {newBalance:F2} (без OCO)");
                await updateWalletAndPositions (newBalance, 0);
                _dataLogger.LogTrade (new TradeLog
                {
                    Symbol = symbol,
                    EntryPrice = price,
                    ExitPrice = price,
                    Quantity = qty,
                    Action = "BUY_OPEN",
                    CloseTime = DateTime.UtcNow,
                    Reason = "SMA Buy"
                });
                return newBalance;
            }

            decimal stopPrice = price * ( 1 - stopLossPercent );
            decimal limitPrice = price * ( 1 + takeProfitPercent );
            long ocoOrderListId = 0;

            var ocoOrder = await _client.PlaceOcoOrder (symbol, qty, stopPrice, limitPrice);
            if (ocoOrder != null)
                ocoOrderListId = (long)ocoOrder["orderListId"];
            else
            {
                await Task.Delay (1000);
                ocoOrder = await _client.PlaceOcoOrder (symbol, qty, stopPrice, limitPrice);
                if (ocoOrder != null) ocoOrderListId = (long)ocoOrder["orderListId"];
                else _logger?.Invoke ($"⚠️ Не удалось разместить OCO-ордер для {symbol}: {_client.LastOrderError}. Защита локальная.");
            }

            var pos2 = new OpenPosition
            {
                Symbol = symbol,
                Quantity = qty,
                EntryPrice = price,
                OpenTime = DateTime.UtcNow,
                StopLossPrice = stopPrice,
                TakeProfitPrice = limitPrice,
                HighestPrice = price,
                HighestPriceSinceOpen = price,
                InitialTakeProfitPrice = limitPrice,
                OcoOrderListId = ocoOrderListId
            };
            _positionManager.AddOrUpdate (symbol, pos2);
            decimal newBalance2 = currentSpotBalance - required;
            _logger?.Invoke ($"✅ КУПЛЕНО: {qty} {symbol} по {price:F4} | Остаток USDC на споте: {newBalance2:F2}");
            await updateWalletAndPositions (newBalance2, 0);
            _dataLogger.LogTrade (new TradeLog
            {
                Symbol = symbol,
                EntryPrice = price,
                ExitPrice = price,
                Quantity = qty,
                Action = "BUY_OPEN",
                CloseTime = DateTime.UtcNow,
                Reason = "SMA Buy"
            });
            return newBalance2;
        }

        public async Task ExecuteSellAsync(string symbol, decimal price, OpenPosition pos, Func<Task> afterSell)
        {
            string asset = symbol.Replace ("USDC", "");
            decimal spotBalance = await _client.GetAccountBalanceAsync (asset);
            decimal qtyToSell = pos.Quantity;
            if (spotBalance < qtyToSell - 0.000001m && spotBalance > 0)
            {
                var (stepSize, minQty) = await _client.GetLotSizeAsync (symbol);
                qtyToSell = Math.Floor (spotBalance / stepSize) * stepSize;
                if (qtyToSell <= 0)
                {
                    _logger?.Invoke ($"⚠️ Недостаточно {asset} для продажи {symbol}. Удаляю позицию.");
                    _positionManager.Remove (symbol);
                    await afterSell ();
                    return;
                }
                _logger?.Invoke ($"⚠️ Корректировка продажи {symbol}: продаю {qtyToSell} вместо {pos.Quantity}");
            }
            if (qtyToSell <= 0)
            {
                _positionManager.Remove (symbol);
                await afterSell ();
                return;
            }

            if (pos.OcoOrderListId != 0)
            {
                bool cancelled = await _client.CancelOcoOrder (symbol, pos.OcoOrderListId);
                if (cancelled) _logger?.Invoke ($"✅ Отменён OCO-ордер {pos.OcoOrderListId}");
                else _logger?.Invoke ($"⚠️ Не удалось отменить OCO-ордер {pos.OcoOrderListId}");
            }

            var order = await _client.PlaceOrder (symbol, "SELL", "MARKET", qtyToSell);
            if (order != null)
            {
                decimal pnl = ( price - pos.EntryPrice ) * qtyToSell;
                decimal pnlPct = ( price / pos.EntryPrice - 1 ) * 100;
                _logger?.Invoke ($"🔒 ЗАКРЫТА: {symbol} по {price:F4} | PnL: {pnl:F2} ({pnlPct:F2}%)");
                var trade = new TradeLog
                {
                    Symbol = symbol,
                    EntryPrice = pos.EntryPrice,
                    ExitPrice = price,
                    Quantity = qtyToSell,
                    PnL = pnl,
                    PnLPercent = pnlPct,
                    OpenTime = pos.OpenTime,
                    CloseTime = DateTime.UtcNow,
                    Reason = "SMA Sell",
                    Duration = DateTime.UtcNow - pos.OpenTime,
                    Action = "SELL_CLOSE"
                };
                _ui.AddTradeToHistory (trade);
                _dataLogger.LogTrade (trade);
                _positionManager.Remove (symbol);
                await afterSell ();
            }
            else
            {
                _logger?.Invoke ($"❌ Не удалось продать {symbol}: {_client.LastOrderError}");
            }
        }

        private async Task LogError(string error)
        {
            _logger?.Invoke ($"❌ {error}");
            await Task.CompletedTask;
        }
    }
}