using Telegram.Bot;
using Telegram.Bot.Types;

var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("TELEGRAM_BOT_TOKEN is not set");
    return 1;
}

var allowed = (Environment.GetEnvironmentVariable("TELEGRAM_ALLOWED_USER_IDS") ?? "")
    .Split(",", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(long.Parse)
    .ToHashSet();

if (allowed.Count == 0)
{
    Console.Error.WriteLine("TELEGRAM_ALLOWED_USER_IDS is empty; nothing would be answered");
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

var bot = new TelegramBotClient(token);
var me = await bot.GetMe(cts.Token);
Console.WriteLine($"logged in as @{me.Username}");

int offset = 0;
while (!cts.IsCancellationRequested)
{
    Update[] updates;
    try
    {
        updates = await bot.GetUpdates(offset: offset, timeout: 30, cancellationToken: cts.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"getUpdates failed: {ex.Message}");
        try { await Task.Delay(TimeSpan.FromSeconds(3), cts.Token); } catch (OperationCanceledException) { break; }
        continue;
    }

    foreach (var update in updates)
    {
        offset = update.Id + 1;

        if (update.Message?.Text is not { } text) continue;

        var fromId = update.Message.From?.Id ?? 0;
        if (!allowed.Contains(fromId))
        {
            Console.WriteLine($"ignored message from {fromId}");
            continue;
        }

        try
        {
            await bot.SendMessage(update.Message.Chat.Id, $"echo: {text}", cancellationToken: cts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"sendMessage failed: {ex.Message}");
        }
    }
}

Console.WriteLine("stopped");
return 0;
