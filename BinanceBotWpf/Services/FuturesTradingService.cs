using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using BinanceBotWpf.Models;
using BinanceBotWpf.ViewModels;
using Newtonsoft.Json.Linq;

namespace BinanceBotWpf.Services
{
    public class FuturesTradingService
    {
        private readonly BinanceFuturesClient _client;
        private readonly int _leverage;
        private readonly decimal _maxRiskPercent;
        private MainWindowViewModel _ui;
        private bool _isRunning;
        private readonly StrategyEngine _strategy = new ();
        private List<string> _activeSymbols = new () { "BTCUSDT", "ETHUSDT", "SOLUSDT" }; // начальный список
        private readonly object _symbolsLock = new object ();
        private readonly Dictionary<string, decimal> _trailingCallbackRate = new ();
        private readonly Dictionary<string, decimal> _highestProfit = new ();
        private readonly decimal _trailingActivationPercent = 0.02m;
        private readonly decimal _defaultCallbackRate = 1.5m;

        public FuturesTradingService(BinanceFuturesClient client, int leverage, decimal maxRiskPercent)
        {
            _client = client;
            _leverage = leverage;
            _maxRiskPercent = maxRiskPercent;
        }

        public void SetLogger(Action<string> logger) => _client.OnLogGenerated += logger;

        public async Task StartAsync(MainWindowViewModel vm)
        {
            _ui = vm;
            _isRunning = true;
            await _client.SyncTimeAsync ();
            // Устанавливаем плечо для каждого символа
            foreach (var sym in _activeSymbols)
                await _client.SetLeverageAsync (sym, _leverage);
            _ui.AddLog ("✅ Фьючерсный модуль запущен");
            await RunAsync ();
        }

        private async Task RunAsync()
        {
            while (_isRunning)
            {
                try
                {
                    decimal balance = await _client.GetAccountBalanceAsync ("USDT");
                    _ui.AddLog ($"💎 Фьючерсный баланс USDT: {balance:F2}");

                    foreach (var symbol in _activeSymbols)
                    {
                        // Получаем цену
                        var klines = await _client.GetKlinesAsync (symbol, "5m", 50);
                        if (klines == null || klines.Count < 20) continue;
                        var closes = klines.Select (k => k.Close).ToList ();
                        decimal price = closes.Last ();
                        // Анализируем сигнал
                        var signal = _strategy.AnalyzePairWithWallet (symbol, closes, 9, 21, price);
                        if (signal.Action == TradeAction.Buy)
                        {
                            // Расчёт позиции с учётом плеча
                            decimal riskAmount = balance * _maxRiskPercent;
                            decimal positionSize = ( riskAmount * _leverage ) / price;
                            // Округление до шага лота
                            decimal stepSize = 0.001m; // упрощённо, лучше запрашивать через API
                            positionSize = Math.Floor (positionSize / stepSize) * stepSize;
                            if (positionSize > 0)
                            {
                                _ui.AddLog ($"📈 Фьючерс: покупка {positionSize} {symbol} по {price}");
                                var order = await _client.PlaceMarketOrder (symbol, "BUY", positionSize);
                                if (order != null)
                                {
                                    _ui.AddLog ($"✅ Фьючерс: ордер исполнен");
                                    _trailingCallbackRate[symbol] = _defaultCallbackRate;
                                    _highestProfit[symbol] = 0m;
                                }
                            }
                        }
                        else if (signal.Action == TradeAction.Sell)
                        {
                            // Закрытие позиции (упрощённо)
                            var positions = await _client.GetPositionsAsync ();
                            var pos = positions.FirstOrDefault (p => p["symbol"].ToString () == symbol && p["positionAmt"].Value<decimal> () != 0);
                            if (pos != null)
                            {
                                decimal qty = Math.Abs (pos["positionAmt"].Value<decimal> ());
                                _ui.AddLog ($"📉 Фьючерс: закрытие {qty} {symbol}");
                                await _client.PlaceMarketOrder (symbol, "SELL", qty);
                            }
                        }
                    }
                    // Трейлинг-стоп для всех открытых фьючерсных позиций
                    var openPositions = await _client.GetPositionsAsync ();
                    foreach (var pos in openPositions)
                    {
                        string sym = pos["symbol"].ToString ();
                        decimal amt = pos["positionAmt"].Value<decimal> ();
                        if (amt == 0) continue;

                        decimal entryPrice = pos["entryPrice"].Value<decimal> ();
                        if (entryPrice <= 0) continue;

                        decimal currentPrice = await _client.GetPriceAsync (sym);
                        if (currentPrice <= 0) continue;

                        decimal profitPercent = ( currentPrice - entryPrice ) / entryPrice;
                        if (profitPercent >= _trailingActivationPercent && profitPercent > _highestProfit.GetValueOrDefault (sym, 0m))
                        {
                            _highestProfit[sym] = profitPercent;
                            decimal callbackRate = Math.Max (1.0m, _defaultCallbackRate - profitPercent * 10m);
                            decimal qty = Math.Abs (amt);
                            var trailingOrder = await _client.PlaceTrailingStopMarketAsync (sym, "SELL", qty, callbackRate);
                            if (trailingOrder != null)
                            {
                                _ui.AddLog ($"📈 Фьючерс трейлинг {sym}: profit={profitPercent:P1}, callback={callbackRate:F1}%");
                            }
                        }
                    }
                    await Task.Delay (10000);
                }
                catch (Exception ex) { _ui.AddLog ($"❌ Фьючерс ошибка: {ex.Message}"); await Task.Delay (10000); }
            }
        }

        public void Stop() => _isRunning = false;
    }
}