using System;
using System.Threading.Tasks;

namespace BinanceBotWpf.Risk
{
    public class RiskManager
    {
        // === КОНСТАНТЫ ===
        private const decimal MIN_BALANCE_LEVERAGE = 500m;   // минимум для 5x
        private const decimal MIN_BALANCE_TRADE = 20m;       // абсолютный минимум
        private const decimal MIN_ORDER_SIZE = 5m;           // Binance futures min
        private const int MAX_GRID_LEVELS = 20;              // потолок уровней

        // === ПУБЛИЧНЫЕ СВОЙСТВА ===
        public decimal BalanceUSDC { get; private set; }
        public decimal Leverage { get; private set; }
        public int LevelsPerSide { get; private set; }
        public double StepPercent { get; private set; }
        public decimal BuyingPower => BalanceUSDC * Leverage;
        public bool CanTrade { get; private set; }
        public string StatusMessage { get; private set; }

        // === СОБЫТИЯ ===
        public event Action<string> OnLog;

        // === ИНИЦИАЛИЗАЦИЯ ===
        public async Task InitializeAsync(Func<string, Task<decimal>> getBalanceFunc)
        {
            try
            {
                BalanceUSDC = await getBalanceFunc ("USDC");
                CalculateSafeConfig ();
            }
            catch (Exception ex)
            {
                CanTrade = false;
                StatusMessage = $"Ошибка получения баланса: {ex.Message}";
                Log (StatusMessage);
            }
        }

        // === РАСЧЁТ БЕЗОПАСНОЙ КОНФИГУРАЦИИ ===
        private void CalculateSafeConfig()
        {
            // Критически мало — торговля запрещена
            if (BalanceUSDC < MIN_BALANCE_TRADE)
            {
                SetNoTradeState ();
                return;
            }

            // Мало для плеча — работаем без плеча, минимальная сетка
            if (BalanceUSDC < MIN_BALANCE_LEVERAGE)
            {
                Leverage = 1m;
                LevelsPerSide = Math.Min ((int)( BalanceUSDC / MIN_ORDER_SIZE / 2 ), 3);
                StepPercent = 2.0;
                CanTrade = true;

                StatusMessage = $"⚠️ Баланс {BalanceUSDC:F2} USDC < {MIN_BALANCE_LEVERAGE}. Плечо отключено (1x). Уровней: {LevelsPerSide}";
                Log (StatusMessage);
                ValidateGridFit ();
                return;
            }

            // Нормальный баланс — полная конфигурация
            Leverage = 5m;
            LevelsPerSide = 10;
            StepPercent = 0.5;
            CanTrade = true;

            StatusMessage = $"✅ Баланс {BalanceUSDC:F2} USDC. Плечо {Leverage}x. Уровней: {LevelsPerSide}, шаг {StepPercent}%";
            Log (StatusMessage);
            ValidateGridFit ();
        }

        // === ПРОВЕРКА: ВЛЕЗЕТ ЛИ СЕТКА В БАЛАНС ===
        private void ValidateGridFit()
        {
            decimal required = LevelsPerSide * 2 * MIN_ORDER_SIZE; // *2 = buy + sell sides
            decimal available = BuyingPower;

            if (available < required)
            {
                int newLevels = Math.Max (1, (int)( available / MIN_ORDER_SIZE / 2 ));
                Log ($"⚠️ Покупательной способности ({available:F2}) недостаточно для {LevelsPerSide} уровней. Уменьшено до {newLevels}");
                LevelsPerSide = newLevels;
            }
        }

        private void SetNoTradeState()
        {
            Leverage = 1m;
            LevelsPerSide = 0;
            StepPercent = 0;
            CanTrade = false;
            StatusMessage = $"❌ КРИТИЧНО: Баланс {BalanceUSDC:F2} USDC < {MIN_BALANCE_TRADE}. Торговля запрещена.";
            Log (StatusMessage);
        }

        private void Log(string msg) => OnLog?.Invoke (msg);
    }
}