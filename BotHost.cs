using System.Collections.Concurrent;
using System.Threading.Channels;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Assistant.Bot;

internal static class BotHost
{
    private const int PollTimeoutSeconds = 30;
    private const int WorkerCount = 4;
    private const int QueueCapacity = 1000;

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
        CancellationToken ct)
    {
        var queue = Channel.CreateBounded<Update>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
        });

        // 유효 사용자만 락을 만들기 때문에 사전 크기가 허용 목록 크기로 제한된다.
        var chatLocks = new ConcurrentDictionary<long, SemaphoreSlim>();

        var workers = Enumerable.Range(0, WorkerCount)
            .Select(index => Task.Run(() => WorkerAsync(index, bot, allowed, queue.Reader, chatLocks, ct), ct))
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
                catch (OperationCanceledException)
                {
                    break;
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
        ConcurrentDictionary<long, SemaphoreSlim> chatLocks,
        CancellationToken ct)
    {
        try
        {
            await foreach (var update in reader.ReadAllAsync(ct))
            {
                var message = update.Message;
                var senderId = message?.From?.Id ?? 0;
                var isTrusted = message is not null
                    && allowed.Contains(senderId)
                    && message.Chat.Type == ChatType.Private;

                if (!isTrusted)
                {
                    // 인가되지 않은 트래픽은 락 없이 바로 처리(로그 후 폐기)한다.
                    await Router.HandleAsync(bot, update, allowed, ct);
                    continue;
                }

                var gate = chatLocks.GetOrAdd(message!.Chat.Id, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(ct);
                try
                {
                    await Router.HandleAsync(bot, update, allowed, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Exception($"worker{index}", ex);
                }
                finally
                {
                    gate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
