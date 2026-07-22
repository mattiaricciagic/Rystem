using Microsoft.Extensions.AI;
using Microsoft.Extensions.Primitives;

namespace Rystem.PlayFramework;

public enum RuntimeDescriptionRefreshMode
{
    Background,
    Manual,
    EveryRequest
}

public enum RuntimeDescriptionRefreshReason
{
    Startup,
    Timer,
    ChangeNotification,
    Manual,
    EveryRequest
}

public enum RuntimeDescriptionConsistencyMode
{
    Request,
    Execution
}

public enum RuntimeDescriptionFailureMode
{
    Throw,
    UseFallback
}

public enum MissingRuntimeDescriptionVersionBehavior
{
    UseLatestAndWarn,
    Throw
}

public enum RuntimeDescriptionSnapshotStoreMode
{
    Memory,
    Distributed
}

public enum RuntimeDescriptionRefreshOutcome
{
    Changed,
    Unchanged,
    Failed,
    SkippedBusy
}

public enum RuntimeDescriptionRecoverySource
{
    None,
    CurrentCatalog,
    SnapshotStore,
    StaticFallback
}

public enum RuntimeDescriptionSnapshotStoreOutcome
{
    NotAttempted,
    Succeeded,
    Failed,
    Rejected
}

public sealed class RuntimeDescriptionSettings
{
    public RuntimeDescriptionFailureMode FailureMode { get; set; }
        = RuntimeDescriptionFailureMode.UseFallback;

    public RuntimeDescriptionRefreshMode RefreshMode { get; set; }
        = RuntimeDescriptionRefreshMode.Background;

    public TimeSpan BackgroundRefreshInterval { get; set; }
        = TimeSpan.FromMinutes(5);

    public bool RefreshAtStartup { get; set; } = true;

    public bool RefreshOnChange { get; set; } = true;

    public RuntimeDescriptionConsistencyMode ConsistencyMode { get; set; }
        = RuntimeDescriptionConsistencyMode.Execution;

    public MissingRuntimeDescriptionVersionBehavior MissingVersionBehavior { get; set; }
        = MissingRuntimeDescriptionVersionBehavior.UseLatestAndWarn;

    public RuntimeDescriptionSnapshotStoreMode SnapshotStoreMode { get; set; }
        = RuntimeDescriptionSnapshotStoreMode.Memory;

    public TimeSpan SnapshotRetention { get; set; }
        = TimeSpan.FromHours(24);

    public int MaxRetainedSnapshots { get; set; } = 10;

    public int MaxDescriptionUtf8Bytes { get; set; } = 16 * 1024;

    public int MaxCatalogUtf8Bytes { get; set; } = 1024 * 1024;
}

public sealed class RuntimeDescriptionContext
{
    public required IServiceProvider Services { get; init; }
    public required RuntimeDescriptionRefreshReason Reason { get; init; }
}

public sealed record RuntimeDescriptionValue
{
    public required string Value { get; init; }
    public string? Version { get; init; }
    public string? Source { get; init; }
    public string? ETag { get; init; }
}

public sealed record RuntimeDescriptionCatalogIdentity
{
    public required string CatalogId { get; init; }
    public required string TemplateHash { get; init; }
    public required string ContentHash { get; init; }
    public required string HashAlgorithm { get; init; }
    public string? SourceVersion { get; init; }
    public bool HasUniformSourceVersion { get; init; }
    public string? Source { get; init; }
    public DateTimeOffset LoadedAt { get; init; }
    public DateTimeOffset PublishedAt { get; init; }
}

