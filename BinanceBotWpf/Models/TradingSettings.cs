using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace BinanceBotWpf.Models
{
    /// <summary>
    /// Централизованные настройки торговой стратегии
    /// </summary>
    public class TradingSettings
    {
        // Настройки SMA
        public int FastSmaPeriod { get; set; } = 13;
        public int SlowSmaPeriod { get; set; } = 34;

        // Настройки RSI
        public int RsiPeriod { get; set; } = 14;
        public int RsiOversold { get; set; } = 30;
        public int RsiOverbought { get; set; } = 70;

        // Настройки управления капиталом
        public decimal RiskPerTradePercent { get; set; } = 0.01m;  // 1% риска на сделку (жёсткий лимит)
        public decimal RiskRewardRatio { get; set; } = 3.0m;       // Соотношение риск/прибыль 1:3
        public decimal MinTradeAmount { get; set; } = 10m;        // Минимальная сумма сделки
        public decimal MaxTradeAmount { get; set; } = 50m;        // Максимальная сумма сделки
        public int MaxConcurrentTrades { get; set; } = 2;         // Максимум открытых позиций

        // Настройки защиты (StopLossPercent вычисляется динамически через ATR или RiskAmount)
        public decimal StopLossPercent { get; set; } = 0.015m;     // 1.5% — fallback если ATR недоступен
        public decimal TakeProfitPercent { get; set; } = 0.045m;   // 4.5% = 1.5% * 3 (R/R 1:3)
        public decimal TrailingStopPercent { get; set; } = 0.01m;  // 1%
        public decimal PartialCloseProfit { get; set; } = 0.05m;   // 5% - частичная фиксация

        // Настройки сетки (Grid Bot)
        public bool GridBotEnabled { get; set; } = false;
        public decimal GridRangePercent { get; set; } = 0.10m;     // ±10% от текущей цены
        public int GridLevels { get; set; } = 10;                 // Количество уровней в каждую сторону
        public decimal TotalInvestmentPercent { get; set; } = 0.20m; // 20% капитала на сетку
        public bool GridDynamicStep { get; set; } = false;        // Динамический шаг на основе ATR

        // Мульти-таймфрейм
        public string MainTimeframe { get; set; } = "1h";         // Основной таймфрейм для сигнала
        public string EntryTimeframe { get; set; } = "5m";        // Таймфрейм для входа

        // Дополнительные стратегии
        public bool VolumeBreakoutEnabled { get; set; } = false;  // Пробой объёма
        public decimal VolumeBreakoutMultiplier { get; set; } = 2.0m; // Множитель среднего объёма
        public bool DcaEnabled { get; set; } = false;             // DCA (усреднение)
        public int DcaIntervalDays { get; set; } = 3;             // Интервал проверки DCA (дни)
        public decimal DcaMaxDrawdownPercent { get; set; } = 0.30m; // Макс. отклонение для DCA
        public decimal DcaBuyPercent { get; set; } = 0.10m;       // 10% портфеля за покупку DCA

        // Фьючерсы
        public bool FuturesEnabled { get; set; } = false;
        public int FuturesLeverage { get; set; } = 3;             // Плечо по умолчанию
        public bool FuturesIsolatedMargin { get; set; } = true;   // Изолированная маржа
        public bool FuturesHedgeMode { get; set; } = true;        // Hedge Mode

        // Настройки фильтрации
        public bool RequireVolumeConfirmation { get; set; } = true;
        public decimal MinVolumeRatio { get; set; } = 0.8m;
        public bool RequireTrendConfirmation { get; set; } = true;

        // Настройки времени
        public bool RestrictTradingHours { get; set; } = false;
        public int TradingStartHour { get; set; } = 9;   // МСК
        public int TradingEndHour { get; set; } = 23;    // МСК

        // Путь к файлу настроек
        private static readonly string SettingsPath = Path.Combine (
            AppDomain.CurrentDomain.BaseDirectory, "Data", "trading_settings.json");

        // Дополнительные фильтры
        public bool UseMarketFilter { get; set; } = true;      // Фильтр по рынку
        public bool AvoidNewsTime { get; set; } = true;        // Избегать новостей
        public decimal MinLiquidity { get; set; } = 1_000_000; // Мин. ликвидность

        // Динамический размер позиции
        public bool DynamicPositionSizing { get; set; } = true;
        public decimal MaxPositionPercent { get; set; } = 0.25m; // Макс 25% баланса

        /// <summary>
        /// Сохранить настройки в файл
        /// </summary>
        public async Task SaveAsync()
        {
            try
            {
                string dir = Path.GetDirectoryName (SettingsPath);
                if (!Directory.Exists (dir)) Directory.CreateDirectory (dir);

                string json = JsonSerializer.Serialize (this, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync (SettingsPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine ($"Save settings error: {ex.Message}");
            }
        }

        /// <summary>
        /// Загрузить настройки из файла
        /// </summary>
        public static async Task<TradingSettings> LoadAsync()
        {
            if (!File.Exists (SettingsPath))
                return new TradingSettings ();

            try
            {
                string json = await File.ReadAllTextAsync (SettingsPath);
                return JsonSerializer.Deserialize<TradingSettings> (json) ?? new TradingSettings ();
            }
            catch
            {
                return new TradingSettings ();
            }
        }

        /// <summary>
        /// Проверка, можно ли сейчас торговать
        /// </summary>
        public bool CanTradeNow()
        {
            if (!RestrictTradingHours) return true;

            var now = DateTime.UtcNow.AddHours (3); // МСК
            int hour = now.Hour;
            return hour >= TradingStartHour && hour < TradingEndHour;
        }

        /// <summary>
        /// Расчёт размера позиции на основе баланса и процента риска
        /// </summary>
        public decimal CalculatePositionSize(decimal balance, decimal price)
        {
            decimal riskAmount = balance * RiskPerTradePercent;
            decimal positionUsdc = Math.Clamp (riskAmount, MinTradeAmount, MaxTradeAmount);
            return positionUsdc / price;
        }

        /// <summary>
        /// Расчёт SL/TP на основе расстояния стопа
        /// StopLossPercent задаётся пользователем или вычисляется через ATR
        /// TakeProfit = StopLoss * RiskRewardRatio
        /// </summary>
        public (decimal SlPercent, decimal TpPercent) CalculateStopLossAndTakeProfit(decimal stopLossPercent)
        {
            decimal sl = Math.Max (stopLossPercent, 0.005m);  // Минимум 0.5%
            decimal tp = sl * RiskRewardRatio;
            return (sl, tp);
        }
    }
}