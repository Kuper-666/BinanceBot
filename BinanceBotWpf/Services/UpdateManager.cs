using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json.Linq;

namespace BinanceBotWpf.Services
{
    public class UpdateManager
    {
        private const string GitHubOwner = "Kuper-666";
        private const string GitHubRepo = "BinanceBot";
        private static readonly Version CurrentVersion = Assembly.GetExecutingAssembly ().GetName ().Version ?? new Version ("1.0.0");
        private readonly HttpClient _httpClient = new HttpClient ();
        private readonly Action<string> _logger;
        private DateTime _lastUpdateCheckDate = DateTime.MinValue;
        private bool _isUpdating = false;

        public UpdateManager(Action<string> logger)
        {
            _logger = logger;
            _httpClient.DefaultRequestHeaders.Add ("User-Agent", "BinanceTradingBot/1.0");
            _httpClient.DefaultRequestHeaders.Add ("Accept", "application/vnd.github.v3+json");
        }

        /// <summary>
        /// Проверяет наличие новой версии и при необходимости обновляет.
        /// </summary>
        /// <param name="silent">Если true, не показывает диалоговое окно</param>
        public async Task<bool> CheckAndUpdateAsync(bool silent = false)
        {
            try
            {
                _logger?.Invoke ("🔍 Проверка обновлений...");
                string apiUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases";

                using var response = await _httpClient.GetAsync (apiUrl);
                if (!response.IsSuccessStatusCode)
                {
                    _logger?.Invoke ($"❌ GitHub API вернул ошибку: {response.StatusCode}");
                    return false;
                }

                string json = await response.Content.ReadAsStringAsync ();
                var releases = JArray.Parse (json);

                if (releases.Count == 0)
                {
                    _logger?.Invoke ("⚠️ Релизы не найдены.");
                    return false;
                }

                var sortedReleases = releases
                    .OrderByDescending (r => r["published_at"]?.Value<DateTime> () ?? DateTime.MinValue)
                    .ToList ();

                var latestRelease = sortedReleases.First ();
                string latestTag = latestRelease["tag_name"]?.ToString () ?? "v0.0.0";
                string latestVersionStr = latestTag.TrimStart ('v');
                Version latestVersion = new Version (latestVersionStr);
                Version currentVersion = Assembly.GetExecutingAssembly ().GetName ().Version ?? new Version ("1.0.0");

                var currentSimple = new Version (currentVersion.Major, currentVersion.Minor,
                    currentVersion.Build >= 0 ? currentVersion.Build : 0);

                if (latestVersion > currentSimple)
                {
                    string downloadUrl = latestRelease["assets"]?[0]?["browser_download_url"]?.ToString ();
                    if (!string.IsNullOrEmpty (downloadUrl))
                    {
                        _logger?.Invoke ($"✨ Новая версия: {latestVersion} (текущая: {currentSimple})");

                        if (silent || MessageBox.Show ($"Доступна версия {latestVersion}. Обновить сейчас?",
                                                      "Обновление", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                        {
                            return await DownloadAndInstall (downloadUrl, latestTag);
                        }
                    }
                    else
                    {
                        _logger?.Invoke ("⚠️ Не найден архив для скачивания.");
                    }
                }
                else
                {
                    _logger?.Invoke ("✅ Установлена актуальная версия.");
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Ошибка проверки обновлений: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> DownloadAndInstall(string downloadUrl, string newVersion)
        {
            try
            {
                _logger?.Invoke ($"📥 Загрузка обновления {newVersion}...");
                string tempZip = Path.GetTempFileName ();
                using (var resp = await _httpClient.GetAsync (downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                using (var fs = new FileStream (tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    await resp.Content.CopyToAsync (fs);
                }

                string extractPath = Path.Combine (Path.GetTempPath (), "BotUpdate_" + Guid.NewGuid ());
                Directory.CreateDirectory (extractPath);
                ZipFile.ExtractToDirectory (tempZip, extractPath);
                _logger?.Invoke ("📦 Файлы распакованы.");

                string currentExe = Environment.ProcessPath ?? Assembly.GetExecutingAssembly ().Location;
                if (string.IsNullOrEmpty (currentExe))
                    currentExe = Path.Combine (AppContext.BaseDirectory, "BinanceBotWpf.exe");
                string appDir = Path.GetDirectoryName (currentExe) ?? AppContext.BaseDirectory;
                string backupDir = Path.Combine (appDir, "Backup_" + DateTime.Now.ToString ("yyyyMMdd_HHmmss"));
                string scriptPath = CreateUpdateScript (extractPath, appDir, backupDir, currentExe);

                _logger?.Invoke ("🔄 Запуск обновления... Бот будет закрыт.");
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = scriptPath,
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        CreateNoWindow = true
                    }
                };
                process.Start ();
                await Task.Delay (2000);
                Environment.Exit (0);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Ошибка установки: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Скачивать и установить обновление по URL напрямую (без проверки версии)
        /// </summary>
        public async Task<bool> DownloadByUrlAsync(string downloadUrl, string version)
        {
            return await DownloadAndInstall (downloadUrl, version);
        }

        private string CreateUpdateScript(string sourceDir, string targetDir, string backupDir, string currentExe)
        {
            string batPath = Path.Combine (Path.GetTempPath (), "UpdateBot_" + Guid.NewGuid () + ".bat");
            string batContent = $@"
@echo off
timeout /t 3 /nobreak > nul
echo Создание резервной копии в {backupDir}
xcopy ""{targetDir}"" ""{backupDir}"" /E /I /Y /Q > nul
taskkill /f /im ""{Path.GetFileName (currentExe)}"" > nul 2>&1
echo Обновление файлов...
xcopy ""{sourceDir}\*"" ""{targetDir}"" /E /I /Y /Q > nul
echo Запуск обновлённого бота...
start "" "" ""{currentExe}""
rmdir /S /Q ""{sourceDir}""
del ""{batPath}""
";
            File.WriteAllText (batPath, batContent);
            return batPath;
        }
    }
}