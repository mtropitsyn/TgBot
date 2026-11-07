using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgBroadcast.Configuration;
using TgBroadcast.Services;

namespace TgBroadcast.Telegram;

public sealed class UpdateHandler(BotStore store, IBotTransport transport, BotOptions options)
{
    public async Task HandleAsync(Update update, CancellationToken ct)
    {
        if (update.CallbackQuery is { } callback)
        {
            await HandleCallbackAsync(callback, ct);
            return;
        }
        if (update.Message is not { Chat.Type: ChatType.Private, From: { IsBot: false } } message) return;
        var command = message.Text?.Split(' ', 2)[0].Split('@', 2)[0].ToLowerInvariant();
        switch (command)
        {
            case "/start":
                await store.SetSubscriptionAsync(message.Chat.Id, true, ct);
                await transport.SendTextAsync(message.Chat.Id, "Вы подписаны на рассылку. Отписаться: /stop", ct);
                return;
            case "/stop":
                await store.SetSubscriptionAsync(message.Chat.Id, false, ct);
                await transport.SendTextAsync(message.Chat.Id, "Вы отписались. Подписаться снова: /start", ct);
                return;
            case "/help":
                await transport.SendTextAsync(message.Chat.Id, "/start — подписаться\n/stop — отписаться" +
                    (message.From.Id == options.AdminUserId ? "\n/stats — статистика\nОтправьте сообщение или альбом для создания рассылки." : ""), ct);
                return;
            case "/stats" when message.From.Id == options.AdminUserId:
                var stats = await store.GetStatsAsync(ct);
                await transport.SendTextAsync(message.Chat.Id,
                    $"Подписчиков: {stats.Subscribers}\nРассылок в очереди: {stats.Queued}\nЗавершено: {stats.Completed}", ct);
                return;
        }
        if (command?.StartsWith('/') == true || message.From.Id != options.AdminUserId || message.Chat.Id != options.AdminUserId)
            return;
        var draft = await store.AddMessageAsync(message.From.Id, message.Chat.Id, message.Id,
            message.Poll is not null, message.MediaGroupId, ct);
        if (draft.AlreadyQueued)
            await transport.SendTextAsync(message.Chat.Id,
                "Это сообщение/альбом уже подтверждено или отменено. Для новой рассылки отправьте его заново.", ct);
    }

    private async Task HandleCallbackAsync(CallbackQuery callback, CancellationToken ct)
    {
        if (callback.From.Id != options.AdminUserId || callback.Message is not { Chat.Type: ChatType.Private } message ||
            message.Chat.Id != options.AdminUserId)
        {
            await transport.AnswerCallbackAsync(callback.Id, "Недостаточно прав.", ct);
            return;
        }
        var parts = callback.Data?.Split(':');
        if (parts is not ["bc", "yes" or "no", var idText] || !Guid.TryParseExact(idText, "N", out var id))
        {
            await transport.AnswerCallbackAsync(callback.Id, "Неизвестная кнопка.", ct);
            return;
        }
        var decision = await store.DecideAsync(id, callback.From.Id, parts[1] == "yes", ct);
        var text = decision switch
        {
            DecisionResult.Queued => "Рассылка поставлена в очередь.",
            DecisionResult.Cancelled => "Рассылка отменена.",
            DecisionResult.Expired => "Срок подтверждения истёк. Отправьте сообщение заново.",
            DecisionResult.AlreadyHandled => "Этот черновик уже обработан.",
            DecisionResult.Unauthorized => "Недостаточно прав.",
            _ => "Черновик не найден."
        };
        await transport.AnswerCallbackAsync(callback.Id, text, ct);
        await transport.EditConfirmationAsync(message.Chat.Id, message.Id, text, ct);
    }
}
