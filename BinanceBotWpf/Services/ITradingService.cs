using BinanceBotWpf.Models;
using BinanceBotWpf.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public interface ITradingService
    {
        Task StartTradingAsync(MainWindowViewModel vm);
        void StopTrading();
        Task StartGridAsync(string symbol, decimal gridRangePercent, int gridLevels, decimal investmentPercent);
        Task StopGridAsync();
        Task StartAutoGridAsync(string symbol);
        decimal GetCurrentPriceForSymbol(string symbol);
        BinanceClient GetBinanceClient();
        Task LoadPairsForDisplayAsync(MainWindowViewModel ui);
    }
}