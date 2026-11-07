using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TgBroadcast.Data;
using TgBroadcast.Services;
using TgBroadcast.Telegram;
using Xunit;

namespace TgBroadcast.Tests;

public sealed class BotTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Subscription_is_idempotent_and_can_be_reactivated()
    {
        await using var env = new TestEnvironment();
        await env.InitializeAsync();
        await env.Store.SetSubscriptionAsync(101, true, Ct);
        await env.Store.SetSubscriptionAsync(101, true, Ct);
        Assert.Equal(1, (await env.Store.GetStatsAsync(Ct)).Subscribers);
        await env.Store.SetSubscriptionAsync(101, false, Ct);
        Assert.False(await env.Store.IsSubscribedAsync(101, Ct));
        await env.Store.SetSubscriptionAsync(101, true, Ct);
        Assert.True(await env.Store.IsSubscribedAsync(101, Ct));
    }

    [Fact]
    public async Task Import_deduplicates_and_rejects_invalid_ids_before_writing()
    {
        await using var env = new TestEnvironment();
        await env.InitializeAsync();
        Assert.Equal(2, await env.Store.ImportSubscribersAsync([101, 101, 102], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => env.Store.ImportSubscribersAsync([103, -1], Ct));
        Assert.Equal(2, (await env.Store.GetStatsAsync(Ct)).Subscribers);
    }

    [Fact]
    public async Task Only_admin_can_create_and_confirm_drafts()
    {
        await using var env = new TestEnvironment();
        await env.InitializeAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => env.Store.AddMessageAsync(99, 99, 1, false, null, Ct));
        var draft = await env.DraftAsync();
        Assert.Equal(DecisionResult.Unauthorized, await env.Store.DecideAsync(draft.Id, 99, true, Ct));
        Assert.Empty(await env.DeliveriesAsync());
    }

    [Fact]
    public async Task Confirming_first_draft_sends_first_message_even_when_second_exists()
    {
        await using var env = new TestEnvironment();
        await env.InitializeAsync();
        await env.Store.ImportSubscribersAsync([101], Ct);
        var first = await env.DraftAsync(10);
        var second = await env.DraftAsync(20);
        Assert.NotEqual(first.Id, second.Id);
        var handler = new UpdateHandler(env.Store, env.Transport, env.Options);
        await handler.HandleAsync(TestEnvironment.Callback(first.Id), Ct);
        await env.Processor.ProcessOnceAsync(Ct);
        Assert.Equal(new[] { 10 }, Assert.Single(env.Transport.Delivered).Content.MessageIds);
        Assert.Equal(second.Id, (await env.Store.NextConfirmationAsync(Ct))!.Id);
    }

    [Fact]
    public async Task Concurrent_double_confirmation_creates_one_recipient_snapshot()
    {
        await using var env = new TestEnvironment();
        await env.InitializeAsync();
        await env.Store.ImportSubscribersAsync([101, 102], Ct);
        var draft = await env.DraftAsync();
        var decisions = await Task.WhenAll(env.Store.DecideAsync(draft.Id, 42, true, Ct),
            env.Store.DecideAsync(draft.Id, 42, true, Ct));
        Assert.Single(decisions, x => x == DecisionResult.Queued);
        Assert.Single(decisions, x => x == DecisionResult.AlreadyHandled);
        Assert.Equal(2, (await env.DeliveriesAsync()).Length);
    }

    [Fact]
    public async Task Cancellation_is_final_and_does_not_create_deliveries()
    {
        await using var env = new TestEnvironment();
        await env.InitializeAsync();
        await env.Store.ImportSubscribersAsync([101], Ct);
        var draft = await env.DraftAsync();
        Assert.Equal(DecisionResult.Cancelled, await env.Store.DecideAsync(draft.Id, 42, false, Ct));
        Assert.Equal(DecisionResult.AlreadyHandled, await env.Store.DecideAsync(draft.Id, 42, true, Ct));
        Assert.Empty(await env.DeliveriesAsync());
    }

    [Fact]
    public async Task Subscription_after_confirmation_does_not_change_recipient_snapshot()
    {
        await using var env = new TestEnvironment();
        await env.InitializeAsync();
        await env.Store.ImportSubscribersAsync([101], Ct);
        var draft = await env.DraftAsync();
        await env.Store.DecideAsync(draft.Id, 42, true, Ct);
        await env.Store.SetSubscriptionAsync(102, true, Ct);
        Assert.Equal(101, Assert.Single(await env.DeliveriesAsync()).ChatId);
    }

    [Fact]
    public async Task Old_confirmation_expires_without_sending()
    {
        await using var env = new TestEnvironment();
        await env.InitializeAsync();
        var draft = await env.DraftAsync();
        env.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(DecisionResult.Expired, await env.Store.DecideAsync(draft.Id, 42, true, Ct));
        Assert.Empty(await env.DeliveriesAsync());
    }

    [Fact]
    public async Task Album_waits_for_quiet_period_and_preserves_sorted_unique_message_ids()
    {
        await using var env = new TestEnvironment();
        await env.InitializeAsync();
        var first = await env.DraftAsync(20, "album-A");
        env.Clock.Advance(TimeSpan.FromMilliseconds(1000));
        var second = await env.DraftAsync(10, "album-A");
        await env.DraftAsync(10, "album-A");
        Assert.Equal(first.Id, second.Id);
        Assert.Empty(await env.Store.FinishAlbumsAsync(Ct));
        env.Clock.Advance(TimeSpan.FromMilliseconds(1600));
        Assert.Equal(first.Id, Assert.Single(await env.Store.FinishAlbumsAsync(Ct)));
        var notification = await env.Store.NextConfirmationAsync(Ct);
        Assert.Equal(new[] { 10, 20 }, notification!.Content.MessageIds);
    }

    [Fact]
    public async Task Confirmed_album_cannot_silently_change_when_a_late_part_arrives()
    {
        await using var env = new TestEnvironment();
        await env.InitializeAsync();
        await env.Store.ImportSubscribersAsync([101], Ct);
        var draft = await env.DraftAsync(10, "album-A");
        env.Clock.Advance(TimeSpan.FromSeconds(2));
        await env.Store.FinishAlbumsAsync(Ct);
        await env.Store.DecideAsync(draft.Id, 42, true, Ct);
        Assert.True((await env.DraftAsync(20, "album-A")).AlreadyQueued);
        await env.Processor.ProcessOnceAsync(Ct);
        Assert.Equal(new[] { 10 }, Assert.Single(env.Transport.Delivered).Content.MessageIds);
    }

    [Fact]
    public async Task Failed_confirmation_prompt_is_retried_and_is_not_sent_again_after_success()
    {
        await using var env = new TestEnvironment();
        await env.InitializeAsync();
        var draft = await env.DraftAsync();
        var notifier = new DraftNotifier(env.Store, env.Transport, env.Options);
        env.Transport.FailNextConfirmation = true;
        await Assert.ThrowsAsync<BotTransportException>(() => notifier.ProcessOnceAsync(Ct));
        Assert.True(await notifier.ProcessOnceAsync(Ct));
        Assert.False(await notifier.ProcessOnceAsync(Ct));
        Assert.Equal(draft.Id, Assert.Single(env.Transport.Confirmations).Id);
    }

    [Fact]
    public async Task Queue_and_receiver_offset_survive_a_new_store_instance()
    {
        await using var env = new TestEnvironment();
        await env.InitializeAsync();
        await env.Store.ImportSubscribersAsync([101], Ct);
        var draft = await env.DraftAsync();
        await env.Store.DecideAsync(draft.Id, 42, true, Ct);
        await env.Store.SaveOffsetAsync(123, Ct);
        var restarted = env.NewStore();
        Assert.Equal(123, await restarted.GetOffsetAsync(Ct));
        await new BroadcastProcessor(restarted, env.Transport, env.Options).ProcessOnceAsync(Ct);
        Assert.Equal(DeliveryStatus.Sent, Assert.Single(await env.DeliveriesAsync()).Status);
    }

    [Fact]
    public async Task Unsubscribing_skips_already_queued_delivery()
    {
        await using var env = new TestEnvironment();
        await env.InitializeAsync();
        await env.Store.ImportSubscribersAsync([101], Ct);
        var draft = await env.DraftAsync();
        await env.Store.DecideAsync(draft.Id, 42, true, Ct);
        await env.Store.SetSubscriptionAsync(101, false, Ct);
        await env.Processor.ProcessOnceAsync(Ct);
        Assert.Empty(env.Transport.Delivered);
        Assert.Equal(0, env.Transport.DeliveryAttempts);
        Assert.Equal(DeliveryStatus.Skipped, Assert.Single(await env.DeliveriesAsync()).Status);
    }

    [Fact]
    public async Task Blocked_user_is_deactivated_but_other_recipients_can_be_processed()
    {
        await using var env = new TestEnvironment();
        await env.InitializeAsync();
        await env.Store.ImportSubscribersAsync([101, 102], Ct);
        var draft = await env.DraftAsync();
        await env.Store.DecideAsync(draft.Id, 42, true, Ct);
        env.Transport.NextDeliveryError = new BotTransportException(TransportFailure.Blocked);
        await env.Processor.ProcessOnceAsync(Ct);
        await env.Processor.ProcessOnceAsync(Ct);
        Assert.Equal(1, (await env.Store.GetStatsAsync(Ct)).Subscribers);
        Assert.Single(await env.DeliveriesAsync(), x => x.Status == DeliveryStatus.Skipped);
        Assert.Single(env.Transport.Delivered);
    }

    [Fact]
    public async Task Rate_limit_respects_retry_after_and_then_retries_the_same_recipient()
    {
        await using var env = new TestEnvironment();
        await env.InitializeAsync();
        await env.Store.ImportSubscribersAsync([101], Ct);
        var draft = await env.DraftAsync();
        await env.Store.DecideAsync(draft.Id, 42, true, Ct);
        env.Transport.NextDeliveryError = new BotTransportException(TransportFailure.RateLimited, TimeSpan.FromSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(7), await env.Processor.ProcessOnceAsync(Ct));
        await env.Processor.ProcessOnceAsync(Ct);
        Assert.Equal(1, env.Transport.DeliveryAttempts);
        env.Clock.Advance(TimeSpan.FromSeconds(7));
        await env.Processor.ProcessOnceAsync(Ct);
        Assert.Equal(DeliveryStatus.Sent, Assert.Single(await env.DeliveriesAsync()).Status);
    }

    [Fact]
    public async Task Transient_failure_has_bounded_retries()
    {
        await using var env = new TestEnvironment();
        await env.InitializeAsync();
        env.Options.MaxDeliveryAttempts = 2;
        await env.Store.ImportSubscribersAsync([101], Ct);
        var draft = await env.DraftAsync();
        await env.Store.DecideAsync(draft.Id, 42, true, Ct);
        env.Transport.NextDeliveryError = new BotTransportException(TransportFailure.Transient);
        await env.Processor.ProcessOnceAsync(Ct);
        env.Clock.Advance(TimeSpan.FromMinutes(1));
        env.Transport.NextDeliveryError = new BotTransportException(TransportFailure.Transient);
        await env.Processor.ProcessOnceAsync(Ct);
        var delivery = Assert.Single(await env.DeliveriesAsync());
        Assert.Equal(DeliveryStatus.Failed, delivery.Status);
        Assert.Equal(2, delivery.Attempts);
    }

    [Fact]
    public async Task Completion_reports_real_outcomes_once()
    {
        await using var env = new TestEnvironment();
        await env.InitializeAsync();
        await env.Store.ImportSubscribersAsync([101], Ct);
        var draft = await env.DraftAsync();
        await env.Store.DecideAsync(draft.Id, 42, true, Ct);
        env.Transport.NextDeliveryError = new BotTransportException(TransportFailure.Permanent);
        await env.Processor.ProcessOnceAsync(Ct);
        await env.Processor.ProcessOnceAsync(Ct);
        await env.Processor.ProcessOnceAsync(Ct);
        var text = Assert.Single(env.Transport.Texts).Text;
        Assert.Contains("Отправлено: 0. Ошибок: 1.", text);
    }

    [Theory]
    [InlineData(99, ChatType.Private)]
    [InlineData(42, ChatType.Group)]
    public async Task Callback_requires_admin_and_private_chat(long actor, ChatType chatType)
    {
        await using var env = new TestEnvironment();
        await env.InitializeAsync();
        var draft = await env.DraftAsync();
        await new UpdateHandler(env.Store, env.Transport, env.Options)
            .HandleAsync(TestEnvironment.Callback(draft.Id, actor, chatType: chatType), Ct);
        Assert.Contains("Недостаточно прав", Assert.Single(env.Transport.CallbackAnswers));
        Assert.Equal(0, (await env.Store.GetStatsAsync(Ct)).Queued);
    }

    [Fact]
    public async Task Unknown_callback_does_not_queue_anything()
    {
        await using var env = new TestEnvironment();
        await env.InitializeAsync();
        var update = TestEnvironment.Callback(Guid.NewGuid());
        update.CallbackQuery!.Data = "malformed";
        await new UpdateHandler(env.Store, env.Transport, env.Options).HandleAsync(update, Ct);
        Assert.Contains("Неизвестная", Assert.Single(env.Transport.CallbackAnswers));
    }

    [Fact]
    public async Task Stop_command_unsubscribes_and_non_admin_messages_never_create_drafts()
    {
        await using var env = new TestEnvironment();
        await env.InitializeAsync();
        await env.Store.ImportSubscribersAsync([101], Ct);
        var handler = new UpdateHandler(env.Store, env.Transport, env.Options);
        var message = new Message
        {
            Id = 1, Chat = new Chat { Id = 101, Type = ChatType.Private },
            From = new User { Id = 101, FirstName = "Test" }, Text = "/stop"
        };
        await handler.HandleAsync(new Update { Message = message }, Ct);
        Assert.False(await env.Store.IsSubscribedAsync(101, Ct));
        message.Text = "Not an admin broadcast";
        await handler.HandleAsync(new Update { Message = message }, Ct);
        Assert.Null(await env.Store.NextConfirmationAsync(Ct));
    }
}
