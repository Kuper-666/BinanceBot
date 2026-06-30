#nullable enable
using BinanceBotWpf.Models;
using Microsoft.ML;
using Microsoft.ML.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public class MlModelManager : IMlModelManager
    {
        private readonly string _modelPath;
        private MLContext? _mlContext;
        private ITransformer? _mlModel;
        private bool _mlModelLoaded = false;
        private readonly Action<string> _logger;

        public bool IsLoaded => _mlModelLoaded;

        public MlModelManager(string modelPath, Action<string> logger)
        {
            _modelPath = modelPath;
            _logger = logger;
            LoadModel ();
        }

        // ✅ Ищем в нескольких местах
        private static readonly string[] _candidatePaths = new[]
        {
    "trading_model.zip",
    "ML/trading_model.zip",
    "Models/trading_model.zip",
};

        private void LoadModel()
        {
            string? foundPath = null;
            if (File.Exists (_modelPath))
                foundPath = _modelPath;
            else
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                foreach (var candidate in _candidatePaths)
                {
                    string full = Path.Combine (baseDir, candidate);
                    if (File.Exists (full)) { foundPath = full; break; }
                }
            }

            if (foundPath == null)
            {
                _logger?.Invoke ("⚠️ ML модель не найдена, фильтрация отключена.");
                _logger?.Invoke ("ℹ️ Нажмите «Переобучить ML» или /retrain в Telegram.");
                return;
            }

            try
            {
                _mlContext = new MLContext ();
                _mlModel = _mlContext.Model.Load (foundPath, out _);
                _mlModelLoaded = true;
                _logger?.Invoke ($"✅ ML модель загружена из: {foundPath}");
            }
            catch (Exception ex) { _logger?.Invoke ($"❌ Ошибка загрузки ML: {ex.Message}"); }
        }

        public (bool IsProfitable, float Probability, string RiskLevel) PredictRisk(decimal fastSma, decimal slowSma, decimal rsi, decimal volumeRatio, decimal atr, decimal macdHist, decimal bbWidth, decimal obv, float marketCapRank = -1f, float sentimentScore = 0f, float galaxyScore = 0f)
        {
            if (!_mlModelLoaded) return (true, 1.0f, "Low Risk");
            try
            {
                var input = new ModelInput
                {
                    FastSma = (float)fastSma,
                    SlowSma = (float)slowSma,
                    Rsi = (float)rsi,
                    VolumeRatio = (float)volumeRatio,
                    Atr = (float)atr,
                    MacdHistogram = (float)macdHist,
                    BbWidth = (float)bbWidth,
                    Obv = (float)obv,
                    MarketCapRank = marketCapRank,
                    SentimentScore = sentimentScore,
                    GalaxyScore = galaxyScore
                };
                if (_mlContext == null || _mlModel == null) return (true, 1.0f, "Low Risk");

                var predEngine = _mlContext.Model.CreatePredictionEngine<ModelInput, ModelOutput> (_mlModel);
                var result = predEngine.Predict (input);
                
                string riskLevel = "High Risk";
                if (result.Probability >= 0.75f) riskLevel = "Low Risk";
                else if (result.Probability >= 0.60f) riskLevel = "Medium Risk";

                return (result.IsProfitable && result.Probability > 0.6f, result.Probability, riskLevel);
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Ошибка ML предсказания: {ex.Message}");
                return (false, 0.5f, "Unknown Risk");
            }
        }

        public async Task RetrainFromFeaturesAsync(List<(decimal FastSma, decimal SlowSma, decimal Rsi, decimal VolumeRatio, decimal Atr, decimal MacdHistogram, decimal BbWidth, decimal Obv, float MarketCapRank, float SentimentScore, float GalaxyScore, bool IsProfitable)> features, Action<string> logger)
        {
            await Task.Run (() =>
            {
                try
                {
                    if (!features.Any (f => f.IsProfitable) || !features.Any (f => !f.IsProfitable))
                    {
                        logger?.Invoke ("⚠️ Нет одновременно прибыльных и убыточных сделок. Обучение отложено.");
                        return;
                    }
                    if (features.Count < 10) { logger?.Invoke ($"⚠️ Мало примеров: {features.Count}"); return; }
                    logger?.Invoke ($"🤖 Обучение ML на {features.Count} примерах...");

                    var mlContext = new MLContext (seed: 42);
                    var dataWithLabel = features.Select (m => new
                    {
                        FastSma = (float)m.FastSma,
                        SlowSma = (float)m.SlowSma,
                        Rsi = (float)m.Rsi,
                        VolumeRatio = (float)m.VolumeRatio,
                        Atr = (float)m.Atr,
                        MacdHistogram = (float)m.MacdHistogram,
                        BbWidth = (float)m.BbWidth,
                        Obv = (float)m.Obv,
                        MarketCapRank = m.MarketCapRank,
                        SentimentScore = m.SentimentScore,
                        GalaxyScore = m.GalaxyScore,
                        Label = m.IsProfitable
                    }).ToList ();
                    var dataView = mlContext.Data.LoadFromEnumerable (dataWithLabel);
                    var split = mlContext.Data.TrainTestSplit (dataView, testFraction: 0.2);
                    var pipeline = mlContext.Transforms.Concatenate ("Features",
                            "FastSma", "SlowSma", "Rsi", "VolumeRatio", "Atr", "MacdHistogram", "BbWidth", "Obv",
                            "MarketCapRank", "SentimentScore", "GalaxyScore")
                        .Append (mlContext.BinaryClassification.Trainers.FastTree (
                            numberOfTrees: 100,
                            numberOfLeaves: 20,
                            minimumExampleCountPerLeaf: 5));
                    var model = pipeline.Fit (split.TrainSet);
                    var predictions = model.Transform (split.TestSet);
                    var metrics = mlContext.BinaryClassification.Evaluate (predictions);
                    logger?.Invoke ($"📊 Точность: {metrics.Accuracy:P2}, AUC: {metrics.AreaUnderRocCurve:P2}, F1: {metrics.F1Score:P2}");
                    string temp = _modelPath + ".tmp";
                    mlContext.Model.Save (model, split.TrainSet.Schema, temp);
                    if (File.Exists (_modelPath)) File.Delete (_modelPath);
                    File.Move (temp, _modelPath);
                    _mlContext = mlContext;
                    _mlModel = model;
                    _mlModelLoaded = true;
                    logger?.Invoke ("✅ ML модель обновлена");
                }
                catch (Exception ex) { logger?.Invoke ($"❌ Ошибка обучения: {ex.Message}"); }
            });
        }
        public Task CollectExamplesFromHistoryAsync(BinanceClient client, List<string> pairs, int lookaheadBars = 12) // 12*5мин = 1 час
        {
            // Для каждой пары загружаем 500 свечей
            // Для каждой позиции i (от 50 до 500-lookaheadBars) вычисляем фичи на баре i
            // Целевая переменная: (close[i+lookaheadBars] - close[i]) / close[i]   – будущая доходность
            // Сохраняем в CSV для обучения на Python (LightGBM) или используем ML.NET регрессию
            return Task.CompletedTask;
        }
    }

    public class ModelInput
    {
        public float FastSma { get; set; }
        public float SlowSma { get; set; }
        public float Rsi { get; set; }
        public float VolumeRatio { get; set; }
        public float Atr { get; set; }
        public float MacdHistogram { get; set; }
        public float BbWidth { get; set; }
        public float Obv { get; set; }
        public float MarketCapRank { get; set; }
        public float SentimentScore { get; set; }
        public float GalaxyScore { get; set; }
    }
    public class ModelOutput
    {
        [ColumnName ("PredictedLabel")] public bool IsProfitable { get; set; }
        public float Probability { get; set; }
    }
}
