using BinanceBotWpf.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    /// <summary>Управление открытыми позициями (загрузка, сохранение, добавление, удаление).</summary>
    public class PositionManager : IPositionManager
    {
        private readonly string _positionsFilePath;
        private readonly Action<string> _logger;
        private readonly ConcurrentDictionary<string, OpenPosition> _positions = new ();
        private readonly SemaphoreSlim _fileSemaphore = new (1, 1);
        public bool IsBreakevenSet { get; set; } = false;

        public IReadOnlyDictionary<string, OpenPosition> Positions => _positions;

        public PositionManager(string positionsFilePath, Action<string> logger)
        {
            _positionsFilePath = positionsFilePath;
            _logger = logger;
        }

        /// <summary>Загружает позиции из файла, проверяет балансы на споте и в Earn.</summary>
        public async Task LoadAsync(IBinanceClient client, Func<string, Task<decimal>> getPrice, Func<decimal, decimal> getStopLossPercent, Func<decimal, decimal> getTakeProfitPercent, IReadOnlyCollection<string> tradedPairs = null)
        {
            if (!File.Exists (_positionsFilePath)) return;
            try
            {
                string json = await File.ReadAllTextAsync (_positionsFilePath);
                var saved = JsonSerializer.Deserialize<Dictionary<string, OpenPosition>> (json);
                if (saved == null) return;

                var toRemove = new List<string> ();
                foreach (var kv in saved)
                {
                    string asset = kv.Key.Replace ("USDC", "");
                    decimal spotBalance = await client.GetAccountBalanceAsync (asset);
                    decimal earnBalance = 0;
                    var earnPositions = await client.GetFlexibleEarnBalanceAsync ();
                    var earnPos = earnPositions?.FirstOrDefault (p => p["asset"]?.ToString () == asset);
                    if (earnPos != null)
                        earnBalance = decimal.Parse (earnPos["totalAmount"]?.ToString () ?? "0", CultureInfo.InvariantCulture);
                    decimal totalBalance = spotBalance + earnBalance;

                    if (totalBalance < kv.Value.Quantity - 0.000001m)
                    {
                        _logger?.Invoke ($"⚠️ Позиция {kv.Key} удалена: недостаточно монет (баланс {totalBalance}, требуется {kv.Value.Quantity}).");
                        toRemove.Add (kv.Key);
                        continue;
                    }

                    decimal currentPrice = await getPrice (kv.Key);
                    if (currentPrice > 0)
                    {
                        if (kv.Value.StopLossPrice <= 0)
                            kv.Value.StopLossPrice = currentPrice * ( 1 - getStopLossPercent (currentPrice) );
                        if (kv.Value.TakeProfitPrice <= 0)
                            kv.Value.TakeProfitPrice = currentPrice * ( 1 + getTakeProfitPercent (currentPrice) );
                        kv.Value.HighestPrice = currentPrice;
                        _positions[kv.Key] = kv.Value;
                        _logger?.Invoke ($"🔄 Восстановлена позиция {kv.Key} ({kv.Value.Quantity} по {kv.Value.EntryPrice:F4})");
                    }
                }
                foreach (var sym in toRemove) saved.Remove (sym);
                if (toRemove.Count > 0) await SaveAsync ();

                if (tradedPairs != null && tradedPairs.Count > 0)
                    await RestoreLeftoverWalletAssetsAsync (client, tradedPairs, getPrice, getStopLossPercent, getTakeProfitPercent);
            }
            catch (Exception ex) { _logger?.Invoke ($"Ошибка загрузки позиций: {ex.Message}"); }
        }

        /// <summary>
        /// Сканирует кошелёк (спот + Earn) на активы торговых пар, которых нет в файле позиций,
        /// и восстанавливает по ним позиции (например, после перезагрузки ПК во время торгов).
        /// </summary>
        private async Task RestoreLeftoverWalletAssetsAsync(IBinanceClient client, IReadOnlyCollection<string> tradedPairs, Func<string, Task<decimal>> getPrice, Func<decimal, decimal> getStopLossPercent, Func<decimal, decimal> getTakeProfitPercent)
        {
            try
            {
                Dictionary<string, decimal> balances = new ();
                JObject account = await client.GetAccountInfoAsync ();
                if (account?["balances"] != null)
                {
                    foreach (JToken b in account["balances"])
                    {
                        string asset = b["asset"]?.ToString ();
                        if (string.IsNullOrEmpty (asset)) continue;
                        decimal free = decimal.Parse (b["free"]?.ToString () ?? "0", CultureInfo.InvariantCulture);
                        decimal locked = decimal.Parse (b["locked"]?.ToString () ?? "0", CultureInfo.InvariantCulture);
                        decimal total = free + locked;
                        if (total > 0)
                            balances[asset] = (balances.TryGetValue (asset, out decimal prev) ? prev : 0m) + total;
                    }
                }

                JArray earnPositions = await client.GetFlexibleEarnBalanceAsync ();
                if (earnPositions != null)
                {
                    foreach (JToken item in earnPositions)
                    {
                        string asset = item["asset"]?.ToString ();
                        if (string.IsNullOrEmpty (asset)) continue;
                        decimal amount = decimal.Parse (item["totalAmount"]?.ToString () ?? "0", CultureInfo.InvariantCulture);
                        if (amount > 0)
                            balances[asset] = (balances.TryGetValue (asset, out decimal prev) ? prev : 0m) + amount;
                    }
                }

                int restored = 0;
                foreach (string pair in tradedPairs)
                {
                    if (_positions.ContainsKey (pair)) continue;
                    string asset = GetBaseAsset (pair);
                    if (string.IsNullOrEmpty (asset)) continue;
                    if (!balances.TryGetValue (asset, out decimal totalBalance) || totalBalance <= 0) continue;

                    decimal currentPrice = await getPrice (pair);
                    if (currentPrice <= 0) continue;
                    if (totalBalance * currentPrice < 1.0m) continue;

                    decimal stepSize = 0;
                    try { stepSize = await client.GetStepSizeAsync (pair); } catch { }
                    decimal quantity = stepSize > 0 ? Math.Floor (totalBalance / stepSize) * stepSize : totalBalance;
                    if (quantity <= 0) continue;

                    OpenPosition pos = new OpenPosition
                    {
                        Symbol = pair,
                        Quantity = quantity,
                        EntryPrice = currentPrice,
                        OpenTime = DateTime.UtcNow,
                        StopLossPrice = currentPrice * ( 1 - getStopLossPercent (currentPrice) ),
                        TakeProfitPrice = currentPrice * ( 1 + getTakeProfitPercent (currentPrice) ),
                        HighestPrice = currentPrice,
                        HighestPriceSinceOpen = currentPrice,
                        IsUnprotected = true
                    };
                    _positions[pair] = pos;
                    restored++;
                    _logger?.Invoke ($"🔄 Восстановлена из кошелька: {pair} ({quantity} {asset} @ {currentPrice:F4}, ~{totalBalance * currentPrice:F2} USDC)");
                }

                if (restored > 0)
                {
                    await SaveAsync ();
                    _logger?.Invoke ($"🔍 Кошелёк просканирован: найдено {restored} не отслеживаемых активов торговых пар, позиции восстановлены.");
                }
            }
            catch (Exception ex) { _logger?.Invoke ($"Ошибка сканирования кошелька: {ex.Message}"); }
        }

        private static string GetBaseAsset(string pair)
        {
            if (pair.EndsWith ("USDC", StringComparison.Ordinal)) return pair.Substring (0, pair.Length - 4);
            if (pair.EndsWith ("USDT", StringComparison.Ordinal)) return pair.Substring (0, pair.Length - 4);
            return null;
        }

        public async Task SaveAsync()
        {
            await _fileSemaphore.WaitAsync ();
            try
            {
                string dir = Path.GetDirectoryName (_positionsFilePath);
                if (!Directory.Exists (dir)) Directory.CreateDirectory (dir);
                string json = JsonSerializer.Serialize (_positions, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync (_positionsFilePath, json);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"Save positions error: {ex.Message}"); }
            finally
            {
                _fileSemaphore.Release ();
            }
        }

        public bool TryGet(string symbol, out OpenPosition pos) => _positions.TryGetValue (symbol, out pos);
        public async Task AddOrUpdateAsync(string symbol, OpenPosition pos)
        {
            _positions[symbol] = pos;
            await SaveAsync ();
        }

        public async Task<bool> RemoveAsync(string symbol)
        {
            var removed = _positions.TryRemove (symbol, out _);
            if (removed) await SaveAsync ();
            return removed;
        }

        // Синхронные обёртки для обратной совместимости
        public void AddOrUpdate(string symbol, OpenPosition pos) { _positions[symbol] = pos; _ = SaveAsync (); }
        public bool Remove(string symbol) { var removed = _positions.TryRemove (symbol, out _); if (removed) _ = SaveAsync (); return removed; }
        public int Count => _positions.Count;
        public List<string> GetSymbols() => new List<string> (_positions.Keys);
        public void Clear() { _positions.Clear (); _ = SaveAsync (); }
    }

    public class OpenPosition
    {
        public string Symbol { get; set; }
        public decimal Quantity { get; set; }
        public decimal EntryPrice { get; set; }
        public DateTime OpenTime { get; set; }
        public decimal StopLossPrice { get; set; }
        public decimal TakeProfitPrice { get; set; }
        public decimal HighestPrice { get; set; }
        public long OcoOrderListId { get; set; }
        public decimal InitialTakeProfitPrice { get; set; }
        public decimal HighestPriceSinceOpen { get; set; }
        public bool IsBreakevenSet { get; set; } = false;
        public bool IsUnprotected { get; set; } = false;
        public bool PartialClosed { get; set; } = false;
    }
}