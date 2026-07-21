using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Rystem.PlayFramework;

internal sealed class RuntimeDescriptionBackgroundService : BackgroundService
{
    private readonly RuntimeDescriptionCatalogManager _manager;
    private readonly IRuntimeDescriptionChangeTokenSource? _changeTokenSource;
    private readonly ILogger<RuntimeDescriptionBackgroundService> _logger;
    private IDisposable? _changeSubscription;
    private bool _backgroundLoopStarted;

    public RuntimeDescriptionBackgroundService(
        RuntimeDescriptionCatalogManager manager,
        IRuntimeDescriptionChangeTokenSource? changeTokenSource,
        ILogger<RuntimeDescriptionBackgroundService> logger)
    {
        _manager = manager;
        _changeTokenSource = changeTokenSource;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_manager.HasDynamicDescriptions
            || _manager.Settings.RefreshMode == RuntimeDescriptionRefreshMode.EveryRequest)
        {
            return;
        }

        if (_manager.Settings.RefreshAtStartup)
        {
            var result = await _manager.RefreshAtStartupAsync(cancellationToken).ConfigureAwait(false);
            if (result.Outcome == RuntimeDescriptionRefreshOutcome.Failed
                && _manager.Settings.FailureMode == RuntimeDescriptionFailureMode.Throw)
            {
                throw new InvalidOperationException(
                    result.ErrorMessage ?? "Runtime description startup refresh failed.");
            }
        }

        if (_manager.Settings.RefreshMode == RuntimeDescriptionRefreshMode.Background)
        {
            if (_manager.Settings.RefreshOnChange && _changeTokenSource is not null)
            {
                _changeSubscription = ChangeToken.OnChange(
                    _changeTokenSource.GetChangeToken,
                    TriggerChangeRefresh);
            }

            _backgroundLoopStarted = true;
            await base.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _changeSubscription?.Dispose();
        _changeSubscription = null;
        if (_backgroundLoopStarted)
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_manager.Settings.BackgroundRefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await _manager.RefreshIfIdleAsync(RuntimeDescriptionRefreshReason.Timer, stoppingToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Runtime description background loop stopping.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Runtime description background loop stopped unexpectedly.");
        }
    }

    private void TriggerChangeRefresh()
        => _ = RefreshFromChangeNotificationAsync();

    private async Task RefreshFromChangeNotificationAsync()
    {
        try
        {
            await _manager.RefreshIfIdleAsync(
                RuntimeDescriptionRefreshReason.ChangeNotification,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Runtime description change-notification refresh failed unexpectedly.");
        }
    }
}
