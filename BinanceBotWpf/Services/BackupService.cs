using System;
using System.IO;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Сервис резервного копирования конфигурации и настроек.
    /// Создаёт бэкапы каждые 24 часа в папку Backup.
    /// </summary>
    public class BackupService : IBackupService
    {
        private readonly Action<string> _logger;
        private readonly string _backupDir;
        private DateTime _lastBackupTime = DateTime.MinValue;

        private static readonly string[] FilesToBackup = new[]
        {
            "config.json",
            "Data/trading_settings.json",
            "Data/open_positions.json",
            "Data/strategy_settings.json"
        };

        public BackupService(Action<string> logger)
        {
            _logger = logger;
            _backupDir = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "Backup");
        }

        /// <summary>
        /// Проверяет и создаёт бэкап если прошло 24+ часов
        /// </summary>
        public async Task CheckAndBackupAsync()
        {
            if ((DateTime.UtcNow - _lastBackupTime).TotalHours < 24) return;

            try
            {
                await CreateBackupAsync ();
                _lastBackupTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Ошибка бэкапа: {ex.Message}");
            }
        }

        /// <summary>
        /// Создаёт резервную копию всех файлов конфигурации
        /// </summary>
        public async Task CreateBackupAsync()
        {
            if (!Directory.Exists (_backupDir))
                Directory.CreateDirectory (_backupDir);

            string timestamp = DateTime.UtcNow.ToString ("yyyyMMdd_HHmmss");
            string backupSubDir = Path.Combine (_backupDir, timestamp);
            Directory.CreateDirectory (backupSubDir);

            int backedUp = 0;
            foreach (var file in FilesToBackup)
            {
                string sourcePath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, file);
                if (File.Exists (sourcePath))
                {
                    string destPath = Path.Combine (backupSubDir, Path.GetFileName (file));
                    File.Copy (sourcePath, destPath, overwrite: true);
                    backedUp++;
                }
            }

            // Удаляем старые бэкапы (оставляем последние 7)
            CleanupOldBackups ();

            _logger?.Invoke ($"💾 Бэкап создан: {backupSubDir} ({backedUp} файлов)");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Восстанавливает конфигурацию из указанного бэкапа
        /// </summary>
        public Task<bool> RestoreFromBackupAsync(string backupDir)
        {
            if (!Directory.Exists (backupDir))
            {
                _logger?.Invoke ($"❌ Бэкап не найден: {backupDir}");
                return Task.FromResult (false);
            }

            try
            {
                foreach (var file in FilesToBackup)
                {
                    string sourcePath = Path.Combine (backupDir, Path.GetFileName (file));
                    string destPath = Path.Combine (AppDomain.CurrentDomain.BaseDirectory, file);

                    if (File.Exists (sourcePath))
                    {
                        string destDir = Path.GetDirectoryName (destPath);
                        if (!Directory.Exists (destDir)) Directory.CreateDirectory (destDir);
                        File.Copy (sourcePath, destPath, overwrite: true);
                    }
                }

                _logger?.Invoke ($"✅ Конфигурация восстановлена из {backupDir}");
                return Task.FromResult (true);
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Ошибка восстановления: {ex.Message}");
                return Task.FromResult (false);
            }
        }

        /// <summary>
        /// Возвращает список доступных бэкапов
        /// </summary>
        public string[] GetAvailableBackups()
        {
            if (!Directory.Exists (_backupDir)) return new string[0];

            var dirs = Directory.GetDirectories (_backupDir);
            Array.Sort (dirs, (a, b) => string.Compare (b, a)); // Новые первыми
            return dirs;
        }

        private void CleanupOldBackups()
        {
            try
            {
                var dirs = Directory.GetDirectories (_backupDir);
                if (dirs.Length <= 7) return;

                Array.Sort (dirs, (a, b) => string.Compare (a, b)); // Старые первыми
                for (int i = 0; i < dirs.Length - 7; i++)
                {
                    Directory.Delete (dirs[i], recursive: true);
                }
            }
            catch { }
        }
    }
}
