using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BinanceBotWpf.Services
{
    // DTO для сохранения позиции (без зависимости от TradingService)
    public class SavedPosition
    {
        public string Symbol { get; set; }
        public decimal Quantity { get; set; }
        public decimal EntryPrice { get; set; }
        public DateTime OpenTime { get; set; }
        public decimal StopLossPrice { get; set; }
        public decimal TakeProfitPrice { get; set; }
        public decimal HighestPrice { get; set; }
    }

    public class PositionPersistence
    {
        private readonly string _filePath;

        public PositionPersistence()
        {
            string dir = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Data");
            if (!Directory.Exists (dir)) Directory.CreateDirectory (dir);
            _filePath = Path.Combine (dir, "open_positions.json");
        }

        public void Save(Dictionary<string, SavedPosition> positions)
        {
            try
            {
                string json = JsonSerializer.Serialize (positions, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText (_filePath, json);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine ($"Save positions error: {ex.Message}"); }
        }

        public Dictionary<string, SavedPosition> Load()
        {
            if (!File.Exists (_filePath)) return new Dictionary<string, SavedPosition> ();
            try
            {
                string json = File.ReadAllText (_filePath);
                return JsonSerializer.Deserialize<Dictionary<string, SavedPosition>> (json)
                       ?? new Dictionary<string, SavedPosition> ();
            }
            catch { return new Dictionary<string, SavedPosition> (); }
        }
    }
}