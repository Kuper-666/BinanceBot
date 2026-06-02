using BinanceBotWpf.Models;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public class PositionManager
    {
        private readonly string _positionsFilePath;
        private readonly Action<string> _logger;
        private Dictionary<string, OpenPosition> _positions = new ();

        public IReadOnlyDictionary<string, OpenPosition> Positions => _positions;

        public PositionManager(string positionsFilePath, Action<string> logger)
        {
            _positionsFilePath = positionsFilePath;
            _logger = logger;
        }

        public async Task LoadAsync(BinanceClient client, Func<string, Task<decimal>> getPrice, Func<decimal, decimal> getStopLossPercent, Func<decimal, decimal> getTakeProfitPercent)
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
                        earnBalance = decimal.Parse (earnPos["totalAmount"]?.ToString () ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                    decimal totalBalance = spotBalance + earnBalance;

                    if (totalBalance < kv.Value.Quantity - 0.000001m)
                    {
                        _logger?.Invoke ($"⚠️ Позиция {kv.Key} имеет недостаточно монет (баланс {totalBalance}, требуется {kv.Value.Quantity}). Удаляю.");
                        toRemove.Add (kv.Key);
                        continue;
                    }

                    decimal currentPrice = await getPrice (kv.Key);
                    if (currentPrice > 0)
                    {
                        kv.Value.StopLossPrice = currentPrice * ( 1 - getStopLossPercent (currentPrice) );
                        kv.Value.TakeProfitPrice = currentPrice * ( 1 + getTakeProfitPercent (currentPrice) );
                        kv.Value.HighestPrice = currentPrice;
                        _positions[kv.Key] = kv.Value;
                        _logger?.Invoke ($"🔄 Восстановлена позиция {kv.Key} ({kv.Value.Quantity} по {kv.Value.EntryPrice:F4})");
                    }
                }
                foreach (var sym in toRemove) saved.Remove (sym);
                if (toRemove.Count > 0) await SaveAsync ();
            }
            catch (Exception ex) { _logger?.Invoke ($"Ошибка загрузки позиций: {ex.Message}"); }
        }

        public async Task SaveAsync()
        {
            try
            {
                string dir = Path.GetDirectoryName (_positionsFilePath);
                if (!Directory.Exists (dir)) Directory.CreateDirectory (dir);
                string json = JsonSerializer.Serialize (_positions, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync (_positionsFilePath, json);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"Save positions error: {ex.Message}"); }
        }

        public bool TryGet(string symbol, out OpenPosition pos) => _positions.TryGetValue (symbol, out pos);
        public void AddOrUpdate(string symbol, OpenPosition pos) { _positions[symbol] = pos; SaveAsync ().ConfigureAwait (false); }
        public bool Remove(string symbol) { var removed = _positions.Remove (symbol); if (removed) SaveAsync ().ConfigureAwait (false); return removed; }
        public int Count => _positions.Count;
        public List<string> GetSymbols() => new List<string> (_positions.Keys);
        public void Clear() { _positions.Clear (); SaveAsync ().ConfigureAwait (false); }
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
    }
}