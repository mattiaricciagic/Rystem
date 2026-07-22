# Runtime scene and tool descriptions

Runtime descriptions are available from `Rystem.PlayFramework` `10.0.11-beta.22`.

```bash
dotnet add package Rystem.PlayFramework --version 10.0.11-beta.23
```

They allow an application to update scene and local-tool descriptions without rebuilding the dependency injection container. The feature is intended for descriptions stored in files, memory, databases, Azure App Configuration, or another application-owned source.

## What can change

The runtime catalog can resolve:

- scene descriptions;
- service-tool descriptions;
- HTTP endpoint-tool descriptions;
- client tool and client command descriptions.

The following remain static because they are part of the executable contract:

- scene and tool names;
- service methods and endpoint routes;
- request and response types;
- JSON Schema and parameter descriptions;
- MCP, RAG, and web-search tool definitions;
- main actors and scene actors, which retain their existing request-time factories.

Runtime descriptions are global to a named PlayFramework registration. A resolver must not vary its result by user, tenant, conversation, request metadata, or request content.

## Minimal configuration

Every dynamic overload accepts a `RuntimeDescriptionContext`. Its `Services` property is a scoped service provider created for the refresh, not the current request provider.

```csharp
services.AddScoped<IAiPromptSnapshot, AiPromptSnapshot>();

services.AddPlayFramework("orders", framework =>
{
    framework.WithRuntimeDescriptions(settings =>
    {
        settings.RefreshMode = RuntimeDescriptionRefreshMode.Background;
        settings.BackgroundRefreshInterval = TimeSpan.FromMinutes(5);
        settings.RefreshAtStartup = true;
        settings.FailureMode = RuntimeDescriptionFailureMode.UseFallback;
        settings.ConsistencyMode = RuntimeDescriptionConsistencyMode.Execution;
    });

    framework.AddScene(
        "orders",
        async (context, cancellationToken) =>
            await context.Services
                .GetRequiredService<IAiPromptSnapshot>()
                .GetSceneDescriptionAsync(cancellationToken),
        scene => scene.WithService<IOrderService>(tools => tools.WithMethod(
            service => service.SearchAsync(default!, default),
            "search_orders",
            async (context, cancellationToken) =>
                await context.Services
                    .GetRequiredService<IAiPromptSnapshot>()
                    .GetSearchDescriptionAsync(cancellationToken),
            fallbackDescription: "Search orders")),
        fallbackDescription: "Manage orders");
});
```

All resolvers in one refresh receive the same `RuntimeDescriptionContext` and DI scope. A scoped snapshot accessor can therefore read one coherent external document and serve all scene/tool lookups without repeating remote I/O.

The sample assumes these application contracts:

```csharp
public interface IAiPromptSnapshot
{
    Task<RuntimeDescriptionValue> GetSceneDescriptionAsync(CancellationToken cancellationToken);
    Task<RuntimeDescriptionValue> GetSearchDescriptionAsync(CancellationToken cancellationToken);
}

public interface IOrderService
{
    Task<string> SearchAsync(string query, CancellationToken cancellationToken);
}
```

## Resolver overloads

Scenes support these overloads in addition to the existing static overload:

```csharp
AddScene(
    string name,
    Func<RuntimeDescriptionContext, string> descriptionFactory,
    Action<SceneBuilder> configure,
    string? fallbackDescription = null)

AddScene(
    string name,
    Func<RuntimeDescriptionContext, CancellationToken, Task<string>> descriptionFactory,
    Action<SceneBuilder> configure,
    string? fallbackDescription = null)

AddScene(
    string name,
    Func<RuntimeDescriptionContext, CancellationToken, Task<RuntimeDescriptionValue>> descriptionFactory,
    Action<SceneBuilder> configure,
    string? fallbackDescription = null)
```

The same three resolver forms are available for:

- `ServiceToolBuilder<TService>.WithMethod(...)`;
- both `EndpointToolBuilder<TClient>.WithAction<TResponse>(...)` and `WithAction<TRequest, TResponse>(...)`;
- typed, untyped, and manual-schema `ClientInteractionBuilder.AddTool(...)`;
- typed, untyped, and manual-schema `ClientInteractionBuilder.AddCommand(...)`.

