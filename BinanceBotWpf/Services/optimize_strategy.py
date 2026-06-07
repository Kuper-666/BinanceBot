import pandas as pd
import numpy as np
import glob
import json
import os
from datetime import datetime

def calculate_sma(close, period):
    return close.rolling(window=period).mean()

def calculate_rsi(close, period=14):
    delta = close.diff()
    gain = delta.clip(lower=0).rolling(window=period).mean()
    loss = (-delta).clip(lower=0).rolling(window=period).mean()
    rs = gain / loss
    rsi = 100 - (100 / (1 + rs))
    return rsi

def backtest(df, fast_sma, slow_sma, rsi_period, rsi_oversold, rsi_overbought):
    df = df.copy()
    df['fast_sma'] = calculate_sma(df['close'], fast_sma)
    df['slow_sma'] = calculate_sma(df['close'], slow_sma)
    df['rsi'] = calculate_rsi(df['close'], rsi_period)
    df['signal'] = 0
    # Buy: fast > slow and RSI < oversold
    df.loc[(df['fast_sma'] > df['slow_sma']) & (df['rsi'] < rsi_oversold), 'signal'] = 1
    # Sell: fast < slow or RSI > overbought
    df.loc[(df['fast_sma'] < df['slow_sma']) | (df['rsi'] > rsi_overbought), 'signal'] = -1
    # Simulate positions (simple, no slippage)
    df['position'] = df['signal'].shift(1).fillna(0)
    df['returns'] = df['close'].pct_change() * df['position']
    total_return = (df['returns'] + 1).prod() - 1
    # Sharpe ratio (annualized)
    sharpe = np.sqrt(252*24*12) * df['returns'].mean() / df['returns'].std() if df['returns'].std() > 0 else 0
    return total_return, sharpe

def optimize_all():
    csv_files = glob.glob('Export/Klines/*_5m.csv')
    if not csv_files:
        print("Нет CSV-файлов. Сначала экспортируйте данные из бота (команда /export_klines).")
        return

    best_params = None
    best_score = -float('inf')
    # Грубый перебор (можно расширить)
    for fast in [5, 9, 13]:
        for slow in [13, 21, 34]:
            if fast >= slow: continue
            for rsi_period in [7, 14, 21]:
                for oversold in [25, 30, 35]:
                    for overbought in [65, 70, 75]:
                        total_return = 0
                        total_sharpe = 0
                        count = 0
                        for file in csv_files:
                            df = pd.read_csv(file, parse_dates=['timestamp'], index_col='timestamp')
                            ret, sharpe = backtest(df, fast, slow, rsi_period, oversold, overbought)
                            if not np.isnan(ret):
                                total_return += ret
                                total_sharpe += sharpe
                                count += 1
                        if count == 0: continue
                        avg_return = total_return / count
                        avg_sharpe = total_sharpe / count
                        # Целевая функция: максимизируем return при ограничении Sharpe > 0.5
                        score = avg_return * (avg_sharpe + 0.5)
                        if score > best_score:
                            best_score = score
                            best_params = {
                                'FastSma': fast,
                                'SlowSma': slow,
                                'RsiPeriod': rsi_period,
                                'RsiBuyThreshold': oversold,
                                'RsiSellThreshold': overbought
                            }
                            print(f"New best: {best_params}, score={score:.4f}")

    if best_params:
        # Добавляем также стандартные параметры SL/TP и риск (можно оставить как есть)
        full_settings = {
            'FastSma': best_params['FastSma'],
            'SlowSma': best_params['SlowSma'],
            'RsiBuyThreshold': best_params['RsiBuyThreshold'],
            'RsiSellThreshold': best_params['RsiSellThreshold'],
            'RsiPeriod': best_params['RsiPeriod'],  # дополнительно
            'StopLossPercent': 0.02,
            'TakeProfitPercent': 0.04,
            'TrailingStopPercent': 0.02,
            'MinBalanceForTrading': 20.0,
            'MaxRiskPercent': 0.25
        }
        with open('Data/strategy_settings.json', 'w') as f:
            json.dump(full_settings, f, indent=4)
        print("✅ Оптимальные параметры сохранены в Data/strategy_settings.json")
    else:
        print("Не удалось найти оптимальные параметры.")

if __name__ == '__main__':
    optimize_all()