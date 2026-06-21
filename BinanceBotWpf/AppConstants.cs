namespace BinanceBotWpf
{
    /// <summary>
    /// Константы приложения.
    /// </summary>
    public static class AppConstants
    {
        /// <summary>
        /// Текущая версия приложения.
        /// Обновляется после каждого релиза на GitHub.
        /// Формат: X.Y.Z (Semantic Versioning)
        /// </summary>
        public const string AppVersion = "1.0.175";

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
