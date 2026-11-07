using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgBroadcast.Configuration;
using TgBroadcast.Data;
using TgBroadcast.Services;

namespace TgBroadcast.Tests;

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now += duration;
}

internal sealed class TestFactory(DbContextOptions<BotDbContext> options) : IDbContextFactory<BotDbContext>
{
    public BotDbContext CreateDbContext() => new(options);
}

internal sealed class FakeTransport : IBotTransport
{
    public List<(long ChatId, string Text)> Texts { get; } = [];
    public List<(Guid Id, MessageContent Content)> Confirmations { get; } = [];
    public List<(long ChatId, MessageContent Content)> Delivered { get; } = [];
    public List<string> CallbackAnswers { get; } = [];
    public int DeliveryAttempts { get; private set; }
    public BotTransportException? NextDeliveryError { get; set; }
    public bool FailNextConfirmation { get; set; }

    public Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken, int? replyToMessageId = null)
    { Texts.Add((chatId, text)); return Task.CompletedTask; }

    public Task SendConfirmationAsync(long chatId, Guid draftId, MessageContent content, CancellationToken cancellationToken)
    {
        if (FailNextConfirmation)
        {
            FailNextConfirmation = false;
            throw new BotTransportException(TransportFailure.Transient);
        }
        Confirmations.Add((draftId, content));
        return Task.CompletedTask;
    }

    public Task AnswerCallbackAsync(string callbackId, string text, CancellationToken cancellationToken)
    { CallbackAnswers.Add(text); return Task.CompletedTask; }

    public Task EditConfirmationAsync(long chatId, int messageId, string text, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeliverAsync(long chatId, MessageContent content, CancellationToken cancellationToken)
    {
        DeliveryAttempts++;
        if (NextDeliveryError is { } error) { NextDeliveryError = null; throw error; }
        Delivered.Add((chatId, content));
        return Task.CompletedTask;
    }
}

internal sealed class TestEnvironment : IAsyncDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "TgBroadcast-tests-" + Guid.NewGuid().ToString("N"));
    public BotOptions Options { get; } = new() { AdminUserId = 42 };
    public ManualClock Clock { get; } = new();
    public FakeTransport Transport { get; } = new();
    public TestFactory Factory { get; }
    public BotStore Store { get; }
    public BroadcastProcessor Processor => new(Store, Transport, Options);

    public TestEnvironment()
    {
        Directory.CreateDirectory(directory);
        Factory = new TestFactory(new DbContextOptionsBuilder<BotDbContext>()
            .UseSqlite($"Data Source={Path.Combine(directory, "test.db")};Pooling=False").Options);
        Store = NewStore();
    }

    public BotStore NewStore() => new(Factory, Options, Clock);

    public async Task InitializeAsync()
    {
        await using var db = Factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    public Task<DraftResult> DraftAsync(int messageId = 10, string? album = null, bool poll = false) =>
        Store.AddMessageAsync(42, 42, messageId, poll, album, CancellationToken.None);

    public async Task<Delivery[]> DeliveriesAsync()
    {
        await using var db = Factory.CreateDbContext();
        return await db.Deliveries.AsNoTracking().OrderBy(x => x.ChatId).ToArrayAsync();
    }

    public static Update Callback(Guid draft, long actor = 42, string action = "yes", ChatType chatType = ChatType.Private) => new()
    {
        CallbackQuery = new CallbackQuery
        {
            Id = "offline-callback", Data = $"bc:{action}:{draft:N}",
            From = new User { Id = actor, FirstName = "Test" },
            Message = new Message { Id = 100, Chat = new Chat { Id = 42, Type = chatType } }
        }
    };

    public ValueTask DisposeAsync()
    {
        Directory.Delete(directory, recursive: true);
        return ValueTask.CompletedTask;
    }
}
