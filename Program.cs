using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace Assistant.Bot;

internal static class Program
{
    public static async Task<int> Main()
    {
        string token;
        try
        {
            token = Env.Require("TELEGRAM_BOT_TOKEN");
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"[fatal] {ex.Message}");
            return 1;
        }

        var allowed = Env.IdSet("TELEGRAM_ALLOWED_USER_IDS");
        if (allowed.Count == 0)
        {
            Console.Error.WriteLine("[warn] TELEGRAM_ALLOWED_USER_IDS is empty: no one will be answered.");
            Console.Error.WriteLine("[warn] Send a message to the bot, then copy the id from the 'message from <id>' log line.");
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        var bot = new TelegramBotClient(token);

        try
        {
            await BotHost.PrepareAsync(bot, cts.Token);
            var me = await bot.GetMe(cts.Token);
            Console.WriteLine($"[info] logged in as @{me.Username}");

            if (allowed.Count > 0)
            {
                await BotHost.NotifyStartedAsync(bot, allowed, cts.Token);
            }

            await BotHost.RunAsync(bot, allowed, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ApiRequestException ex)
        {
            Console.Error.WriteLine($"[fatal] {ex.ErrorCode}: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fatal] {ex}");
            return 1;
        }

        Console.WriteLine("[info] stopped");
        return 0;
    }
}
