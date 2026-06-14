using Microsoft.Extensions.Options;
using MkGreens.IdentitySync.Configuration;

namespace MkGreens.IdentitySync.Services;

public sealed class IdentitySyncHostedService : BackgroundService
{
    private readonly IdentitySyncOrchestrator _orchestrator;
    private readonly SyncOptions _syncOptions;
    private readonly ILogger<IdentitySyncHostedService> _logger;

    public IdentitySyncHostedService(
        IdentitySyncOrchestrator orchestrator,
        IOptions<SyncOptions> syncOptions,
        ILogger<IdentitySyncHostedService> logger)
    {
        _orchestrator = orchestrator;
        _syncOptions = syncOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _syncOptions.IntervalMinutes));

        if (_syncOptions.RunImmediatelyOnStart)
        {
            await RunOnceAsync(stoppingToken);
        }

        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting scheduled identity sync run.");
        await _orchestrator.RunAsync(cancellationToken);
    }
}
