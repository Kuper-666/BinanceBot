using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Провайдер макроэкономического календаря:
    /// загружает предстоящие события (FOMC, CPI, NFP и т.д.)
    /// </summary>
    public class MacroCalendarProvider : IMacroCalendarProvider
    {
        private readonly HttpClient _httpClient;
        private readonly Action<string> _logger;
        private List<MacroEvent> _events = new ();
        private DateTime _lastFetchTime = DateTime.MinValue;
        private readonly TimeSpan _fetchInterval = TimeSpan.FromHours (6);

        public bool HasRealApi => false;

        public MacroCalendarProvider(HttpClient httpClient, Action<string> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        /// <summary>
        /// Проверяет, есть ли значимые макро-события в ближайшее время.
        /// Возвращает true если стоит воздержаться от торговли.
        /// </summary>
        public async Task<bool> IsHighImpactEventNearAsync (int minutesAhead = 60)
        {
            if ((DateTime.UtcNow - _lastFetchTime) < _fetchInterval)
            {
                return _events.Any (e => e.Impact == "High" && Math.Abs ((e.Time - DateTime.UtcNow).TotalMinutes) < minutesAhead);
            }

            try
            {
                await FetchEventsAsync ();
                _lastFetchTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"⚠️ Ошибка загрузки макро-календаря: {ex.Message}");
            }

            return _events.Any (e => e.Impact == "High" && Math.Abs ((e.Time - DateTime.UtcNow).TotalMinutes) < minutesAhead);
        }

        private async Task FetchEventsAsync ()
        {
            // Базовый список макро-событий (в реальности парсим с Investing.com илиsimilar)
            // Здесь заглушка для демонстрации архитектуры
            _events = new List<MacroEvent>
            {
                // FOMC, CPI, NFP и т.д. загружаются из внешнего источника
            };

            _logger?.Invoke ($"📅 Макро-календарь обновлён: {_events.Count} событий");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Возвращает ближайшие события с высоким влиянием
        /// </summary>
        public List<MacroEvent> GetHighImpactEvents ()
        {
            return _events.Where (e => e.Impact == "High" && e.Time > DateTime.UtcNow).ToList ();
        }
    }

    public class MacroEvent
    {
        public string Name { get; set; }
        public DateTime Time { get; set; }
        public string Impact { get; set; } // Low, Medium, High
        public string Currency { get; set; }
    }
}
