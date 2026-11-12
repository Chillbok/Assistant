using System.Collections.Concurrent;
using System.Threading.Channels;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace Assistant.Bot;

internal static class BotHost
{
    private const int PollTimeoutSeconds = 30;
    private const int WorkerCount = 4;

    public static async Task<int> PrepareAsync(ITelegramBotClient bot, CancellationToken ct)
    {
        var webhook = await bot.GetWebhookInfo(ct);
        if (!string.IsNullOrEmpty(webhook.Url))
        {
            Console.WriteLine($"[info] removing webhook: {webhook.Url}");
            await bot.DeleteWebhook(cancellationToken: ct);
        }

        var backlog = await bot.GetUpdates(offset: -1, limit: 1, timeout: 0, cancellationToken: ct);
        if (backlog.Length > 0)
        {
            Console.WriteLine($"[info] discarding backlog up to update {backlog[^1].Id}");
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
                Console.Error.WriteLine($"[warn] startup notice to {id} failed: {ex.Message}");
            }
        }
    }

    public static async Task RunAsync(ITelegramBotClient bot, HashSet<long> allowed, int startOffset, CancellationToken ct)
    {
        var queue = Channel.CreateUnbounded<Update>();
        var chatLocks = new ConcurrentDictionary<long, SemaphoreSlim>();

        var workers = Enumerable.Range(0, WorkerCount)
            .Select(index => Task.Run(() => WorkerAsync(index, bot, allowed, queue.Reader, chatLocks, ct), ct))
            .ToArray();

        var offset = startOffset;

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
                    Console.Error.WriteLine($"[warn] rate limited, waiting {wait}s");
                    await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                    continue;
                }
                catch (ApiRequestException ex) when (ex.ErrorCode is 401 or 409)
                {
                    Console.Error.WriteLine($"[fatal] {ex.ErrorCode}: {ex.Message}");
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[error] getUpdates: {ex.Message}");
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
                var chatId = update.Message?.Chat.Id ?? 0;
                var gate = chatLocks.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));

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
                    Console.Error.WriteLine($"[error] worker{index}: {ex.Message}");
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
