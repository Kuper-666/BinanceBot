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

        // Обучение из готовых признаков (история ордеров)
        public async Task RetrainFromFeaturesAsync(List<(decimal FastSma, decimal SlowSma, decimal Rsi, decimal Volume, decimal Volatility, bool IsProfitable)> features, Action<string> logger)
        {
            await Task.Run (() =>
            {
                try
                {
                    logger?.Invoke ($"🤖 Запуск обучения ML на {features.Count} примерах...");
                    var mlContext = new MLContext (seed: 42);
                    var dataWithLabel = features.Select (m => new
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

                    var model = pipeline.Fit (trainData);
                    var predictions = model.Transform (testData);
                    var metrics = mlContext.BinaryClassification.Evaluate (predictions);
                    logger?.Invoke ($"📊 Точность: {metrics.Accuracy:P2}, AUC: {metrics.AreaUnderRocCurve:P2}, F1: {metrics.F1Score:P2}");

                    string tempModelPath = _modelPath + ".tmp";
                    mlContext.Model.Save (model, trainData.Schema, tempModelPath);
                    if (File.Exists (_modelPath)) File.Delete (_modelPath);
                    File.Move (tempModelPath, _modelPath);

                    _mlContext = mlContext;
                    _mlModel = model;
                    _mlModelLoaded = true;
                    logger?.Invoke ("✅ ML модель обновлена из истории ордеров");
                }
                catch (Exception ex)
                {
                    logger?.Invoke ($"❌ Ошибка обучения: {ex.Message}");
                }
            });
        }

        // Прежний метод RetrainAsync (из CSV логов, можно оставить)
        public async Task RetrainAsync(string logsDir, Action<string> logger)
        {
            // ... (код из предыдущих версий, можно оставить как есть, но он не будет использоваться, если используем историю ордеров)
            await Task.Delay (1); // заглушка
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