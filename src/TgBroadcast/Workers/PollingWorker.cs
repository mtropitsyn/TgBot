using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.Enums;
using TgBroadcast.Services;
using TgBroadcast.Telegram;

namespace TgBroadcast.Workers;

public sealed class PollingWorker(ITelegramBotClient client, UpdateHandler handler, BotStore store,
    ILogger<PollingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var offset = await store.GetOffsetAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await client.GetUpdates(offset, timeout: 25,
                    allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery], cancellationToken: stoppingToken);
                foreach (var update in updates)
                {
                    try { await handler.HandleAsync(update, stoppingToken); }
                    catch (BotTransportException error) when (error.Failure is TransportFailure.Blocked or TransportFailure.Permanent)
                    { logger.LogWarning("Update reply rejected: {Failure}.", error.Failure); }
                    offset = update.Id + 1;
                    await store.SaveOffsetAsync(offset, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (ApiRequestException error) when (error.ErrorCode is 401 or 409)
            {
                logger.LogCritical("Telegram rejected polling (code {Code}). Check the token and stop other bot instances/webhooks.", error.ErrorCode);
                throw new InvalidOperationException("Telegram polling cannot continue.");
            }
            catch (Exception error)
            {
                // SDK HTTP errors may contain the token in their URL: never log exception text.
                logger.LogWarning("Polling paused after {Type}.", error.GetType().Name);
                var delay = error is BotTransportException { RetryAfter: { } retry } ? retry : TimeSpan.FromSeconds(5);
                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
