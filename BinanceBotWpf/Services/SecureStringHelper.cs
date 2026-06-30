using System;
using System.Security.Cryptography;
using System.Text;

namespace BinanceBotWpf.Services
{
    public static class SecureStringHelper
    {
        // Для DPAPI требуется ссылка на System.Security.Cryptography.ProtectedData
        // Добавьте NuGet: System.Security.Cryptography.ProtectedData

        public static string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty (plainText))
                return plainText;
            byte[] plainBytes = Encoding.UTF8.GetBytes (plainText);
            byte[] encryptedBytes = ProtectedData.Protect (plainBytes, null, DataProtectionScope.CurrentUser);
            return "ENC:" + Convert.ToBase64String (encryptedBytes);
        }

        public static string Decrypt(string encryptedText)
        {
            if (string.IsNullOrEmpty (encryptedText))
                return encryptedText;
            if (!encryptedText.StartsWith ("ENC:"))
                return encryptedText; // не зашифровано
            try
            {
                string base64 = encryptedText.Substring (4);
                byte[] encryptedBytes = Convert.FromBase64String (base64);
                byte[] plainBytes = ProtectedData.Unprotect (encryptedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString (plainBytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine ($"SecureStringHelper.Decrypt error: {ex.Message}");
                return encryptedText;
            }
        }
    }
}