The schema overloads only make the main tool description dynamic. Their JSON Schema remains fixed.

Use `RuntimeDescriptionValue` when the source has useful version metadata:

```csharp
framework.AddScene(
    "orders",
    async (context, cancellationToken) =>
    {
        var snapshot = await context.Services
            .GetRequiredService<IAiPromptDocumentProvider>()
            .GetAsync(cancellationToken);

        return new RuntimeDescriptionValue
        {
            Value = snapshot.OrderSceneDescription,
            Version = snapshot.Version,
            Source = "azure-app-configuration",
            ETag = snapshot.ETag
        };
    },
    scene => { },
    fallbackDescription: "Manage orders");
```

`Source`, `Version`, and `ETag` are diagnostic metadata. Only description content and the static template determine `CatalogId`.

## Refresh modes

### Background

`Background` is the production default.

```csharp
framework.WithRuntimeDescriptions(settings =>
{
    settings.RefreshMode = RuntimeDescriptionRefreshMode.Background;
    settings.RefreshAtStartup = true;
    settings.BackgroundRefreshInterval = TimeSpan.FromMinutes(5);
    settings.RefreshOnChange = true;
});
```

The hosted service performs the initial load, then refreshes on the configured timer. Requests only acquire the current immutable in-memory catalog and do not call description resolvers.

### Manual

`Manual` is suitable for controlled deployments, evaluation batches, and administrative workflows.

```csharp
framework.WithRuntimeDescriptions(settings =>
{
    settings.RefreshMode = RuntimeDescriptionRefreshMode.Manual;
    settings.RefreshAtStartup = false;
    settings.FailureMode = RuntimeDescriptionFailureMode.Throw;
    settings.MissingVersionBehavior = MissingRuntimeDescriptionVersionBehavior.Throw;
});
```

Resolve the refresher through the named factory and use its result as a barrier:

```csharp
var refresher = serviceProvider
    .GetRequiredService<IFactory<IRuntimeDescriptionRefresher>>()
    .Create("orders")
    ?? throw new InvalidOperationException("Runtime description refresher not found.");

var result = await refresher.RefreshAsync(cancellationToken);

if (result.Outcome == RuntimeDescriptionRefreshOutcome.Failed)
{
    throw new InvalidOperationException(
        $"Runtime descriptions were not published: {result.ErrorMessage}");
}

logger.LogInformation(
    "Runtime catalog {CatalogId}, operation {OperationId}, source version {SourceVersion}",
    result.CurrentCatalogId,
    result.OperationId,
    result.SourceVersion);
```

With `RefreshAtStartup = false`, complete a successful manual refresh before accepting requests. Refresh is intentionally not exposed through `SceneRequestSettings` or client input.

### EveryRequest

`EveryRequest` forces one complete request-local resolution at the start of each PlayFramework request.

```csharp
framework.WithRuntimeDescriptions(settings =>
{
    settings.RefreshMode = RuntimeDescriptionRefreshMode.EveryRequest;
    settings.FailureMode = RuntimeDescriptionFailureMode.Throw;
});
```

Use it for deterministic integration tests and diagnostics. It adds provider latency, validation, hashing, and possible declaration materialization to every request, serializes refreshes for the named factory, does not update the global catalog, and is not recommended for normal production traffic.

## Change notifications

A background configuration can subscribe to an application-owned `IChangeToken` source:

```csharp
public sealed class ConfigurationRuntimeDescriptionChanges(
    IConfiguration configuration) : IRuntimeDescriptionChangeTokenSource
{
    public IChangeToken GetChangeToken()
        => configuration.GetReloadToken();
}
```

Register it on the same PlayFramework builder:

```csharp
framework.AddRuntimeDescriptionChangeTokenSource<ConfigurationRuntimeDescriptionChanges>();
```

The source must return a new token after each notification. Timer and notification triggers never overlap an active refresh; a busy trigger is reported as `SkippedBusy`, while the periodic timer remains the safety net for missed changes.

The sample requires `Microsoft.Extensions.Primitives` for `IChangeToken`.

For Azure App Configuration, configure its refresh mechanism in the host, expose the resulting snapshot through a scoped provider, and optionally adapt its configuration reload token as above. PlayFramework does not own Azure credentials, polling, or key selection.

## File or configuration-backed provider

