using System;

namespace BinanceBotWpf.Services
{
    public enum MarketSession
    {
        Asia,
        Europe,
        Us,
        EuropeUsOverlap,
        Weekend,
        OffHours
    }

    public static class MarketSessionService
    {
        public static MarketSession GetCurrentSession ()
        {
            return GetCurrentSession (DateTime.UtcNow);
        }

        public static MarketSession GetCurrentSession (DateTime utcNow)
        {
            if (utcNow.DayOfWeek == DayOfWeek.Saturday || utcNow.DayOfWeek == DayOfWeek.Sunday)
            {
                return MarketSession.Weekend;
            }

            int hour = utcNow.Hour;

            bool isAsia = hour >= 0 && hour < 8;
            bool isEurope = hour >= 7 && hour < 16;
            bool isUs = hour >= 13 && hour < 22;

            if (isEurope && isUs)
                return MarketSession.EuropeUsOverlap;
            if (isEurope)
                return MarketSession.Europe;
            if (isUs)
                return MarketSession.Us;
            if (isAsia)
                return MarketSession.Asia;

            return MarketSession.OffHours;
        }

        public static string GetSessionLabel (MarketSession session)
        {
            return session switch
            {
                MarketSession.Asia => "🌏 Asia",
                MarketSession.Europe => "🌍 EU",
                MarketSession.Us => "🌎 US",
                MarketSession.EuropeUsOverlap => "🌍🌎 EU+US",
                MarketSession.Weekend => "💤 Weekend",
                MarketSession.OffHours => "🌙 Off",
                _ => "❓"
            };
        }

        public static string GetSessionLabel ()
        {
            return GetSessionLabel (GetCurrentSession ());
        }

        public static bool IsHighVolumeSession (MarketSession session)
        {
            return session == MarketSession.EuropeUsOverlap
                || session == MarketSession.Europe
                || session == MarketSession.Us;
        }

        public static bool IsHighVolumeSession ()
        {
            return IsHighVolumeSession (GetCurrentSession ());
        }

        public static bool ShouldTrade (MarketSession session, bool restrictToEuUs = false)
        {
            if (session == MarketSession.Weekend)
                return false;

            if (restrictToEuUs && !IsHighVolumeSession (session))
                return false;

            return true;
        }
    }
}
