using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BinanceBotWpf.Services
{
    public enum AuthResult
    {
        Success,
        InvalidCode,
        Expired,
        RateLimited
    }

    public class TelegramAuthenticator
    {
        private readonly TelegramNotifier _telegram;
        private readonly Action<string> _logger;

        private string _codeHash;
        private DateTime _codeGeneratedAt;
        private int _attemptCount;
        private const int MaxAttempts = 3;
        private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes (5);

        private readonly object _lock = new ();
        private Timer _expiryTimer;

        public bool IsAuthenticated { get; private set; }
        public bool IsCodePending => !IsAuthenticated && _codeHash != null;

        public event Action OnAuthRequired;
        public event Action OnAuthSuccess;
        public event Action<string> OnAuthFailed;
        public event Action OnAuthExpired;

        public TelegramAuthenticator (TelegramNotifier telegram, Action<string> logger)
        {
            _telegram = telegram;
            _logger = logger;
        }

        public async Task GenerateAndSendCodeAsync ()
        {
            if (IsAuthenticated) return;

            string code = GenerateSecureCode ();
            _codeHash = HashCode (code);
            _codeGeneratedAt = DateTime.UtcNow;
            _attemptCount = 0;

            string message = $"🔐 <b>Код подтверждения</b>\n\n" +
                             $"Ваш код: <code>{code}</code>\n\n" +
                             $"⏱ Действителен {CodeLifetime.Minutes} мин.\n" +
                             $"🔑 Введите его в приложении для разблокировки.";

            await _telegram.SendMessageAsync (message);
            _logger?.Invoke ($"Auth: код отправлен в Telegram");

            StartExpiryTimer ();
            OnAuthRequired?.Invoke ();
        }

        public AuthResult ValidateCode (string input)
        {
            if (IsAuthenticated) return AuthResult.Success;
            if (_codeHash == null) return AuthResult.Expired;

            lock (_lock)
            {
                if (DateTime.UtcNow - _codeGeneratedAt > CodeLifetime)
                {
                    ClearCode ();
                    OnAuthExpired?.Invoke ();
                    return AuthResult.Expired;
                }

                if (_attemptCount >= MaxAttempts)
                {
                    ClearCode ();
                    OnAuthExpired?.Invoke ();
                    return AuthResult.RateLimited;
                }

                _attemptCount++;

                string inputHash = HashCode (input?.Trim () ?? "");
                if (CryptographicOperations.FixedTimeEquals (
                        Encoding.UTF8.GetBytes (inputHash),
                        Encoding.UTF8.GetBytes (_codeHash)))
                {
                    IsAuthenticated = true;
                    StopExpiryTimer ();
                    _logger?.Invoke ("Auth: аутентификация успешна");
                    OnAuthSuccess?.Invoke ();
                    return AuthResult.Success;
                }

                int remaining = MaxAttempts - _attemptCount;
                _logger?.Invoke ($"Auth: неверный код, осталось попыток {remaining}");
                OnAuthFailed?.Invoke ($"Неверный код. Осталось попыток: {remaining}");
                return AuthResult.InvalidCode;
            }
        }

        public void Reset ()
        {
            lock (_lock)
            {
                IsAuthenticated = false;
                ClearCode ();
                StopExpiryTimer ();
            }
        }

        private string GenerateSecureCode ()
        {
            byte[] bytes = new byte[4];
            RandomNumberGenerator.Fill (bytes);
            int number = BitConverter.ToInt32 (bytes, 0) % 1_000_000;
            if (number < 0) number += 1_000_000;
            return number.ToString ("D6");
        }

        private static string HashCode (string code)
        {
            byte[] bytes = Encoding.UTF8.GetBytes (code);
            byte[] hash = SHA256.HashData (bytes);
            return Convert.ToHexString (hash);
        }

        private void ClearCode ()
        {
            _codeHash = null;
            _attemptCount = 0;
        }

        private void StartExpiryTimer ()
        {
            StopExpiryTimer ();
            _expiryTimer = new Timer (OnCodeExpired, null, CodeLifetime, Timeout.InfiniteTimeSpan);
        }

        private void StopExpiryTimer ()
        {
            _expiryTimer?.Dispose ();
            _expiryTimer = null;
        }

        private void OnCodeExpired (object state)
        {
            if (IsAuthenticated) return;
            lock (_lock)
            {
                if (IsAuthenticated) return;
                ClearCode ();
                _logger?.Invoke ("Auth: код истёк");
                OnAuthExpired?.Invoke ();
            }
        }
    }
}
