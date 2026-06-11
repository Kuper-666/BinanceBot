using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace BinanceBotWpf.Services
{
    public class TelegramNotifier
    {
        private readonly TelegramBotClient _botClient;
        private readonly string _chatId;
        private bool _enabled;                 // <-- убран readonly
        private Func<string, string, Task> _commandHandler;
        private CancellationTokenSource _cts;
        public string GetChatId() => _chatId;

        private readonly ConcurrentDictionary<string, DateTime> _lastCommandTime = new ();
        private readonly TimeSpan _commandCooldown = TimeSpan.FromSeconds (2);

        public bool IsEnabled => _enabled;

        public TelegramNotifier(string botToken, string chatId)
        {
            _botClient = new TelegramBotClient (botToken);
            _chatId = chatId;
            _enabled = !string.IsNullOrEmpty (botToken) && !string.IsNullOrEmpty (chatId);

            if (_enabled)
            {
                _ = Task.Run (async () =>
                {
                    try
                    {
                        var me = await _botClient.GetMeAsync ();
                        System.Diagnostics.Debug.WriteLine ($"Telegram бот @{me.Username} готов");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine ($"Telegram init error: {ex.Message}");
                        _enabled = false;   // теперь работает
                    }
                });
            }
        }

        // Остальные методы без изменений ...
    }
}