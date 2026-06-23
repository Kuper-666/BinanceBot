using Microsoft.ML;
using Microsoft.ML.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BinanceBotWpf.Services
{
    public class SignalValidator
    {
        private readonly Action<string> _logger;
        private readonly decimal _volumeThreshold;
        private readonly decimal _atrThreshold;
        private readonly int _rsiLow;
        private readonly int _rsiHigh;
        private MLContext _mlContext;
        private ITransformer _onnxModel;
        private bool _modelLoaded;

        private static readonly string[] _onnxCandidatePaths = new[]
        {
            "ML/signal_validator.onnx",
            "signal_validator.onnx",
            "Models/signal_validator.onnx",
        };

        public bool IsModelLoaded => _modelLoaded;

        public SignalValidator (Action<string> logger, decimal volumeThreshold = 8.0m, decimal atrThreshold = 0.15m, int rsiLow = 20, int rsiHigh = 80)
        {
            _logger = logger;
            _volumeThreshold = volumeThreshold;
            _atrThreshold = atrThreshold;
            _rsiLow = rsiLow;
            _rsiHigh = rsiHigh;
            LoadOnnxModel ();
        }

        private void LoadOnnxModel ()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string foundPath = null;

            foreach (var candidate in _onnxCandidatePaths)
            {
                string full = Path.Combine (baseDir, candidate);
                if (File.Exists (full)) { foundPath = full; break; }
            }

            if (foundPath == null)
            {
                _logger?.Invoke ("ℹ️ ONNX модель не найдена — используется эврическая валидация.");
                return;
            }

            try
            {
                _mlContext = new MLContext ();
                _onnxModel = _mlContext.Model.Load (foundPath, out _);
                _modelLoaded = true;
                _logger?.Invoke ($"✅ ONNX валидатор загружен: {foundPath}");
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"⚠️ Ошибка загрузки ONNX: {ex.Message}. Используется эврическая валидация.");
            }
        }

        public ValidationResult Validate (SignalValidationInput input)
        {
            if (_modelLoaded && _onnxModel != null)
            {
                return ValidateWithOnnx (input);
            }
            return ValidateHeuristic (input);
        }

        private ValidationResult ValidateWithOnnx (SignalValidationInput input)
        {
            try
            {
                var predEngine = _mlContext.Model.CreatePredictionEngine<SignalValidationInput, OnnxSignalOutput> (_onnxModel);
                var prediction = predEngine.Predict (input);

                return new ValidationResult
                {
                    IsValid = prediction.IsValid,
                    Confidence = prediction.Confidence,
                    RiskFlag = prediction.RiskFlag,
                    Method = "ONNX"
                };
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"⚠️ ONNX ошибка: {ex.Message}. Fallback на эвристику.");
                return ValidateHeuristic (input);
            }
        }

        private ValidationResult ValidateHeuristic (SignalValidationInput input)
        {
            float confidence = 0.5f;
            bool riskFlag = false;
            int positiveFactors = 0;
            int totalFactors = 0;

            totalFactors++;
            if (input.Rsi > _rsiLow && input.Rsi < _rsiHigh)
            {
                positiveFactors++;
                confidence += 0.1f;
            }
            else
            {
                confidence -= 0.1f;
            }

            totalFactors++;
            if (input.VolumeRatio > 0.8f && input.VolumeRatio < 3.0f)
            {
                positiveFactors++;
                confidence += 0.1f;
            }
            else if (input.VolumeRatio > (float)_volumeThreshold)
            {
                riskFlag = true;
                confidence -= 0.15f;
            }

            totalFactors++;
            if (input.AtrPercent > 0.005f && input.AtrPercent < 0.08f)
            {
                positiveFactors++;
                confidence += 0.05f;
            }
            else if (input.AtrPercent > (float)_atrThreshold)
            {
                riskFlag = true;
                confidence -= 0.2f;
            }

            totalFactors++;
            if (Math.Abs (input.MacdHistogram) > 0.001f)
            {
                bool macdAligns = (input.SignalDirection == 1 && input.MacdHistogram > 0) ||
                                  (input.SignalDirection == -1 && input.MacdHistogram < 0);
                if (macdAligns)
                {
                    positiveFactors++;
                    confidence += 0.1f;
                }
                else
                {
                    confidence -= 0.1f;
                }
            }

            totalFactors++;
            if (input.BbWidth > 0.01f && input.BbWidth < 0.15f)
            {
                positiveFactors++;
                confidence += 0.05f;
            }

            totalFactors++;
            if (input.Price > 0 && input.SmaFast > 0 && input.SmaSlow > 0)
            {
                bool trendAligns = (input.SignalDirection == 1 && input.Price > input.SmaFast && input.SmaFast > input.SmaSlow) ||
                                   (input.SignalDirection == -1 && input.Price < input.SmaFast && input.SmaFast < input.SmaSlow);
                if (trendAligns)
                {
                    positiveFactors++;
                    confidence += 0.1f;
                }
            }

            confidence = Math.Clamp (confidence, 0f, 1f);
            bool isValid = confidence > 0.45f && !riskFlag;

            if (totalFactors > 0 && positiveFactors < totalFactors * 0.3)
            {
                riskFlag = true;
            }

            _logger?.Invoke ($"🔍 Эврическая валидация: valid={isValid}, conf={confidence:P0}, risk={riskFlag} ({positiveFactors}/{totalFactors} factors)");

            return new ValidationResult
            {
                IsValid = isValid,
                Confidence = confidence,
                RiskFlag = riskFlag,
                Method = "Heuristic"
            };
        }
    }

    public class SignalValidationInput
    {
        [LoadColumn (0)] public float Price { get; set; }
        [LoadColumn (1)] public float Rsi { get; set; }
        [LoadColumn (2)] public float MacdHistogram { get; set; }
        [LoadColumn (3)] public float BbWidth { get; set; }
        [LoadColumn (4)] public float AtrPercent { get; set; }
        [LoadColumn (5)] public float VolumeRatio { get; set; }
        [LoadColumn (6)] public float SmaFast { get; set; }
        [LoadColumn (7)] public float SmaSlow { get; set; }
        [LoadColumn (8)] public float SignalDirection { get; set; }
    }

    public class OnnxSignalOutput
    {
        [ColumnName ("IsValid")] public bool IsValid { get; set; }
        [ColumnName ("Confidence")] public float Confidence { get; set; }
        [ColumnName ("RiskFlag")] public bool RiskFlag { get; set; }
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; } = true;
        public float Confidence { get; set; } = 0.5f;
        public bool RiskFlag { get; set; } = false;
        public string Method { get; set; } = "Unknown";
    }
}
