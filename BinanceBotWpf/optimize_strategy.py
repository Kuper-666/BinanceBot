import pandas as pd
import numpy as np
import glob
import json
import os
import sys
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

def calculate_bollinger_bands(close, period=20, k=2):
    middle = close.rolling(window=period).mean()
    std = close.rolling(window=period).std()
    upper = middle + k * std
    lower = middle - k * std
    return upper, middle, lower

def calculate_macd(close, fast=12, slow=26, signal=9):
    ema_fast = close.ewm(span=fast, adjust=False).mean()
    ema_slow = close.ewm(span=slow, adjust=False).mean()
    macd_line = ema_fast - ema_slow
    signal_line = macd_line.ewm(span=signal, adjust=False).mean()
    histogram = macd_line - signal_line
    return macd_line, signal_line, histogram

def calculate_atr(high, low, close, period=14):
    tr1 = high - low
    tr2 = abs(high - close.shift())
    tr3 = abs(low - close.shift())
    tr = pd.concat([tr1, tr2, tr3], axis=1).max(axis=1)
    atr = tr.rolling(window=period).mean()
    return atr

def backtest(df, params):
    """
    Бэктест улучшенной стратегии:
    - покупка: RSI < rsi_buy, цена <= нижняя BB, MACD гистограмма растёт, объём > 1.2*среднего
    - продажа: RSI > rsi_sell, цена >= верхняя BB, MACD гистограмма падает, объём > 1.2*среднего
    - стоп-лосс / тейк-профит в процентах
    """
    df = df.copy()
    period = max(params['bb_period'], params['macd_fast'], params['macd_slow']) + 5
    if len(df) < period:
        return np.nan

    # Индикаторы
    df['rsi'] = calculate_rsi(df['close'], params['rsi_period'])
    upper_bb, _, lower_bb = calculate_bollinger_bands(df['close'], params['bb_period'], params['bb_k'])
    df['bb_upper'] = upper_bb
    df['bb_lower'] = lower_bb
    _, _, df['macd_hist'] = calculate_macd(df['close'], params['macd_fast'], params['macd_slow'], params['macd_signal'])
    df['avg_volume'] = df['volume'].rolling(window=20).mean()
    df['high_volume'] = df['volume'] > df['avg_volume'] * params['volume_threshold']

    # Сигналы
    df['buy_signal'] = (
        (df['rsi'] < params['rsi_buy']) &
        (df['close'] <= df['bb_lower']) &
        (df['macd_hist'] > df['macd_hist'].shift(1)) &
        (df['high_volume'])
    )
    df['sell_signal'] = (
        (df['rsi'] > params['rsi_sell']) &
        (df['close'] >= df['bb_upper']) &
        (df['macd_hist'] < df['macd_hist'].shift(1)) &
        (df['high_volume'])
    )

    capital = 1000.0  # начальный капитал
    position = 0.0
    entry_price = 0.0
    trades = 0
    wins = 0

    for i in range(period, len(df)):
        price = df.iloc[i]['close']
        # Проверка стоп-лосса / тейк-профита (если есть позиция)
        if position > 0:
            pnl_pct = (price - entry_price) / entry_price
            if pnl_pct <= -params['stop_loss'] or pnl_pct >= params['take_profit']:
                capital = position * price
                position = 0.0
                trades += 1
                if pnl_pct > 0: wins += 1
                continue

        if position == 0 and df.iloc[i]['buy_signal']:
            position = capital / price
            capital = 0.0
            entry_price = price
        elif position > 0 and df.iloc[i]['sell_signal']:
            capital = position * price
            position = 0.0
            trades += 1
            pnl_pct = (price - entry_price) / entry_price
            if pnl_pct > 0: wins += 1

    if position > 0:
        capital = position * df.iloc[-1]['close']
        trades += 1
        pnl_pct = (df.iloc[-1]['close'] - entry_price) / entry_price
        if pnl_pct > 0: wins += 1

    if trades == 0:
        return np.nan

    total_return = (capital - 1000) / 1000
    win_rate = wins / trades if trades > 0 else 0
    # Комбинированная метрика: доходность + доля прибыльных (чем выше, тем лучше)
    score = total_return + win_rate * 0.5
    return score, total_return, win_rate, trades

def load_csv_files(folder_path="Export/Klines"):
    """Загружает все CSV-файлы из папки и возвращает список DataFrame"""
    csv_files = glob.glob(os.path.join(folder_path, "*.csv"))
    if not csv_files:
        print(f"❌ Нет CSV-файлов в папке: {folder_path}")
        return []
    data_frames = []
    for file in csv_files:
        try:
            # Формат бота: timestamp,open,high,low,close,volume
            df = pd.read_csv(file, parse_dates=['timestamp'], index_col='timestamp')
            # Убедимся, что нужные колонки есть
            required = ['open', 'high', 'low', 'close', 'volume']
            if not all(col in df.columns for col in required):
                print(f"⚠️ Пропущен {file}: неверный формат")
                continue
            df = df[required]
            df = df.astype(float)
            if len(df) >= 100:
                data_frames.append(df)
                print(f"✅ Загружен {os.path.basename(file)}: {len(df)} свечей")
            else:
                print(f"⚠️ {os.path.basename(file)}: слишком мало данных ({len(df)} < 100)")
        except Exception as e:
            print(f"❌ Ошибка загрузки {file}: {e}")
    return data_frames

