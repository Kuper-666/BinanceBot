using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public interface INewsProvider
    {
        bool HasRealApi { get; }
        Task<bool> IsEventNearAsync (int minutesAhead = 30);
        List<DateTime> GetUpcomingEvents ();
    }
}
