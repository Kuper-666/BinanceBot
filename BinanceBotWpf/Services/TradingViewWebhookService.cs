using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BinanceBotWpf.Models;
using BinanceBotWpf.ViewModels;

namespace BinanceBotWpf.Services
{
    public class TradingViewWebhookService
    {
        private readonly MainWindowViewModel _ui;
        private readonly Func<string, Dictionary<string, decimal>, decimal, Task> _executeBuy;
        private readonly Func<string, Task> _executeSell;
        private readonly BinanceClient _client;
        private readonly TradingSettings _settings;
        private readonly Func<string> _getBalance;
        private readonly Func<string, decimal> _getCurrentPrice;

        private static readonly HashSet<string> AllowedActions = new (StringComparer.OrdinalIgnoreCase) { "buy", "sell", "close", "long", "short" };

        public TradingViewWebhookService (
            MainWindowViewModel ui,
            BinanceClient client,
            TradingSettings settings,
            Func<string, Dictionary<string, decimal>, decimal, Task> executeBuy,
            Func<string, Task> executeSell,
            Func<string> getBalance,
            Func<string, decimal> getCurrentPrice)
        {
            _ui = ui;
            _client = client;
            _settings = settings;
            _executeBuy = executeBuy;
            _executeSell = executeSell;
            _getBalance = getBalance;
            _getCurrentPrice = getCurrentPrice;
        }

        public async Task<string> HandleWebhookAsync (string source, string body)
        {
            try
            {
                if (!_settings.TradingViewEnabled)
                {
                    _ui?.AddLog ("⚠️ TradingView webhook: отключен в настройках");
                    return "{\"status\":\"disabled\",\"error\":\"TradingView webhook is disabled\"}";
                }

                if (!string.IsNullOrEmpty (_settings.TradingViewSecret) && source != _settings.TradingViewSecret)
                {
                    _ui?.AddLog ($"⚠️ TradingView webhook: неверный секрет ({source})");
                    return "{\"status\":\"error\",\"error\":\"invalid secret\"}";
                }

                using JsonDocument doc = JsonDocument.Parse (body);
                JsonElement root = doc.RootElement;

                string action = root.TryGetProperty ("action", out JsonElement actionEl) ? actionEl.GetString ()?.ToLower () : "";
                string symbol = root.TryGetProperty ("symbol", out JsonElement symbolEl) ? symbolEl.GetString ()?.ToUpper () : "";
                decimal price = 0;
                if (root.TryGetProperty ("price", out JsonElement priceEl))
                {
                    if (priceEl.ValueKind == JsonValueKind.Number)
                        price = priceEl.GetDecimal ();
                    else if (priceEl.ValueKind == JsonValueKind.String)
                        decimal.TryParse (priceEl.GetString (), NumberStyles.Any, CultureInfo.InvariantCulture, out price);
                }

                string message = root.TryGetProperty ("message", out JsonElement msgEl) ? msgEl.GetString () : "";
                string timeframe = root.TryGetProperty ("timeframe", out JsonElement tfEl) ? tfEl.GetString () : "";

                if (string.IsNullOrEmpty (action) || string.IsNullOrEmpty (symbol))
                {
                    _ui?.AddLog ($"⚠️ TradingView webhook: не указан action или symbol");
                    return "{\"status\":\"error\",\"error\":\"missing action or symbol\"}";
                }

                if (!AllowedActions.Contains (action))
                {
                    _ui?.AddLog ($"⚠️ TradingView webhook: неизвестное действие '{action}'");
                    return "{\"status\":\"error\",\"error\":\"unknown action\"}";
                }

                if (!symbol.EndsWith ("USDC") && !symbol.EndsWith ("USDT"))
                {
                    _ui?.AddLog ($"⚠️ TradingView webhook: символ {symbol} не заканчивается на USDC/USDT");
                    return "{\"status\":\"error\",\"error\":\"invalid symbol\"}";
                }

                bool isBuy = action == "buy" || action == "long";
                bool isSell = action == "sell" || action == "close" || action == "short";

                if (isBuy)
                {
                    return await HandleBuyAsync (symbol, price, message, timeframe);
                }
                else if (isSell)
                {
                    return await HandleSellAsync (symbol, message, timeframe);
                }

                return "{\"status\":\"error\",\"error\":\"no action taken\"}";
            }
            catch (JsonException ex)
            {
                _ui?.AddLog ($"⚠️ TradingView webhook: ошибка парсинга JSON — {ex.Message}");
                return "{\"status\":\"error\",\"error\":\"invalid json\"}";
            }
            catch (Exception ex)
            {
                _ui?.AddLog ($"❌ TradingView webhook: {ex.Message}");
                return $"{{\"status\":\"error\",\"error\":\"{ex.Message}\"}}";
            }
        }

        private async Task<string> HandleBuyAsync (string symbol, decimal price, string message, string timeframe)
        {
            decimal balance = 0;
            try { balance = decimal.Parse (_getBalance (), CultureInfo.InvariantCulture); }
            catch { }

            if (balance <= 5m)
            {
                _ui?.AddLog ($"⚠️ TradingView BUY {symbol}: недостаточно баланса ({balance:F2} USDC)");
                return "{\"status\":\"error\",\"error\":\"insufficient balance\"}";
            }

            decimal currentPrice = price > 0 ? price : _getCurrentPrice (symbol);
            if (currentPrice <= 0)
            {
                _ui?.AddLog ($"⚠️ TradingView BUY {symbol}: не удалось получить цену");
                return "{\"status\":\"error\",\"error\":\"no price\"}";
            }

            var indicators = new Dictionary<string, decimal>
            {
                ["price"] = currentPrice,
                ["rsi"] = 50,
                ["fastSma"] = currentPrice,
                ["slowSma"] = currentPrice,
                ["macdHist"] = 0,
                ["bbWidth"] = 0.02m,
                ["volumeRatio"] = 1.0m,
                ["obv"] = 0,
                ["adaptiveSlMultiplier"] = 0.4m
            };

            string tf = string.IsNullOrEmpty (timeframe) ? "TV" : timeframe;
            string msg = string.IsNullOrEmpty (message) ? "" : $" ({message})";
            _ui?.AddLog ($"📡 TradingView BUY {symbol} @ {currentPrice:F6}{msg} [{tf}]");

            try
            {
                await _executeBuy (symbol, indicators, balance);
                return "{\"status\":\"ok\",\"action\":\"buy\",\"symbol\":\"" + symbol + "\"}";
            }
            catch (Exception ex)
            {
                _ui?.AddLog ($"❌ TradingView BUY {symbol} failed: {ex.Message}");
                return $"{{\"status\":\"error\",\"error\":\"{ex.Message}\"}}";
            }
        }

        private async Task<string> HandleSellAsync (string symbol, string message, string timeframe)
        {
            string tf = string.IsNullOrEmpty (timeframe) ? "TV" : timeframe;
            string msg = string.IsNullOrEmpty (message) ? "" : $" ({message})";
            _ui?.AddLog ($"📡 TradingView SELL {symbol}{msg} [{tf}]");

            try
            {
                await _executeSell (symbol);
                return "{\"status\":\"ok\",\"action\":\"sell\",\"symbol\":\"" + symbol + "\"}";
            }
            catch (Exception ex)
            {
                _ui?.AddLog ($"❌ TradingView SELL {symbol} failed: {ex.Message}");
                return $"{{\"status\":\"error\",\"error\":\"{ex.Message}\"}}";
            }
        }
    }
}