The host can load a reloadable JSON file through the standard configuration pipeline:

```csharp
host.Configuration.AddJsonFile(
    "ai-prompts.json",
    optional: false,
    reloadOnChange: true);

services.AddScoped<IAiPromptSnapshot, ConfigurationAiPromptSnapshot>();
```

A scoped accessor should copy the values it needs when the refresh scope is created:

```csharp
public sealed class ConfigurationAiPromptSnapshot : IAiPromptSnapshot
{
    private readonly string _scene;
    private readonly string _tool;
    private readonly string? _version;

    public ConfigurationAiPromptSnapshot(IConfiguration configuration)
    {
        _scene = configuration["AiPrompts:Orders:Scene"]
            ?? throw new InvalidOperationException("Missing orders scene description.");
        _tool = configuration["AiPrompts:Orders:SearchTool"]
            ?? throw new InvalidOperationException("Missing search tool description.");
        _version = configuration["AiPrompts:Version"];
    }

    public Task<RuntimeDescriptionValue> GetSceneDescriptionAsync(
        CancellationToken cancellationToken)
        => Task.FromResult(new RuntimeDescriptionValue
        {
            Value = _scene,
            Version = _version,
            Source = "configuration"
        });

    public Task<RuntimeDescriptionValue> GetSearchDescriptionAsync(
        CancellationToken cancellationToken)
        => Task.FromResult(new RuntimeDescriptionValue
        {
            Value = _tool,
            Version = _version,
            Source = "configuration"
        });
}
```

## Consistency and interrupted executions

The default consistency is `Execution`:

```csharp
settings.ConsistencyMode = RuntimeDescriptionConsistencyMode.Execution;
settings.MissingVersionBehavior = MissingRuntimeDescriptionVersionBehavior.UseLatestAndWarn;
```

When an execution is interrupted for a client interaction, its `CatalogId` is stored in `ExecutionState`. A resumed execution asks for the same catalog. If it is no longer retained:

- `UseLatestAndWarn` continues with the current catalog and emits a warning;
- `Throw` fails the resume operation.

`Request` consistency ignores the stored catalog and acquires the latest catalog on each request. Neither option freezes MainActor or scene-actor text for an entire conversation; actors retain their separate lifecycle.

## Last-known-good storage

The default store is process-local memory:

```csharp
settings.SnapshotStoreMode = RuntimeDescriptionSnapshotStoreMode.Memory;
settings.SnapshotRetention = TimeSpan.FromHours(24);
settings.MaxRetainedSnapshots = 10;
```

Use `Distributed` when catalogs must survive process restarts or be available to several instances:

```csharp
services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = configuration.GetConnectionString("Redis");
});

services.AddPlayFramework("orders", framework =>
{
    framework.WithRuntimeDescriptions(settings =>
    {
        settings.SnapshotStoreMode = RuntimeDescriptionSnapshotStoreMode.Distributed;
    });
});
```

`Distributed` requires an `IDistributedCache` registration and fails explicitly if it is missing. Its retention index is best effort because `IDistributedCache` has no transactional set primitives.

Applications requiring stronger guarantees can implement `IRuntimeDescriptionSnapshotStore` and register it with:

```csharp
framework.AddRuntimeDescriptionSnapshotStore<MyRuntimeDescriptionSnapshotStore>();
```

Store write failures do not discard a fully validated candidate: it is published in memory and the failure is reported through warnings, refresh results, traces, and metrics.

## Failure policy

```csharp
settings.FailureMode = RuntimeDescriptionFailureMode.UseFallback;
```

`UseFallback` applies this recovery order:

1. retain the current complete catalog;
2. recover a compatible, non-expired last-known-good snapshot;
3. build a complete catalog from the static `fallbackDescription` values.

Catalogs are never assembled from a mixture of newly resolved values, previous values, and static fallbacks. A candidate is published only after every dynamic value has resolved and the complete catalog has passed validation.

Use `Throw` for fail-closed startup, request-local tests, and evaluation pipelines. A manual refresh always returns a structured `RuntimeDescriptionRefreshResult`; callers should inspect `Outcome` rather than treating task completion as publication success.

## Validation and limits

Default limits are:

