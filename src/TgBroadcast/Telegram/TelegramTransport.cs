using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using TgBroadcast.Services;

namespace TgBroadcast.Telegram;

public sealed class TelegramTransport(ITelegramBotClient client) : IBotTransport
{
    private static async Task CallAsync(Func<Task> action, CancellationToken ct)
    {
        try { await action(); }
        catch (ApiRequestException error)
        {
            var failure = error.ErrorCode switch
            {
                403 => TransportFailure.Blocked,
                429 => TransportFailure.RateLimited,
                >= 500 => TransportFailure.Transient,
                _ => TransportFailure.Permanent
            };
            throw new BotTransportException(failure,
                error.Parameters?.RetryAfter is int seconds ? TimeSpan.FromSeconds(seconds) : null);
        }
        catch (HttpRequestException) { throw new BotTransportException(TransportFailure.Transient); }
        catch (RequestException) { throw new BotTransportException(TransportFailure.Transient); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new BotTransportException(TransportFailure.Transient); }
    }

    public Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken, int? replyToMessageId = null) =>
        CallAsync(async () =>
        {
            await client.SendMessage(chatId, text,
                replyParameters: replyToMessageId is { } id ? new ReplyParameters { MessageId = id, AllowSendingWithoutReply = true } : null,
                cancellationToken: cancellationToken);
        }, cancellationToken);

    public Task SendConfirmationAsync(long chatId, Guid draftId, MessageContent content, CancellationToken cancellationToken) =>
        CallAsync(async () =>
        {
            var keyboard = new InlineKeyboardMarkup(new[] { new[]
                {
                    InlineKeyboardButton.WithCallbackData("Подтвердить", $"bc:yes:{draftId:N}"),
                    InlineKeyboardButton.WithCallbackData("Отменить", $"bc:no:{draftId:N}")
                } });
            var text = content.MessageIds.Length == 1 ? "Разослать это сообщение всем подписчикам?"
                : $"Разослать альбом из {content.MessageIds.Length} сообщений всем подписчикам?";
            await client.SendMessage(chatId, text, replyMarkup: keyboard,
                replyParameters: new ReplyParameters { MessageId = content.MessageIds[0], AllowSendingWithoutReply = true },
                cancellationToken: cancellationToken);
        }, cancellationToken);

    public Task AnswerCallbackAsync(string callbackId, string text, CancellationToken cancellationToken) =>
        CallAsync(() => client.AnswerCallbackQuery(callbackId, text, cancellationToken: cancellationToken), cancellationToken);

    public Task EditConfirmationAsync(long chatId, int messageId, string text, CancellationToken cancellationToken) =>
        CallAsync(async () =>
        {
            await client.EditMessageText(chatId, messageId, text,
                replyMarkup: new InlineKeyboardMarkup(Array.Empty<InlineKeyboardButton[]>()),
                cancellationToken: cancellationToken);
        }, cancellationToken);

    public Task DeliverAsync(long chatId, MessageContent content, CancellationToken cancellationToken) =>
        CallAsync(async () =>
        {
            if (content.ForwardOriginal)
            {
                foreach (var id in content.MessageIds)
                    await client.ForwardMessage(chatId, content.SourceChatId, id, cancellationToken: cancellationToken);
            }
            else if (content.MessageIds.Length == 1)
                await client.CopyMessage(chatId, content.SourceChatId, content.MessageIds[0], cancellationToken: cancellationToken);
            else
            {
                var copied = await client.CopyMessages(chatId, content.SourceChatId,
                    content.MessageIds, cancellationToken: cancellationToken);
                if (copied.Length != content.MessageIds.Length)
                    throw new BotTransportException(TransportFailure.Permanent);
            }
        }, cancellationToken);
}
