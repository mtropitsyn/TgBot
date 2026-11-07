using System.Net;
using System.Text;
using System.Text.Json;
using Telegram.Bot;
using TgBroadcast.Services;
using TgBroadcast.Telegram;
using Xunit;

namespace TgBroadcast.Tests;

public sealed class TransportTests
{
    private sealed class FakeHttp(string response) : HttpMessageHandler
    {
        public string? Method { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.RequestUri!.Segments.Last();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var payload = JsonDocument.Parse(response);
            var status = payload.RootElement.TryGetProperty("error_code", out var code)
                ? (HttpStatusCode)code.GetInt32() : HttpStatusCode.OK;
            return new HttpResponseMessage(status)
            { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private static TelegramTransport Transport(HttpClient http) => new(new TelegramBotClient(
        new TelegramBotClientOptions("123:offline-test-placeholder") { RetryCount = 0 }, http));

    [Fact]
    public async Task Album_uses_copy_messages_with_all_message_ids()
    {
        using var handler = new FakeHttp("""{"ok":true,"result":[{"message_id":1},{"message_id":2}]}""");
        using var http = new HttpClient(handler);
        await Transport(http).DeliverAsync(101, new MessageContent(42, [10, 20], false), CancellationToken.None);
        Assert.Equal("copyMessages", handler.Method);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(new[] { 10, 20 }, body.RootElement.GetProperty("message_ids").EnumerateArray().Select(x => x.GetInt32()));
    }

    [Theory]
    [InlineData(403, TransportFailure.Blocked)]
    [InlineData(429, TransportFailure.RateLimited)]
    [InlineData(500, TransportFailure.Transient)]
    [InlineData(400, TransportFailure.Permanent)]
    public async Task Telegram_errors_are_classified_without_retaining_sensitive_urls(int code, TransportFailure expected)
    {
        using var handler = new FakeHttp(JsonSerializer.Serialize(new
        { ok = false, error_code = code, description = "offline error", parameters = new { retry_after = 7 } }));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<BotTransportException>(() =>
            Transport(http).DeliverAsync(101, new MessageContent(42, [10], false), CancellationToken.None));
        Assert.Equal(expected, error.Failure);
        Assert.DoesNotContain("123:", error.ToString());
        if (code == 429) Assert.Equal(TimeSpan.FromSeconds(7), error.RetryAfter);
    }

    [Fact]
    public async Task Partially_copied_album_is_not_reported_as_successful()
    {
        using var handler = new FakeHttp("""{"ok":true,"result":[{"message_id":1}]}""");
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<BotTransportException>(() =>
            Transport(http).DeliverAsync(101, new MessageContent(42, [10, 20], false), CancellationToken.None));
        Assert.Equal(TransportFailure.Permanent, error.Failure);
    }

    [Fact]
    public async Task Confirmation_replies_to_source_and_buttons_identify_the_exact_draft()
    {
        using var handler = new FakeHttp("""{"ok":true,"result":{"message_id":100,"date":1,"chat":{"id":42,"type":"private"}}}""");
        using var http = new HttpClient(handler);
        var draftId = Guid.NewGuid();
        await Transport(http).SendConfirmationAsync(42, draftId, new MessageContent(42, [10, 20], false), CancellationToken.None);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(10, body.RootElement.GetProperty("reply_parameters").GetProperty("message_id").GetInt32());
        var buttons = body.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard")[0];
        Assert.Equal($"bc:yes:{draftId:N}", buttons[0].GetProperty("callback_data").GetString());
        Assert.Equal($"bc:no:{draftId:N}", buttons[1].GetProperty("callback_data").GetString());
    }

    [Fact]
    public async Task Poll_is_forwarded_instead_of_copied()
    {
        using var handler = new FakeHttp("""{"ok":true,"result":{"message_id":100,"date":1,"chat":{"id":101,"type":"private"}}}""");
        using var http = new HttpClient(handler);
        await Transport(http).DeliverAsync(101, new MessageContent(42, [10], true), CancellationToken.None);
        Assert.Equal("forwardMessage", handler.Method);
    }

    [Fact]
    public async Task Invalid_response_is_a_bounded_transient_failure()
    {
        using var handler = new FakeHttp("""{"ok":true}""");
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<BotTransportException>(() =>
            Transport(http).DeliverAsync(101, new MessageContent(42, [10], false), CancellationToken.None));
        Assert.Equal(TransportFailure.Transient, error.Failure);
    }
}
