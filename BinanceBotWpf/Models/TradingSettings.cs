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
        public decimal RiskPerTradePercent { get; set; } = 0.02m;  // 2% риска на сделку
        public decimal MinTradeAmount { get; set; } = 10m;        // Минимальная сумма сделки
        public decimal MaxTradeAmount { get; set; } = 50m;        // Максимальная сумма сделки
        public int MaxConcurrentTrades { get; set; } = 2;         // Максимум открытых позиций

        // Настройки защиты
        public decimal StopLossPercent { get; set; } = 0.015m;     // 1.5%
        public decimal TakeProfitPercent { get; set; } = 0.03m;    // 3%
        public decimal TrailingStopPercent { get; set; } = 0.01m;  // 1%
        public decimal PartialCloseProfit { get; set; } = 0.05m;   // 5% - частичная фиксация

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
        /// Расчёт размера позиции на основе баланса
        /// </summary>
        public decimal CalculatePositionSize(decimal balance, decimal price)
        {
            decimal riskAmount = balance * RiskPerTradePercent;
            decimal positionUsdc = Math.Clamp (riskAmount, MinTradeAmount, MaxTradeAmount);
            return positionUsdc / price;
        }
    }
}