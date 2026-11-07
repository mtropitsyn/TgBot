using TgBroadcast.Configuration;
using TgBroadcast.Data;

namespace TgBroadcast.Services;

public sealed class BroadcastProcessor(BotStore store, IBotTransport transport, BotOptions options)
{
    public async Task<TimeSpan> ProcessOnceAsync(CancellationToken ct)
    {
        var work = await store.NextDeliveryAsync(ct);
        if (work is null)
        {
            await store.CompleteFinishedAsync(ct);
            if (await store.NextSummaryAsync(ct) is { } summary)
            {
                await transport.SendTextAsync(options.AdminUserId,
                    "Рассылка завершена.\n" +
                    $"Отправлено: {summary.Sent}. Ошибок: {summary.Failed}. Пропущено: {summary.Skipped}.", ct, summary.SourceMessageId);
                await store.MarkNotifiedAsync(summary.Id, ct);
            }
            return TimeSpan.FromSeconds(1);
        }
        if (!await store.IsSubscribedAsync(work.ChatId, ct))
        {
            await store.SaveDeliveryAsync(work, DeliveryStatus.Skipped, "Unsubscribed", null, false, ct);
            return TimeSpan.Zero;
        }
        try
        {
            await transport.DeliverAsync(work.ChatId, work.Content, ct);
            await store.SaveDeliveryAsync(work, DeliveryStatus.Sent, null, null, false, ct);
        }
        catch (BotTransportException error)
        {
            var retryable = error.Failure is TransportFailure.RateLimited or TransportFailure.Transient;
            var retry = retryable && work.Attempts + 1 < options.MaxDeliveryAttempts;
            var delay = error.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, work.Attempts + 1)));
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            var status = error.Failure == TransportFailure.Blocked ? DeliveryStatus.Skipped
                : retry ? DeliveryStatus.Pending : DeliveryStatus.Failed;
            await store.SaveDeliveryAsync(work, status, error.Failure.ToString(), retry ? delay : null,
                error.Failure == TransportFailure.Blocked, ct);
            if (error.Failure == TransportFailure.RateLimited) return delay;
        }
        return TimeSpan.FromMilliseconds(options.SendIntervalMilliseconds);
    }
}
