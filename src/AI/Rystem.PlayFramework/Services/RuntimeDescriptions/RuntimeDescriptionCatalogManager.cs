using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rystem.PlayFramework.Configuration;
using Rystem.PlayFramework.Telemetry;

namespace Rystem.PlayFramework;

using DiagnosticsStopwatch = System.Diagnostics.Stopwatch;

internal sealed class RuntimeDescriptionCatalogManager : IRuntimeDescriptionRefresher, IFactoryName
{
    private const string HashAlgorithm = "sha256-v1";
    private static readonly UTF8Encoding s_strictUtf8 = new(false, true);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFactory<List<SceneConfiguration>> _configurationFactory;
    private readonly IFactory<PlayFrameworkSettings> _settingsFactory;
    private readonly IFactory<IJsonService> _jsonServiceFactory;
    private readonly IFactory<IRuntimeDescriptionSnapshotStore> _snapshotStoreFactory;
    private readonly ILogger<RuntimeDescriptionCatalogManager> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly ConcurrentDictionary<string, MaterializedSceneCatalog> _history = new(StringComparer.Ordinal);

    private string _factoryName = "default";
    private IReadOnlyList<SceneConfiguration> _templates = [];
    private RuntimeDescriptionSettings _settings = new();
    private IJsonService _jsonService = null!;
    private IRuntimeDescriptionSnapshotStore _snapshotStore = null!;
    private string _templateHash = string.Empty;
    private bool _hasDynamicDescriptions;
    private PublishedRuntimeDescriptionState? _current;

    public RuntimeDescriptionCatalogManager(
        IServiceScopeFactory scopeFactory,
        IFactory<List<SceneConfiguration>> configurationFactory,
        IFactory<PlayFrameworkSettings> settingsFactory,
        IFactory<IJsonService> jsonServiceFactory,
        IFactory<IRuntimeDescriptionSnapshotStore> snapshotStoreFactory,
        ILogger<RuntimeDescriptionCatalogManager> logger)
    {
        _scopeFactory = scopeFactory;
        _configurationFactory = configurationFactory;
        _settingsFactory = settingsFactory;
        _jsonServiceFactory = jsonServiceFactory;
        _snapshotStoreFactory = snapshotStoreFactory;
        _logger = logger;
    }

    public bool FactoryNameAlreadySetup { get; set; }
    internal bool HasDynamicDescriptions => _hasDynamicDescriptions;
    internal RuntimeDescriptionSettings Settings => _settings;
    internal MaterializedSceneCatalog? CurrentCatalog => Volatile.Read(ref _current)?.Catalog;
    internal string? CurrentSourceVersion => Volatile.Read(ref _current)?.SourceVersion;

    public void SetFactoryName(AnyOf<string?, Enum>? name)
    {
        _factoryName = name?.ToString() ?? "default";
        _templates = _configurationFactory.Create(name) ?? [];
        _settings = (_settingsFactory.Create(name) ?? new PlayFrameworkSettings()).RuntimeDescriptions;
        _jsonService = _jsonServiceFactory.Create(name) ?? new DefaultJsonService();
        _snapshotStore = _snapshotStoreFactory.Create(name)
            ?? throw new InvalidOperationException($"Runtime description snapshot store not registered for factory '{_factoryName}'.");
        ValidateSettings(_settings);
        _templateHash = ComputeHash(BuildTemplateCanonical(_templates));
        _hasDynamicDescriptions = EnumerateRuntimeConfigurations(_templates).Any();
    }

    public Task<RuntimeDescriptionRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
        => RefreshCoreAsync(RuntimeDescriptionRefreshReason.Manual, waitForLock: true, cancellationToken);

    internal Task<RuntimeDescriptionRefreshResult> RefreshAtStartupAsync(CancellationToken cancellationToken)
        => RefreshCoreAsync(RuntimeDescriptionRefreshReason.Startup, waitForLock: true, cancellationToken);

    internal Task<RuntimeDescriptionRefreshResult> RefreshIfIdleAsync(
        RuntimeDescriptionRefreshReason reason,
        CancellationToken cancellationToken)
        => RefreshCoreAsync(reason, waitForLock: false, cancellationToken);

    internal async Task<RuntimeDescriptionAcquisition> AcquireAsync(
        string? requestedCatalogId,
        CancellationToken cancellationToken)
    {
        var started = DiagnosticsStopwatch.GetTimestamp();
        PublishedRuntimeDescriptionState state;
        var isRequestLocal = false;

        if (_settings.RefreshMode == RuntimeDescriptionRefreshMode.EveryRequest && _hasDynamicDescriptions)
        {
            state = await CreateRequestLocalStateAsync(cancellationToken).ConfigureAwait(false);
            isRequestLocal = true;
        }
        else
        {
            state = Volatile.Read(ref _current) ?? await EnsureInitialStateAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(requestedCatalogId)
            && !string.Equals(requestedCatalogId, state.Catalog.Identity.CatalogId, StringComparison.Ordinal))
        {
            state = await AcquirePinnedStateAsync(requestedCatalogId, state, cancellationToken).ConfigureAwait(false);
        }

        var acquisitionDuration = DiagnosticsStopwatch.GetElapsedTime(started);
        var executionInfo = new RuntimeDescriptionExecutionInfo
        {
            CatalogIdentity = state.Catalog.Identity,
            RequestedCatalogId = requestedCatalogId,
            SourceVersion = state.SourceVersion,
            HasUniformSourceVersion = state.HasUniformSourceVersion,
            RefreshMode = _settings.RefreshMode,
            ConsistencyMode = _settings.ConsistencyMode,
            IsRequestLocal = isRequestLocal,
            RecoverySource = state.RecoverySource,
            UsedFallback = state.UsedFallback,
            LastValidationOperationId = state.LastValidationOperationId,
            LastValidatedAt = state.LastValidatedAt,
            AcquisitionDuration = acquisitionDuration
        };

        return new RuntimeDescriptionAcquisition(
            state.Catalog,
            executionInfo,
            state.Catalog.CreateView(executionInfo));
    }

