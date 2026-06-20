using System;
using System.IO;
using BinanceBotWpf.Models;
using BinanceBotWpf.Services;
using Xunit;

namespace BinanceBotWpf.Tests
{
    /// <summary>
    /// Тесты работают с реальным Windows DPAPI (CI запускается на windows-latest),
    /// поэтому шифрование/расшифровку проверяем по-настоящему, а не через моки.
    /// Каждый тест использует свою временную директорию, чтобы не зависеть друг от друга
    /// и не трогать config.json самого тестового раннера.
    /// </summary>
    public class BotConfigTests : IDisposable
    {
        private readonly string _testDir;
        private readonly string _originalBaseDirectory;

        public BotConfigTests()
        {
            _testDir = Path.Combine (Path.GetTempPath (), "BinanceBotTests_" + Guid.NewGuid ().ToString ("N"));
            Directory.CreateDirectory (_testDir);
        }

        public void Dispose()
        {
            try { Directory.Delete (_testDir, recursive: true); } catch { /* best effort cleanup */ }
        }

        [Fact]
        public void SecureStringHelper_EncryptThenDecrypt_ReturnsOriginalValue()
        {
            string original = "super-secret-api-key-12345";

            string encrypted = SecureStringHelper.Encrypt (original);
            string decrypted = SecureStringHelper.Decrypt (encrypted);

            Assert.Equal (original, decrypted);
        }

        [Fact]
        public void SecureStringHelper_EncryptedValue_DoesNotContainPlainText()
        {
            string original = "super-secret-api-key-12345";

            string encrypted = SecureStringHelper.Encrypt (original);

            Assert.DoesNotContain (original, encrypted);
            Assert.StartsWith ("ENC:", encrypted);
        }

        [Fact]
        public void SecureStringHelper_Decrypt_OnPlainTextWithoutPrefix_ReturnsAsIs()
        {
            // Защита от двойного шифрования и от падения на старых/ручных значениях
            string plain = "not-encrypted-value";

            string result = SecureStringHelper.Decrypt (plain);

            Assert.Equal (plain, result);
        }

        [Fact]
        public void SecureStringHelper_EncryptDecrypt_HandlesEmptyAndNull()
        {
            Assert.Equal ("", SecureStringHelper.Encrypt (""));
            Assert.Null (SecureStringHelper.Encrypt (null));
            Assert.Equal ("", SecureStringHelper.Decrypt (""));
            Assert.Null (SecureStringHelper.Decrypt (null));
        }

        [Fact]
        public void BotConfig_ApiKeyProperty_RoundTripsThroughEncryption()
        {
            var config = new BotConfig ();

            config.ApiKey = "my-real-api-key";

            // На диске/в сериализованном виде должно быть зашифровано...
            Assert.StartsWith ("ENC:", config.ApiKeyEncrypted);
            Assert.DoesNotContain ("my-real-api-key", config.ApiKeyEncrypted);

            // ...но при чтении через свойство возвращается оригинал
            Assert.Equal ("my-real-api-key", config.ApiKey);
        }

        [Fact]
        public void BotConfig_SaveAndLoad_PreservesDecryptedValues()
        {
            var config = new BotConfig
            {
                ApiKey = "test-api-key",
                ApiSecret = "test-api-secret",
                TelegramBotToken = "123456:ABC-token",
                TelegramChatId = "987654321",
                IsTestnet = true,
                MinUsdcBalance = 12.34m
            };

            string jsonPath = Path.Combine (_testDir, "config.json");
            string json = System.Text.Json.JsonSerializer.Serialize (config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText (jsonPath, json);

            var loaded = System.Text.Json.JsonSerializer.Deserialize<BotConfig> (File.ReadAllText (jsonPath));

            Assert.Equal ("test-api-key", loaded.ApiKey);
            Assert.Equal ("test-api-secret", loaded.ApiSecret);
            Assert.Equal ("123456:ABC-token", loaded.TelegramBotToken);
            Assert.Equal ("987654321", loaded.TelegramChatId);
            Assert.True (loaded.IsTestnet);
            Assert.Equal (12.34m, loaded.MinUsdcBalance);
        }

        [Fact]
        public void BotConfig_SerializedJson_NeverContainsPlainTextSecrets()
        {
            // Самое важное свойство: что бы ни случилось, секреты не должны утечь
            // в файл на диске в читаемом виде.
            var config = new BotConfig
            {
                ApiKey = "TOP-SECRET-KEY-VALUE",
                ApiSecret = "TOP-SECRET-SECRET-VALUE",
                TelegramBotToken = "TOP-SECRET-TG-TOKEN"
            };

            string json = System.Text.Json.JsonSerializer.Serialize (config);

            Assert.DoesNotContain ("TOP-SECRET-KEY-VALUE", json);
            Assert.DoesNotContain ("TOP-SECRET-SECRET-VALUE", json);
            Assert.DoesNotContain ("TOP-SECRET-TG-TOKEN", json);
        }

        [Fact]
        public void BotConfig_MigrateFromLegacyTxt_ParsesAllKnownFields()
        {
            string legacyContent =
                "# comment line, should be skipped\n" +
                "ApiKey=legacy-api-key\n" +
                "ApiSecret=legacy-api-secret\n" +
                "isTestnet=true\n" +
                "minUsdcBalance=7.25\n" +
                "telegramBotToken=legacy-tg-token\n" +
                "telegramChatId=111222333\n";

            string legacyPath = Path.Combine (_testDir, "config.txt");
            File.WriteAllText (legacyPath, legacyContent);

            // Используем рефлексию для вызова приватного MigrateFromLegacyTxt напрямую,
            // чтобы не зависеть от AppDomain.CurrentDomain.BaseDirectory в тестовом окружении
            var method = typeof (BotConfig).GetMethod ("MigrateFromLegacyTxt",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var migrated = (BotConfig)method.Invoke (null, new object[] { legacyPath });

            Assert.Equal ("legacy-api-key", migrated.ApiKey);
            Assert.Equal ("legacy-api-secret", migrated.ApiSecret);
            Assert.True (migrated.IsTestnet);
            Assert.Equal (7.25m, migrated.MinUsdcBalance);
            Assert.Equal ("legacy-tg-token", migrated.TelegramBotToken);
            Assert.Equal ("111222333", migrated.TelegramChatId);
        }

        [Fact]
        public void BotConfig_MigrateFromLegacyTxt_SkipsCommentsAndEmptyLines()
        {
            string legacyContent =
                "# Binance Bot Configuration\n" +
                "\n" +
                "ApiKey=key123\n" +
                "ApiSecret=secret123\n" +
                "\n" +
                "# другие параметры\n" +
                "isTestnet=false\n";

            string legacyPath = Path.Combine (_testDir, "config.txt");
            File.WriteAllText (legacyPath, legacyContent);

            var method = typeof (BotConfig).GetMethod ("MigrateFromLegacyTxt",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var migrated = (BotConfig)method.Invoke (null, new object[] { legacyPath });

            Assert.Equal ("key123", migrated.ApiKey);
            Assert.Equal ("secret123", migrated.ApiSecret);
            Assert.False (migrated.IsTestnet);
        }
    }
}
