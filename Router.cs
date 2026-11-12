using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Assistant.Bot;

internal static class Router
{
    private const int MaxReplyLength = 4096;

    public static async Task HandleAsync(
        ITelegramBotClient bot,
        Update update,
        HashSet<long> allowed,
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
            Console.WriteLine($"[info] message from {fromId} ({username}) ignored");
            return;
        }

        if (message.Chat.Type != ChatType.Private)
        {
            Console.WriteLine($"[info] ignored message in {message.Chat.Type} chat {message.Chat.Id}");
            return;
        }

        if (message.Text is not { } text)
        {
            Console.WriteLine($"[info] ignored non-text message from {fromId}");
            return;
        }

        var reply = $"echo: {text}";
        if (reply.Length > MaxReplyLength)
        {
            reply = reply[..MaxReplyLength];
        }

        try
        {
            await bot.SendMessage(message.Chat.Id, reply, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[error] sendMessage: {ex.Message}");
        }
    }
}
