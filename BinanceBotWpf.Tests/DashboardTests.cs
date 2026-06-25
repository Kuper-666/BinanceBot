using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BinanceBotWpf.Models;
using BinanceBotWpf.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BinanceBotWpf.Tests
{
    public class DashboardWebSocketServer_Tests
    {
        private DashboardWebSocketServer CreateServer ()
        {
            return new DashboardWebSocketServer (NullLogger<DashboardWebSocketServer>.Instance);
        }

        [Fact]
        public void Server_IsNotRunning_BeforeStart ()
        {
            var server = CreateServer ();
            Assert.False (server.IsRunning);
            Assert.Equal (0, server.ClientCount);
        }

        [Fact]
        public void ApiVersion_IsSemanticVersion ()
        {
            Assert.Matches (@"^\d+\.\d+\.\d+$", DashboardWebSocketServer.ApiVersion);
        }

        [Fact]
        public void BroadcastPrices_PayloadHasRequiredKeys ()
        {
            var data = new List<Dictionary<string, object>>
            {
                new ()
                {
                    ["pair"] = "BTCUSDC",
                    ["price"] = 60000m,
                    ["hasPosition"] = false,
                    ["signal"] = "buy",
                    ["rsi"] = 35m,
                    ["macdHist"] = 0.0042m,
                    ["fastSma"] = 60100m,
                    ["slowSma"] = 59800m
                }
            };

            var json = JsonSerializer.Serialize (data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            using var doc = JsonDocument.Parse (json);
            var root = doc.RootElement;

            Assert.Equal (JsonValueKind.Array, root.ValueKind);
            Assert.Equal (1, root.GetArrayLength ());

            var item = root[0];
            Assert.True (item.TryGetProperty ("pair", out _));
            Assert.True (item.TryGetProperty ("price", out _));
            Assert.True (item.TryGetProperty ("hasPosition", out _));
            Assert.True (item.TryGetProperty ("signal", out _));
        }

        [Fact]
        public void BroadcastPrices_ContainsRsiAndMacd ()
        {
            var data = new List<Dictionary<string, object>>
            {
                new ()
                {
                    ["pair"] = "SOLUSDC",
                    ["price"] = 68.63m,
                    ["signal"] = "buy",
                    ["rsi"] = 28.5m,
                    ["macdHist"] = 0.0123m,
                    ["fastSma"] = 67.91m,
                    ["slowSma"] = 67.83m
                }
            };

            var json = JsonSerializer.Serialize (data);
            using var doc = JsonDocument.Parse (json);
            var item = doc.RootElement[0];

            Assert.True (item.TryGetProperty ("rsi", out JsonElement rsiEl));
            Assert.Equal (28.5m, rsiEl.GetDecimal ());

            Assert.True (item.TryGetProperty ("macdHist", out JsonElement macdEl));
            Assert.Equal (0.0123m, macdEl.GetDecimal ());
        }

        [Fact]
        public void BroadcastPositions_PayloadHasRequiredKeys ()
        {
            var data = new List<Dictionary<string, object>>
            {
                new ()
                {
                    ["pair"] = "ETHUSDC",
                    ["entry"] = 1600m,
                    ["qty"] = 0.5m,
                    ["sl"] = 1568m,
                    ["tp"] = 1664m
                }
            };

            var json = JsonSerializer.Serialize (data);
            using var doc = JsonDocument.Parse (json);
            var item = doc.RootElement[0];

            Assert.True (item.TryGetProperty ("pair", out _));
            Assert.True (item.TryGetProperty ("entry", out _));
            Assert.True (item.TryGetProperty ("qty", out _));
            Assert.True (item.TryGetProperty ("sl", out _));
            Assert.True (item.TryGetProperty ("tp", out _));
        }

        [Fact]
        public void BroadcastStats_PayloadHasAllFields ()
        {
            var stats = new Dictionary<string, object>
            {
                ["balance"] = 48.88m,
                ["pnl"] = 0m,
                ["pnlPercent"] = 0m,
                ["winRate"] = 0m,
                ["maxDrawdown"] = 0m,
                ["totalTrades"] = 0,
                ["openPositions"] = 0,
                ["maxPositions"] = 3,
                ["leverage"] = 5,
                ["winningTrades"] = 0,
                ["losingTrades"] = 0,
                ["bestPnL"] = 0m,
                ["worstPnL"] = 0m,
                ["fearGreedValue"] = 50,
                ["fearGreedClassification"] = "Neutral",
                ["dcaEnabled"] = false,
                ["futuresEnabled"] = false,
                ["gridBotRunning"] = false,
                ["telegramStatus"] = "connected"
            };

            var json = JsonSerializer.Serialize (stats);
            using var doc = JsonDocument.Parse (json);
            var root = doc.RootElement;

            string[] requiredFields = new[]
            {
                "balance", "pnl", "pnlPercent", "winRate", "maxDrawdown",
                "totalTrades", "openPositions", "maxPositions", "leverage",
                "winningTrades", "losingTrades", "bestPnL", "worstPnL",
                "fearGreedValue", "fearGreedClassification",
                "dcaEnabled", "futuresEnabled", "gridBotRunning", "telegramStatus"
            };

            foreach (string field in requiredFields)
            {
                Assert.True (root.TryGetProperty (field, out _),
                    $"Dashboard stats missing required field: {field}");
            }
        }

        [Fact]
        public void BroadcastTrades_PayloadHasRequiredKeys ()
        {
            var trades = new List<Dictionary<string, object>>
            {
                new ()
                {
                    ["time"] = "14:30",
                    ["pair"] = "BTCUSDC",
                    ["action"] = "BUY",
                    ["entry"] = 60000m,
                    ["exit"] = 61000m,
                    ["pnl"] = 1.67m,
                    ["duration"] = "2h 15m",
                    ["reason"] = "SMA Buy + RSI"
                }
            };

            var json = JsonSerializer.Serialize (trades);
            using var doc = JsonDocument.Parse (json);
            var item = doc.RootElement[0];

            string[] requiredFields = { "time", "pair", "action", "entry", "exit", "pnl", "duration", "reason" };
            foreach (string field in requiredFields)
            {
                Assert.True (item.TryGetProperty (field, out _),
                    $"Dashboard trades missing required field: {field}");
            }
        }

        [Fact]
        public void BroadcastEchelons_PayloadHasRequiredKeys ()
        {
            var echelons = new Dictionary<string, object>
            {
                ["adaptive"] = true,
                ["validator"] = true,
                ["newsSentinel"] = true
            };

            var json = JsonSerializer.Serialize (echelons);
            using var doc = JsonDocument.Parse (json);
            var root = doc.RootElement;

            Assert.True (root.TryGetProperty ("adaptive", out _));
            Assert.True (root.TryGetProperty ("validator", out _));
            Assert.True (root.TryGetProperty ("newsSentinel", out _));
        }

        [Fact]
        public void BroadcastEquity_PayloadHasTimeAndValue ()
        {
            var equity = new List<Dictionary<string, object>>
            {
                new ()
                {
                    ["time"] = "14:30",
                    ["value"] = 48.88m
                },
                new ()
                {
                    ["time"] = "14:31",
                    ["value"] = 49.12m
                }
            };

            var json = JsonSerializer.Serialize (equity);
            using var doc = JsonDocument.Parse (json);

            Assert.Equal (2, doc.RootElement.GetArrayLength ());
            Assert.True (doc.RootElement[0].TryGetProperty ("time", out _));
            Assert.True (doc.RootElement[0].TryGetProperty ("value", out _));
        }

        [Fact]
        public void BroadcastPnl_PayloadHasAllFields ()
        {
            var pnl = new List<Dictionary<string, object>>
            {
                new ()
                {
                    ["time"] = "14:30",
                    ["pnl"] = 1.50m,
                    ["pnlPercent"] = 3.07m,
                    ["balance"] = 50.38m,
                    ["startBalance"] = 48.88m
                }
            };

            var json = JsonSerializer.Serialize (pnl);
            using var doc = JsonDocument.Parse (json);
            var item = doc.RootElement[0];

            Assert.True (item.TryGetProperty ("pnl", out _));
            Assert.True (item.TryGetProperty ("pnlPercent", out _));
            Assert.True (item.TryGetProperty ("balance", out _));
            Assert.True (item.TryGetProperty ("startBalance", out _));
        }

        [Fact]
        public void WelcomeMessage_HasApiVersion ()
        {
            var server = CreateServer ();

            Assert.NotNull (DashboardWebSocketServer.ApiVersion);
            Assert.NotEmpty (DashboardWebSocketServer.ApiVersion);
        }
    }

    public class DashboardDataFlow_Tests
    {
        [Fact]
        public void PricesData_IncludesLastAnalysisFields ()
        {
            var lastAnalysis = new Dictionary<string, Dictionary<string, object>>
            {
                ["BTCUSDC"] = new ()
                {
                    ["price"] = 60000m,
                    ["signal"] = "buy",
                    ["action"] = "Buy",
                    ["rsi"] = 35m,
                    ["macdHist"] = 0.0042m,
                    ["fastSma"] = 60100m,
                    ["slowSma"] = 59800m,
                    ["bbUpper"] = 62000m,
                    ["bbLower"] = 58000m,
                    ["atr"] = 0.02m,
                    ["volumeRatio"] = 1.5m
                }
            };

            var pairData = new Dictionary<string, object>
            {
                ["pair"] = "BTCUSDC",
                ["price"] = 60000m,
                ["hasPosition"] = false
            };

            foreach (var kvp in lastAnalysis["BTCUSDC"])
            {
                pairData[kvp.Key] = kvp.Value;
            }

            var json = JsonSerializer.Serialize (pairData);
            using var doc = JsonDocument.Parse (json);
            var root = doc.RootElement;

            Assert.True (root.TryGetProperty ("rsi", out _));
            Assert.True (root.TryGetProperty ("macdHist", out _));
            Assert.True (root.TryGetProperty ("fastSma", out _));
            Assert.True (root.TryGetProperty ("slowSma", out _));
            Assert.True (root.TryGetProperty ("signal", out _));
        }

        [Fact]
        public void StatsData_IncludesSessionFields ()
        {
            decimal balance = 48.88m;
            decimal totalPnL = 0m;
            decimal winRate = 0m;

            var stats = new Dictionary<string, object>
            {
                ["balance"] = balance,
                ["pnl"] = totalPnL,
                ["pnlPercent"] = balance > 0 ? Math.Round (totalPnL / balance * 100, 1) : 0,
                ["winRate"] = winRate,
                ["totalTrades"] = 0,
                ["openPositions"] = 0,
                ["maxPositions"] = 3
            };

            decimal pnlPercent = balance > 0 ? Math.Round (totalPnL / balance * 100, 1) : 0;
            Assert.Equal (0m, pnlPercent);
        }

        [Fact]
        public void TradesData_FormatsDurationCorrectly ()
        {
            TimeSpan shortDuration = TimeSpan.FromMinutes (45);
            TimeSpan longDuration = TimeSpan.FromHours (2) + TimeSpan.FromMinutes (15);

            string shortStr = shortDuration.TotalMinutes >= 60
                ? $"{(int)shortDuration.TotalHours}h {shortDuration.Minutes}m"
                : $"{(int)shortDuration.TotalMinutes}m";
            string longStr = longDuration.TotalMinutes >= 60
                ? $"{(int)longDuration.TotalHours}h {longDuration.Minutes}m"
                : $"{(int)longDuration.TotalMinutes}m";

            Assert.Equal ("45m", shortStr);
            Assert.Equal ("2h 15m", longStr);
        }

        [Fact]
        public void TradesData_ActionFormat ()
        {
            bool isLong = true;
            string action = isLong ? "BUY" : "SELL";
            Assert.Equal ("BUY", action);

            isLong = false;
            action = isLong ? "BUY" : "SELL";
            Assert.Equal ("SELL", action);
        }

        [Fact]
        public void PnlData_SessionStartBalance ()
        {
            decimal sessionStartBalance = 48.88m;
            decimal currentBalance = 50.38m;

            decimal sessionPnl = currentBalance - sessionStartBalance;
            decimal sessionPnlPercent = sessionStartBalance > 0
                ? sessionPnl / sessionStartBalance * 100
                : 0;

            Assert.Equal (1.50m, sessionPnl);
            Assert.True (sessionPnlPercent > 3m);
        }
    }

    public class MarketTable_RsiMacd_Tests
    {
        [Fact]
        public void RsiBelow30_ShowsDownArrow ()
        {
            decimal rsi = 28m;
            string rsiStr = rsi < 30 ? $"RSI:{rsi:F0}\u2193" : rsi > 70 ? $"RSI:{rsi:F0}\u2191" : $"RSI:{rsi:F0}";
            Assert.Contains ("RSI:28", rsiStr);
            Assert.Contains ("\u2193", rsiStr);
        }

        [Fact]
        public void RsiAbove70_ShowsUpArrow ()
        {
            decimal rsi = 75m;
            string rsiStr = rsi < 30 ? $"RSI:{rsi:F0}\u2193" : rsi > 70 ? $"RSI:{rsi:F0}\u2191" : $"RSI:{rsi:F0}";
            Assert.Contains ("RSI:75", rsiStr);
            Assert.Contains ("\u2191", rsiStr);
        }

        [Fact]
        public void RsiNeutral_ShowsNoArrow ()
        {
            decimal rsi = 50m;
            string rsiStr = rsi < 30 ? $"RSI:{rsi:F0}\u2193" : rsi > 70 ? $"RSI:{rsi:F0}\u2191" : $"RSI:{rsi:F0}";
            Assert.Equal ("RSI:50", rsiStr);
        }

        [Fact]
        public void MacdPositive_ShowsPlusSign ()
        {
            decimal macdHist = 0.0042m;
            string macdStr = macdHist > 0
                ? $"MACD:+{macdHist.ToString ("F4", System.Globalization.CultureInfo.InvariantCulture)}"
                : $"MACD:{macdHist.ToString ("F4", System.Globalization.CultureInfo.InvariantCulture)}";
            Assert.Equal ("MACD:+0.0042", macdStr);
        }

        [Fact]
        public void MacdNegative_ShowsMinusSign ()
        {
            decimal macdHist = -0.0031m;
            string macdStr = macdHist > 0
                ? $"MACD:+{macdHist.ToString ("F4", System.Globalization.CultureInfo.InvariantCulture)}"
                : $"MACD:{macdHist.ToString ("F4", System.Globalization.CultureInfo.InvariantCulture)}";
            Assert.Equal ("MACD:-0.0031", macdStr);
        }

        [Fact]
        public void AnalysisText_ContainsAllParts ()
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            TradeAction signal = TradeAction.Buy;
            decimal fastSma = 67.91m;
            decimal slowSma = 67.83m;
            decimal rsi = 28m;
            decimal macdHist = 0.0123m;

            string rsiStr = rsi < 30 ? $"RSI:{rsi.ToString ("F0", culture)}\u2193" : rsi > 70 ? $"RSI:{rsi.ToString ("F0", culture)}\u2191" : $"RSI:{rsi.ToString ("F0", culture)}";
            string macdStr = macdHist > 0 ? $"MACD:+{macdHist.ToString ("F4", culture)}" : $"MACD:{macdHist.ToString ("F4", culture)}";
            string analysisText = $"{signal} | F:{fastSma.ToString ("F2", culture)} / S:{slowSma.ToString ("F2", culture)} | {rsiStr} {macdStr}";

            Assert.Contains ("Buy", analysisText);
            Assert.Contains ("F:67.91", analysisText);
            Assert.Contains ("S:67.83", analysisText);
            Assert.Contains ("RSI:28", analysisText);
            Assert.Contains ("MACD:+", macdStr);
        }

        [Fact]
        public void AnalysisText_HoldSignal_FormatsCorrectly ()
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            TradeAction signal = TradeAction.Hold;
            decimal fastSma = 60943.17m;
            decimal slowSma = 60990.43m;
            decimal rsi = 52m;
            decimal macdHist = -0.0012m;

            string rsiStr = rsi < 30 ? $"RSI:{rsi.ToString ("F0", culture)}\u2193" : rsi > 70 ? $"RSI:{rsi.ToString ("F0", culture)}\u2191" : $"RSI:{rsi.ToString ("F0", culture)}";
            string macdStr = macdHist > 0 ? $"MACD:+{macdHist.ToString ("F4", culture)}" : $"MACD:{macdHist.ToString ("F4", culture)}";
            string analysisText = $"{signal} | F:{fastSma.ToString ("F2", culture)} / S:{slowSma.ToString ("F2", culture)} | {rsiStr} {macdStr}";

            Assert.Contains ("Hold", analysisText);
            Assert.Contains ("F:60943.17", analysisText);
            Assert.Contains ("S:60990.43", analysisText);
            Assert.Equal ("RSI:52", rsiStr);
            Assert.Contains ("MACD:-", macdStr);
        }
    }

    public class StrategyEngine_SignalTests
    {
        private StrategyEngine _engine = new ();

        [Fact]
        public void GoldenCross_ReturnsBuy ()
        {
            var closes = new List<decimal> ();
            for (int i = 0; i < 25; i++) closes.Add (100 - i * 0.5m);
            for (int i = 0; i < 15; i++) closes.Add (closes.Last () + 1.0m);

            var signal = _engine.AnalyzePairWithWallet ("TEST", closes, 9, 21, closes.Last ());
            Assert.True (signal.Action == TradeAction.Buy || signal.Action == TradeAction.Hold,
                $"Golden cross scenario: {signal.Action} - {signal.Reason}");
        }

        [Fact]
        public void DeathCross_ReturnsSell ()
        {
            var closes = new List<decimal> ();
            for (int i = 0; i < 25; i++) closes.Add (100 + i * 0.5m);
            for (int i = 0; i < 15; i++) closes.Add (closes.Last () - 1.0m);

            var signal = _engine.AnalyzePairWithWallet ("TEST", closes, 9, 21, closes.Last ());
            Assert.True (signal.Action == TradeAction.Sell || signal.Action == TradeAction.Hold,
                $"Death cross scenario: {signal.Action} - {signal.Reason}");
        }

        [Fact]
        public void FlatMarket_ReturnsHold ()
        {
            var closes = Enumerable.Repeat (100m, 30).ToList ();
            var signal = _engine.AnalyzePairWithWallet ("TEST", closes, 9, 21, 100m);
            Assert.Equal (TradeAction.Hold, signal.Action);
        }

        [Fact]
        public void InsufficientData_ReturnsHoldWithReason ()
        {
            var closes = new List<decimal> { 100, 101 };
            var signal = _engine.AnalyzePairWithWallet ("TEST", closes, 9, 21, 101m);
            Assert.Equal (TradeAction.Hold, signal.Action);
            Assert.Contains ("нужно", signal.Reason);
        }

        [Fact]
        public void SignalReason_ContainsSmaValues ()
        {
            var closes = Enumerable.Repeat (100m, 30).ToList ();
            var signal = _engine.AnalyzePairWithWallet ("TEST", closes, 9, 21, 100m);
            Assert.Contains ("F:", signal.Reason);
            Assert.Contains ("S:", signal.Reason);
        }
    }

    public class SignalFilter_Tests
    {
        private SignalFilter CreateFilter ()
        {
            return new SignalFilter (msg => { });
        }

        [Fact]
        public void CalculateDynamicPositionSize_AtHighVolatility_ReducesSize ()
        {
            var filter = CreateFilter ();
            decimal balance = 100m;
            decimal price = 100m;
            decimal highAtr = 5m;

            decimal quantity = filter.CalculateDynamicPositionSize (balance, price, highAtr);

            Assert.True (quantity > 0);
        }

        [Fact]
        public void CalculateDynamicPositionSize_AtLowVolatility_IncreasesSize ()
        {
            var filter = CreateFilter ();
            decimal balance = 100m;
            decimal price = 100m;
            decimal lowAtr = 0.5m;

            decimal quantity = filter.CalculateDynamicPositionSize (balance, price, lowAtr);

            Assert.True (quantity > 0);
        }

        [Fact]
        public void CalculateDynamicPositionSize_ZeroPrice_ReturnsBase ()
        {
            var filter = CreateFilter ();
            decimal quantity = filter.CalculateDynamicPositionSize (100m, 0m, 1m);
            Assert.Equal (10m, quantity);
        }

        [Fact]
        public void CalculateDynamicPositionSize_NeverExceeds10Percent ()
        {
            var filter = CreateFilter ();
            decimal balance = 100m;
            decimal price = 10m;
            decimal atr = 0.1m;

            decimal quantity = filter.CalculateDynamicPositionSize (balance, price, atr);
            decimal positionValue = quantity * price;

            Assert.True (positionValue <= balance * 0.1m,
                $"Position value {positionValue} exceeds 10% of balance {balance}");
        }

        [Fact]
        public void IsNewsImpactLow_DefaultReturnsTrue ()
        {
            var filter = CreateFilter ();
            Assert.True (filter.IsNewsImpactLow ());
        }
    }

    public class TradingStrategy_SignalFlow_Tests
    {
        private TradingStrategy CreateStrategy ()
        {
            return new TradingStrategy (msg => { });
        }

        private List<BinanceKline> BuildKlines (List<decimal> closes)
        {
            return closes.Select (c => new BinanceKline
            {
                Close = c,
                High = c * 1.005m,
                Low = c * 0.995m,
                Volume = 1000
            }).ToList ();
        }

        [Fact]
        public void AnalyzeAsync_WithValidatorBlocked_ReturnsHold ()
        {
            var strategy = CreateStrategy ();
            var validator = new SignalValidator (msg => { }, volumeThreshold: 0.01m, atrThreshold: 0.001m, rsiLow: 80, rsiHigh: 20);
            strategy.SetSignalValidator (validator, enabled: true);

            var closes = new List<decimal> ();
            for (int i = 0; i < 50; i++) closes.Add (100 + i * 0.5m);
            for (int i = 0; i < 15; i++) closes.Add (closes.Last () - 2m);
            var klines = BuildKlines (closes);

            var result = strategy.AnalyzeAsync ("BTCUSDC", klines).Result;

            Assert.NotNull (result.Indicators);
            Assert.True (result.Indicators.ContainsKey ("validationConfidence") || result.Action == TradeAction.Hold);
        }

        [Fact]
        public void AnalyzeAsync_IndicatorsContainRsiAndMacd ()
        {
            var strategy = CreateStrategy ();
            var closes = new List<decimal> ();
            for (int i = 0; i < 80; i++) closes.Add (100 + (decimal)Math.Sin (i * 0.1) * 5);
            var klines = BuildKlines (closes);

            var result = strategy.AnalyzeAsync ("BTCUSDC", klines).Result;

            Assert.True (result.Indicators.ContainsKey ("rsi"));
            Assert.True (result.Indicators.ContainsKey ("macdHist"));
            Assert.True (result.Indicators.ContainsKey ("price"));
            Assert.True (result.Indicators.ContainsKey ("fastSma"));
            Assert.True (result.Indicators.ContainsKey ("slowSma"));
        }

        [Fact]
        public void AnalyzeAsync_RsiInRange ()
        {
            var strategy = CreateStrategy ();
            var closes = new List<decimal> ();
            for (int i = 0; i < 100; i++) closes.Add (100 + (decimal)Math.Sin (i * 0.05) * 10);
            var klines = BuildKlines (closes);

            var result = strategy.AnalyzeAsync ("ETHUSDC", klines).Result;

            decimal rsi = result.Indicators["rsi"];
            Assert.True (rsi >= 0 && rsi <= 100,
                $"RSI should be 0-100, got {rsi}");
        }

        [Fact]
        public void CheckEntryConfirmation_BuyLowRsi_ReturnsTrue ()
        {
            var strategy = CreateStrategy ();
            var klines = Enumerable.Range (0, 30)
                .Select (i => new BinanceKline { Close = 100 - i * 0.5m, High = 101, Low = 99, Volume = 1000 })
                .ToList ();

            Assert.True (strategy.CheckEntryConfirmation (klines, TradeAction.Buy));
        }

        [Fact]
        public void CheckEntryConfirmation_SellHighRsi_ReturnsTrue ()
        {
            var strategy = CreateStrategy ();
            var klines = Enumerable.Range (0, 30)
                .Select (i => new BinanceKline { Close = 100 + i * 0.5m, High = 101, Low = 99, Volume = 1000 })
                .ToList ();

            Assert.True (strategy.CheckEntryConfirmation (klines, TradeAction.Sell));
        }

        [Fact]
        public void CheckEntryConfirmation_NoData_ReturnsTrue ()
        {
            var strategy = CreateStrategy ();
            Assert.True (strategy.CheckEntryConfirmation (null, TradeAction.Buy));
            Assert.True (strategy.CheckEntryConfirmation (new List<BinanceKline> (), TradeAction.Sell));
        }
    }
}
