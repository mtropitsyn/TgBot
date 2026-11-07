using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TgBroadcast.Configuration;
using TgBroadcast.Data;

namespace TgBroadcast.Services;

// One running bot instance owns this store. Serialize short writes, never network calls.
public sealed class BotStore(IDbContextFactory<BotDbContext> factory, BotOptions options, TimeProvider time)
{
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private async Task<T> WriteAsync<T>(Func<BotDbContext, Task<T>> action, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return await action(db);
        }
        finally { writeLock.Release(); }
    }

    public Task<int> ImportSubscribersAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToArray();
        if (distinct.Any(id => id <= 0))
            throw new ArgumentException("Only positive private-chat IDs can be imported.");
        return WriteAsync(async db =>
        {
            var existing = await db.Subscribers.ToDictionaryAsync(x => x.ChatId, ct);
            foreach (var id in distinct)
                if (existing.TryGetValue(id, out var subscriber)) subscriber.IsActive = true;
                else db.Subscribers.Add(new Subscriber { ChatId = id, SubscribedAtUtc = Now });
            await db.SaveChangesAsync(ct);
            return distinct.Length;
        }, ct);
    }

    public Task SetSubscriptionAsync(long chatId, bool active, CancellationToken ct) => WriteAsync(async db =>
    {
        if (chatId <= 0) throw new ArgumentOutOfRangeException(nameof(chatId));
        var subscriber = await db.Subscribers.FindAsync([chatId], ct);
        if (subscriber is null && active)
            db.Subscribers.Add(new Subscriber { ChatId = chatId, SubscribedAtUtc = Now });
        else if (subscriber is not null) subscriber.IsActive = active;
        return await db.SaveChangesAsync(ct);
    }, ct);

    public async Task<bool> IsSubscribedAsync(long chatId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Subscribers.AnyAsync(x => x.ChatId == chatId && x.IsActive, ct);
    }

    public Task<DraftResult> AddMessageAsync(long actor, long sourceChat, int messageId,
        bool isPoll, string? mediaGroup, CancellationToken ct) => WriteAsync(async db =>
    {
        if (actor != options.AdminUserId || sourceChat != actor)
            throw new UnauthorizedAccessException("Only the configured administrator can create drafts.");
        var key = mediaGroup is null ? $"message:{sourceChat}:{messageId}" : $"album:{sourceChat}:{mediaGroup}";
        var draft = await db.Broadcasts.SingleOrDefaultAsync(x => x.SourceKey == key, ct);
        var created = draft is null;
        if (draft is null)
        {
            draft = new Broadcast
            {
                SourceKey = key, CreatedByUserId = actor, SourceChatId = sourceChat,
                Status = mediaGroup is null ? BroadcastStatus.Draft : BroadcastStatus.Collecting,
                CreatedAtUtc = Now, UpdatedAtUtc = Now
            };
            db.Broadcasts.Add(draft);
        }
        else if (draft.Status is not (BroadcastStatus.Draft or BroadcastStatus.Collecting))
            return new DraftResult(draft.Id, false, true);
        var ids = JsonSerializer.Deserialize<int[]>(draft.MessageIdsJson)!;
        if (!ids.Contains(messageId))
        {
            draft.MessageIdsJson = JsonSerializer.Serialize(ids.Append(messageId).Order().ToArray());
            draft.ForwardOriginal |= isPoll;
            draft.UpdatedAtUtc = Now;
        }
        await db.SaveChangesAsync(ct);
        return new DraftResult(draft.Id, created && mediaGroup is null, false);
    }, ct);

    public Task<List<Guid>> FinishAlbumsAsync(CancellationToken ct) => WriteAsync(async db =>
    {
        var cutoff = Now.AddMilliseconds(-options.MediaGroupDelayMilliseconds);
        var drafts = await db.Broadcasts.Where(x => x.Status == BroadcastStatus.Collecting && x.UpdatedAtUtc <= cutoff)
            .ToListAsync(ct);
        foreach (var draft in drafts) draft.Status = BroadcastStatus.Draft;
        await db.SaveChangesAsync(ct);
        return drafts.Select(x => x.Id).ToList();
    }, ct);

    public Task<DecisionResult> DecideAsync(Guid id, long actor, bool confirm, CancellationToken ct) => WriteAsync(async db =>
    {
        if (actor != options.AdminUserId) return DecisionResult.Unauthorized;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var draft = await db.Broadcasts.FindAsync([id], ct);
        if (draft is null) return DecisionResult.NotFound;
        if (draft.CreatedByUserId != actor) return DecisionResult.Unauthorized;
        if (draft.Status != BroadcastStatus.Draft) return DecisionResult.AlreadyHandled;
        if (draft.CreatedAtUtc.AddHours(options.DraftLifetimeHours) <= Now)
        {
            draft.Status = BroadcastStatus.Expired;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return DecisionResult.Expired;
        }
        draft.Status = confirm ? BroadcastStatus.Queued : BroadcastStatus.Cancelled;
        draft.UpdatedAtUtc = Now;
        if (confirm)
        {
            var subscribers = await db.Subscribers.Where(x => x.IsActive).Select(x => x.ChatId).ToListAsync(ct);
            db.Deliveries.AddRange(subscribers.Select(chatId => new Delivery
            {
                BroadcastId = id, ChatId = chatId, NextAttemptAtUtc = Now
            }));
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return confirm ? DecisionResult.Queued : DecisionResult.Cancelled;
    }, ct);

    public Task<DraftNotification?> NextConfirmationAsync(CancellationToken ct) => WriteAsync<DraftNotification?>(async db =>
    {
        var expiredBefore = Now.AddHours(-options.DraftLifetimeHours);
        await db.Broadcasts.Where(x => x.Status == BroadcastStatus.Draft && x.CreatedAtUtc <= expiredBefore)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, BroadcastStatus.Expired), ct);
        var draft = await db.Broadcasts.Where(x => x.Status == BroadcastStatus.Draft && !x.ConfirmationSent)
            .OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        return draft is null ? null : new DraftNotification(draft.Id, new MessageContent(draft.SourceChatId,
            JsonSerializer.Deserialize<int[]>(draft.MessageIdsJson)!, draft.ForwardOriginal));
    }, ct);

    public Task MarkConfirmationSentAsync(Guid id, CancellationToken ct) => WriteAsync(async db =>
    {
        var draft = await db.Broadcasts.FindAsync([id], ct);
        if (draft is not null) draft.ConfirmationSent = true;
        return await db.SaveChangesAsync(ct);
    }, ct);

    public Task<DeliveryWork?> NextDeliveryAsync(CancellationToken ct) => WriteAsync<DeliveryWork?>(async db =>
    {
        var now = Now;
        var delivery = await db.Deliveries.Include(x => x.Broadcast)
            .Where(x => x.Status == DeliveryStatus.Pending && x.NextAttemptAtUtc <= now &&
                (x.Broadcast.Status == BroadcastStatus.Queued || x.Broadcast.Status == BroadcastStatus.Sending))
            .OrderBy(x => x.NextAttemptAtUtc).ThenBy(x => x.Broadcast.CreatedAtUtc).FirstOrDefaultAsync(ct);
        if (delivery is null) return null;
        delivery.Broadcast.Status = BroadcastStatus.Sending;
        await db.SaveChangesAsync(ct);
        return new DeliveryWork(delivery.BroadcastId, delivery.ChatId, delivery.Attempts,
            new MessageContent(delivery.Broadcast.SourceChatId,
                JsonSerializer.Deserialize<int[]>(delivery.Broadcast.MessageIdsJson)!, delivery.Broadcast.ForwardOriginal));
    }, ct);

    public Task SaveDeliveryAsync(DeliveryWork work, DeliveryStatus status, string? error,
        TimeSpan? retryDelay, bool deactivate, CancellationToken ct) => WriteAsync(async db =>
    {
        var delivery = await db.Deliveries.FindAsync([work.BroadcastId, work.ChatId], ct)
            ?? throw new InvalidOperationException("Delivery disappeared.");
        delivery.Attempts++;
        delivery.Status = status;
        delivery.LastError = error;
        if (retryDelay is { } delay) delivery.NextAttemptAtUtc = Now.Add(delay);
        if (deactivate)
        {
            var subscriber = await db.Subscribers.FindAsync([work.ChatId], ct);
            if (subscriber is not null) subscriber.IsActive = false;
        }
        return await db.SaveChangesAsync(ct);
    }, ct);

    public Task CompleteFinishedAsync(CancellationToken ct) => WriteAsync(async db =>
    {
        var finished = await db.Broadcasts.Where(x =>
            (x.Status == BroadcastStatus.Queued || x.Status == BroadcastStatus.Sending) &&
            !db.Deliveries.Any(d => d.BroadcastId == x.Id && d.Status == DeliveryStatus.Pending)).ToListAsync(ct);
        foreach (var broadcast in finished)
        {
            broadcast.Status = BroadcastStatus.Completed;
            broadcast.UpdatedAtUtc = Now;
        }
        return await db.SaveChangesAsync(ct);
    }, ct);

    public async Task<BroadcastSummary?> NextSummaryAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var broadcast = await db.Broadcasts.AsNoTracking()
            .Where(x => x.Status == BroadcastStatus.Completed && !x.CompletionNotified)
            .OrderBy(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        if (broadcast is null) return null;
        var statuses = await db.Deliveries.Where(x => x.BroadcastId == broadcast.Id).Select(x => x.Status).ToListAsync(ct);
        return new BroadcastSummary(broadcast.Id, JsonSerializer.Deserialize<int[]>(broadcast.MessageIdsJson)![0],
            statuses.Count(x => x == DeliveryStatus.Sent),
            statuses.Count(x => x == DeliveryStatus.Failed), statuses.Count(x => x == DeliveryStatus.Skipped));
    }

    public Task MarkNotifiedAsync(Guid id, CancellationToken ct) => WriteAsync(async db =>
    {
        var broadcast = await db.Broadcasts.FindAsync([id], ct);
        if (broadcast is not null) broadcast.CompletionNotified = true;
        return await db.SaveChangesAsync(ct);
    }, ct);

    public async Task<BotStats> GetStatsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return new BotStats(await db.Subscribers.CountAsync(x => x.IsActive, ct),
            await db.Broadcasts.CountAsync(x => x.Status == BroadcastStatus.Queued || x.Status == BroadcastStatus.Sending, ct),
            await db.Broadcasts.CountAsync(x => x.Status == BroadcastStatus.Completed, ct));
    }

    public async Task<int> GetOffsetAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await db.ReceiverStates.FindAsync([1], ct))?.NextUpdateId ?? 0;
    }

    public Task SaveOffsetAsync(int offset, CancellationToken ct) => WriteAsync(async db =>
    {
        var state = await db.ReceiverStates.FindAsync([1], ct);
        if (state is null) db.ReceiverStates.Add(new ReceiverState { NextUpdateId = offset });
        else state.NextUpdateId = Math.Max(state.NextUpdateId, offset);
        return await db.SaveChangesAsync(ct);
    }, ct);
}
