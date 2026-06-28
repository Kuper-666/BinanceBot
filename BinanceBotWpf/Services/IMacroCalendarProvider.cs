using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public interface IMacroCalendarProvider
    {
        bool HasRealApi { get; }
        Task<bool> IsHighImpactEventNearAsync (int minutesAhead = 60);
        List<MacroEvent> GetHighImpactEvents ();
    }
}
