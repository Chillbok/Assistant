using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace Assistant.Bot;

internal static class Program
{
    private const int ExitTokenMissing = 1;
    private const int ExitDuplicatePoller = 2;
    private const int ExitUnauthorized = 3;

    public static async Task<int> Main()
    {
        string token;
        try
        {
            token = Env.Require("TELEGRAM_BOT_TOKEN");
        }
        catch (InvalidOperationException ex)
        {
            Log.Fatal($"{ex.Message}. Run ./scripts/setup.sh and edit ~/.config/assistant/env.");
            return ExitTokenMissing;
        }

        var allowed = Env.IdSet("TELEGRAM_ALLOWED_USER_IDS");
        if (allowed.Count == 0)
        {
            Log.Warn("TELEGRAM_ALLOWED_USER_IDS is empty: no one will be answered.");
            Log.Warn("Send a message to the bot, then copy the id from the 'message from <id>' log line.");
        }
        else
        {
            Log.Info($"allowed users: {allowed.Count}");
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        var bot = new TelegramBotClient(token);

        try
        {
            var startOffset = await BotHost.PrepareAsync(bot, cts.Token);
            var me = await bot.GetMe(cts.Token);
            Log.Info($"logged in as @{me.Username}");

            if (allowed.Count > 0)
            {
                await BotHost.NotifyStartedAsync(bot, allowed, cts.Token);
            }

            var exitCode = await BotHost.RunAsync(bot, allowed, startOffset, cts.Token);
            if (exitCode != 0)
            {
                return exitCode;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 401)
        {
            Log.Fatal("401 Unauthorized: check TELEGRAM_BOT_TOKEN at @BotFather.");
            return ExitUnauthorized;
        }
        catch (Exception ex)
        {
            Log.Exception("unhandled", ex);
            return 1;
        }

        Log.Info("stopped");
        return 0;
    }
}
