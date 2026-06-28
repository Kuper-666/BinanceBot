using System;

namespace BinanceBotWpf.Services
{
    public interface IEarnManager
    {
        event Action<string> OnLogGenerated;
    }
}
