using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Rystem.PlayFramework;

internal sealed class MemoryRuntimeDescriptionSnapshotStore : IRuntimeDescriptionSnapshotStore, IFactoryName
{
    private readonly IFactory<PlayFrameworkSettings> _settingsFactory;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RuntimeDescriptionSnapshot>> _snapshots = new();
    private readonly ConcurrentDictionary<string, string> _latest = new();
    private RuntimeDescriptionSettings _settings = new();

    public MemoryRuntimeDescriptionSnapshotStore(IFactory<PlayFrameworkSettings> settingsFactory)
    {
        _settingsFactory = settingsFactory;
    }

    public bool FactoryNameAlreadySetup { get; set; }

    public void SetFactoryName(AnyOf<string?, Enum>? name)
    {
        _settings = (_settingsFactory.Create(name) ?? new PlayFrameworkSettings()).RuntimeDescriptions;
    }

    public ValueTask<RuntimeDescriptionSnapshot?> GetLatestAsync(
        string factoryName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_latest.TryGetValue(factoryName, out var catalogId))
            return ValueTask.FromResult<RuntimeDescriptionSnapshot?>(null);

        return GetAsync(factoryName, catalogId, cancellationToken);
    }

    public ValueTask<RuntimeDescriptionSnapshot?> GetAsync(
        string factoryName,
        string catalogId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_snapshots.TryGetValue(factoryName, out var factorySnapshots)
            || !factorySnapshots.TryGetValue(catalogId, out var snapshot))
        {
            return ValueTask.FromResult<RuntimeDescriptionSnapshot?>(null);
        }

        if (snapshot.LastValidatedAt + _settings.SnapshotRetention < DateTimeOffset.UtcNow)
        {
            factorySnapshots.TryRemove(catalogId, out _);
            if (_latest.TryGetValue(factoryName, out var latest) && latest == catalogId)
                _latest.TryRemove(factoryName, out _);
            return ValueTask.FromResult<RuntimeDescriptionSnapshot?>(null);
        }

        return ValueTask.FromResult<RuntimeDescriptionSnapshot?>(snapshot);
    }

    public ValueTask SaveAsync(
        string factoryName,
        RuntimeDescriptionSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var factorySnapshots = _snapshots.GetOrAdd(factoryName, _ => new());
        factorySnapshots[snapshot.Identity.CatalogId] = snapshot;
        _latest[factoryName] = snapshot.Identity.CatalogId;

        var now = DateTimeOffset.UtcNow;
        foreach (var expired in factorySnapshots
            .Where(x => x.Value.LastValidatedAt + _settings.SnapshotRetention < now)
            .Select(x => x.Key)
            .ToList())
        {
            factorySnapshots.TryRemove(expired, out _);
        }

        var overflow = factorySnapshots
            .OrderByDescending(x => x.Value.LastValidatedAt)
            .Skip(_settings.MaxRetainedSnapshots)
            .Select(x => x.Key)
            .ToList();
        foreach (var catalogId in overflow)
            factorySnapshots.TryRemove(catalogId, out _);

        return ValueTask.CompletedTask;
    }

    public async ValueTask RefreshLatestExpirationAsync(
        string factoryName,
        string catalogId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetAsync(factoryName, catalogId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
            return;

        await SaveAsync(factoryName, snapshot with { LastValidatedAt = DateTimeOffset.UtcNow }, cancellationToken)
            .ConfigureAwait(false);
    }
}
