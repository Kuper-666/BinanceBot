using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BinanceBotWpf.Risk
{
    public class RiskManager : IRiskManager
    {
        // === КОНСТАНТЫ ===
        private const decimal MIN_BALANCE_LEVERAGE = 500m;
        private const decimal MIN_BALANCE_TRADE = 20m;
        private const decimal MIN_ORDER_SIZE = 5m;
        private const int MAX_GRID_LEVELS = 20;

        // === ПОРТФЕЛЬНЫЕ ЛИМИТЫ (#14) ===
        public int MaxOpenOrders { get; set; } = 5;
        public decimal MaxDailyLossPercent { get; set; } = 0.10m;   // 10% от баланса
        public decimal MaxExposurePercent { get; set; } = 0.50m;    // 50% от баланса в открытых позициях

        private decimal _dailyPnL;
        private DateTime _dailyPnLReset = DateTime.UtcNow.Date;
        private readonly List<decimal> _tradeHistory = new ();

        // === ПУБЛИЧНЫЕ СВОЙСТВА ===
        public decimal BalanceUSDC { get; internal set; }
        public decimal Leverage { get; private set; }
        public int LevelsPerSide { get; private set; }
        public double StepPercent { get; private set; }
        public decimal BuyingPower => BalanceUSDC * Leverage;
        public bool CanTrade { get; private set; }
        public string StatusMessage { get; private set; }

        public decimal DailyPnL
        {
            get
            {
                ResetDailyIfNeeded ();
                return _dailyPnL;
            }
        }

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

        // === ПРОВЕРКА: МОЖНО ЛИ ОТКРЫТЬ НОВУЮ ПОЗИЦИЮ ===
        public (bool Allowed, string Reason) CanOpenPosition(int currentOpenPositions, decimal orderValueUsdc, decimal tradePnL = 0)
        {
            ResetDailyIfNeeded ();

            // 1. Лимит открытых позиций
            if (currentOpenPositions >= MaxOpenOrders)
                return (false, $"Достигнут лимит открытых позиций ({MaxOpenOrders})");

            // 2. Дневной убыток
            decimal potentialLoss = _dailyPnL + tradePnL;
            decimal maxDailyLoss = BalanceUSDC * MaxDailyLossPercent;
            if (potentialLoss < 0 && Math.Abs (potentialLoss) >= maxDailyLoss)
                return (false, $"Дневной убыток {Math.Abs (potentialLoss):F2} USDC >= лимита {maxDailyLoss:F2} USDC ({MaxDailyLossPercent:P0})");

            // 3. Общая экспозиция
            decimal totalExposure = orderValueUsdc; // TODO: суммировать с текущими открытыми позициями
            decimal maxExposure = BalanceUSDC * MaxExposurePercent;
            if (totalExposure > maxExposure)
                return (false, $"Экспозиция {totalExposure:F2} USDC > лимита {maxExposure:F2} USDC ({MaxExposurePercent:P0})");

            return (true, "OK");
        }

        public void RecordTrade(decimal pnlUsdc)
        {
            _tradeHistory.Add (pnlUsdc);
            ResetDailyIfNeeded ();
            _dailyPnL += pnlUsdc;
        }

        private void ResetDailyIfNeeded ()
        {
            DateTime today = DateTime.UtcNow.Date;
            if (_dailyPnLReset < today)
            {
                _dailyPnL = 0;
                _dailyPnLReset = today;
            }
        }

        // === РАСЧЁТ БЕЗОПАСНОЙ КОНФИГУРАЦИИ ===
        private void CalculateSafeConfig()
        {
            if (BalanceUSDC < MIN_BALANCE_TRADE)
            {
                SetNoTradeState ();
                return;
            }

            if (BalanceUSDC < MIN_BALANCE_LEVERAGE)
            {
                Leverage = 1m;
                LevelsPerSide = Math.Min ((int)( BalanceUSDC / MIN_ORDER_SIZE / 2 ), 3);
                StepPercent = 2.0;
                CanTrade = true;
                StatusMessage = $"⚠️ Баланс {BalanceUSDC:F2} USDC < {MIN_BALANCE_LEVERAGE}. Плечо 1x. Уровней: {LevelsPerSide}";
                Log (StatusMessage);
                ValidateGridFit ();
                return;
            }

            Leverage = 5m;
            LevelsPerSide = 10;
            StepPercent = 0.5;
            CanTrade = true;
            StatusMessage = $"✅ Баланс {BalanceUSDC:F2} USDC. Плечо {Leverage}x. Уровней: {LevelsPerSide}, шаг {StepPercent}%";
            Log (StatusMessage);
            ValidateGridFit ();
        }

        private void ValidateGridFit()
        {
            decimal required = LevelsPerSide * 2 * MIN_ORDER_SIZE;
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