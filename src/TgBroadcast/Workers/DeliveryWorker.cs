using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TgBroadcast.Services;

namespace TgBroadcast.Workers;

public sealed class DeliveryWorker(BroadcastProcessor processor, ILogger<DeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(5);
            try { delay = await processor.ProcessOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (BotTransportException error)
            {
                logger.LogWarning("Could not notify administrator: {Failure}.", error.Failure);
                delay = error.RetryAfter ?? delay;
            }
            catch (Exception error) { logger.LogError("Queue processing failed ({Type}).", error.GetType().Name); }
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
