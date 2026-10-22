namespace InvoiceMesh.Services.Automation;

/// <summary>
/// Looks every half minute whether an intake source or a planned export is due and runs it. It lives as long as the application; a failure of one tick
/// never ends it.
/// </summary>
public sealed class AutomationScheduler(IServiceScopeFactory scopes, ILogger<AutomationScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken);
                await Task.Delay(Tick, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // the application is stopping
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using (var scope = scopes.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<IntakeService>().RunDueAsync(ct);
            }

            using (var scope = scopes.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<ExportPlanService>().RunDueAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The automation scheduler failed; it tries again at the next tick.");
        }
    }
}
