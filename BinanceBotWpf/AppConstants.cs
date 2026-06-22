using System.Reflection;

namespace BinanceBotWpf
{
    /// <summary>
    /// Константы приложения.
    /// </summary>
    public static class AppConstants
    {
        /// <summary>
        /// Текущая версия приложения.
        /// Автоматически берётся из VersionPrefix в .csproj.
        /// </summary>
        public static string AppVersion =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        /// <summary>
        /// GitHub владелец репозитория для проверки обновлений.
        /// </summary>
        public const string GitHubOwner = "Kuper-666";

        /// <summary>
        /// GitHub название репозитория для проверки обновлений.
        /// </summary>
        public const string GitHubRepo = "BinanceBot";

        /// <summary>
        /// URL для скачивания последней версии.
        /// </summary>
        public static string GetLatestReleaseUrl() => 
            $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest";
    }
}
