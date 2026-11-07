namespace TgBroadcast.Configuration;

public sealed class BotOptions
{
    public string Token { get; set; } = "";
    public long AdminUserId { get; set; }
    public string DatabasePath { get; set; } = "data/tgbroadcast.db";
    public int SendIntervalMilliseconds { get; set; } = 100;
    public int MaxDeliveryAttempts { get; set; } = 5;
    public int MediaGroupDelayMilliseconds { get; set; } = 1500;
    public int DraftLifetimeHours { get; set; } = 24;

    public void Validate(bool requireTelegramCredentials)
    {
        if (string.IsNullOrWhiteSpace(DatabasePath))
            throw new InvalidOperationException("Set Bot:DatabasePath to a writable SQLite file path.");
        if (SendIntervalMilliseconds < 50 || MaxDeliveryAttempts is < 1 or > 20 ||
            MediaGroupDelayMilliseconds is < 500 or > 10000 || DraftLifetimeHours is < 1 or > 168)
            throw new InvalidOperationException("Invalid Bot timing/retry configuration. See README.");
        if (requireTelegramCredentials && (string.IsNullOrWhiteSpace(Token) || AdminUserId <= 0))
            throw new InvalidOperationException("Set Bot__Token and Bot__AdminUserId (or .NET user secrets).");
    }
}
