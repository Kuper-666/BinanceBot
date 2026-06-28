namespace BinanceBotWpf.Models
{
    /// <summary>
    /// Поддерживаемые интервалы для анализа свечей.
    /// Минимум: 1 час (чтобы избежать шума на коротких интервалах).
    /// Максимум: 1 месяц.
    /// </summary>
    public enum CandleInterval
    {
        /// <summary>1 час (стандартный, рекомендуется для дневной торговли)</summary>
        OneHour = 1,

        /// <summary>4 часа (хороший баланс между шумом и скоростью)</summary>
        FourHours = 4,

        /// <summary>1 день (свинг-трейдинг)</summary>
        OneDay = 24,

        /// <summary>1 неделя (долгосрочный анализ)</summary>
        OneWeek = 168,

        /// <summary>1 месяц (анализ больших трендов)</summary>
        OneMonth = 720
    }

    public static class CandleIntervalExtensions
    {
        /// <summary>
        /// Преобразует enum в строку для Binance API.
        /// </summary>
        public static string ToBinanceInterval(this CandleInterval interval)
        {
            return interval switch
            {
                CandleInterval.OneHour => "1h",
                CandleInterval.FourHours => "4h",
                CandleInterval.OneDay => "1d",
                CandleInterval.OneWeek => "1w",
                CandleInterval.OneMonth => "1M",
                _ => "1h" // по умолчанию 1h
            };
        }

        /// <summary>
        /// Преобразует строку Binance API в enum с валидацией.
        /// </summary>
        public static bool TryParseBinanceInterval(string interval, out CandleInterval result)
        {
            result = CandleInterval.OneHour; // по умолчанию

            // Проверяем что интервал не меньше 1 часа
            switch (interval)
            {
                case "1h":
                    result = CandleInterval.OneHour;
                    return true;
                case "4h":
                    result = CandleInterval.FourHours;
                    return true;
                case "1d":
                    result = CandleInterval.OneDay;
                    return true;
                case "1w":
                    result = CandleInterval.OneWeek;
                    return true;
                case "1M":
                    result = CandleInterval.OneMonth;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Человеческое описание интервала (например, "1 час", "4 часа").
        /// </summary>
        public static string ToDisplayName(this CandleInterval interval)
        {
            return interval switch
            {
                CandleInterval.OneHour => "1 час",
                CandleInterval.FourHours => "4 часа",
                CandleInterval.OneDay => "1 день",
                CandleInterval.OneWeek => "1 неделя",
                CandleInterval.OneMonth => "1 месяц",
                _ => "1 час"
            };
        }
    }
}
