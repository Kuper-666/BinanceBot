using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BinanceBotWpf.Models
{
    /// <summary>
    /// Конфигурация бота. ApiKey, ApiSecret и TelegramBotToken хранятся на диске
    /// в зашифрованном виде (Windows DPAPI, привязка к текущему пользователю Windows).
    /// Используй свойства ApiKey/ApiSecret/TelegramBotToken для чтения расшифрованных
    /// значений в памяти — на диске всегда лежит зашифрованная версия.
    /// </summary>
    public class BotConfig
    {
        // Зашифрованные значения — то, что реально сохраняется в JSON на диске
        public string ApiKeyEncrypted { get; set; } = "";
        public string ApiSecretEncrypted { get; set; } = "";
        public string TelegramBotTokenEncrypted { get; set; } = "";

        // Фьючерсные API ключи (отдельные от спотовых)
        public string FuturesApiKeyEncrypted { get; set; } = "";
        public string FuturesApiSecretEncrypted { get; set; } = "";

        // Внешние API (не обязательны, можно оставить пустыми)
        // CoinGecko: бесплатный план — 30 req/min, без ключа; Pro — до 500 req/min
        // Получить: https://www.coingecko.com/en/api
        public string CoinGeckoApiKeyEncrypted { get; set; } = "";

        // LunarCrush: требует регистрацию на https://lunarcrush.com/developers
        public string LunarCrushApiKeyEncrypted { get; set; } = "";

        public bool IsTestnet { get; set; } = true;
        public decimal MinUsdcBalance { get; set; } = 5.50m;
        public string TelegramChatId { get; set; } = "";
        
        /// <summary>
        /// Интервал свечей для анализа. Минимум 1 час.
        /// Поддерживаются: 1h (default), 4h, 1d, 1w, 1M
        /// </summary>
        public string CandleInterval { get; set; } = "1h";

        public decimal MinBalanceForZeroPercent { get; set; } = 30;
        public decimal TargetBalanceForFullPercent { get; set; } = 100;
        public decimal MaxTradePercent { get; set; } = 0.25m;
        public decimal AbsoluteMaxPercent { get; set; } = 0.30m;
        public decimal MinBalanceForRisk { get; set; } = 50;
        public decimal MaxBalanceForRisk { get; set; } = 300;
        public decimal MinRiskPercent { get; set; } = 0.10m;
        public decimal MaxRiskPercent { get; set; } = 0.30m;

        // Настройки фьючерсов
        public int FuturesLeverage { get; set; } = 5;
        public decimal FuturesMaxRiskPercent { get; set; } = 0.10m;

        // Настройки ИИ-агентов (Золотая архитектура)
        public bool AdaptiveAgentEnabled { get; set; } = true;
        public bool SignalValidatorEnabled { get; set; } = true;
        public bool NewsSentinelEnabled { get; set; } = true;
        public int NewsSentinelMaxAgeHours { get; set; } = 6;

        // Калиброванные пороги эшелонов (по результатам бэктеста)
        public decimal AdaptiveSlMultiplier { get; set; } = 0.4m;
        public decimal AdaptivePeriodMultiplier { get; set; } = 0.3m;
        public decimal ValidatorVolumeThreshold { get; set; } = 8.0m;
        public decimal ValidatorAtrThreshold { get; set; } = 0.15m;
        public int ValidatorRsiLow { get; set; } = 20;
        public int ValidatorRsiHigh { get; set; } = 80;

        // Расшифрованные значения для использования в коде (не сериализуются в JSON)
        [JsonIgnore]
        public string ApiKey
        {
            get => Services.SecureStringHelper.Decrypt (ApiKeyEncrypted);
            set => ApiKeyEncrypted = Services.SecureStringHelper.Encrypt (value);
        }

        [JsonIgnore]
        public string ApiSecret
        {
            get => Services.SecureStringHelper.Decrypt (ApiSecretEncrypted);
            set => ApiSecretEncrypted = Services.SecureStringHelper.Encrypt (value);
        }

        [JsonIgnore]
        public string FuturesApiKey
        {
            get => Services.SecureStringHelper.Decrypt (FuturesApiKeyEncrypted);
            set => FuturesApiKeyEncrypted = Services.SecureStringHelper.Encrypt (value);
        }

        [JsonIgnore]
        public string FuturesApiSecret
        {
            get => Services.SecureStringHelper.Decrypt (FuturesApiSecretEncrypted);
            set => FuturesApiSecretEncrypted = Services.SecureStringHelper.Encrypt (value);
        }

        [JsonIgnore]
        public string TelegramBotToken
        {
            get => Services.SecureStringHelper.Decrypt (TelegramBotTokenEncrypted);
            set => TelegramBotTokenEncrypted = Services.SecureStringHelper.Encrypt (value);
        }

        [JsonIgnore]
        public string CoinGeckoApiKey
        {
            get => Services.SecureStringHelper.Decrypt (CoinGeckoApiKeyEncrypted);
            set => CoinGeckoApiKeyEncrypted = Services.SecureStringHelper.Encrypt (value);
        }

        [JsonIgnore]
        public string LunarCrushApiKey
        {
            get => Services.SecureStringHelper.Decrypt (LunarCrushApiKeyEncrypted);
            set => LunarCrushApiKeyEncrypted = Services.SecureStringHelper.Encrypt (value);
        }

        private static readonly JsonSerializerOptions JsonOptions = new () { WriteIndented = true };

        public static string GetConfigPath() =>
            Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "config.json");

        public static string GetLegacyConfigPath() =>
            Path.Combine (AppDomain.CurrentDomain.BaseDirectory, "config.txt");

        /// <summary>
        /// Загружает конфигурацию: если есть новый config.json — читает его.
        /// Если есть только старый config.txt — мигрирует значения в зашифрованный
        /// config.json и переименовывает старый файл в config.txt.bak (как напоминание
        /// удалить его, поскольку он хранит ключи в открытом виде).
        /// Возвращает null, если ни одного конфига не найдено (первый запуск).
        /// </summary>
        public static BotConfig LoadOrMigrate(out bool wasMigrated)
        {
            wasMigrated = false;
            string jsonPath = GetConfigPath ();
            string legacyPath = GetLegacyConfigPath ();

            if (File.Exists (jsonPath))
            {
                string json = File.ReadAllText (jsonPath);
                var config = JsonSerializer.Deserialize<BotConfig> (json);
                if (config == null) return new BotConfig ();

                // Если пользователь вручную отредактировал config.json и вписал ключи открытым
                // текстом (без префикса ENC:) — перешифровываем и сохраняем сразу при загрузке,
                // чтобы открытый текст не оставался на диске дольше одного запуска.
                bool needsReencryption =
                    HasPlainTextValue (config.ApiKeyEncrypted) ||
                    HasPlainTextValue (config.ApiSecretEncrypted) ||
                    HasPlainTextValue (config.TelegramBotTokenEncrypted);

                if (needsReencryption)
                {
                    // Чтение через свойства ApiKey/ApiSecret/TelegramBotToken расшифровывает
                    // ENC:-значения как есть и возвращает открытый текст без изменений —
                    // присваивание обратно тем же свойствам гарантированно зашифрует всё через DPAPI.
                    config.ApiKey = config.ApiKey;
                    config.ApiSecret = config.ApiSecret;
                    config.TelegramBotToken = config.TelegramBotToken;
                    config.Save ();
                }

                return config;
            }

            if (File.Exists (legacyPath))
            {
                var config = MigrateFromLegacyTxt (legacyPath);
                config.Save ();

                // Переименовываем старый файл, чтобы ключи в открытом виде не остались на диске,
                // но не удаляем безвозвратно — на случай если миграция прошла некорректно
                try
                {
                    string bakPath = legacyPath + ".bak";
                    if (File.Exists (bakPath)) File.Delete (bakPath);
                    File.Move (legacyPath, bakPath);
                }
                catch
                {
                    // Если переименовать не удалось (например, файл занят) — не критично,
                    // конфигурация уже успешно сохранена в зашифрованном config.json
                }

                wasMigrated = true;
                return config;
            }

            return null;
        }

        private static bool HasPlainTextValue(string value) =>
            !string.IsNullOrEmpty (value) && !value.StartsWith ("ENC:") && value != "YOUR_API_KEY_HERE" && value != "YOUR_API_SECRET_HERE";

        private static BotConfig MigrateFromLegacyTxt(string legacyPath)
        {
            var config = new BotConfig ();
            var lines = File.ReadAllLines (legacyPath);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace (line) || line.StartsWith ("#") || !line.Contains ("=")) continue;
                var parts = line.Split ('=', 2);
                string key = parts[0].Trim ().ToLower ();
                string value = parts[1].Trim ();
                if (string.IsNullOrEmpty (value)) continue;

                switch (key)
                {
                    case "apikey": config.ApiKey = value; break;
                    case "apisecret": config.ApiSecret = value; break;
                    case "istestnet": config.IsTestnet = bool.Parse (value); break;
                    case "minusdcbalance": config.MinUsdcBalance = decimal.Parse (value, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "telegrambottoken": config.TelegramBotToken = value; break;
                    case "telegramchatid": config.TelegramChatId = value; break;
                    case "coingecooapikey":
                    case "coingeckoapikey": config.CoinGeckoApiKey = value; break;
                    case "lunarcrushapikey": config.LunarCrushApiKey = value; break;
                    case "minbalanceforzeropercent": config.MinBalanceForZeroPercent = decimal.Parse (value, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "targetbalanceforfullpercent": config.TargetBalanceForFullPercent = decimal.Parse (value, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "maxtradepercent": config.MaxTradePercent = decimal.Parse (value, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "absolutemaxpercent": config.AbsoluteMaxPercent = decimal.Parse (value, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "minbalanceforrisk": config.MinBalanceForRisk = decimal.Parse (value, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "maxbalanceforrisk": config.MaxBalanceForRisk = decimal.Parse (value, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "minriskpercent": config.MinRiskPercent = decimal.Parse (value, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "maxriskpercent": config.MaxRiskPercent = decimal.Parse (value, System.Globalization.CultureInfo.InvariantCulture); break;
                }
            }

            return config;
        }

        public void Save()
        {
            string json = JsonSerializer.Serialize (this, JsonOptions);
            string configPath = GetConfigPath ();
            string tempPath = configPath + ".tmp";
            File.WriteAllText (tempPath, json);
            File.Move (tempPath, configPath, overwrite: true);
        }

        public static void CreateDefault()
        {
            var config = new BotConfig
            {
                ApiKey = "YOUR_API_KEY_HERE",
                ApiSecret = "YOUR_API_SECRET_HERE",
                IsTestnet = false
            };
            config.Save ();
        }
    }
}
