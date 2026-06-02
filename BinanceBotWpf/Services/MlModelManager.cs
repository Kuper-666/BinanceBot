using Microsoft.ML;
using Microsoft.ML.Data;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CsvHelper;
using System.Globalization;
using System.Collections.Generic;

namespace BinanceBotWpf.Services
{
    public class MlModelManager
    {
        private readonly string _modelPath;
        private MLContext _mlContext;
        private ITransformer _mlModel;
        private bool _mlModelLoaded = false;
        private readonly Action<string> _logger;

        public bool IsLoaded => _mlModelLoaded;

        public MlModelManager(string modelPath, Action<string> logger)
        {
            _modelPath = modelPath;
            _logger = logger;
            LoadModel ();
        }

        private void LoadModel()
        {
            if (!File.Exists (_modelPath))
            {
                _logger?.Invoke ("⚠️ ML модель не найдена, фильтрация отключена.");
                return;
            }
            try
            {
                _mlContext = new MLContext ();
                _mlModel = _mlContext.Model.Load (_modelPath, out _);
                _mlModelLoaded = true;
                _logger?.Invoke ("✅ ML модель загружена");
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Ошибка загрузки ML: {ex.Message}");
            }
        }

        public bool IsProfitable(decimal fastSma, decimal slowSma, decimal rsi, decimal volume, decimal volatility)
        {
            if (!_mlModelLoaded) return true;
            try
            {
                var input = new ModelInput
                {
                    FastSma = (float)fastSma,
                    SlowSma = (float)slowSma,
                    Rsi = (float)rsi,
                    Volume = (float)volume,
                    Volatility = (float)volatility
                };
                var predEngine = _mlContext.Model.CreatePredictionEngine<ModelInput, ModelOutput> (_mlModel);
                var result = predEngine.Predict (input);
                return result.IsProfitable && result.Probability > 0.6f;
            }
            catch { return true; }
        }

        public async Task RetrainAsync(string logsDir, Action<string> progressLogger)
        {
            try
            {
                progressLogger?.Invoke ("🤖 Запуск переобучения ML модели...");

                var trades = new List<(DateTime CloseTime, string Symbol, bool IsProfitable)> ();
                var tradeFiles = Directory.GetFiles (logsDir, "trades_*.csv");
                foreach (var file in tradeFiles)
                {
                    using var reader = new StreamReader (file);
                    using var csv = new CsvReader (reader, CultureInfo.InvariantCulture);
                    var records = csv.GetRecords<dynamic> ();
                    foreach (var r in records)
                    {
                        string action = r.Action;
                        if (action != "SELL_CLOSE") continue;
                        DateTime dt = DateTime.Parse (r.CloseTime);
                        string symbol = r.Symbol;
                        decimal pnl = decimal.Parse (r.PnL, CultureInfo.InvariantCulture);
                        trades.Add ((dt, symbol, pnl > 0));
                    }
                }

                string featuresPath = Path.Combine (logsDir, "features.csv");
                if (!File.Exists (featuresPath))
                {
                    progressLogger?.Invoke ("❌ Файл features.csv не найден.");
                    return;
                }

                var features = new List<(DateTime Timestamp, string Symbol, decimal FastSma, decimal SlowSma, decimal Rsi, decimal Volume, decimal Volatility)> ();
                using (var reader = new StreamReader (featuresPath))
                using (var csv = new CsvReader (reader, CultureInfo.InvariantCulture))
                {
                    var records = csv.GetRecords<dynamic> ();
                    foreach (var r in records)
                    {
                        DateTime ts = DateTime.Parse (r.Timestamp);
                        string symbol = r.Symbol;
                        decimal fast = decimal.Parse (r.FastSma, CultureInfo.InvariantCulture);
                        decimal slow = decimal.Parse (r.SlowSma, CultureInfo.InvariantCulture);
                        decimal rsi = decimal.Parse (r.Rsi, CultureInfo.InvariantCulture);
                        decimal vol = decimal.Parse (r.Volume, CultureInfo.InvariantCulture);
                        decimal volt = decimal.Parse (r.Volatility, CultureInfo.InvariantCulture);
                        features.Add ((ts, symbol, fast, slow, rsi, vol, volt));
                    }
                }

                if (trades.Count < 30)
                {
                    progressLogger?.Invoke ($"⚠️ Недостаточно сделок: {trades.Count} (нужно 30)");
                    return;
                }

                var merged = new List<(decimal FastSma, decimal SlowSma, decimal Rsi, decimal Volume, decimal Volatility, bool IsProfitable)> ();
                foreach (var t in trades)
                {
                    var closest = features
                        .Where (f => f.Symbol == t.Symbol && f.Timestamp <= t.CloseTime)
                        .OrderByDescending (f => f.Timestamp)
                        .FirstOrDefault ();
                    if (closest.Timestamp != DateTime.MinValue)
                    {
                        merged.Add ((closest.FastSma, closest.SlowSma, closest.Rsi, closest.Volume, closest.Volatility, t.IsProfitable));
                    }
                }

                if (merged.Count < 20)
                {
                    progressLogger?.Invoke ($"⚠️ Недостаточно объединённых записей: {merged.Count} (нужно 20)");
                    return;
                }

                var mlContext = new MLContext (seed: 42);
                var dataWithLabel = merged.Select (m => new
                {
                    FastSma = (float)m.FastSma,
                    SlowSma = (float)m.SlowSma,
                    Rsi = (float)m.Rsi,
                    Volume = (float)m.Volume,
                    Volatility = (float)m.Volatility,
                    Label = m.IsProfitable
                }).ToList ();

                var dataView = mlContext.Data.LoadFromEnumerable (dataWithLabel);
                var split = mlContext.Data.TrainTestSplit (dataView, testFraction: 0.2);
                var trainData = split.TrainSet;
                var testData = split.TestSet;

                var pipeline = mlContext.Transforms.Concatenate ("Features",
                        "FastSma", "SlowSma", "Rsi", "Volume", "Volatility")
                    .Append (mlContext.BinaryClassification.Trainers.FastTree (
                        numberOfTrees: 100,
                        numberOfLeaves: 20,
                        minimumExampleCountPerLeaf: 5));

                progressLogger?.Invoke ($"🔄 Обучение на {merged.Count} примерах...");
                var model = pipeline.Fit (trainData);

                var predictions = model.Transform (testData);
                var metrics = mlContext.BinaryClassification.Evaluate (predictions);
                progressLogger?.Invoke ($"📊 Точность: {metrics.Accuracy:P2}, AUC: {metrics.AreaUnderRocCurve:P2}, F1: {metrics.F1Score:P2}");

                string tempModelPath = _modelPath + ".tmp";
                mlContext.Model.Save (model, trainData.Schema, tempModelPath);
                if (File.Exists (_modelPath)) File.Delete (_modelPath);
                File.Move (tempModelPath, _modelPath);

                _mlContext = mlContext;
                _mlModel = model;
                _mlModelLoaded = true;
                progressLogger?.Invoke ("✅ ML модель переобучена и загружена!");
            }
            catch (Exception ex)
            {
                progressLogger?.Invoke ($"❌ Ошибка переобучения: {ex.Message}");
            }
        }
    }

    public class ModelInput
    {
        public float FastSma { get; set; }
        public float SlowSma { get; set; }
        public float Rsi { get; set; }
        public float Volume { get; set; }
        public float Volatility { get; set; }
    }

    public class ModelOutput
    {
        [ColumnName ("PredictedLabel")]
        public bool IsProfitable { get; set; }
        public float Probability { get; set; }
    }
}