public sealed record RuntimeDescriptionExecutionInfo
{
    public required RuntimeDescriptionCatalogIdentity CatalogIdentity { get; init; }
    public string? RequestedCatalogId { get; init; }
    public string? SourceVersion { get; init; }
    public required bool HasUniformSourceVersion { get; init; }
    public required RuntimeDescriptionRefreshMode RefreshMode { get; init; }
    public required RuntimeDescriptionConsistencyMode ConsistencyMode { get; init; }
    public required bool IsRequestLocal { get; init; }
    public required RuntimeDescriptionRecoverySource RecoverySource { get; init; }
    public required bool UsedFallback { get; init; }
    public required Guid LastValidationOperationId { get; init; }
    public required DateTimeOffset LastValidatedAt { get; init; }
    public required TimeSpan AcquisitionDuration { get; init; }
}

public sealed record RuntimeSceneCatalogView
{
    public required RuntimeDescriptionExecutionInfo ExecutionInfo { get; init; }
    public required IReadOnlyList<RuntimeSceneDescriptor> Scenes { get; init; }

    public RuntimeSceneDescriptor? TryGetScene(string name)
        => Scenes.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed record RuntimeSceneDescriptor
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required AIFunctionDeclaration RoutingDeclaration { get; init; }
    public required IReadOnlyList<RuntimeToolDescriptor> Tools { get; init; }
}

public sealed record RuntimeToolDescriptor
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required AIFunctionDeclaration Declaration { get; init; }
}

public interface IRuntimeDescriptionRefresher
{
    Task<RuntimeDescriptionRefreshResult> RefreshAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional application-owned notification source used to trigger background refreshes.
/// A new change token must be returned after each notification.
/// </summary>
public interface IRuntimeDescriptionChangeTokenSource
{
    IChangeToken GetChangeToken();
}

public sealed record RuntimeDescriptionRefreshResult
{
    public required Guid OperationId { get; init; }
    public required RuntimeDescriptionRefreshOutcome Outcome { get; init; }
    public string? PreviousCatalogId { get; init; }
    public string? CurrentCatalogId { get; init; }
    public RuntimeDescriptionCatalogIdentity? CatalogIdentity { get; init; }
    public string? TemplateHash { get; init; }
    public string? SourceVersion { get; init; }
    public bool HasUniformSourceVersion { get; init; }
    public DateTimeOffset? LastValidatedAt { get; init; }
    public RuntimeDescriptionRecoverySource RecoverySource { get; init; }
    public RuntimeDescriptionSnapshotStoreOutcome SnapshotStoreOutcome { get; init; }
    public int ChangedItemCount { get; init; }
    public int FallbackItemCount { get; init; }
    public bool UsedFallback => RecoverySource is not RuntimeDescriptionRecoverySource.None;
    public TimeSpan SourceDuration { get; init; }
    public TimeSpan ValidationDuration { get; init; }
    public TimeSpan HashDuration { get; init; }
    public TimeSpan MaterializationDuration { get; init; }
    public TimeSpan PublicationDuration { get; init; }
    public TimeSpan SnapshotStoreDuration { get; init; }
    public string? FailureStage { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record RuntimeDescriptionSnapshot
{
    public int FormatVersion { get; init; } = 1;
    public required RuntimeDescriptionCatalogIdentity Identity { get; init; }
    public required IReadOnlyDictionary<string, string> Descriptions { get; init; }
    public string? SourceVersion { get; init; }
    public bool HasUniformSourceVersion { get; init; }
    public string? Source { get; init; }
    public DateTimeOffset PublishedAt { get; init; }
    public DateTimeOffset LastValidatedAt { get; init; }
}

public interface IRuntimeDescriptionSnapshotStore
{
    ValueTask<RuntimeDescriptionSnapshot?> GetLatestAsync(
        string factoryName,
        CancellationToken cancellationToken = default);

    ValueTask<RuntimeDescriptionSnapshot?> GetAsync(
        string factoryName,
        string catalogId,
        CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        string factoryName,
        RuntimeDescriptionSnapshot snapshot,
        CancellationToken cancellationToken = default);

    ValueTask RefreshLatestExpirationAsync(
        string factoryName,
        string catalogId,
        CancellationToken cancellationToken = default);
}
