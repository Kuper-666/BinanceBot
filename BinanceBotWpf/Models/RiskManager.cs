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
        public decimal MaxExposurePerSymbolPercent { get; set; } = 0.20m; // 20% на одну пару
        public decimal MaxWeeklyLossPercent { get; set; } = 0.20m;  // 20% недельный лимит

        private decimal _dailyPnL;
        private decimal _weeklyPnL;
        private DateTime _dailyPnLReset = DateTime.UtcNow.Date;
        private DateTime _weeklyPnLReset = DateTime.UtcNow.Date;
        private readonly List<decimal> _tradeHistory = new ();

        // === ПУБЛИЧНЫЕ СВОЙСТВА ===
        public decimal BalanceUSDC { get; set; }
        public decimal Leverage { get; private set; }
        public int LevelsPerSide { get; private set; }
        public double StepPercent { get; private set; }
        public decimal BuyingPower => BalanceUSDC * Leverage;
        public bool CanTrade { get; private set; }
        public string StatusMessage { get; private set; }

        /// <summary>
        /// Kill-switch: true если достигнут дневной или недельный лимит убытка.
        /// Бот должен полностью остановить торговлю и закрыть позиции.
        /// </summary>
        public bool IsKillSwitchActive => IsDailyLossKillSwitchTriggered () || IsWeeklyLossTriggered ();

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
        public (bool Allowed, string Reason) CanOpenPosition(int currentOpenPositions, decimal orderValueUsdc, decimal currentTotalExposure = 0, decimal tradePnL = 0, decimal currentSymbolExposure = 0)
        {
            ResetDailyIfNeeded ();
            ResetWeeklyIfNeeded ();

            // 0. Kill-switch: полная остановка при дневной/недельной просадке
            if (IsKillSwitchActive)
                return (false, $"🚨 KILL-SWITCH: дневной убыток {Math.Abs (_dailyPnL):F2} USDC ({_dailyPnL / BalanceUSDC:P0}) или недельный {_weeklyPnL:F2} USDC ({_weeklyPnL / BalanceUSDC:P0})");

            // 1. Лимит открытых позиций
            if (currentOpenPositions >= MaxOpenOrders)
                return (false, $"Достигнут лимит открытых позиций ({MaxOpenOrders})");

            // 2. Дневной убыток
            decimal potentialLoss = _dailyPnL + tradePnL;
            decimal maxDailyLoss = BalanceUSDC * MaxDailyLossPercent;
            if (potentialLoss < 0 && Math.Abs (potentialLoss) >= maxDailyLoss)
                return (false, $"Дневной убыток {Math.Abs (potentialLoss):F2} USDC >= лимита {maxDailyLoss:F2} USDC ({MaxDailyLossPercent:P0})");

            // 3. Недельный убыток
            decimal weeklyPotentialLoss = _weeklyPnL + tradePnL;
            decimal maxWeeklyLoss = BalanceUSDC * MaxWeeklyLossPercent;
            if (weeklyPotentialLoss < 0 && Math.Abs (weeklyPotentialLoss) >= maxWeeklyLoss)
                return (false, $"Недельный убыток {Math.Abs (weeklyPotentialLoss):F2} USDC >= лимита {maxWeeklyLoss:F2} USDC ({MaxWeeklyLossPercent:P0})");

            // 4. Общая экспозиция (сумма текущих открытых позиций + новый ордер)
            decimal totalExposure = currentTotalExposure + orderValueUsdc;
            decimal maxExposure = BalanceUSDC * MaxExposurePercent;
            if (totalExposure > maxExposure)
                return (false, $"Экспозиция {totalExposure:F2} USDC > лимита {maxExposure:F2} USDC ({MaxExposurePercent:P0})");

            // 5. Экспозиция на одну пару
            decimal symbolExposure = currentSymbolExposure + orderValueUsdc;
            decimal maxSymbolExposure = BalanceUSDC * MaxExposurePerSymbolPercent;
            if (symbolExposure > maxSymbolExposure)
                return (false, $"Экспозиция на пару {symbolExposure:F2} USDC > лимита {maxSymbolExposure:F2} USDC ({MaxExposurePerSymbolPercent:P0})");

            return (true, "OK");
        }

        public void RecordTrade(decimal pnlUsdc)
        {
            _tradeHistory.Add (pnlUsdc);
            ResetDailyIfNeeded ();
            ResetWeeklyIfNeeded ();
            _dailyPnL += pnlUsdc;
            _weeklyPnL += pnlUsdc;
        }

        public bool IsDailyLossKillSwitchTriggered ()
        {
            ResetDailyIfNeeded ();
            decimal maxDailyLoss = BalanceUSDC * MaxDailyLossPercent;
            return _dailyPnL < 0 && Math.Abs (_dailyPnL) >= maxDailyLoss;
        }

        private bool IsWeeklyLossTriggered ()
        {
            ResetWeeklyIfNeeded ();
            decimal maxWeeklyLoss = BalanceUSDC * MaxWeeklyLossPercent;
            return _weeklyPnL < 0 && Math.Abs (_weeklyPnL) >= maxWeeklyLoss;
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

        private void ResetWeeklyIfNeeded ()
        {
            DateTime now = DateTime.UtcNow;
            // Неделя начинается в понедельник
            DateTime weekStart = now.Date.AddDays (-(int)now.DayOfWeek + 1);
            if (_weeklyPnLReset < weekStart)
            {
                _weeklyPnL = 0;
                _weeklyPnLReset = weekStart;
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