using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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

        public UpdateManager(Action<string> logger)
        {
            _logger = logger;
            _httpClient.DefaultRequestHeaders.Add ("User-Agent", "BinanceTradingBot");
        }

        public async Task<bool> CheckAndUpdateAsync(bool silent = false)
        {
            try
            {
                _logger ("🔍 Проверка обновлений...");
                string apiUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
                string json = await _httpClient.GetStringAsync (apiUrl);
                var release = JObject.Parse (json);
                string latestTag = release["tag_name"]?.ToString ()?.TrimStart ('v') ?? "0.0.0";
                Version latestVersion = new Version (latestTag);
                string downloadUrl = release["assets"]?[0]?["browser_download_url"]?.ToString ();

                if (latestVersion > CurrentVersion && !string.IsNullOrEmpty (downloadUrl))
                {
                    _logger ($"✨ Новая версия: {latestVersion} (текущая: {CurrentVersion})");
                    if (silent || MessageBox.Show ($"Доступна версия {latestVersion}. Обновить сейчас?",
                                                  "Обновление", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        return await DownloadAndInstall (downloadUrl, latestTag);
                    }
                }
                else
                {
                    _logger ("✅ Установлена актуальная версия.");
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger ($"❌ Ошибка проверки обновлений: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> DownloadAndInstall(string downloadUrl, string newVersion)
        {
            try
            {
                _logger ($"📥 Загрузка обновления {newVersion}...");
                string tempZip = Path.GetTempFileName ();
                using (var resp = await _httpClient.GetAsync (downloadUrl, HttpCompletionOption.ResponseHeadersRead))
                using (var fs = new FileStream (tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    await resp.Content.CopyToAsync (fs);
                }

                string extractPath = Path.Combine (Path.GetTempPath (), "BotUpdate_" + Guid.NewGuid ());
                Directory.CreateDirectory (extractPath);
                ZipFile.ExtractToDirectory (tempZip, extractPath);
                _logger ("📦 Файлы распакованы.");

                string currentExe = Assembly.GetExecutingAssembly ().Location;
                string appDir = Path.GetDirectoryName (currentExe);
                string backupDir = Path.Combine (appDir, "Backup_" + DateTime.Now.ToString ("yyyyMMdd_HHmmss"));
                string scriptPath = CreateUpdateScript (extractPath, appDir, backupDir, currentExe);

                _logger ("🔄 Запуск обновления... Бот будет закрыт.");
                Process.Start (new ProcessStartInfo
                {
                    FileName = scriptPath,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true
                });
                Application.Current.Shutdown ();
                return true;
            }
            catch (Exception ex)
            {
                _logger ($"❌ Ошибка установки: {ex.Message}");
                return false;
            }
        }

        private string CreateUpdateScript(string sourceDir, string targetDir, string backupDir, string currentExe)
        {
            string batPath = Path.Combine (Path.GetTempPath (), "UpdateBot_" + Guid.NewGuid () + ".bat");
            string batContent = $@"
@echo off
timeout /t 2 /nobreak > nul
echo Создание резервной копии в {backupDir}
xcopy ""{targetDir}"" ""{backupDir}"" /E /I /Y /Q > nul
echo Обновление файлов...
xcopy ""{sourceDir}\*"" ""{targetDir}"" /E /I /Y /Q > nul
echo Запуск обновлённого бота...
start "" "" ""{currentExe}""
echo Очистка...
rmdir /S /Q ""{sourceDir}""
del ""{batPath}""
";
            File.WriteAllText (batPath, batContent);
            return batPath;
        }
    }
}