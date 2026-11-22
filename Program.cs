using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace Assistant.Bot;

internal static class Program
{
    private const int ExitTokenMissing = 1;
    private const int ExitDuplicatePoller = 2;
    private const int ExitUnauthorized = 3;
    private const int ExitConfigMissing = 78;
    private const string OpenCodeConfigPath = "/etc/assistant/opencode.json";

    private static readonly JsonSerializerOptions ResultJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> Main()
    {
        if (Environment.GetEnvironmentVariable("ASSISTANT_RUN_ONCE") is { Length: > 0 } instruction)
        {
            return await RunOnceAsync(instruction);
        }

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

        string binary;
        try
        {
            binary = Runner.SelectBotBinary(out var diagnostic);
            if (diagnostic)
            {
                Log.Warn("diagnostic binary in use");
            }
        }
        catch (InvalidOperationException ex)
        {
            Log.Fatal(ex.Message);
            return ExitConfigMissing;
        }

        if (!File.Exists(OpenCodeConfigPath))
        {
            Log.Fatal($"{OpenCodeConfigPath} is missing: refusing to run opencode without permission rules.");
            return ExitConfigMissing;
        }

        Log.Info($"timeout {Runner.TimeoutSeconds()}s, bin {binary}");

        using var cts = new CancellationTokenSource();
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
        {
            context.Cancel = true;
            cts.Cancel();
        });
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            Log.Info("SIGTERM: shutting down");
            cts.Cancel();
        });

        var bot = new TelegramBotClient(token);

        try
        {
            var me = await BotHost.WaitForTelegramAsync(bot, cts.Token);
            var startOffset = await BotHost.PrepareAsync(bot, cts.Token);
            Log.Info($"logged in as @{me.Username}");

            if (allowed.Count > 0)
            {
                await BotHost.NotifyStartedAsync(bot, allowed, cts.Token);
            }

            var stopwatch = Stopwatch.StartNew();
            var exitCode = await BotHost.RunAsync(bot, allowed, startOffset, binary, cts.Token);
            if (cts.IsCancellationRequested)
            {
                Log.Info($"shutdown took {stopwatch.Elapsed.TotalSeconds:0.0}s");
            }

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

    private static async Task<int> RunOnceAsync(string instruction)
    {
        if (!File.Exists(OpenCodeConfigPath))
        {
            Log.Fatal($"{OpenCodeConfigPath} is missing: refusing to run opencode without permission rules.");
            return ExitConfigMissing;
        }

        string binary;
        try
        {
            binary = Runner.ResolveBinary(Environment.GetEnvironmentVariable("ASSISTANT_OPENCODE_BIN"));
        }
        catch (InvalidOperationException ex)
        {
            Log.Fatal(ex.Message);
            return ExitConfigMissing;
        }

        using var cts = new CancellationTokenSource();
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cts.Cancel();
        });
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
        {
            context.Cancel = true;
            cts.Cancel();
        });

        Log.Info($"run-once: {instruction.Length} chars, timeout {Runner.TimeoutSeconds()}s, bin {binary}");

        var result = await Runner.RunAsync(binary, instruction, cts.Token);
        Console.WriteLine(JsonSerializer.Serialize(result, ResultJson));

        return result.Kind switch
        {
            ExitKind.Success => 0,
            ExitKind.Timeout => 124,
            ExitKind.Cancelled => 130,
            ExitKind.RunnerError => 127,
            _ => result.ExitCode == 0 ? 1 : result.ExitCode,
        };
    }
}
