namespace Homestead.Features.Publishing;

public sealed class ScheduledPublicationWorker(IServiceScopeFactory scopes, ILogger<ScheduledPublicationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PublishingService>().RunSchedulesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Scheduled publication check failed; saved schedules will retry."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
