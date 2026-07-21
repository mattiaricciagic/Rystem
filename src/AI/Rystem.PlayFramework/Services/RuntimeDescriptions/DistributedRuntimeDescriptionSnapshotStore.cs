using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Rystem.PlayFramework.Helpers;

namespace Rystem.PlayFramework;

internal sealed class DistributedRuntimeDescriptionSnapshotStore : IRuntimeDescriptionSnapshotStore, IFactoryName
{
    private readonly IDistributedCache _cache;
    private readonly IFactory<PlayFrameworkSettings> _settingsFactory;
    private RuntimeDescriptionSettings _settings = new();

    public DistributedRuntimeDescriptionSnapshotStore(
        IServiceProvider serviceProvider,
        IFactory<PlayFrameworkSettings> settingsFactory)
    {
        _cache = serviceProvider.GetService(typeof(IDistributedCache)) as IDistributedCache
            ?? throw new InvalidOperationException(
                "RuntimeDescriptionSnapshotStoreMode.Distributed requires an IDistributedCache registration.");
        _settingsFactory = settingsFactory;
    }

    public bool FactoryNameAlreadySetup { get; set; }

    public void SetFactoryName(AnyOf<string?, Enum>? name)
    {
        _settings = (_settingsFactory.Create(name) ?? new PlayFrameworkSettings()).RuntimeDescriptions;
    }

    public async ValueTask<RuntimeDescriptionSnapshot?> GetLatestAsync(
        string factoryName,
        CancellationToken cancellationToken = default)
    {
        var catalogId = await _cache.GetStringAsync(LatestKey(factoryName), cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(catalogId)
            ? null
            : await GetAsync(factoryName, catalogId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<RuntimeDescriptionSnapshot?> GetAsync(
        string factoryName,
        string catalogId,
        CancellationToken cancellationToken = default)
    {
        var payload = await _cache.GetStringAsync(SnapshotKey(factoryName, catalogId), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        return JsonSerializer.Deserialize<RuntimeDescriptionSnapshot>(payload, JsonHelper.JsonSerializerOptions);
    }

    public async ValueTask SaveAsync(
        string factoryName,
        RuntimeDescriptionSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = _settings.SnapshotRetention
        };
        var payload = JsonSerializer.Serialize(snapshot, JsonHelper.JsonSerializerOptions);
        await _cache.SetStringAsync(SnapshotKey(factoryName, snapshot.Identity.CatalogId), payload, options, cancellationToken)
            .ConfigureAwait(false);
        await _cache.SetStringAsync(LatestKey(factoryName), snapshot.Identity.CatalogId, options, cancellationToken)
            .ConfigureAwait(false);

        var indexKey = IndexKey(factoryName);
        var indexPayload = await _cache.GetStringAsync(indexKey, cancellationToken).ConfigureAwait(false);
        var index = string.IsNullOrWhiteSpace(indexPayload)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(indexPayload, JsonHelper.JsonSerializerOptions) ?? [];
        index.Remove(snapshot.Identity.CatalogId);
        index.Insert(0, snapshot.Identity.CatalogId);
        foreach (var expiredId in index.Skip(_settings.MaxRetainedSnapshots).ToList())
            await _cache.RemoveAsync(SnapshotKey(factoryName, expiredId), cancellationToken).ConfigureAwait(false);
        index = [.. index.Take(_settings.MaxRetainedSnapshots)];
        await _cache.SetStringAsync(indexKey, JsonSerializer.Serialize(index, JsonHelper.JsonSerializerOptions), options, cancellationToken)
            .ConfigureAwait(false);
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

    private static string Prefix(string factoryName)
        => $"playframework:runtime-descriptions:sha256-v1:{factoryName}";

    private static string LatestKey(string factoryName) => $"{Prefix(factoryName)}:latest";
    private static string IndexKey(string factoryName) => $"{Prefix(factoryName)}:index";
    private static string SnapshotKey(string factoryName, string catalogId) => $"{Prefix(factoryName)}:snapshot:{catalogId}";
}
