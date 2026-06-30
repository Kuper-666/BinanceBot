using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BinanceBotWpf.Models;
using BinanceBotWpf.Risk;
using BinanceBotWpf.ViewModels;
using Newtonsoft.Json.Linq;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Handles buy and sell order execution with cooldown management.
    /// </summary>
    public class OrderExecutor
    {
        private readonly BinanceClient _client;
        private readonly IAiRiskEngine _aiRiskEngine;
        private readonly IPositionManager _positionManager;
        private readonly IRiskManager _riskManager;
        private readonly Func<CancellationToken> _getCancellationToken;
        private readonly Func<string, Task> _sendNotification;
        private Func<string, decimal> _getPrice;

        private MainWindowViewModel _ui;

        private readonly Dictionary<string, DateTime> _lastBuyTime = new ();
        private readonly List<DateTime> _recentTradeTimes = new ();
        private readonly object _cooldownLock = new ();

        public OrderExecutor (
            BinanceClient client,
            IAiRiskEngine aiRiskEngine,
            IPositionManager positionManager,
            IRiskManager riskManager,
            Func<CancellationToken> getCancellationToken,
            Func<string, Task> sendNotification)
        {
            _client = client;
            _aiRiskEngine = aiRiskEngine;
            _positionManager = positionManager;
            _riskManager = riskManager;
            _getCancellationToken = getCancellationToken;
            _sendNotification = sendNotification;
        }

        public void SetViewModel (MainWindowViewModel ui)
        {
            _ui = ui;
        }

        public void SetPriceProvider (Func<string, decimal> getPrice)
        {
            _getPrice = getPrice;
        }

        public Dictionary<string, DateTime> GetLastBuyTimes ()
        {
            lock (_cooldownLock)
            {
                return new Dictionary<string, DateTime> (_lastBuyTime);
            }
        }

        public List<DateTime> GetRecentTradeTimes ()
        {
            lock (_cooldownLock)
            {
                return new List<DateTime> (_recentTradeTimes);
            }
        }

        public void RestoreCooldowns (Dictionary<string, DateTime> lastBuyTimes, List<DateTime> recentTradeTimes)
        {
            lock (_cooldownLock)
            {
                _lastBuyTime.Clear ();
                foreach (KeyValuePair<string, DateTime> kvp in lastBuyTimes)
                {
                    if (DateTime.UtcNow - kvp.Value < TimeSpan.FromMinutes (15))
                        _lastBuyTime[kvp.Key] = kvp.Value;
                }

                _recentTradeTimes.Clear ();
                foreach (DateTime t in recentTradeTimes)
                {
                    if (DateTime.UtcNow - t < TimeSpan.FromHours (1))
                        _recentTradeTimes.Add (t);
                }
            }
        }

        private decimal GetCurrentPrice (string symbol)
        {
            if (_getPrice != null) return _getPrice (symbol);
            return 0m;
        }

        public async Task ExecuteBuyAsync (string symbol, Dictionary<string, decimal> indicators, decimal currentBalance)
        {
            if (!indicators.ContainsKey ("price")) return;

            decimal price = indicators["price"];
            decimal rsi = indicators.ContainsKey ("rsi") ? indicators["rsi"] : 50;
            decimal fastSma = indicators.ContainsKey ("fastSma") ? indicators["fastSma"] : 0;
            decimal slowSma = indicators.ContainsKey ("slowSma") ? indicators["slowSma"] : 0;
            decimal macdHist = indicators.ContainsKey ("macdHist") ? indicators["macdHist"] : 0;
            decimal bbWidth = indicators.ContainsKey ("bbWidth") ? indicators["bbWidth"] : 0.05m;
            decimal volumeRatio = indicators.ContainsKey ("volumeRatio") ? indicators["volumeRatio"] : 1.0m;
            decimal obv = indicators.ContainsKey ("obv") ? indicators["obv"] : 0;

            AiRiskResult aiRisk = await _aiRiskEngine.CalculateRiskAsync (
                symbol, currentBalance, price, fastSma, slowSma, rsi, volumeRatio, macdHist, bbWidth, obv);

            _ui?.AddLog ($"{symbol}: Risk={aiRisk.RiskPerTradePercent:P1} TP={aiRisk.TakeProfitPercent:P2} SL={aiRisk.StopLossPercent:P2} R/R={aiRisk.RiskRewardRatio:F1}");

            decimal riskPerTrade = aiRisk.RiskPerTradePercent;
            decimal riskRewardRatio = aiRisk.RiskRewardRatio;
            decimal riskAmount = RiskCalculator.CalculateRiskAmount (currentBalance, riskPerTrade);

            decimal stepSize = await _client.GetStepSizeAsync (symbol);
            decimal minQty = 0m;
            (decimal qty, RiskCalculator.QuantityResult qtyResult) = RiskCalculator.CalculatePositionQuantity (riskAmount, price, stepSize, minQty, currentBalance);

            decimal symbolMinNotional = await _client.GetMinNotionalAsync (symbol);
            decimal notional = qty * price;
            if (notional < symbolMinNotional)
            {
                decimal minQtyForNotional = Math.Ceiling (symbolMinNotional / price / stepSize) * stepSize;
                if (minQtyForNotional * price <= currentBalance)
                {
                    qty = minQtyForNotional;
                    notional = qty * price;
                    qtyResult = RiskCalculator.QuantityResult.Ok;
                    _ui?.AddLog ($"{symbol}: поднято до {qty} ({notional:F2} USDC) для MIN_NOTIONAL ({symbolMinNotional} USDC)");
                }
                else
                {
                    _ui?.AddLog ($"{symbol}: BUY пропущен — {currentBalance:F2} USDC < MIN_NOTIONAL ({symbolMinNotional} USDC)");
                    return;
                }
            }

            switch (qtyResult)
            {
                case RiskCalculator.QuantityResult.InsufficientBalanceForMinNotional:
                    _ui?.AddLog ($"{symbol}: BUY проигнорирован — баланс {currentBalance:F2} USDC ниже минимального ордера");
                    return;
                case RiskCalculator.QuantityResult.ZeroQuantityAfterRounding:
                    _ui?.AddLog ($"{symbol}: BUY проигнорирован — нулевое количество после округления");
                    return;
                case RiskCalculator.QuantityResult.ExceedsAvailableBalance:
                    _ui?.AddLog ($"{symbol}: BUY проигнорирован — ордер превышает доступный баланс {currentBalance:F2} USDC");
                    return;
            }

            if (qty * price > riskAmount * 1.01m)
                _ui?.AddLog ($"{symbol}: риск {riskAmount:F2} USDC ниже минимального ордера, сумма поднята до {qty * price:F2} USDC");

            if (aiRisk.TakeProfitPercent < 0.004m)
            {
                _ui?.AddLog ($"{symbol}: BUY пропущен — TP {aiRisk.TakeProfitPercent:P2} < минимального 0.4%");
                return;
            }

            lock (_cooldownLock)
            {
                if (_lastBuyTime.TryGetValue (symbol, out DateTime lastTime) && DateTime.UtcNow - lastTime < TimeSpan.FromMinutes (15))
                {
                    _ui?.AddLog ($"{symbol}: BUY проигнорирован — кулдаун ({(TimeSpan.FromMinutes (15) - (DateTime.UtcNow - lastTime)).TotalSeconds:F0} сек)");
                    return;
                }

                _recentTradeTimes.RemoveAll (t => DateTime.UtcNow - t > TimeSpan.FromHours (1));
                if (_recentTradeTimes.Count >= 3)
                {
                    _ui?.AddLog ($"{symbol}: BUY пропущен — глобальный лимит 3 сделки/час");
                    return;
                }

                _lastBuyTime[symbol] = DateTime.UtcNow;
                _recentTradeTimes.Add (DateTime.UtcNow);
            }

            decimal adaptiveSlMult = indicators.ContainsKey ("adaptiveSlMultiplier") ? indicators["adaptiveSlMultiplier"] : 1.0m;
            decimal slPrice = price * (1 - aiRisk.StopLossPercent * adaptiveSlMult);
            decimal tpPrice = price * (1 + aiRisk.TakeProfitPercent * adaptiveSlMult);
            decimal slPct = aiRisk.StopLossPercent * adaptiveSlMult;

            _ui?.AddLog ($"{symbol}: Risk={riskAmount:F2} ({riskPerTrade:P2}), SL={slPrice:F4} (-{slPct:P2}), TP={tpPrice:F4} (+{aiRisk.TakeProfitPercent:P2}), R/R 1:{riskRewardRatio:F1}");

            decimal tickSize = await _client.GetTickSizeAsync (symbol);
            decimal limitPrice = price * 0.998m;
            if (tickSize > 0)
                limitPrice = Math.Floor (limitPrice / tickSize) * tickSize;

            _ui?.AddLog ($"Покупка {qty} {symbol} | лимит {limitPrice:F4} ( рынок {price:F4}, -0.2%)");
            JObject order = await _client.PlaceLimitOrder (symbol, "BUY", qty, limitPrice);

            if (order != null)
            {
                OpenPosition pos = new OpenPosition
                {
                    Symbol = symbol,
                    Quantity = qty,
                    EntryPrice = limitPrice,
                    OpenTime = DateTime.UtcNow,
                    StopLossPrice = slPrice,
                    TakeProfitPrice = tpPrice,
                    HighestPrice = limitPrice,
                    HighestPriceSinceOpen = limitPrice,
                    OcoOrderListId = 0
                };

                await _positionManager.AddOrUpdateAsync (symbol, pos);
                _ui?.AddLog ($"Куплено {qty} {symbol} | SL={slPrice:F4} TP={tpPrice:F4} | R/R 1:{riskRewardRatio:F1}");
                _ui?.UpdatePositionsStatus (_positionManager.Count, _ui?.MaxConcurrentTrades ?? 3, _positionManager.GetSymbols ());

                await _sendNotification ($"🟢 <b>ПОКУПКА</b>\n" +
                    $"📊 {symbol}\n" +
                    $"💵 Цена: {limitPrice:F4}\n" +
                    $"📦 Количество: {qty}\n" +
                    $"🛡 SL: {slPrice:F4} (-{slPct:P2})\n" +
                    $"🎯 TP: {tpPrice:F4} (+{aiRisk.TakeProfitPercent:P2})\n" +
                    $"⚖️ Риск: {riskAmount:F2} USDC ({riskPerTrade:P2})\n" +
                    $"📐 R/R: 1:{riskRewardRatio:F1}");
            }
        }

        public async Task ExecuteSellAsync (string symbol)
        {
            if (!_positionManager.TryGet (symbol, out OpenPosition pos)) return;

            string asset = symbol.Replace ("USDC", "");
            decimal price = GetCurrentPrice (symbol);

            decimal spotBalance = await _client.GetAccountBalanceAsync (asset);

            if (spotBalance < pos.Quantity * 0.99m)
            {
                _ui?.AddLog ($"{symbol}: на споте {spotBalance:F8} {asset}, нужно {pos.Quantity:F8}. Проверяю Earn...");

                JArray earnPositions = await _client.GetFlexibleEarnBalanceAsync ();
                if (earnPositions != null)
                {
                    JObject earnPos = null;
                    foreach (JObject p in earnPositions)
                    {
                        if (p["asset"]?.ToString () == asset)
                        {
                            earnPos = p;
                            break;
                        }
                    }

                    if (earnPos != null)
                    {
                        decimal earnAmount = decimal.Parse (earnPos["totalAmount"]?.ToString () ?? "0", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
                        _ui?.AddLog ($"   В Earn: {earnAmount:F8} {asset}");

                        if (earnAmount > 0)
                        {
                            decimal needToRedeem = Math.Min (pos.Quantity - spotBalance, earnAmount);
                            _ui?.AddLog ($"   Выкупаю {needToRedeem:F8} {asset} из Earn...");
                            bool redeemed = await _client.RedeemFlexibleEarnWithWaitAsync (asset, needToRedeem);
                            if (redeemed)
                            {
                                CancellationToken ct = _getCancellationToken ();
                                await Task.Delay (2000, ct);
                                spotBalance = await _client.GetAccountBalanceAsync (asset);
                                _ui?.AddLog ($"   После выкупа на споте: {spotBalance:F8} {asset}");
                            }
                            else
                            {
                                _ui?.AddLog ($"   Не удалось выкупить из Earn");
                            }
                        }
                    }
                }
            }

            decimal qtyToSell = Math.Min (pos.Quantity, spotBalance);
            if (qtyToSell <= 0.000001m)
            {
                _ui?.AddLog ($"{symbol}: нет актива для продажи (ни на споте, ни в Earn)");
                await _positionManager.RemoveAsync (symbol);
                return;
            }

            decimal stepSize = await _client.GetStepSizeAsync (symbol);
            if (stepSize > 0)
            {
                qtyToSell = Math.Floor (qtyToSell / stepSize) * stepSize;
                if (qtyToSell <= 0)
                {
                    _ui?.AddLog ($"{symbol}: количество {pos.Quantity} меньше шага лота {stepSize}");
                    return;
                }
            }

            if (pos.OcoOrderListId != 0)
            {
                await _client.CancelOcoOrder (symbol, pos.OcoOrderListId);
            }

            decimal tickSize = await _client.GetTickSizeAsync (symbol);
            decimal limitPrice = price * 1.002m;
            if (tickSize > 0)
                limitPrice = Math.Ceiling (limitPrice / tickSize) * tickSize;

            _ui?.AddLog ($"Продажа {qtyToSell} {symbol} | лимит {limitPrice:F4} ( рынок {price:F4}, +0.2%)");
            JObject sellOrder = await _client.PlaceLimitOrder (symbol, "SELL", qtyToSell, limitPrice);
            if (sellOrder != null)
            {
                decimal pnl = (limitPrice - pos.EntryPrice) * qtyToSell;
                decimal pnlPct = (limitPrice / pos.EntryPrice - 1) * 100;

                _ui?.AddLog ($"Закрыта {symbol}: PnL {pnl:F2} ({pnlPct:F2}%)");

                TradeLog trade = new TradeLog
                {
                    Symbol = symbol,
                    EntryPrice = pos.EntryPrice,
                    ExitPrice = limitPrice,
                    Quantity = qtyToSell,
                    PnL = pnl,
                    PnLPercent = pnlPct,
                    OpenTime = pos.OpenTime,
                    CloseTime = DateTime.UtcNow,
                    Reason = "Signal Sell",
                    Duration = DateTime.UtcNow - pos.OpenTime,
                    Action = "SELL_CLOSE"
                };

                _ui.AddTradeToHistory (trade);
                _riskManager.RecordTrade (pnl);
                await _positionManager.RemoveAsync (symbol);
                _ui?.UpdatePositionsStatus (_positionManager.Count, _ui?.MaxConcurrentTrades ?? 3, _positionManager.GetSymbols ());

                string emoji = pnl >= 0 ? "🟢" : "🔴";
                await _sendNotification ($"{emoji} <b>ПРОДАЖА</b>\n" +
                    $"📊 {symbol}\n" +
                    $"💵 Вход: {pos.EntryPrice:F4} → Выход: {limitPrice:F4}\n" +
                    $"📦 Количество: {qtyToSell}\n" +
                    $"📈 PnL: {pnl:+F2;-F2} USDC ({pnlPct:+F2;-F2}%)\n" +
                    $"⏱ Длительность: {(DateTime.UtcNow - pos.OpenTime):hh\\:mm}");
            }
        }
    }
}
