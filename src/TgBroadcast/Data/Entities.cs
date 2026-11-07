namespace TgBroadcast.Data;

public enum BroadcastStatus { Collecting, Draft, Queued, Sending, Completed, Cancelled, Expired }
public enum DeliveryStatus { Pending, Sent, Failed, Skipped }

public sealed class Subscriber
{
    public long ChatId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime SubscribedAtUtc { get; set; }
}

public sealed class Broadcast
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SourceKey { get; set; } = "";
    public long CreatedByUserId { get; set; }
    public long SourceChatId { get; set; }
    public string MessageIdsJson { get; set; } = "[]";
    public bool ForwardOriginal { get; set; }
    public BroadcastStatus Status { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public bool ConfirmationSent { get; set; }
    public bool CompletionNotified { get; set; }
}

public sealed class Delivery
{
    public Guid BroadcastId { get; set; }
    public long ChatId { get; set; }
    public DeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public string? LastError { get; set; }
    public Broadcast Broadcast { get; set; } = null!;
}

public sealed class ReceiverState
{
    public int Id { get; set; } = 1;
    public int NextUpdateId { get; set; }
}
