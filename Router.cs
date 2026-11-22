using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Assistant.Bot;

internal static class Router
{
    public static async Task HandleAsync(
        ITelegramBotClient bot,
        Update update,
        HashSet<long> allowed,
        string binary,
        CancellationToken ct)
    {
        var message = update.Message;
        if (message is null)
        {
            return;
        }

        var fromId = message.From?.Id ?? 0;
        if (!allowed.Contains(fromId))
        {
            var username = message.From?.Username is { } name ? $"@{name}" : "no-username";
            Log.Info($"message from {fromId} ({username}) ignored");
            return;
        }

        if (message.Chat.Type != ChatType.Private)
        {
            Log.Info($"ignored message in {message.Chat.Type} chat {message.Chat.Id}");
            return;
        }

        if (message.Text is not { } text)
        {
            Log.Info($"ignored non-text message from {fromId}");
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Log.Info($"ignored empty message from {fromId}");
            return;
        }

        await Delegation.HandleAsync(bot, message, text, binary, ct);
    }
}
