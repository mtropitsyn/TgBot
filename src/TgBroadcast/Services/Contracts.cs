namespace TgBroadcast.Services;

public sealed record MessageContent(long SourceChatId, int[] MessageIds, bool ForwardOriginal);
public sealed record DraftResult(Guid Id, bool NeedsConfirmation, bool AlreadyQueued);
public sealed record DraftNotification(Guid Id, MessageContent Content);
public sealed record DeliveryWork(Guid BroadcastId, long ChatId, int Attempts, MessageContent Content);
public sealed record BroadcastSummary(Guid Id, int SourceMessageId, int Sent, int Failed, int Skipped);
public sealed record BotStats(int Subscribers, int Queued, int Completed);
public enum DecisionResult { Queued, Cancelled, AlreadyHandled, NotFound, Expired, Unauthorized }
public enum TransportFailure { Blocked, RateLimited, Transient, Permanent }

public sealed class BotTransportException(TransportFailure failure, TimeSpan? retryAfter = null)
    : Exception($"Telegram transport failure: {failure}")
{
    public TransportFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public interface IBotTransport
{
    Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken, int? replyToMessageId = null);
    Task SendConfirmationAsync(long chatId, Guid draftId, MessageContent content, CancellationToken cancellationToken);
    Task AnswerCallbackAsync(string callbackId, string text, CancellationToken cancellationToken);
    Task EditConfirmationAsync(long chatId, int messageId, string text, CancellationToken cancellationToken);
    Task DeliverAsync(long chatId, MessageContent content, CancellationToken cancellationToken);
}
