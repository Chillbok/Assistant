using System.Threading.Channels;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace Assistant.Bot;

internal static class BotHost
{
    private const int PollTimeoutSeconds = 30;
    private const int WorkerCount = 4;
    private const int QueueCapacity = 1000;

    public static async Task<User> WaitForTelegramAsync(ITelegramBotClient bot, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(3);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await bot.GetMe(ct);
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 401)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (attempt == 1 || attempt % 10 == 0)
                {
                    Log.Exception($"telegram unreachable (attempt {attempt}), retrying", ex);
                }

                await Task.Delay(delay, ct);
                if (delay < TimeSpan.FromSeconds(30))
                {
                    delay += TimeSpan.FromSeconds(3);
                }
            }
        }
    }

    public static async Task<int> PrepareAsync(ITelegramBotClient bot, CancellationToken ct)
    {
        var webhook = await bot.GetWebhookInfo(ct);
        if (!string.IsNullOrEmpty(webhook.Url))
        {
            Log.Info("removing existing webhook");
            await bot.DeleteWebhook(cancellationToken: ct);
        }

        var backlog = await bot.GetUpdates(offset: -1, limit: 1, timeout: 0, cancellationToken: ct);
        if (backlog.Length > 0)
        {
            Log.Info($"discarding backlog up to update {backlog[^1].Id}");
            return backlog[^1].Id + 1;
        }

        return 0;
    }

    public static async Task NotifyStartedAsync(ITelegramBotClient bot, HashSet<long> allowed, CancellationToken ct)
    {
        foreach (var id in allowed)
        {
            try
            {
                await bot.SendMessage(id, "Assistant started.", cancellationToken: ct);
            }
            catch (Exception ex)
            {
                Log.Exception($"startup notice to {id} failed", ex);
            }
        }
    }

    public static async Task<int> RunAsync(
        ITelegramBotClient bot,
        HashSet<long> allowed,
        int startOffset,
        string binary,
        CancellationToken ct)
    {
        var queue = Channel.CreateBounded<Update>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
        });

        var workers = Enumerable.Range(0, WorkerCount)
            .Select(index => Task.Run(() => WorkerAsync(index, bot, allowed, queue.Reader, binary, ct), ct))
            .ToArray();

        var offset = startOffset;
        var exitCode = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                Update[] updates;
                try
                {
                    updates = await bot.GetUpdates(offset: offset, timeout: PollTimeoutSeconds, cancellationToken: ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    Log.Warn("getUpdates cancelled unexpectedly, retrying");
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                    continue;
                }
                catch (ApiRequestException ex) when (ex.ErrorCode == 429)
                {
                    var wait = ex.Parameters?.RetryAfter ?? 5;
                    Log.Warn($"rate limited, waiting {wait}s");
                    await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                    continue;
                }
                catch (ApiRequestException ex) when (ex.ErrorCode == 401)
                {
                    Log.Fatal("401 Unauthorized: the bot token is invalid or revoked.");
                    exitCode = 3;
                    break;
                }
                catch (ApiRequestException ex) when (ex.ErrorCode == 409)
                {
                    Log.Fatal("409 Conflict: another process is polling with the same token.");
                    Log.Fatal("Check for a second instance (test bot on another machine) and use a separate token.");
                    await Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None);
                    exitCode = 2;
                    break;
                }
                catch (Exception ex)
                {
                    Log.Exception("getUpdates failed", ex);
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    continue;
                }

                foreach (var update in updates)
                {
                    offset = update.Id + 1;
                    await queue.Writer.WriteAsync(update, ct);
                }
            }
        }
        finally
        {
            queue.Writer.TryComplete();
            if (ct.IsCancellationRequested)
            {
                Log.Info($"shutdown: dropping {queue.Reader.Count} queued updates");
            }

            try
            {
                await Task.WhenAll(workers);
            }
            catch (OperationCanceledException)
            {
            }
        }

        return exitCode;
    }

    private static async Task WorkerAsync(
        int index,
        ITelegramBotClient bot,
        HashSet<long> allowed,
        ChannelReader<Update> reader,
        string binary,
        CancellationToken ct)
    {
        try
        {
            await foreach (var update in reader.ReadAllAsync(ct))
            {
                try
                {
                    // 전역 위임 슬롯이 하나이므로 chat별 게이트는 두지 않는다.
                    await Router.HandleAsync(bot, update, allowed, binary, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 워커 예외가 WhenAll 밖으로 나가 재기동 루프가 되지 않게 한다.
                    Log.Exception($"worker{index}", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
