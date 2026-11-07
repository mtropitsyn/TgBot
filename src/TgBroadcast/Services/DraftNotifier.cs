using TgBroadcast.Configuration;

namespace TgBroadcast.Services;

public sealed class DraftNotifier(BotStore store, IBotTransport transport, BotOptions options)
{
    public async Task<bool> ProcessOnceAsync(CancellationToken ct)
    {
        await store.FinishAlbumsAsync(ct);
        if (await store.NextConfirmationAsync(ct) is not { } draft) return false;
        await transport.SendConfirmationAsync(options.AdminUserId, draft.Id, draft.Content, ct);
        await store.MarkConfirmationSentAsync(draft.Id, ct);
        return true;
    }
}
