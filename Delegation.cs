using System.Diagnostics;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace Assistant.Bot;

internal static class Delegation
{
    private static readonly TimeSpan NotifyBudget = TimeSpan.FromSeconds(8);
    private const int MaxLogLine = 160;

    private static int busy;

    public static async Task HandleAsync(
        ITelegramBotClient bot,
        Message message,
        string instruction,
        string binary,
        CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
        {
            Log.Info("job rejected: busy");
            await SendWithBudgetAsync(bot, message.Chat.Id, "이미 작업이 실행 중입니다. 끝난 뒤 다시 보내주세요.");
            return;
        }

        try
        {
            var start = await SendStartAsync(bot, message);

            RunResult result;
            try
            {
                result = await Runner.RunAsync(binary, instruction, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                result = Runner.CancelledResult();
            }
            catch (Exception ex)
            {
                Log.Exception("runner threw", ex);
                result = Runner.ErrorResult();
            }

            if (result.Kind == ExitKind.RunnerError)
            {
                // 실행기 예외 문구에는 절대 경로가 섞인다. 로그에만 남기고 전송문에서 뺀다.
                Log.Warn($"runner error: {FirstLine(result.Stderr)}");
                result = result with { Stderr = string.Empty, StderrTail = string.Empty };
            }

            var notifyWatch = Stopwatch.StartNew();
            await NotifyAsync(bot, message.Chat.Id, start, result);
            notifyWatch.Stop();
            LogJob(result, notifyWatch.Elapsed.TotalSeconds);
        }
        finally
        {
            // 알림까지 끝난 뒤 반납한다. 결과와 거절 답장의 순서가 뒤집히지 않게 한다.
            Interlocked.Exchange(ref busy, 0);
        }
    }

    private static async Task NotifyAsync(ITelegramBotClient bot, long chatId, Message? start, RunResult result)
    {
        using var cts = new CancellationTokenSource(NotifyBudget);
        var token = cts.Token;

        if (result.Kind == ExitKind.Cancelled)
        {
            await SendTextAsync(bot, chatId, MessageFormat.Interrupted(), token);
            await EditStatusAsync(bot, chatId, start, "[중단됨]", token, fallback: false);
            return;
        }

        await EditStatusAsync(bot, chatId, start, MessageFormat.StatusLabel(result), token, fallback: true);
        await SendTextAsync(bot, chatId, MessageFormat.Compose(result, Runner.TimeoutSeconds()), token);
    }

    private static async Task<Message?> SendStartAsync(ITelegramBotClient bot, Message message)
    {
        using var cts = new CancellationTokenSource(NotifyBudget);
        try
        {
            return await bot.SendMessage(
                chatId: message.Chat.Id,
                text: "[실행 중] 지시를 실행합니다. 끝나면 알려드립니다.",
                replyParameters: new ReplyParameters
                {
                    MessageId = message.Id,
                    AllowSendingWithoutReply = true,
                },
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                cancellationToken: cts.Token);
        }
        catch (ApiRequestException ex)
        {
            Log.Warn($"start reply failed ({ex.ErrorCode}): {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            Log.Warn("start reply timed out");
        }
        catch (Exception ex)
        {
            Log.Exception("start reply failed", ex);
        }

        return null;
    }

    private static async Task EditStatusAsync(
        ITelegramBotClient bot,
        long chatId,
        Message? start,
        string text,
        CancellationToken token,
        bool fallback)
    {
        if (start is null)
        {
            if (fallback)
            {
                await SendTextAsync(bot, chatId, text, token);
            }

            return;
        }

        try
        {
            await bot.EditMessageText(
                chatId: chatId,
                messageId: start.Id,
                text: text,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                cancellationToken: token);
            return;
        }
        catch (ApiRequestException ex) when (IsNotModified(ex))
        {
            Log.Info("status edit skipped: not modified");
            return;
        }
        catch (ApiRequestException ex)
        {
            Log.Warn($"status edit failed ({ex.ErrorCode}): {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            Log.Warn("status edit timed out");
        }
        catch (Exception ex)
        {
            Log.Exception("status edit failed", ex);
        }

        if (fallback)
        {
            await SendTextAsync(bot, chatId, text, token);
        }
    }

    private static async Task SendWithBudgetAsync(ITelegramBotClient bot, long chatId, string text)
    {
        using var cts = new CancellationTokenSource(NotifyBudget);
        await SendTextAsync(bot, chatId, text, cts.Token);
    }

    private static async Task<bool> SendTextAsync(
        ITelegramBotClient bot,
        long chatId,
        string text,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            await bot.SendMessage(
                chatId: chatId,
                text: text,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                cancellationToken: token);
            return true;
        }
        catch (ApiRequestException ex)
        {
            Log.Warn($"send failed ({ex.ErrorCode}): {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            Log.Warn("send timed out");
        }
        catch (Exception ex)
        {
            Log.Exception("send failed", ex);
        }

        return false;
    }

    private static bool IsNotModified(ApiRequestException ex) =>
        ex.ErrorCode == 400
        && ex.Message.Contains("message is not modified", StringComparison.Ordinal);

    private static void LogJob(RunResult result, double notifySeconds)
    {
        Log.Info(
            $"job kind={result.Kind} exit={result.ExitCode} elapsed={result.ElapsedSeconds:0.0}s " +
            $"out={result.TotalStdoutBytes}B err={result.TotalStderrBytes}B " +
            $"procs={result.RemainingInGroup} zomb={result.ZombiesInGroup} notify={notifySeconds:0.0}s");

        if (result.Kind is ExitKind.Application or ExitKind.ExternalSignal or ExitKind.RunnerError)
        {
            // head의 첫 줄을 쓴다. 꼬리 첫 줄은 64KB 초과 시 원인과 다를 수 있다.
            var line = FirstLine(result.Stderr);
            if (line.Length > 0)
            {
                Log.Warn($"job err1=\"{line}\"");
            }
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Replace('\r', ' ').Trim();
        return line.Length > MaxLogLine ? line[..MaxLogLine] : line;
    }
}