def optimize(data_frames, params_grid):
    best_score = -float('inf')
    best_params = None
    best_metrics = {}

    total_combinations = 1
    for grid in params_grid.values():
        total_combinations *= len(grid)

    print(f"🔄 Перебор {total_combinations} комбинаций параметров на {len(data_frames)} файлах...")
    current = 0

    for rsi_period in params_grid['rsi_period']:
        for rsi_buy in params_grid['rsi_buy']:
            for rsi_sell in params_grid['rsi_sell']:
                if rsi_buy >= rsi_sell: continue
                for bb_period in params_grid['bb_period']:
                    for bb_k in params_grid['bb_k']:
                        for macd_fast in params_grid['macd_fast']:
                            for macd_slow in params_grid['macd_slow']:
                                if macd_fast >= macd_slow: continue
                                for macd_signal in params_grid['macd_signal']:
                                    for volume_threshold in params_grid['volume_threshold']:
                                        for stop_loss in params_grid['stop_loss']:
                                            for take_profit in params_grid['take_profit']:
                                                current += 1
                                                if current % 100 == 0:
                                                    print(f"   Прогресс: {current}/{total_combinations}")

                                                params = {
                                                    'rsi_period': rsi_period,
                                                    'rsi_buy': rsi_buy,
                                                    'rsi_sell': rsi_sell,
                                                    'bb_period': bb_period,
                                                    'bb_k': bb_k,
                                                    'macd_fast': macd_fast,
                                                    'macd_slow': macd_slow,
                                                    'macd_signal': macd_signal,
                                                    'volume_threshold': volume_threshold,
                                                    'stop_loss': stop_loss,
                                                    'take_profit': take_profit
                                                }

                                                total_score = 0.0
                                                total_return = 0.0
                                                total_win_rate = 0.0
                                                total_trades = 0
                                                valid_files = 0

                                                for df in data_frames:
                                                    result = backtest(df, params)
                                                    if not np.isnan(result):
                                                        score, ret, wr, trades = result
                                                        total_score += score
                                                        total_return += ret
                                                        total_win_rate += wr
                                                        total_trades += trades
                                                        valid_files += 1

                                                if valid_files == 0:
                                                    continue

                                                avg_score = total_score / valid_files
                                                avg_return = total_return / valid_files
                                                avg_win_rate = total_win_rate / valid_files
                                                avg_trades = total_trades / valid_files

                                                if avg_score > best_score:
                                                    best_score = avg_score
                                                    best_params = params
                                                    best_metrics = {
                                                        'avg_score': avg_score,
                                                        'avg_return': avg_return,
                                                        'avg_win_rate': avg_win_rate,
                                                        'avg_trades': avg_trades,
                                                        'valid_files': valid_files
                                                    }
                                                    print(f"\n✨ Новый лучший результат!")
                                                    print(f"   Параметры: {best_params}")
                                                    print(f"   Средняя доходность: {avg_return:.2%}, Win Rate: {avg_win_rate:.1%}, Сделок: {avg_trades:.1f}")
                                                    print(f"   Комбинированный скор: {best_score:.4f}\n")

    return best_params, best_metrics

def main():
    # Папка с CSV-файлами (можно передать аргументом командной строки)
    if len(sys.argv) > 1:
        folder = sys.argv[1]
    else:
        folder = os.path.join(os.getcwd(), "Export", "Klines")

    print(f"📂 Поиск CSV-файлов в: {folder}")
    data_frames = load_csv_files(folder)
    if len(data_frames) < 2:
        print("❌ Недостаточно данных для оптимизации (нужно минимум 2 файла).")
        return

    # Сетка параметров для перебора
    params_grid = {
        'rsi_period': [7, 14, 21],
        'rsi_buy': [25, 30, 35],
        'rsi_sell': [65, 70, 75],
        'bb_period': [20],
        'bb_k': [2.0, 2.5],
        'macd_fast': [8, 12],
        'macd_slow': [24, 26],
        'macd_signal': [9],
        'volume_threshold': [1.2, 1.5],
        'stop_loss': [0.03, 0.05, 0.07],
        'take_profit': [0.06, 0.10, 0.15]
    }

    best_params, metrics = optimize(data_frames, params_grid)

    if best_params:
        # Формируем выходной JSON в формате, совместимом с ботом
        result = {
            'FastSma': best_params['bb_period'],  # используем BB период как быструю SMA для совместимости (не критично)
            'SlowSma': best_params['bb_period'] * 2,
            'RsiBuyThreshold': best_params['rsi_buy'],
            'RsiSellThreshold': best_params['rsi_sell'],
            'StopLossPercent': best_params['stop_loss'],
            'TakeProfitPercent': best_params['take_profit'],
            'TrailingStopPercent': 0.02,
            'MinBalanceForTrading': 20,
            'MaxRiskPercent': 0.25,
            # Дополнительные параметры для расширенной стратегии (бот пока не использует, но можно добавить)
            'BbPeriod': best_params['bb_period'],
            'BbK': best_params['bb_k'],
            'MacdFast': best_params['macd_fast'],
            'MacdSlow': best_params['macd_slow'],
            'MacdSignal': best_params['macd_signal'],
            'VolumeThreshold': best_params['volume_threshold']
        }

        with open('optimized_params.json', 'w') as f:
            json.dump(result, f, indent=4)

        print("\n✅ Оптимизация завершена!")
        print(f"📁 Результат сохранён в optimized_params.json")
        print(f"📊 Метрики: {metrics}")
    else:
        print("❌ Не удалось найти оптимальные параметры.")

if __name__ == '__main__':
    main()