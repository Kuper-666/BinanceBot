#!/usr/bin/env python3
"""
SignalValidator ONNX Model Trainer
Обучает XGBoost/GradientBoosting классификатор для валидации торговых сигналов.
Экспортирует в ONNX для загрузки в C# бот через Microsoft.ML.OnnxRuntime.

Зависимости: pip install scikit-learn skl2onnx pandas numpy
Данные: CSV файл с колонками: Price,Rsi,MacdHistogram,BbWidth,AtrPercent,
       VolumeRatio,SmaFast,SmaSlow,SignalDirection,IsProfitable

Пример данных (первые 2 строки файла training_data.csv):
Price,Rsi,MacdHistogram,BbWidth,AtrPercent,VolumeRatio,SmaFast,SmaSlow,SignalDirection,IsProfitable
45000,35.2,0.0012,0.045,0.025,1.3,44800,44500,1,1
"""

import argparse
import os
import sys
import json
import pickle
from datetime import datetime

try:
    import numpy as np
    import pandas as pd
    from sklearn.ensemble import GradientBoostingClassifier
    from sklearn.model_selection import train_test_split
    from sklearn.metrics import accuracy_score, classification_report
    from skl2onnx import convert_sklearn
    from skl2onnx.common.data_types import FloatTensorType
    HAS_DEPS = True
except ImportError as e:
    HAS_DEPS = False
    print(f"Missing dependency: {e}")
    print("Install: pip install scikit-learn skl2onnx pandas numpy")
    sys.exit(1)

FEATURE_COLUMNS = [
    "Price", "Rsi", "MacdHistogram", "BbWidth", "AtrPercent",
    "VolumeRatio", "SmaFast", "SmaSlow", "SignalDirection"
]
LABEL_COLUMN = "IsProfitable"


def load_data(csv_path):
    if not os.path.exists(csv_path):
        print(f"Error: {csv_path} not found.")
        print("Generate training data using DataCollector service in C# bot,")
        print("or create a CSV manually with the required columns.")
        sys.exit(1)

    df = pd.read_csv(csv_path)
    missing = [c for c in FEATURE_COLUMNS + [LABEL_COLUMN] if c not in df.columns]
    if missing:
        print(f"Error: Missing columns in CSV: {missing}")
        sys.exit(1)

    df = df.dropna(subset=FEATURE_COLUMNS + [LABEL_COLUMN])
    print(f"Loaded {len(df)} samples from {csv_path}")
    print(f"Positive: {df[LABEL_COLUMN].sum()}, Negative: {len(df) - df[LABEL_COLUMN].sum()}")
    return df


def train_model(df):
    X = df[FEATURE_COLUMNS].values.astype(np.float32)
    y = df[LABEL_COLUMN].values.astype(np.int32)

    X_train, X_test, y_train, y_test = train_test_split(
        X, y, test_size=0.2, random_state=42, stratify=y if len(set(y)) > 1 else None
    )

    model = GradientBoostingClassifier(
        n_estimators=200,
        max_depth=5,
        learning_rate=0.1,
        min_samples_split=10,
        min_samples_leaf=5,
        random_state=42
    )

    print("Training GradientBoosting model...")
    model.fit(X_train, y_train)

    y_pred = model.predict(X_test)
    accuracy = accuracy_score(y_test, y_pred)
    print(f"Test accuracy: {accuracy:.4f}")
    print()
    print(classification_report(y_test, y_pred, target_names=["Loss", "Profit"]))

    importances = model.feature_importances_
    print("Feature importances:")
    for name, imp in sorted(zip(FEATURE_COLUMNS, importances), key=lambda x: -x[1]):
        print(f"  {name}: {imp:.4f}")

    return model


def export_to_onnx(model, output_path):
    initial_type = [("float_input", FloatTensorType([None, len(FEATURE_COLUMNS)]))]
    options = {id(model): {"zipmap": False}}

    onnx_model = convert_sklearn(model, initial_types=initial_type, options=options)

    with open(output_path, "wb") as f:
        f.write(onnx_model.SerializeToString())

    size_kb = os.path.getsize(output_path) / 1024
    print(f"ONNX model exported to: {output_path} ({size_kb:.1f} KB)")


def export_metadata(model, output_dir):
    meta = {
        "trained_at": datetime.utcnow().isoformat() + "Z",
        "features": FEATURE_COLUMNS,
        "model_type": "GradientBoostingClassifier",
        "n_estimators": model.n_estimators,
        "max_depth": model.max_depth,
        "learning_rate": model.learning_rate,
        "feature_importances": dict(zip(FEATURE_COLUMNS, model.feature_importances_.tolist())),
    }
    meta_path = os.path.join(output_dir, "signal_validator_meta.json")
    with open(meta_path, "w") as f:
        json.dump(meta, f, indent=2)
    print(f"Metadata saved to: {meta_path}")


def main():
    parser = argparse.ArgumentParser(description="Train SignalValidator ONNX model")
    parser.add_argument("--data", type=str, default="training_data.csv", help="Training CSV path")
    parser.add_argument("--output", type=str, default="signal_validator.onnx", help="Output ONNX path")
    parser.add_argument("--output-dir", type=str, default=".", help="Output directory")
    args = parser.parse_args()

    df = load_data(args.data)
    model = train_model(df)

    onnx_path = os.path.join(args.output_dir, args.output)
    export_to_onnx(model, onnx_path)
    export_metadata(model, args.output_dir)

    print("\nDone! Copy the .onnx file to BinanceBotWpf/ML/ directory.")
    print("The C# bot will auto-load it via SignalValidator service.")


if __name__ == "__main__":
    main()
