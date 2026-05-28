using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML;
using Microsoft.ML.Data;
using CsvHelper;

namespace ModelTrainer
{
    // Класс для признаков (соответствует колонкам features.csv)
    public class TradeFeatures
    {
        public float FastSma { get; set; }
        public float SlowSma { get; set; }
        public float Rsi { get; set; }
        public float Volume { get; set; }
        public float Volatility { get; set; }
        public bool IsProfitable { get; set; }   // целевая переменная
    }

    // Класс для предсказания
    public class Prediction
    {
        [ColumnName ("PredictedLabel")]
        public bool IsProfitable { get; set; }

        public float Probability { get; set; }
    }

    class Program
    {
        static void Main(string[] args)
        {
            // Путь к папке с экспортированными данными (там лежат features.csv и trades_*.csv)
            string dataPath = @"C:\Users\Radik\Desktop\уроки по C#\BinanceBotWpf\BinanceBotWpf\bin\Debug\net8.0-windows\Export\";
            // или используйте диалог выбора папки

            Console.WriteLine ("Загрузка и объединение данных...");
            var trades = LoadTrades (dataPath);
            var features = LoadFeatures (dataPath);

            if (trades.Count == 0 || features.Count == 0)
            {
                Console.WriteLine ("Нет данных для обучения! Сначала нажмите 'ЭКСПОРТ ДАННЫХ' в боте.");
                return;
            }

            // Объединяем по времени (ближайшие признаки к моменту закрытия сделки)
            var merged = MergeData (trades, features);
            Console.WriteLine ($"Загружено {merged.Count} сделок.");

            // Преобразуем в список признаков для ML.NET
            var data = merged.Select (m => new TradeFeatures
            {
                FastSma = (float)m.FastSma,
                SlowSma = (float)m.SlowSma,
                Rsi = (float)m.Rsi,
                Volume = (float)m.Volume,
                Volatility = (float)m.Volatility,
                IsProfitable = m.IsProfitable
            }).ToList ();

            // Обучение модели
            var mlContext = new MLContext (seed: 42);
            IDataView dataView = mlContext.Data.LoadFromEnumerable (data);

            // Разделение на тренировочную и тестовую выборки (80/20)
            var split = mlContext.Data.TrainTestSplit (dataView, testFraction: 0.2);
            var trainData = split.TrainSet;
            var testData = split.TestSet;

            // Конвейер обучения: признаки → бинарная классификация (RandomForest)
            var pipeline = mlContext.Transforms.Concatenate ("Features",
                    nameof (TradeFeatures.FastSma),
                    nameof (TradeFeatures.SlowSma),
                    nameof (TradeFeatures.Rsi),
                    nameof (TradeFeatures.Volume),
                    nameof (TradeFeatures.Volatility))
                .Append (mlContext.BinaryClassification.Trainers.FastTree (
                    numberOfTrees: 100,
                    numberOfLeaves: 20,
                    minimumExampleCountPerLeaf: 5));

            Console.WriteLine ("Обучение модели...");
            var model = pipeline.Fit (trainData);

            // Оценка на тестовых данных
            var predictions = model.Transform (testData);
            var metrics = mlContext.BinaryClassification.Evaluate (predictions);
            Console.WriteLine ($"Точность (Accuracy): {metrics.Accuracy:P2}");
            Console.WriteLine ($"AUC: {metrics.AreaUnderRocCurve:P2}");
            Console.WriteLine ($"F1 Score: {metrics.F1Score:P2}");

            // Сохраняем модель
            string modelPath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "trading_model.zip");
            mlContext.Model.Save (model, trainData.Schema, modelPath);
            Console.WriteLine ($"Модель сохранена: {modelPath}");
        }

        // Загрузка сделок из CSV (только закрытые, SELL_CLOSE)
        static List<(DateTime CloseTime, string Symbol, bool IsProfitable)> LoadTrades(string path)
        {
            var result = new List<(DateTime, string, bool)> ();
            var files = Directory.GetFiles (path, "trades_*.csv");
            if (files.Length == 0) return result;

            using var reader = new StreamReader (files[0]);
            using var csv = new CsvReader (reader, System.Globalization.CultureInfo.InvariantCulture);
            var records = csv.GetRecords<dynamic> ();
            foreach (var r in records)
            {
                string action = r.Action;
                if (action != "SELL_CLOSE") continue;
                DateTime dt = DateTime.Parse (r.DateTime);
                string symbol = r.Symbol;
                decimal pnl = decimal.Parse (r.PnL, System.Globalization.CultureInfo.InvariantCulture);
                result.Add ((dt, symbol, pnl > 0));
            }
            return result;
        }

        // Загрузка признаков из features.csv
        static List<(DateTime Timestamp, string Symbol, decimal FastSma, decimal SlowSma, decimal Rsi, decimal Volume, decimal Volatility)> LoadFeatures(string path)
        {
            var result = new List<(DateTime, string, decimal, decimal, decimal, decimal, decimal)> ();
            var file = Path.Combine (path, "features.csv");
            if (!File.Exists (file)) return result;

            using var reader = new StreamReader (file);
            using var csv = new CsvReader (reader, System.Globalization.CultureInfo.InvariantCulture);
            var records = csv.GetRecords<dynamic> ();
            foreach (var r in records)
            {
                DateTime ts = DateTime.Parse (r.Timestamp);
                string symbol = r.Symbol;
                decimal fast = decimal.Parse (r.FastSma, System.Globalization.CultureInfo.InvariantCulture);
                decimal slow = decimal.Parse (r.SlowSma, System.Globalization.CultureInfo.InvariantCulture);
                decimal rsi = decimal.Parse (r.Rsi, System.Globalization.CultureInfo.InvariantCulture);
                decimal vol = decimal.Parse (r.Volume, System.Globalization.CultureInfo.InvariantCulture);
                decimal volt = decimal.Parse (r.Volatility, System.Globalization.CultureInfo.InvariantCulture);
                result.Add ((ts, symbol, fast, slow, rsi, vol, volt));
            }
            return result;
        }

        // Объединение сделок с ближайшими признаками (по времени до закрытия)
        static List<(decimal FastSma, decimal SlowSma, decimal Rsi, decimal Volume, decimal Volatility, bool IsProfitable)> MergeData(
            List<(DateTime CloseTime, string Symbol, bool IsProfitable)> trades,
            List<(DateTime Timestamp, string Symbol, decimal FastSma, decimal SlowSma, decimal Rsi, decimal Volume, decimal Volatility)> features)
        {
            var result = new List<(decimal, decimal, decimal, decimal, decimal, bool)> ();
            foreach (var t in trades)
            {
                // Ищем ближайший признак по времени (до закрытия сделки)
                var closest = features
                    .Where (f => f.Symbol == t.Symbol && f.Timestamp <= t.CloseTime)
                    .OrderByDescending (f => f.Timestamp)
                    .FirstOrDefault ();
                if (closest.Timestamp != DateTime.MinValue)
                {
                    result.Add ((
                        closest.FastSma,
                        closest.SlowSma,
                        closest.Rsi,
                        closest.Volume,
                        closest.Volatility,
                        t.IsProfitable
                    ));
                }
            }
            return result;
        }
    }
}