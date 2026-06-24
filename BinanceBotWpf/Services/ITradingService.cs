using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BinanceBotWpf.Models;
using BinanceBotWpf.ViewModels;

namespace BinanceBotWpf.Services
{
    public interface ITradingService
    {
        Task StartTradingAsync (MainWindowViewModel vm);
        void StopTrading ();
        Task StartGridAsync (string symbol, decimal gridRangePercent, int gridLevels, decimal investmentPercent);
        Task StopGridAsync ();
        Task StartAutoGridAsync (string symbol);
        decimal GetCurrentPriceForSymbol (string symbol);
        BinanceClient GetBinanceClient ();
        Task LoadPairsForDisplayAsync (MainWindowViewModel ui);
    }
}