    private async Task<PublishedRuntimeDescriptionState> EnsureInitialStateAsync(CancellationToken cancellationToken)
    {
        if (!_hasDynamicDescriptions)
        {
            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var existing = Volatile.Read(ref _current);
                if (existing is not null)
                    return existing;

                var operationId = Guid.NewGuid();
                var candidate = BuildStaticCandidate(requireComplete: true, RuntimeDescriptionRefreshReason.Startup);
                return Publish(candidate, operationId, RuntimeDescriptionRecoverySource.None, usedFallback: false);
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        if (_settings.RefreshMode == RuntimeDescriptionRefreshMode.Manual)
        {
            if (_settings.FailureMode == RuntimeDescriptionFailureMode.Throw)
                throw new InvalidOperationException($"Runtime descriptions for factory '{_factoryName}' require a successful manual refresh before the first request.");

            var recovery = await RecoverWithoutSourceAsync(Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
            return recovery?.State
                ?? throw new InvalidOperationException($"No complete runtime description catalog is available for factory '{_factoryName}'.");
        }

        var result = await RefreshAtStartupAsync(cancellationToken).ConfigureAwait(false);
        var state = Volatile.Read(ref _current);
        if (state is not null)
            return state;

        throw new InvalidOperationException(result.ErrorMessage ?? $"Runtime descriptions could not be initialized for factory '{_factoryName}'.");
    }

    private async Task<PublishedRuntimeDescriptionState> CreateRequestLocalStateAsync(CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        using var activity = StartRefreshActivity(operationId, RuntimeDescriptionRefreshReason.EveryRequest);
        activity?.AddEvent(new ActivityEvent(PlayFrameworkActivitySource.Events.RuntimeDescriptionRefreshTriggered));
        _logger.LogInformation(
            "playframework.runtime_metadata.refresh_triggered OperationId={OperationId} Factory={FactoryName} Reason=EveryRequest",
            operationId,
            _factoryName);

        try
        {
            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CompleteCancelledRefresh(operationId, RuntimeDescriptionRefreshReason.EveryRequest, activity);
            throw;
        }
        var sourceStarted = DiagnosticsStopwatch.GetTimestamp();
        try
        {
            try
            {
                activity?.AddEvent(new ActivityEvent(PlayFrameworkActivitySource.Events.RuntimeDescriptionRefreshStarted));
                _logger.LogInformation(
                    "playframework.runtime_metadata.refresh_started OperationId={OperationId} Factory={FactoryName} Reason=EveryRequest",
                    operationId,
                    _factoryName);
                var candidate = await ResolveCandidateAsync(RuntimeDescriptionRefreshReason.EveryRequest, cancellationToken).ConfigureAwait(false);
                var sourceDuration = candidate.SourceDuration;
                var global = Volatile.Read(ref _current);
                var outcome = global is not null && global.Catalog.Identity.CatalogId == candidate.Identity.CatalogId
                    ? RuntimeDescriptionRefreshOutcome.Unchanged
                    : RuntimeDescriptionRefreshOutcome.Changed;
                var materializationStarted = DiagnosticsStopwatch.GetTimestamp();
                var catalog = global is not null && global.Catalog.Identity.CatalogId == candidate.Identity.CatalogId
                    ? global.Catalog
                    : Materialize(candidate);
                var materializationDuration = outcome == RuntimeDescriptionRefreshOutcome.Changed
                    ? DiagnosticsStopwatch.GetElapsedTime(materializationStarted)
                    : TimeSpan.Zero;
                var state = new PublishedRuntimeDescriptionState(
                    catalog,
                    candidate.SourceVersion,
                    candidate.HasUniformSourceVersion,
                    operationId,
                    DateTimeOffset.UtcNow,
                    RuntimeDescriptionRecoverySource.None,
                    false);

                if (outcome == RuntimeDescriptionRefreshOutcome.Changed)
                {
                    _logger.LogInformation(
                        "playframework.runtime_metadata.change_detected OperationId={OperationId} Factory={FactoryName} CandidateCatalogId={CandidateCatalogId} ChangedItemCount={ChangedItemCount}",
                        operationId,
                        _factoryName,
                        catalog.Identity.CatalogId,
                        CountChanged(global?.Catalog.Descriptions, candidate.Descriptions));
                    activity?.AddEvent(new ActivityEvent(PlayFrameworkActivitySource.Events.RuntimeDescriptionChangeDetected));
                }

                CompleteRefresh(
                    CreateResult(
                        operationId,
                        outcome,
                        global?.Catalog.Identity.CatalogId,
                        state,
                        RuntimeDescriptionSnapshotStoreOutcome.NotAttempted,
                        outcome == RuntimeDescriptionRefreshOutcome.Changed
                            ? CountChanged(global?.Catalog.Descriptions, candidate.Descriptions)
                            : 0,
                        new RefreshTimings(
                            Source: sourceDuration,
                            Materialization: materializationDuration,
                            Validation: candidate.ValidationDuration,
                            Hash: candidate.HashDuration)),
                    RuntimeDescriptionRefreshReason.EveryRequest,
                    activity);
                return state;
            }
            catch (OperationCanceledException)
            {
                CompleteCancelledRefresh(operationId, RuntimeDescriptionRefreshReason.EveryRequest, activity);
                throw;
            }
            catch (Exception ex) when (_settings.FailureMode == RuntimeDescriptionFailureMode.UseFallback)
            {
                var sourceDuration = DiagnosticsStopwatch.GetElapsedTime(sourceStarted);
                _logger.LogWarning(ex, "playframework.runtime_metadata.source_resolution_failed OperationId={OperationId} Factory={FactoryName} Reason=EveryRequest", operationId, _factoryName);
                return await HandleEveryRequestFallbackAsync(operationId, ex, sourceDuration, activity, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var sourceDuration = DiagnosticsStopwatch.GetElapsedTime(sourceStarted);
                _logger.LogWarning(
                    ex,
                    "playframework.runtime_metadata.source_resolution_failed OperationId={OperationId} Factory={FactoryName} Reason=EveryRequest",
                    operationId,
                    _factoryName);
                CompleteRefresh(new RuntimeDescriptionRefreshResult
                {
                    OperationId = operationId,
                    Outcome = RuntimeDescriptionRefreshOutcome.Failed,
                    PreviousCatalogId = Volatile.Read(ref _current)?.Catalog.Identity.CatalogId,
                    CurrentCatalogId = Volatile.Read(ref _current)?.Catalog.Identity.CatalogId,
                    CatalogIdentity = Volatile.Read(ref _current)?.Catalog.Identity,
                    TemplateHash = _templateHash,
                    SourceDuration = sourceDuration,
                    FailureStage = "source",
                    ErrorMessage = ex.Message
                }, RuntimeDescriptionRefreshReason.EveryRequest, activity);
                throw new InvalidOperationException(
                    $"Runtime description refresh (EveryRequest) failed for factory '{_factoryName}'.", ex);
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<PublishedRuntimeDescriptionState> HandleEveryRequestFallbackAsync(
        Guid operationId,
        Exception sourceEx,
        TimeSpan sourceDuration,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var current = Volatile.Read(ref _current);
        RuntimeDescriptionRecovery? recovery;
        try
        {
            recovery = current is null
                ? await RecoverWithoutSourceAsync(operationId, cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (OperationCanceledException)
        {
            CompleteCancelledRefresh(operationId, RuntimeDescriptionRefreshReason.EveryRequest, activity);
            throw;
        }

        var recovered = current ?? recovery?.State
            ?? throw new InvalidOperationException($"No runtime description fallback is available for factory '{_factoryName}'.", sourceEx);
        var outcome = current is null
            ? RuntimeDescriptionRefreshOutcome.Changed
            : RuntimeDescriptionRefreshOutcome.Failed;
        CompleteRefresh(
            outcome == RuntimeDescriptionRefreshOutcome.Failed
                ? CreateFailedResult(operationId, recovered, sourceDuration, RuntimeDescriptionRecoverySource.CurrentCatalog, sourceEx)
                : CreateResult(
                    operationId,
                    outcome,
                    null,
                    recovered,
                    recovery!.SnapshotStoreOutcome,
                    recovered.Catalog.Descriptions.Count,
                    new RefreshTimings(Source: sourceDuration)),
            RuntimeDescriptionRefreshReason.EveryRequest,
            activity);
        return recovered;
    }

    private async Task<PublishedRuntimeDescriptionState> AcquirePinnedStateAsync(
        string requestedCatalogId,
        PublishedRuntimeDescriptionState latest,
        CancellationToken cancellationToken)
    {
        if (_history.TryGetValue(requestedCatalogId, out var historic))
        {
            return new PublishedRuntimeDescriptionState(
                historic,
                historic.Identity.SourceVersion,
                historic.Identity.HasUniformSourceVersion,
                Guid.Empty,
                historic.Identity.PublishedAt,
                RuntimeDescriptionRecoverySource.None,
                false);
        }

        RuntimeDescriptionSnapshot? snapshot = null;
        try
        {
            snapshot = await _snapshotStore.GetAsync(_factoryName, requestedCatalogId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "playframework.runtime_metadata.snapshot_store_read_failed Factory={FactoryName} CatalogId={CatalogId}", _factoryName, requestedCatalogId);
        }

        var rejectionReason = string.Empty;
        if (snapshot is not null && TryMaterializeSnapshot(snapshot, out var recovered, out rejectionReason))
        {
            _history[recovered.Identity.CatalogId] = recovered;
            return new PublishedRuntimeDescriptionState(
                recovered,
                snapshot.SourceVersion,
                snapshot.HasUniformSourceVersion,
                Guid.Empty,
                snapshot.LastValidatedAt,
                RuntimeDescriptionRecoverySource.SnapshotStore,
                true);
        }

        if (snapshot is not null)
            _logger.LogWarning("playframework.runtime_metadata.snapshot_rejected Factory={FactoryName} Reason={Reason}", _factoryName, rejectionReason);

        _logger.LogWarning(
            "playframework.runtime_metadata.pinned_catalog_miss Factory={FactoryName} RequestedCatalogId={RequestedCatalogId} CurrentCatalogId={CurrentCatalogId}",
            _factoryName,
            requestedCatalogId,
            latest.Catalog.Identity.CatalogId);

        if (_settings.MissingVersionBehavior == MissingRuntimeDescriptionVersionBehavior.Throw)
            throw new InvalidOperationException($"Runtime description catalog '{requestedCatalogId}' is not available for factory '{_factoryName}'.");

        return latest;
    }

    private async Task<RuntimeDescriptionRefreshResult> RefreshCoreAsync(
        RuntimeDescriptionRefreshReason reason,
        bool waitForLock,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        using var activity = StartRefreshActivity(operationId, reason);
        activity?.AddEvent(new ActivityEvent(PlayFrameworkActivitySource.Events.RuntimeDescriptionRefreshTriggered));
        _logger.LogInformation(
            "playframework.runtime_metadata.refresh_triggered OperationId={OperationId} Factory={FactoryName} Reason={Reason}",
            operationId,
            _factoryName,
            reason);

        bool acquired;
        try
        {
            acquired = waitForLock
                ? await WaitAndReturnTrueAsync(cancellationToken).ConfigureAwait(false)
                : await _refreshLock.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CompleteCancelledRefresh(operationId, reason, activity);
            throw;
        }

        if (!acquired)
        {
            return CompleteRefresh(new RuntimeDescriptionRefreshResult
            {
                OperationId = operationId,
                Outcome = RuntimeDescriptionRefreshOutcome.SkippedBusy,
                CurrentCatalogId = Volatile.Read(ref _current)?.Catalog.Identity.CatalogId,
                TemplateHash = _templateHash
            }, reason, activity);
        }

        var sourceStarted = DiagnosticsStopwatch.GetTimestamp();
        try
        {
            _logger.LogInformation(
                "playframework.runtime_metadata.refresh_started OperationId={OperationId} Factory={FactoryName} Reason={Reason}",
                operationId,
                _factoryName,
                reason);
            activity?.AddEvent(new ActivityEvent(PlayFrameworkActivitySource.Events.RuntimeDescriptionRefreshStarted));

            var candidate = await ResolveCandidateAsync(reason, cancellationToken).ConfigureAwait(false);
            var previous = Volatile.Read(ref _current);
            var now = DateTimeOffset.UtcNow;

            if (previous is not null && previous.Catalog.Identity.CatalogId == candidate.Identity.CatalogId)
            {
                return CompleteRefresh(
                    await HandleUnchangedCatalogAsync(operationId, candidate, previous, now, cancellationToken).ConfigureAwait(false),
                    reason, activity);
            }

            return CompleteRefresh(
                await HandleChangedCatalogAsync(operationId, candidate, previous, now, activity, cancellationToken).ConfigureAwait(false),
                reason, activity);
        }
        catch (OperationCanceledException)
        {
            CompleteCancelledRefresh(operationId, reason, activity);
            throw;
        }
        catch (Exception ex)
        {
            var sourceDuration = DiagnosticsStopwatch.GetElapsedTime(sourceStarted);
            return CompleteRefresh(
                await HandleRefreshFailureAsync(operationId, ex, sourceDuration, reason, activity, cancellationToken).ConfigureAwait(false),
                reason, activity);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<RuntimeDescriptionRefreshResult> HandleUnchangedCatalogAsync(
        Guid operationId,
        ResolvedCandidate candidate,
        PublishedRuntimeDescriptionState previous,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var updated = previous with
        {
            SourceVersion = candidate.SourceVersion,
            HasUniformSourceVersion = candidate.HasUniformSourceVersion,
            LastValidationOperationId = operationId,
            LastValidatedAt = now,
            RecoverySource = RuntimeDescriptionRecoverySource.None,
            UsedFallback = false
        };
        var (storeOutcome, storeDuration) = await PersistSnapshotAsync(operationId, updated, cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _current, updated);

        return CreateResult(
            operationId,
            RuntimeDescriptionRefreshOutcome.Unchanged,
            previous.Catalog.Identity.CatalogId,
            updated,
            storeOutcome,
            changedItemCount: 0,
            new RefreshTimings(
                Source: candidate.SourceDuration,
                Store: storeDuration,
                Validation: candidate.ValidationDuration,
                Hash: candidate.HashDuration));
    }

    private async Task<RuntimeDescriptionRefreshResult> HandleChangedCatalogAsync(
        Guid operationId,
        ResolvedCandidate candidate,
        PublishedRuntimeDescriptionState? previous,
        DateTimeOffset now,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var changedItemCount = CountChanged(previous?.Catalog.Descriptions, candidate.Descriptions);
        _logger.LogInformation(
            "playframework.runtime_metadata.change_detected OperationId={OperationId} Factory={FactoryName} CandidateCatalogId={CandidateCatalogId} ChangedItemCount={ChangedItemCount}",
            operationId,
            _factoryName,
            candidate.Identity.CatalogId,
            changedItemCount);
        activity?.AddEvent(new ActivityEvent(
            PlayFrameworkActivitySource.Events.RuntimeDescriptionChangeDetected,
            tags: new ActivityTagsCollection
            {
                { "playframework.runtime_metadata.candidate_catalog_id", candidate.Identity.CatalogId },
                { "playframework.runtime_metadata.changed_item_count", changedItemCount }
            }));

        var materializationStarted = DiagnosticsStopwatch.GetTimestamp();
        var catalog = Materialize(candidate);
        var materializationDuration = DiagnosticsStopwatch.GetElapsedTime(materializationStarted);
        var state = new PublishedRuntimeDescriptionState(
            catalog,
            candidate.SourceVersion,
            candidate.HasUniformSourceVersion,
            operationId,
            now,
            RuntimeDescriptionRecoverySource.None,
            false);

        var (storeOutcome, storeDuration) = await PersistSnapshotAsync(operationId, state, cancellationToken).ConfigureAwait(false);

        var publicationStarted = DiagnosticsStopwatch.GetTimestamp();
        Interlocked.Exchange(ref _current, state);
        _history[catalog.Identity.CatalogId] = catalog;
        PruneHistory();
        var publicationDuration = DiagnosticsStopwatch.GetElapsedTime(publicationStarted);

        return CreateResult(
            operationId,
            RuntimeDescriptionRefreshOutcome.Changed,
            previous?.Catalog.Identity.CatalogId,
            state,
            storeOutcome,
            changedItemCount,
            new RefreshTimings(
                Source: candidate.SourceDuration,
                Store: storeDuration,
                Materialization: materializationDuration,
                Publication: publicationDuration,
                Validation: candidate.ValidationDuration,
                Hash: candidate.HashDuration));
    }

    private async Task<(RuntimeDescriptionSnapshotStoreOutcome outcome, TimeSpan duration)> PersistSnapshotAsync(
        Guid operationId,
        PublishedRuntimeDescriptionState state,
        CancellationToken cancellationToken)
    {
        var storeStarted = DiagnosticsStopwatch.GetTimestamp();
        try
        {
            await _snapshotStore.SaveAsync(_factoryName, ToSnapshot(state), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "playframework.runtime_metadata.snapshot_persisted OperationId={OperationId} Factory={FactoryName} CatalogId={CatalogId}",
                operationId,
                _factoryName,
                state.Catalog.Identity.CatalogId);
            return (RuntimeDescriptionSnapshotStoreOutcome.Succeeded, DiagnosticsStopwatch.GetElapsedTime(storeStarted));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "playframework.runtime_metadata.snapshot_store_write_failed OperationId={OperationId} Factory={FactoryName}", operationId, _factoryName);
            return (RuntimeDescriptionSnapshotStoreOutcome.Failed, DiagnosticsStopwatch.GetElapsedTime(storeStarted));
        }
    }

    private async Task<RuntimeDescriptionRefreshResult> HandleRefreshFailureAsync(
        Guid operationId,
        Exception ex,
        TimeSpan sourceDuration,
        RuntimeDescriptionRefreshReason reason,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            ex,
            "playframework.runtime_metadata.source_resolution_failed OperationId={OperationId} Factory={FactoryName} Reason={Reason}",
            operationId,
            _factoryName,
            reason);

        if (_settings.FailureMode == RuntimeDescriptionFailureMode.UseFallback)
        {
            var current = Volatile.Read(ref _current);
            if (current is not null)
            {
                _logger.LogWarning(
                    "playframework.runtime_metadata.current_catalog_retained OperationId={OperationId} Factory={FactoryName} CatalogId={CatalogId}",
                    operationId,
                    _factoryName,
                    current.Catalog.Identity.CatalogId);
                return CreateFailedResult(operationId, current, sourceDuration, RuntimeDescriptionRecoverySource.CurrentCatalog, ex);
            }

            RuntimeDescriptionRecovery? recovery;
            try
            {
                recovery = await RecoverWithoutSourceAsync(operationId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                CompleteCancelledRefresh(operationId, reason, activity);
                throw;
            }
            if (recovery is not null)
            {
                return CreateResult(operationId, RuntimeDescriptionRefreshOutcome.Changed, null, recovery.State, recovery.SnapshotStoreOutcome, recovery.State.Catalog.Descriptions.Count, new RefreshTimings(Source: sourceDuration));
            }
        }

        return new RuntimeDescriptionRefreshResult
        {
            OperationId = operationId,
            Outcome = RuntimeDescriptionRefreshOutcome.Failed,
            PreviousCatalogId = Volatile.Read(ref _current)?.Catalog.Identity.CatalogId,
            CurrentCatalogId = Volatile.Read(ref _current)?.Catalog.Identity.CatalogId,
            CatalogIdentity = Volatile.Read(ref _current)?.Catalog.Identity,
            TemplateHash = _templateHash,
            SourceDuration = sourceDuration,
            RecoverySource = RuntimeDescriptionRecoverySource.None,
            FailureStage = "source",
            ErrorMessage = ex.Message
        };
    }

    private async Task<bool> WaitAndReturnTrueAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<RuntimeDescriptionRecovery?> RecoverWithoutSourceAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        RuntimeDescriptionSnapshotStoreOutcome snapshotStoreOutcome;
        try
        {
            var snapshot = await _snapshotStore.GetLatestAsync(_factoryName, cancellationToken).ConfigureAwait(false);
            snapshotStoreOutcome = RuntimeDescriptionSnapshotStoreOutcome.Succeeded;
            var rejectionReason = string.Empty;
            if (snapshot is not null && TryMaterializeSnapshot(snapshot, out var catalog, out rejectionReason))
            {
                var recovered = new PublishedRuntimeDescriptionState(
                    catalog,
                    snapshot.SourceVersion,
                    snapshot.HasUniformSourceVersion,
                    operationId,
                    snapshot.LastValidatedAt,
                    RuntimeDescriptionRecoverySource.SnapshotStore,
                    true);
                Interlocked.Exchange(ref _current, recovered);
                _history[catalog.Identity.CatalogId] = catalog;
                _logger.LogWarning("playframework.runtime_metadata.snapshot_recovered OperationId={OperationId} Factory={FactoryName} CatalogId={CatalogId}", operationId, _factoryName, catalog.Identity.CatalogId);
                return new RuntimeDescriptionRecovery(recovered, snapshotStoreOutcome);
            }

            if (snapshot is not null)
            {
                snapshotStoreOutcome = RuntimeDescriptionSnapshotStoreOutcome.Rejected;
                _logger.LogWarning("playframework.runtime_metadata.snapshot_rejected OperationId={OperationId} Factory={FactoryName} Reason={Reason}", operationId, _factoryName, rejectionReason);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            snapshotStoreOutcome = RuntimeDescriptionSnapshotStoreOutcome.Failed;
            _logger.LogWarning(ex, "playframework.runtime_metadata.snapshot_store_read_failed OperationId={OperationId} Factory={FactoryName}", operationId, _factoryName);
        }

        try
        {
            var fallback = BuildStaticCandidate(requireComplete: true, RuntimeDescriptionRefreshReason.Startup);
            var state = Publish(fallback, operationId, RuntimeDescriptionRecoverySource.StaticFallback, usedFallback: true);
            _logger.LogWarning("playframework.runtime_metadata.fallback_used OperationId={OperationId} Factory={FactoryName} Items={Items}", operationId, _factoryName, fallback.Descriptions.Count);
            return new RuntimeDescriptionRecovery(state, snapshotStoreOutcome);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Static runtime description fallback is incomplete for factory {FactoryName}", _factoryName);
            return null;
        }
    }

    private PublishedRuntimeDescriptionState Publish(
        ResolvedCandidate candidate,
        Guid operationId,
        RuntimeDescriptionRecoverySource recoverySource,
        bool usedFallback)
    {
        var catalog = Materialize(candidate);
        var now = DateTimeOffset.UtcNow;
        var state = new PublishedRuntimeDescriptionState(
            catalog,
            candidate.SourceVersion,
            candidate.HasUniformSourceVersion,
            operationId,
            now,
            recoverySource,
            usedFallback);
        Interlocked.Exchange(ref _current, state);
        _history[catalog.Identity.CatalogId] = catalog;
        PruneHistory();
        return state;
    }

    private async Task<ResolvedCandidate> ResolveCandidateAsync(
        RuntimeDescriptionRefreshReason reason,
        CancellationToken cancellationToken)
    {
        var sourceStarted = DiagnosticsStopwatch.GetTimestamp();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = new RuntimeDescriptionContext
        {
            Services = scope.ServiceProvider,
            Reason = reason
        };
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        var dynamicValues = new List<RuntimeDescriptionValue>();

        foreach (var scene in _templates)
            await ResolveCandidateForSceneAsync(scene, context, descriptions, dynamicValues, cancellationToken).ConfigureAwait(false);

        var sourceDuration = DiagnosticsStopwatch.GetElapsedTime(sourceStarted);
        var validationStarted = DiagnosticsStopwatch.GetTimestamp();
        ValidateDescriptions(descriptions);
        var validationDuration = DiagnosticsStopwatch.GetElapsedTime(validationStarted);
        return CreateCandidate(descriptions, dynamicValues, reason, sourceDuration, validationDuration);
    }

    private async Task ResolveCandidateForSceneAsync(
        SceneConfiguration scene,
        RuntimeDescriptionContext context,
        Dictionary<string, string> descriptions,
        List<RuntimeDescriptionValue> dynamicValues,
        CancellationToken cancellationToken)
    {
        var sceneValue = await ResolveAsync(scene.RuntimeDescription, scene.Description, context, cancellationToken).ConfigureAwait(false);
        if (scene.RuntimeDescription is not null)
            dynamicValues.Add(sceneValue);
        descriptions[SceneKey(scene)] = EffectiveSceneDescription(scene, sceneValue.Value);

        foreach (var serviceTool in scene.ServiceTools)
        {
            var value = await ResolveAsync(serviceTool.RuntimeDescription, serviceTool.Description, context, cancellationToken).ConfigureAwait(false);
            if (serviceTool.RuntimeDescription is not null)
                dynamicValues.Add(value);
            descriptions[ServiceToolKey(scene, serviceTool)] = value.Value;
        }

        foreach (var endpointTool in scene.EndpointTools)
        {
            var value = await ResolveAsync(endpointTool.RuntimeDescription, endpointTool.Description, context, cancellationToken).ConfigureAwait(false);
            if (endpointTool.RuntimeDescription is not null)
                dynamicValues.Add(value);
            descriptions[EndpointToolKey(scene, endpointTool)] = value.Value;
        }

        foreach (var clientTool in scene.ClientInteractionDefinitions ?? [])
        {
            var staticValue = clientTool.RuntimeDescription is null
                ? clientTool.Description ?? $"Client-side tool: {clientTool.ToolName}"
                : clientTool.Description ?? string.Empty;
            var value = await ResolveAsync(clientTool.RuntimeDescription, staticValue, context, cancellationToken).ConfigureAwait(false);
            if (clientTool.RuntimeDescription is not null)
                dynamicValues.Add(value);
            descriptions[ClientToolKey(scene, clientTool)] = value.Value;
        }
    }

    private ResolvedCandidate BuildStaticCandidate(bool requireComplete, RuntimeDescriptionRefreshReason reason)
    {
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var scene in _templates)
        {
            var sceneDescription = scene.RuntimeDescription?.FallbackValue ?? scene.Description ?? string.Empty;
            descriptions[SceneKey(scene)] = EffectiveSceneDescription(scene, sceneDescription ?? string.Empty);
            foreach (var serviceTool in scene.ServiceTools)
                descriptions[ServiceToolKey(scene, serviceTool)] = serviceTool.RuntimeDescription?.FallbackValue ?? serviceTool.Description ?? string.Empty;
            foreach (var endpointTool in scene.EndpointTools)
                descriptions[EndpointToolKey(scene, endpointTool)] = endpointTool.RuntimeDescription?.FallbackValue ?? endpointTool.Description ?? string.Empty;
            foreach (var clientTool in scene.ClientInteractionDefinitions ?? [])
            {
                descriptions[ClientToolKey(scene, clientTool)] = clientTool.RuntimeDescription is null
                    ? clientTool.Description ?? $"Client-side tool: {clientTool.ToolName}"
                    : clientTool.RuntimeDescription.FallbackValue ?? string.Empty;
            }
        }

        if (requireComplete && _hasDynamicDescriptions)
            ValidateDescriptions(descriptions);
        return CreateCandidate(descriptions, [], reason);
    }

    private ResolvedCandidate CreateCandidate(
        IReadOnlyDictionary<string, string> descriptions,
        IReadOnlyList<RuntimeDescriptionValue> dynamicValues,
        RuntimeDescriptionRefreshReason reason,
        TimeSpan sourceDuration = default,
        TimeSpan validationDuration = default)
    {
        var versions = dynamicValues.Select(x => x.Version).ToList();
        var hasUniformVersion = versions.Count > 0
            && versions.All(x => !string.IsNullOrWhiteSpace(x))
            && versions.Distinct(StringComparer.Ordinal).Count() == 1;
        var sources = dynamicValues.Select(x => x.Source).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();
        var hashStarted = DiagnosticsStopwatch.GetTimestamp();
        var contentHash = ComputeHash(BuildContentCanonical(descriptions));
        var catalogId = ComputeHash($"{HashAlgorithm}|{_templateHash}|{contentHash}");
        var hashDuration = DiagnosticsStopwatch.GetElapsedTime(hashStarted);
        var now = DateTimeOffset.UtcNow;
        var identity = new RuntimeDescriptionCatalogIdentity
        {
            CatalogId = catalogId,
            TemplateHash = _templateHash,
            ContentHash = contentHash,
            HashAlgorithm = HashAlgorithm,
            SourceVersion = hasUniformVersion ? versions[0] : null,
            HasUniformSourceVersion = hasUniformVersion,
            Source = sources.Count == 1 ? sources[0] : null,
            LoadedAt = now,
            PublishedAt = now
        };
        return new ResolvedCandidate(
            identity,
            new ReadOnlyDictionary<string, string>(descriptions.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal)),
            hasUniformVersion ? versions[0] : null,
            hasUniformVersion,
            reason,
            sourceDuration,
            validationDuration,
            hashDuration);
    }

    private MaterializedSceneCatalog Materialize(ResolvedCandidate candidate)
    {
        var scenes = _templates.Select(template => new Scene(CloneResolvedConfiguration(template, candidate.Descriptions), _jsonService))
            .Cast<IScene>()
            .ToList();
        return new MaterializedSceneCatalog(
            candidate.Identity,
            new ReadOnlyCollection<IScene>(scenes),
            candidate.Descriptions);
    }

    private bool TryMaterializeSnapshot(
        RuntimeDescriptionSnapshot snapshot,
        out MaterializedSceneCatalog catalog,
        out string rejectionReason)
    {
        catalog = null!;
        rejectionReason = string.Empty;
        try
        {
            if (snapshot.FormatVersion != 1)
                throw new InvalidOperationException("unsupported_format");
            if (snapshot.Identity.TemplateHash != _templateHash)
                throw new InvalidOperationException("template_mismatch");
            ValidateDescriptions(snapshot.Descriptions);
            var expectedKeys = ExpectedDescriptionKeys().OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var actualKeys = snapshot.Descriptions.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (!expectedKeys.SequenceEqual(actualKeys, StringComparer.Ordinal))
                throw new InvalidOperationException("incomplete_catalog");
            var contentHash = ComputeHash(BuildContentCanonical(snapshot.Descriptions));
            var catalogId = ComputeHash($"{HashAlgorithm}|{_templateHash}|{contentHash}");
            if (contentHash != snapshot.Identity.ContentHash || catalogId != snapshot.Identity.CatalogId)
                throw new InvalidOperationException("hash_mismatch");
            if (snapshot.LastValidatedAt + _settings.SnapshotRetention < DateTimeOffset.UtcNow)
                throw new InvalidOperationException("expired");

            var candidate = new ResolvedCandidate(
                snapshot.Identity,
                snapshot.Descriptions,
                snapshot.SourceVersion,
                snapshot.HasUniformSourceVersion,
                RuntimeDescriptionRefreshReason.Startup);
            catalog = Materialize(candidate);
            return true;
        }
        catch (Exception ex)
        {
            rejectionReason = ex.Message;
            return false;
        }
    }

    private SceneConfiguration CloneResolvedConfiguration(
        SceneConfiguration source,
        IReadOnlyDictionary<string, string> descriptions)
    {
        return new SceneConfiguration
        {
            Name = source.Name,
            Description = descriptions[SceneKey(source)],
            RuntimeDescription = null,
            AutoGenerateToolDescription = false,
            Actors = source.Actors,
            McpServerReferences = source.McpServerReferences,
            RagSettings = new(source.RagSettings),
            WebSearchSettings = new(source.WebSearchSettings),
            RequiresCache = source.RequiresCache,
            CacheExpiration = source.CacheExpiration,
            ServiceTools = source.ServiceTools.Select(tool => new ServiceToolConfiguration
            {
                ServiceType = tool.ServiceType,
                Method = tool.Method,
                ToolName = tool.ToolName,
                Description = descriptions[ServiceToolKey(source, tool)]
            }).ToList(),
            EndpointTools = source.EndpointTools.Select(tool => new EndpointToolConfiguration
            {
                ClientType = tool.ClientType,
                ToolName = tool.ToolName,
                Description = descriptions[EndpointToolKey(source, tool)],
                HttpMethod = tool.HttpMethod,
                RouteTemplate = tool.RouteTemplate,
                RequestBodyType = tool.RequestBodyType,
                ResponseType = tool.ResponseType,
                QueryParameters = tool.QueryParameters.Select(parameter => new EndpointParameterDefinition
                {
                    Name = parameter.Name,
                    Description = parameter.Description,
                    Type = parameter.Type
                }).ToList()
            }).ToList(),
            ClientInteractionDefinitions = source.ClientInteractionDefinitions?.Select(tool => new ClientInteractionDefinition
            {
                ToolName = tool.ToolName,
                Description = descriptions[ClientToolKey(source, tool)],
                TimeoutSeconds = tool.TimeoutSeconds,
                JsonSchema = tool.JsonSchema,
                ArgumentType = tool.ArgumentType,
                IsCommand = tool.IsCommand,
                FeedbackMode = tool.FeedbackMode
            }).ToList().AsReadOnly()
        };
    }

    private async ValueTask<RuntimeDescriptionValue> ResolveAsync(
        RuntimeTextConfiguration? runtime,
        string? staticValue,
        RuntimeDescriptionContext context,
        CancellationToken cancellationToken)
    {
        if (runtime is null)
            return new RuntimeDescriptionValue { Value = staticValue ?? string.Empty };
        var resolved = await runtime.Resolver(context, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Runtime description resolver returned null.");
        if (resolved.Value is null)
            throw new InvalidOperationException("Runtime description resolver returned a null value.");
        return resolved;
    }

    private void ValidateDescriptions(IReadOnlyDictionary<string, string> descriptions)
    {
        var requiredNonEmptyKeys = DynamicDescriptionKeys().ToHashSet(StringComparer.Ordinal);
        var totalBytes = 0;
        foreach (var (key, value) in descriptions)
        {
            if (requiredNonEmptyKeys.Contains(key) && string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"Runtime description '{key}' is null, empty, or whitespace.");
            if (value is null)
                continue;
            if (value.IndexOf('\0') >= 0)
                throw new InvalidOperationException($"Runtime description '{key}' contains a NUL character.");
            int bytes;
            try
            {
                bytes = s_strictUtf8.GetByteCount(value);
            }
            catch (EncoderFallbackException ex)
            {
                throw new InvalidOperationException($"Runtime description '{key}' contains invalid Unicode.", ex);
            }
            if (bytes > _settings.MaxDescriptionUtf8Bytes)
                throw new InvalidOperationException($"Runtime description '{key}' exceeds the {_settings.MaxDescriptionUtf8Bytes} byte limit.");
            totalBytes += bytes;
        }
        if (totalBytes > _settings.MaxCatalogUtf8Bytes)
            throw new InvalidOperationException($"Runtime description catalog exceeds the {_settings.MaxCatalogUtf8Bytes} byte limit.");
    }

    private IEnumerable<string> DynamicDescriptionKeys()
    {
        foreach (var scene in _templates)
        {
            if (scene.RuntimeDescription is not null)
                yield return SceneKey(scene);
            foreach (var tool in scene.ServiceTools.Where(t => t.RuntimeDescription is not null))
                yield return ServiceToolKey(scene, tool);
            foreach (var tool in scene.EndpointTools.Where(t => t.RuntimeDescription is not null))
                yield return EndpointToolKey(scene, tool);
            foreach (var tool in (scene.ClientInteractionDefinitions ?? []).Where(t => t.RuntimeDescription is not null))
                yield return ClientToolKey(scene, tool);
        }
    }

    private static void ValidateSettings(RuntimeDescriptionSettings settings)
    {
        if (settings.BackgroundRefreshInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("BackgroundRefreshInterval must be positive.");
        if (settings.SnapshotRetention <= TimeSpan.Zero)
            throw new InvalidOperationException("SnapshotRetention must be positive.");
        if (settings.MaxRetainedSnapshots <= 0)
            throw new InvalidOperationException("MaxRetainedSnapshots must be positive.");
        if (settings.MaxDescriptionUtf8Bytes <= 0 || settings.MaxCatalogUtf8Bytes <= 0)
            throw new InvalidOperationException("Runtime description size limits must be positive.");
        if (settings.MaxDescriptionUtf8Bytes > settings.MaxCatalogUtf8Bytes)
            throw new InvalidOperationException("MaxDescriptionUtf8Bytes cannot exceed MaxCatalogUtf8Bytes.");
    }

    private static string EffectiveSceneDescription(SceneConfiguration scene, string description)
    {
        if (!scene.AutoGenerateToolDescription)
            return description;
        var toolNames = scene.ServiceTools.Select(x => x.ToolName)
            .Concat(scene.EndpointTools.Select(x => x.ToolName))
            .Concat((scene.ClientInteractionDefinitions ?? []).Select(x => x.ToolName))
            .ToList();
        if (toolNames.Count == 0)
            return description;
        if (string.IsNullOrWhiteSpace(description))
            return $"This scene provides the following capabilities: {string.Join(", ", toolNames)}";
        var trimmed = description.TrimEnd();
        return $"{trimmed}{(trimmed.EndsWith('.') ? " " : ". ")}Available tools: {string.Join(", ", toolNames)}";
    }

    private static IEnumerable<RuntimeTextConfiguration> EnumerateRuntimeConfigurations(IEnumerable<SceneConfiguration> scenes)
    {
        foreach (var scene in scenes)
        {
            if (scene.RuntimeDescription is not null)
                yield return scene.RuntimeDescription;
            foreach (var tool in scene.ServiceTools.Where(t => t.RuntimeDescription is not null))
                yield return tool.RuntimeDescription!;
            foreach (var tool in scene.EndpointTools.Where(t => t.RuntimeDescription is not null))
                yield return tool.RuntimeDescription!;
            foreach (var tool in (scene.ClientInteractionDefinitions ?? []).Where(t => t.RuntimeDescription is not null))
                yield return tool.RuntimeDescription!;
        }
    }

    private IEnumerable<string> ExpectedDescriptionKeys()
    {
        foreach (var scene in _templates)
        {
            yield return SceneKey(scene);
            foreach (var tool in scene.ServiceTools)
                yield return ServiceToolKey(scene, tool);
            foreach (var tool in scene.EndpointTools)
                yield return EndpointToolKey(scene, tool);
            foreach (var tool in scene.ClientInteractionDefinitions ?? [])
                yield return ClientToolKey(scene, tool);
        }
    }

    private static string BuildTemplateCanonical(IEnumerable<SceneConfiguration> scenes)
    {
        var builder = new StringBuilder();
        foreach (var scene in scenes.OrderBy(x => x.Name, StringComparer.Ordinal))
        {
            AppendCanonical(builder, "scene", scene.Name);
            foreach (var tool in scene.ServiceTools.OrderBy(x => x.ToolName, StringComparer.Ordinal))
            {
                AppendCanonical(builder, "service", tool.ToolName);
                AppendCanonical(builder, "type", tool.ServiceType.AssemblyQualifiedName ?? tool.ServiceType.FullName ?? tool.ServiceType.Name);
                AppendCanonical(builder, "method", tool.Method.ToString() ?? tool.Method.Name);
            }
            foreach (var tool in scene.EndpointTools.OrderBy(x => x.ToolName, StringComparer.Ordinal))
            {
                AppendCanonical(builder, "endpoint", tool.ToolName);
                AppendCanonical(builder, "method", tool.HttpMethod.Method);
                AppendCanonical(builder, "route", tool.RouteTemplate);
                AppendCanonical(builder, "request", tool.RequestBodyType?.AssemblyQualifiedName ?? string.Empty);
                foreach (var parameter in tool.QueryParameters.OrderBy(x => x.Name, StringComparer.Ordinal))
                    AppendCanonical(builder, parameter.Name, parameter.Type.AssemblyQualifiedName ?? parameter.Type.FullName ?? parameter.Type.Name);
            }
            foreach (var tool in (scene.ClientInteractionDefinitions ?? []).OrderBy(x => x.ToolName, StringComparer.Ordinal))
            {
                AppendCanonical(builder, "client", tool.ToolName);
                AppendCanonical(builder, "schema", tool.JsonSchema ?? string.Empty);
                AppendCanonical(builder, "command", tool.IsCommand.ToString());
            }
        }
        return builder.ToString();
    }

    private static string BuildContentCanonical(IReadOnlyDictionary<string, string> descriptions)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in descriptions.OrderBy(x => x.Key, StringComparer.Ordinal))
            AppendCanonical(builder, key, value);
        return builder.ToString();
    }

    private static void AppendCanonical(StringBuilder builder, string key, string value)
    {
        builder.Append(key.Length).Append(':').Append(key)
            .Append(value.Length).Append(':').Append(value).Append('|');
    }

    private static string ComputeHash(string value)
        => Convert.ToHexStringLower(SHA256.HashData(s_strictUtf8.GetBytes(value)));

    private static string SceneKey(SceneConfiguration scene) => $"scene:{scene.Name}";
    private static string ServiceToolKey(SceneConfiguration scene, ServiceToolConfiguration tool) => $"tool:{scene.Name}:service:{tool.ToolName}";
    private static string EndpointToolKey(SceneConfiguration scene, EndpointToolConfiguration tool) => $"tool:{scene.Name}:endpoint:{tool.ToolName}";
    private static string ClientToolKey(SceneConfiguration scene, ClientInteractionDefinition tool) => $"tool:{scene.Name}:client:{tool.ToolName}";

    private RuntimeDescriptionSnapshot ToSnapshot(PublishedRuntimeDescriptionState state)
        => new()
        {
            Identity = state.Catalog.Identity,
            Descriptions = state.Catalog.Descriptions,
            SourceVersion = state.SourceVersion,
            HasUniformSourceVersion = state.HasUniformSourceVersion,
            Source = state.Catalog.Identity.Source,
            PublishedAt = state.Catalog.Identity.PublishedAt,
            LastValidatedAt = state.LastValidatedAt
        };

    private Activity? StartRefreshActivity(Guid operationId, RuntimeDescriptionRefreshReason reason)
    {
        var activity = PlayFrameworkActivitySource.Instance.StartActivity(
            PlayFrameworkActivitySource.Activities.RuntimeDescriptionRefresh,
            ActivityKind.Internal);
        activity?.SetTag(PlayFrameworkActivitySource.Tags.FactoryName, _factoryName);
        activity?.SetTag(PlayFrameworkActivitySource.Tags.RuntimeDescriptionOperationId, operationId.ToString());
        activity?.SetTag(PlayFrameworkActivitySource.Tags.RuntimeDescriptionRefreshReason, reason.ToString());
        activity?.SetTag(PlayFrameworkActivitySource.Tags.RuntimeDescriptionRefreshMode, _settings.RefreshMode.ToString());
        activity?.SetTag(PlayFrameworkActivitySource.Tags.RuntimeDescriptionConsistencyMode, _settings.ConsistencyMode.ToString());
        return activity;
    }

    private RuntimeDescriptionRefreshResult CompleteRefresh(
        RuntimeDescriptionRefreshResult result,
        RuntimeDescriptionRefreshReason reason,
        Activity? activity)
    {
        activity?.SetTag(PlayFrameworkActivitySource.Tags.RuntimeDescriptionOutcome, result.Outcome.ToString());
        activity?.SetTag(PlayFrameworkActivitySource.Tags.RuntimeDescriptionCatalogId, result.CurrentCatalogId);
        activity?.SetTag(PlayFrameworkActivitySource.Tags.RuntimeDescriptionSourceVersion, result.SourceVersion);
        activity?.SetTag(PlayFrameworkActivitySource.Tags.RuntimeDescriptionRecoverySource, result.RecoverySource.ToString());
        activity?.SetTag(PlayFrameworkActivitySource.Tags.RuntimeDescriptionStoreOutcome, result.SnapshotStoreOutcome.ToString());
        activity?.SetTag(PlayFrameworkActivitySource.Tags.RuntimeDescriptionFailureStage, result.FailureStage);

        var terminalEvent = result.Outcome switch
        {
            RuntimeDescriptionRefreshOutcome.Changed => PlayFrameworkActivitySource.Events.RuntimeDescriptionCatalogPublished,
            RuntimeDescriptionRefreshOutcome.Unchanged => PlayFrameworkActivitySource.Events.RuntimeDescriptionCatalogUnchanged,
            RuntimeDescriptionRefreshOutcome.SkippedBusy => PlayFrameworkActivitySource.Events.RuntimeDescriptionRefreshSkippedBusy,
            _ => PlayFrameworkActivitySource.Events.RuntimeDescriptionRefreshFailed
        };
        activity?.AddEvent(new ActivityEvent(terminalEvent));
        activity?.SetStatus(
            result.Outcome == RuntimeDescriptionRefreshOutcome.Failed ? ActivityStatusCode.Error : ActivityStatusCode.Ok,
            result.ErrorMessage);

        switch (result.Outcome)
        {
            case RuntimeDescriptionRefreshOutcome.Changed:
                _logger.LogInformation(
                    "playframework.runtime_metadata.catalog_published OperationId={OperationId} Factory={FactoryName} CatalogId={CatalogId} RecoverySource={RecoverySource}",
                    result.OperationId,
                    _factoryName,
                    result.CurrentCatalogId,
                    result.RecoverySource);
                break;
            case RuntimeDescriptionRefreshOutcome.Unchanged:
                _logger.LogInformation(
                    "playframework.runtime_metadata.catalog_unchanged OperationId={OperationId} Factory={FactoryName} CatalogId={CatalogId}",
                    result.OperationId,
                    _factoryName,
                    result.CurrentCatalogId);
                break;
            case RuntimeDescriptionRefreshOutcome.SkippedBusy:
                _logger.LogInformation(
                    "playframework.runtime_metadata.refresh_skipped_busy OperationId={OperationId} Factory={FactoryName}",
                    result.OperationId,
                    _factoryName);
                break;
            default:
                _logger.LogWarning(
                    "playframework.runtime_metadata.refresh_failed OperationId={OperationId} Factory={FactoryName} Stage={FailureStage} RecoverySource={RecoverySource}",
                    result.OperationId,
                    _factoryName,
                    result.FailureStage,
                    result.RecoverySource);
                break;
        }

        PlayFrameworkMetrics.RecordRuntimeDescriptionRefresh(
            result,
            reason,
            _settings.RefreshMode,
            _settings.ConsistencyMode);
        return result;
    }

    private void CompleteCancelledRefresh(
        Guid operationId,
        RuntimeDescriptionRefreshReason reason,
        Activity? activity)
        => CompleteRefresh(new RuntimeDescriptionRefreshResult
        {
            OperationId = operationId,
            Outcome = RuntimeDescriptionRefreshOutcome.Failed,
            PreviousCatalogId = Volatile.Read(ref _current)?.Catalog.Identity.CatalogId,
            CurrentCatalogId = Volatile.Read(ref _current)?.Catalog.Identity.CatalogId,
            CatalogIdentity = Volatile.Read(ref _current)?.Catalog.Identity,
            TemplateHash = _templateHash,
            RecoverySource = RuntimeDescriptionRecoverySource.None,
            FailureStage = "cancelled",
            ErrorMessage = "Runtime description refresh was cancelled."
        }, reason, activity);

    private sealed record RefreshTimings(
        TimeSpan Source = default,
        TimeSpan Store = default,
        TimeSpan Materialization = default,
        TimeSpan Publication = default,
        TimeSpan Validation = default,
        TimeSpan Hash = default);

    private RuntimeDescriptionRefreshResult CreateResult(
        Guid operationId,
        RuntimeDescriptionRefreshOutcome outcome,
        string? previousCatalogId,
        PublishedRuntimeDescriptionState state,
        RuntimeDescriptionSnapshotStoreOutcome storeOutcome,
        int changedItemCount,
        RefreshTimings? timings = null)
        => new()
        {
            OperationId = operationId,
            Outcome = outcome,
            PreviousCatalogId = previousCatalogId,
            CurrentCatalogId = state.Catalog.Identity.CatalogId,
            CatalogIdentity = state.Catalog.Identity,
            TemplateHash = state.Catalog.Identity.TemplateHash,
            SourceVersion = state.SourceVersion,
            HasUniformSourceVersion = state.HasUniformSourceVersion,
            LastValidatedAt = state.LastValidatedAt,
            RecoverySource = state.RecoverySource,
            SnapshotStoreOutcome = storeOutcome,
            ChangedItemCount = changedItemCount,
            FallbackItemCount = state.RecoverySource == RuntimeDescriptionRecoverySource.StaticFallback
                ? state.Catalog.Descriptions.Count
                : 0,
            SourceDuration = timings?.Source ?? default,
            ValidationDuration = timings?.Validation ?? default,
            HashDuration = timings?.Hash ?? default,
            MaterializationDuration = timings?.Materialization ?? default,
            PublicationDuration = timings?.Publication ?? default,
            SnapshotStoreDuration = timings?.Store ?? default
        };

    private RuntimeDescriptionRefreshResult CreateFailedResult(
        Guid operationId,
        PublishedRuntimeDescriptionState current,
        TimeSpan sourceDuration,
        RuntimeDescriptionRecoverySource recoverySource,
        Exception exception)
        => new()
        {
            OperationId = operationId,
            Outcome = RuntimeDescriptionRefreshOutcome.Failed,
            PreviousCatalogId = current.Catalog.Identity.CatalogId,
            CurrentCatalogId = current.Catalog.Identity.CatalogId,
            CatalogIdentity = current.Catalog.Identity,
            TemplateHash = current.Catalog.Identity.TemplateHash,
            SourceVersion = current.SourceVersion,
            HasUniformSourceVersion = current.HasUniformSourceVersion,
            LastValidatedAt = current.LastValidatedAt,
            RecoverySource = recoverySource,
            SourceDuration = sourceDuration,
            FailureStage = "source",
            ErrorMessage = exception.Message
        };

    private static int CountChanged(
        IReadOnlyDictionary<string, string>? previous,
        IReadOnlyDictionary<string, string> current)
    {
        if (previous is null)
            return current.Count;
        return current.Count(x => !previous.TryGetValue(x.Key, out var oldValue) || oldValue != x.Value);
    }

    private void PruneHistory()
    {
        var retained = _history.Values
            .OrderByDescending(x => x.Identity.PublishedAt)
            .Take(_settings.MaxRetainedSnapshots)
            .Select(x => x.Identity.CatalogId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var catalogId in _history.Keys.Where(x => !retained.Contains(x)).ToList())
            _history.TryRemove(catalogId, out _);
    }

    private sealed record ResolvedCandidate(
        RuntimeDescriptionCatalogIdentity Identity,
        IReadOnlyDictionary<string, string> Descriptions,
        string? SourceVersion,
        bool HasUniformSourceVersion,
        RuntimeDescriptionRefreshReason Reason,
        TimeSpan SourceDuration = default,
        TimeSpan ValidationDuration = default,
        TimeSpan HashDuration = default);

    private sealed record RuntimeDescriptionRecovery(
        PublishedRuntimeDescriptionState State,
        RuntimeDescriptionSnapshotStoreOutcome SnapshotStoreOutcome);
}
