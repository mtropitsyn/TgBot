using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TgBroadcast.Services;

namespace TgBroadcast.Workers;

public sealed class DraftWorker(DraftNotifier notifier, ILogger<DraftWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromMilliseconds(500);
            try { await notifier.ProcessOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (BotTransportException error)
            {
                logger.LogWarning("Draft notification failed: {Failure}.", error.Failure);
                delay = error.RetryAfter ?? TimeSpan.FromSeconds(5);
            }
            catch (Exception error)
            {
                logger.LogError("Draft processing failed ({Type}).", error.GetType().Name);
                delay = TimeSpan.FromSeconds(5);
            }
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
