using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    /// <summary>
    /// Проверяет доступность новых версий бота на GitHub.
    /// Сравнивает текущую версию с последним release.
    /// </summary>
    public class UpdateChecker
    {
        private readonly HttpClient _httpClient;
        private readonly Action<string> _logger;
        private readonly Func<string, Task> _notifyTelegram; // для отправки уведомлений в Telegram
        
        private const string GitHubApiUrl = "https://api.github.com/repos";

        /// <summary>Событие для UI: новая версия доступна</summary>
        public event Action<string, string> OnNewVersionAvailable; // (newVersion, downloadUrl)

        public UpdateChecker(HttpClient httpClient, Action<string> logger, Func<string, Task> notifyTelegram = null)
        {
            _httpClient = httpClient;
            _logger = logger;
            _notifyTelegram = notifyTelegram;
            
            // Настройки для GitHub API
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "BinanceBotWpf");
        }

        /// <summary>
        /// Проверяет есть ли новая версия на GitHub.
        /// Сравнивает текущую версию с последним release.
        /// Если currentVersion не указана, использует AppConstants.AppVersion.
        /// </summary>
        public async Task CheckForUpdatesAsync(string currentVersion = null)
        {
            if (string.IsNullOrEmpty(currentVersion))
                currentVersion = AppConstants.AppVersion;

            try
            {
                string url = $"{GitHubApiUrl}/{AppConstants.GitHubOwner}/{AppConstants.GitHubRepo}/releases/latest";
                var response = await _httpClient.GetAsync (url);

                if (!response.IsSuccessStatusCode)
                {
                    _logger?.Invoke ($"⚠️ Не удалось проверить обновления: HTTP {response.StatusCode}");
                    return;
                }

                string json = await response.Content.ReadAsStringAsync ();
                using var doc = JsonDocument.Parse (json);
                var root = doc.RootElement;

                string latestVersion = root.GetProperty ("tag_name").GetString ();
                string releasePageUrl = root.GetProperty ("html_url").GetString ();
                string releaseName = root.GetProperty ("name").GetString ();
                string releaseBody = root.TryGetProperty ("body", out var bodyElement)
                    ? bodyElement.GetString ()
                    : "";

                // Берём URL zip-файла из assets (а не ссылку на страницу релиза)
                string downloadUrl = releasePageUrl;
                if (root.TryGetProperty ("assets", out var assets) && assets.GetArrayLength () > 0)
                {
                    string assetUrl = assets[0].GetProperty ("browser_download_url").GetString ();
                    if (!string.IsNullOrEmpty (assetUrl))
                        downloadUrl = assetUrl;
                }

                // Убираем 'v' префикс если есть для сравнения
                latestVersion = latestVersion.TrimStart ('v');
                currentVersion = currentVersion.TrimStart ('v');

                if (IsNewerVersion (latestVersion, currentVersion))
                {
                    string updateMessage = $"🎉 Доступна новая версия: {latestVersion}\n" +
                                         $"Текущая версия: {currentVersion}\n\n" +
                                         $"📝 {releaseName}\n\n" +
                                         $"Скачать: {downloadUrl}";

                    _logger?.Invoke (updateMessage);

                    // Уведомить в Telegram если подключен
                    if (_notifyTelegram != null)
                    {
                        try
                        {
                            await _notifyTelegram ($"🎉 *BinanceBot обновление доступно!*\n\n" +
                                                 $"Новая версия: `{latestVersion}`\n" +
                                                 $"Текущая: `{currentVersion}`\n\n" +
                                                 $"[Скачать обновление]({downloadUrl})");
                        }
                        catch (Exception ex)
                        {
                            _logger?.Invoke ($"⚠️ Не удалось отправить Telegram уведомление: {ex.Message}");
                        }
                    }

                    // Триггер события для UI
                    OnNewVersionAvailable?.Invoke (latestVersion, downloadUrl);
                }
                else
                {
                    _logger?.Invoke ($"✅ Установлена актуальная версия {currentVersion}");
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke ($"❌ Ошибка проверки обновлений: {ex.Message}");
            }
        }

        /// <summary>
        /// Сравнивает две версии в формате X.Y.Z.
        /// Возвращает true если newVersion > currentVersion.
        /// </summary>
        private bool IsNewerVersion(string newVersion, string currentVersion)
        {
            try
            {
                var parts1 = newVersion.Split ('.');
                var parts2 = currentVersion.Split ('.');

                for (int i = 0; i < Math.Max (parts1.Length, parts2.Length); i++)
                {
                    int v1 = i < parts1.Length && int.TryParse (parts1[i], out int val1) ? val1 : 0;
                    int v2 = i < parts2.Length && int.TryParse (parts2[i], out int val2) ? val2 : 0;

                    if (v1 > v2) return true;
                    if (v1 < v2) return false;
                }

                return false; // Версии одинаковые
            }
            catch
            {
                return false;
            }
        }
    }
}
