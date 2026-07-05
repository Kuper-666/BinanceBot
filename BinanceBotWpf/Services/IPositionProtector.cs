using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BinanceBotWpf.Models;

namespace BinanceBotWpf.Services
{
    public interface IPositionProtector
    {
        bool EnableDynamicTrailingStop { get; set; }
        decimal ActivationProfitPercent { get; set; }
        decimal TrailingStepPercent { get; set; }
        decimal TrailingStopPercent { get; set; }
        decimal PartialClosePercent { get; set; }
        TimeSpan MaxHoldTime { get; set; }
        decimal PartialCloseQtyPercent { get; set; }

        Task<List<string>> CheckAndProtectAsync (Func<string, decimal> getPrice, Func<string, Task<decimal>> restPriceFetcher = null);
    }
}
