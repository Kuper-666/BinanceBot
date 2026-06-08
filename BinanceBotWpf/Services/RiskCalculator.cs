using BinanceBotWpf.Models;
using BinanceBotWpf.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public class RiskCalculator
    {
        private readonly BinanceClient _client;
        private readonly MainWindowViewModel _ui;
        private readonly Action<string> _logger;

        public RiskCalculator(BinanceClient client, MainWindowViewModel ui, Action<string> logger)
        {
            _client = client;
            _ui = ui;
            _logger = logger;
        }

        public Task<decimal> CalculateDynamicRiskAsync(decimal totalBalance, decimal baseRisk, decimal volatility)
        {
            volatility = Math.Clamp (volatility, 0.005m, 0.30m);
            decimal riskMultiplier = Math.Max (0.2m, 1 - ( volatility - 0.02m ) * 10);
            decimal adjustedRisk = Math.Clamp (baseRisk * riskMultiplier, 0.05m, 0.25m);
            _logger?.Invoke ($"📊 Волатильность: {volatility:P2}, скорректированный риск: {adjustedRisk:P2}");
            return Task.FromResult (totalBalance * adjustedRisk);
        }

        public async Task<decimal> CalculateAtrAsync(string symbol, int period = 14)
        {
            return await _client.GetATRAsync (symbol, period);
        }

        public async Task<decimal> CalculatePositionSizeAsync(string symbol, decimal riskCapital, decimal price)
        {
            decimal atr = await CalculateAtrAsync (symbol);
            if (atr <= 0) atr = price * 0.02m;
            decimal positionSize = riskCapital / atr; // размер в единицах актива
            decimal rawQty = positionSize / price;
            decimal stepSize = await _client.GetStepSizeAsync (symbol);
            decimal qty = Math.Floor (rawQty / stepSize) * stepSize;
            qty = Math.Round (qty, 8);
            return qty;
        }
    }
}
