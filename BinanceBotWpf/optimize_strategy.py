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

def backtest(df, fast, slow, rsi_period, rsi_buy, rsi_sell, stop_loss=0.02, take_profit=0.04):
    df = df.copy()
    df['fast_sma'] = calculate_sma(df['close'], fast)
    df['slow_sma'] = calculate_sma(df['close'], slow)
    df['rsi'] = calculate_rsi(df['close'], rsi_period)
    
    capital = 1000
    position = 0
    entry_price = 0
    
    for i in range(max(fast, slow, rsi_period) + 5, len(df)):
        price = df.iloc[i]['close']
        
        if position == 0:
            # Buy signal
            if df.iloc[i]['fast_sma'] > df.iloc[i]['slow_sma'] and df.iloc[i]['rsi'] < rsi_buy:
                position = capital / price
                capital = 0
                entry_price = price
        else:
            # Sell signal or SL/TP
            pnl_pct = (price - entry_price) / entry_price
            if (df.iloc[i]['fast_sma'] < df.iloc[i]['slow_sma'] or df.iloc[i]['rsi'] > rsi_sell) or pnl_pct >= take_profit or pnl_pct <= -stop_loss:
                capital = position * price
                position = 0
                entry_price = 0
    
    if position > 0:
        capital = position * df.iloc[-1]['close']
    
    total_return = (capital - 1000) / 1000
    return total_return

def optimize():
    df = pd.read_csv(file, names=['open','high','low','close','volume'], skiprows=1)
    df['timestamp'] = pd.date_range(start='2024-01-01', periods=len(df), freq='5min')
    df.set_index('timestamp', inplace=True)
    if not csv_files:
        print("NO_DATA")
        return
    
    best_params = None
    best_return = -float('inf')
    
    # Параметры для перебора
    fast_list = [5, 9, 13, 17]
    slow_list = [13, 21, 34, 50]
    rsi_period_list = [7, 14, 21]
    rsi_buy_list = [25, 30, 35, 40]
    rsi_sell_list = [60, 65, 70, 75]
    
    total_files = len(csv_files)
    
    for fast in fast_list:
        for slow in slow_list:
            if fast >= slow: continue
            for rsi_period in rsi_period_list:
                for rsi_buy in rsi_buy_list:
                    for rsi_sell in rsi_sell_list:
                        if rsi_buy >= rsi_sell: continue
                        total_return = 0
                        valid = 0
                        for file in csv_files:
                            df = pd.read_csv(file, parse_dates=['timestamp'], index_col='timestamp')
                            if len(df) < 100: continue
                            ret = backtest(df, fast, slow, rsi_period, rsi_buy, rsi_sell)
                            if not np.isnan(ret):
                                total_return += ret
                                valid += 1
                        if valid == 0: continue
                        avg_return = total_return / valid
                        if avg_return > best_return:
                            best_return = avg_return
                            best_params = {
                                'FastSma': fast,
                                'SlowSma': slow,
                                'RsiPeriod': rsi_period,
                                'RsiBuyThreshold': rsi_buy,
                                'RsiSellThreshold': rsi_sell,
                                'StopLossPercent': 0.02,
                                'TakeProfitPercent': 0.04,
                                'TrailingStopPercent': 0.02,
                                'MinBalanceForTrading': 20.0,
                                'MaxRiskPercent': 0.25
                            }
                            print(f"New best: {best_params}, return={avg_return:.4f}")
    
    if best_params:
        with open('optimized_params.json', 'w') as f:
            json.dump(best_params, f, indent=4)
        print("SUCCESS")
    else:
        print("FAILED")

if __name__ == '__main__':
    optimize()