```csharp
settings.MaxDescriptionUtf8Bytes = 16 * 1024;
settings.MaxCatalogUtf8Bytes = 1024 * 1024;
```

Dynamic descriptions must be non-empty valid Unicode, cannot contain NUL, and must fit both limits. Snapshot format, template hash, key completeness, content hash, catalog hash, and retention are revalidated before recovery.

Static-only registrations preserve their existing behavior, including historically accepted empty tool descriptions.

## Reading the effective request catalog

Responses emitted after acquisition carry the exact identity used by that request:

```csharp
await foreach (var response in sceneManager.ExecuteAsync(input, cancellationToken: cancellationToken))
{
    var runtime = response.RuntimeDescriptions;
    if (runtime is null)
        continue;

    logger.LogInformation(
        "Request used catalog {CatalogId}, source {SourceVersion}, fallback {UsedFallback}",
        runtime.CatalogIdentity.CatalogId,
        runtime.SourceVersion,
        runtime.UsedFallback);
}
```

`SceneContext` exposes:

- `RuntimeDescriptions`: request-local identity, source version, recovery state, consistency, and acquisition duration;
- `RuntimeSceneCatalog`: a read-only projection of effective scenes, tools, and `AIFunctionDeclaration` instances for custom planners and instrumentation.

Do not use `IScene.Description`, `ISceneFactory.Scenes`, or `ISceneFactory.ScenesAsAiTool` to attest runtime values. They remain the startup template/static fallback view for compatibility.

## Discovery

The existing discovery endpoint includes:

- `IsRuntimeResolved`;
- `RuntimeDescriptionCatalogId`;
- `RuntimeDescriptionVersion`.

Discovery reads the current globally published catalog when available and otherwise returns the static view. It never invokes resolvers or triggers a refresh. In `EveryRequest`, discovery cannot attest the request-local catalog; use `AiSceneResponse.RuntimeDescriptions` for that purpose.

## Observability

Refresh logs and Activity events use the `playframework.runtime_metadata.*` namespace. The lifecycle distinguishes:

- `refresh_triggered`;
- `refresh_started`;
- `change_detected`;
- exactly one terminal event: `catalog_published`, `catalog_unchanged`, `refresh_failed`, or `refresh_skipped_busy`.

The refresh result separates source, validation, hashing, materialization, snapshot store, and publication durations. Metrics use bounded dimensions; catalog IDs, operation IDs, and source versions are emitted only in logs and traces. Description text, source credentials, connection strings, and full source URIs are never emitted by default.

## Testing pattern

Use `Manual` when a test controls publication explicitly:

```csharp
source.SetDescriptions(candidate);

var refresh = await refresher.RefreshAsync(testCancellationToken);
Assert.Equal(RuntimeDescriptionRefreshOutcome.Changed, refresh.Outcome);

var responses = new List<AiSceneResponse>();
await foreach (var response in sceneManager.ExecuteAsync("test request"))
    responses.Add(response);

var completed = Assert.Single(responses.Where(x => x.Status == AiResponseStatus.Completed));
Assert.Equal(
    refresh.CurrentCatalogId,
    completed.RuntimeDescriptions?.CatalogIdentity.CatalogId);
```

Use `EveryRequest` only for tests that must mutate the source between requests without calling the administrative refresher.

## Security boundary

Runtime descriptions are privileged prompt configuration:

- resolvers and sources are selected only by host configuration;
- client payloads cannot select a source, trigger refresh, or choose a catalog;
- source credentials remain in the application provider;
- an application-created refresh endpoint must be authenticated, authorized, rate limited, and audited;
- PlayFramework validates structure, size, hashes, and atomicity, but does not decide whether the text is semantically safe.

Applications should apply approval, linting, adversarial evaluation, and access control before publishing prompt text.

## Primary drawback

The feature adds a second, versioned lifecycle next to the static template:

- every semantic change recreates scene and tool declarations;
- retained catalogs consume bounded memory or distributed-cache space;
- availability fallback can intentionally use stale descriptions;
- static `ISceneFactory` views and effective request-local views have different meanings;
- `EveryRequest` moves provider latency and allocation into the request path.

Applications that do not register dynamic descriptions keep the static behavior. Applications enabling the feature should explicitly choose refresh, failure, consistency, retention, and observability policies rather than treating hot reload as free.
