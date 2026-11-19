using Microsoft.Extensions.Options;

namespace WakeMesh.Api.Services;

/// <summary>
/// Best-effort outbox recovery: startup drain + bounded periodic drain.
/// Does not guarantee always-on/F1 sleep precision. When FCM is missing/unconfigured,
/// uses bounded exponential backoff (capped) to avoid busy-loop.
/// Pending rows also recover on the next settings request path and on server restart.
/// </summary>
public sealed class PushDrainHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<FcmOptions> _options;
    private readonly ILogger<PushDrainHostedService> _logger;

    public PushDrainHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<FcmOptions> options,
        ILogger<PushDrainHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup recovery (fire once; failures must not kill the host loop).
        await SafeDrainAsync(stoppingToken).ConfigureAwait(false);

        var notConfiguredStreak = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _options.Value;
            var configured = opts.Configured;
            TimeSpan delay;

            if (!configured)
            {
                notConfiguredStreak = Math.Min(notConfiguredStreak + 1, 16);
                var maxBackoff = Math.Clamp(
                    opts.NotConfiguredBackoffMaxSeconds <= 0 ? 300 : opts.NotConfiguredBackoffMaxSeconds,
                    5,
                    3600);
                var seconds = Math.Min(maxBackoff, 5 * (1 << Math.Min(notConfiguredStreak - 1, 6)));
                delay = TimeSpan.FromSeconds(seconds);
            }
            else
            {
                notConfiguredStreak = 0;
                var interval = Math.Clamp(
                    opts.DrainIntervalSeconds <= 0 ? 30 : opts.DrainIntervalSeconds,
                    5,
                    3600);
                await SafeDrainAsync(stoppingToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(interval);
            }

            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task SafeDrainAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var push = scope.ServiceProvider.GetRequiredService<PushDispatchService>();
            await push.DrainPendingAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Background push drain skipped or failed: {ErrorType}", ex.GetType().Name);
        }
    }
}
