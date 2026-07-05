using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
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
        private readonly HttpClient _httpClient = SharedHttpClient.Instance;
        private readonly Action<string> _logger;
        private DateTime _lastUpdateCheckDate = DateTime.MinValue;
        private static readonly TimeSpan MinCheckInterval = TimeSpan.FromHours (1);

        public UpdateManager(Action<string> logger)
        {
            _logger = logger;
            if (!_httpClient.DefaultRequestHeaders.Contains ("User-Agent"))
                _httpClient.DefaultRequestHeaders.Add ("User-Agent", "BinanceTradingBot/1.0");
            if (!_httpClient.DefaultRequestHeaders.Contains ("Accept"))
                _httpClient.DefaultRequestHeaders.Add ("Accept", "application/vnd.github.v3+json");
        }

        public async Task<bool> CheckAndUpdateAsync (bool silent = false, bool hasOpenPositions = false)
        {
            // Не проверяем чаще раза в час
            if (DateTime.UtcNow - _lastUpdateCheckDate < MinCheckInterval)
            {
                _logger?.Invoke ($"⏭️ Следующая проверка обновлений через {( MinCheckInterval - ( DateTime.UtcNow - _lastUpdateCheckDate ) ).Minutes} мин");
                return false;
            }
            _lastUpdateCheckDate = DateTime.UtcNow;

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

                // Фильтруем: только публичные релизы (не draft, не prerelease)
                var stableReleases = releases
                    .Where (r => r["draft"]?.Value<bool> () == false && r["prerelease"]?.Value<bool> () == false)
                    .OrderByDescending (r => r["published_at"]?.Value<DateTime> () ?? DateTime.MinValue)
                    .ToList ();

                if (stableReleases.Count == 0)
                {
                    _logger?.Invoke ("⚠️ Стабильные релизы не найдены.");
                    return false;
                }

                var latestRelease = stableReleases.First () as JObject;
                string latestTag = latestRelease["tag_name"]?.ToString () ?? "v0.0.0";
                string latestVersionStr = latestTag.TrimStart ('v');

                if (!Version.TryParse (latestVersionStr, out Version latestVersion))
                {
                    _logger?.Invoke ($"⚠️ Не удалось распарсить версию: {latestTag}");
                    return false;
                }

                Version currentVersion = Assembly.GetExecutingAssembly ().GetName ().Version ?? new Version ("1.0.0");
                var currentSimple = new Version (currentVersion.Major, currentVersion.Minor,
                    currentVersion.Build >= 0 ? currentVersion.Build : 0);

                if (latestVersion <= currentSimple)
                {
                    _logger?.Invoke ("✅ Установлена актуальная версия.");
                    return false;
                }

                // Ищем архив по имени файла (не assets[0])
                var assets = latestRelease["assets"] as JArray;
                if (assets == null || assets.Count == 0)
                {
                    _logger?.Invoke ("⚠️ Не найдены ассеты для скачивания.");
                    return false;
                }

                var zipAsset = assets.FirstOrDefault (a =>
                {
                    string name = a["name"]?.ToString () ?? "";
                    return name.EndsWith (".zip", StringComparison.OrdinalIgnoreCase)
                        && name.Contains ("BinanceBot", StringComparison.OrdinalIgnoreCase);
                });

                if (zipAsset == null)
                {
                    // Фолбэк: берём первый .zip файл
                    zipAsset = assets.FirstOrDefault (a =>
                        ( a["name"]?.ToString () ?? "" ).EndsWith (".zip", StringComparison.OrdinalIgnoreCase));
                }

                if (zipAsset == null)
                {
                    _logger?.Invoke ("⚠️ ZIP-архив не найден среди ассетов.");
                    return false;
                }

                string downloadUrl = zipAsset["browser_download_url"]?.ToString ();
                if (string.IsNullOrEmpty (downloadUrl))
                {
                    _logger?.Invoke ("⚠️ URL для скачивания пуст.");
                    return false;
                }

                _logger?.Invoke ($"✨ Новая версия: {latestVersion} (текущая: {currentSimple})");

                // Проверка открытых позиций перед тихим обновлением
                if (silent && hasOpenPositions)
                {
                    _logger?.Invoke ("⚠️ Есть открытые позиции. Обновление отложено.");
                    return false;
                }

                if (silent || MessageBox.Show ($"Доступна версия {latestVersion}. Обновить сейчас?",
                                              "Обновление", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    return await DownloadAndInstall (downloadUrl, latestTag, latestRelease);
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Ошибка проверки обновлений: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> DownloadAndInstall (string downloadUrl, string newVersion, JObject release = null)
        {
            try
            {
                _logger?.Invoke ($"📥 Загрузка обновления {newVersion}...");
                string tempZip = Path.ChangeExtension (Path.GetTempFileName (), ".zip");

                using (var resp = await _httpClient.GetAsync (downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger?.Invoke ($"❌ Сервер вернул ошибку: {resp.StatusCode}");
                        return false;
                    }

                    using (var fs = new FileStream (tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        await resp.Content.CopyToAsync (fs);
                    }

                    // Проверка ZIP-заголовка
                    byte[] header = new byte[2];
                    using (var fs = File.OpenRead (tempZip))
                    {
                        if (fs.Read (header, 0, 2) < 2 || header[0] != 0x50 || header[1] != 0x4B)
                        {
                            _logger?.Invoke ("❌ Скачанный файл не является ZIP.");
                            try { File.Delete (tempZip); } catch { }
                            return false;
                        }
                    }
                }

                // Проверка SHA256 если доступен .sha256 файл в релизе
                if (release != null)
                {
                    var sha256Asset = ( release["assets"] as JArray )?.FirstOrDefault (a =>
                        ( a["name"]?.ToString () ?? "" ).EndsWith (".sha256", StringComparison.OrdinalIgnoreCase));

                    if (sha256Asset != null)
                    {
                        string sha256Url = sha256Asset["browser_download_url"]?.ToString ();
                        if (!string.IsNullOrEmpty (sha256Url))
                        {
                            try
                            {
                                string expectedHash = ( await _httpClient.GetStringAsync (sha256Url) ).Trim ();
                                string actualHash = ComputeSha256 (tempZip);
                                if (!string.Equals (expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                                {
                                    _logger?.Invoke ($"❌ SHA256 не совпадает! Ожидалось: {expectedHash}, получено: {actualHash}");
                                    try { File.Delete (tempZip); } catch { }
                                    return false;
                                }
                                _logger?.Invoke ("✅ SHA256 проверка пройдена.");
                            }
                            catch (Exception ex)
                            {
                                _logger?.Invoke ($"⚠️ Не удалось проверить SHA256: {ex.Message}. Продолжаем...");
                            }
                        }
                    }
                    else
                    {
                        _logger?.Invoke ("ℹ️ SHA256 файл не найден в релизе, проверка пропущена.");
                    }
                }

                string extractPath = Path.Combine (Path.GetTempPath (), "BotUpdate_" + Guid.NewGuid ());
                Directory.CreateDirectory (extractPath);
                ZipFile.ExtractToDirectory (tempZip, extractPath);
                _logger?.Invoke ("📦 Файлы распакованы.");

                string currentExe = Environment.ProcessPath;
                if (string.IsNullOrEmpty (currentExe))
                    currentExe = Assembly.GetExecutingAssembly ().Location;
                if (string.IsNullOrEmpty (currentExe) || !File.Exists (currentExe))
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
                System.Windows.Application.Current?.Dispatcher.Invoke (() =>
                {
                    System.Windows.Application.Current.Shutdown ();
                });
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Ошибка установки: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> DownloadByUrlAsync (string downloadUrl, string version)
        {
            return await DownloadAndInstall (downloadUrl, version);
        }

        private string CreateUpdateScript (string sourceDir, string targetDir, string backupDir, string currentExe)
        {
            string batPath = Path.Combine (Path.GetTempPath (), "UpdateBot_" + Guid.NewGuid () + ".bat");
            string batContent = $@"
@echo off
timeout /t 3 /nobreak > nul
taskkill /f /im ""{Path.GetFileName (currentExe)}"" > nul 2>&1
timeout /t 2 /nobreak > nul
echo Создение резервной копии...
if not exist ""{backupDir}"" mkdir ""{backupDir}""
xcopy ""{targetDir}\*"" ""{backupDir}"" /E /I /Y /Q > nul 2>&1
echo Обновление файлов...
xcopy ""{sourceDir}\*"" ""{targetDir}"" /E /I /Y /Q > nul
if %errorlevel% neq 0 (
    echo Ошибка копирования! Откат из резервной копии...
    xcopy ""{backupDir}\*"" ""{targetDir}"" /E /I /Y /Q > nul 2>&1
    echo Откат выполнен. Запуск предыдущей версии...
) else (
    echo Обновление успешно.
)
echo Запуск бота...
start "" "" ""{currentExe}""
timeout /t 2 /nobreak > nul
rmdir /S /Q ""{sourceDir}"" > nul 2>&1
del ""{batPath}"" > nul 2>&1
";
            File.WriteAllText (batPath, batContent);
            return batPath;
        }

        private static string ComputeSha256 (string filePath)
        {
            using var sha256 = SHA256.Create ();
            using var stream = File.OpenRead (filePath);
            byte[] hash = sha256.ComputeHash (stream);
            return Convert.ToHexString (hash);
        }
    }
}
