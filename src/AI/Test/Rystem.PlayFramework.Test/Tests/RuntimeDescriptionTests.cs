using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using Rystem.PlayFramework.Api;
using Rystem.PlayFramework.Mcp;
using Rystem.PlayFramework.Telemetry;

namespace Rystem.PlayFramework.Test.Tests;

public sealed class RuntimeDescriptionTests
{
    [Fact]
    public async Task ManualRefresh_MaterializesSceneAndTool_FromOneScopedSnapshot()
    {
        PromptSnapshotAccessor.InstanceCount = 0;
        var source = new MutablePromptSource("scene-v1", "tool-v1", "snapshot-v1");
        await using var provider = BuildProvider(source);

        var refresher = provider.GetRequiredService<IFactory<IRuntimeDescriptionRefresher>>().Create("runtime");
        var manager = provider.GetRequiredService<IFactory<RuntimeDescriptionCatalogManager>>().Create("runtime");

        var result = await refresher!.RefreshAsync();
        var acquired = await manager!.AcquireAsync(null, CancellationToken.None);

        Assert.Equal(RuntimeDescriptionRefreshOutcome.Changed, result.Outcome);
        Assert.Equal(RuntimeDescriptionRecoverySource.None, result.RecoverySource);
        Assert.False(result.UsedFallback);
        Assert.Equal("snapshot-v1", result.SourceVersion);
        Assert.True(result.HasUniformSourceVersion);
        Assert.Equal(1, PromptSnapshotAccessor.InstanceCount);

        var scene = Assert.Single(acquired.PublicView.Scenes);
        Assert.Equal("scene-v1", scene.Description);
        Assert.Equal("scene-v1", scene.RoutingDeclaration.Description);
        var tool = Assert.Single(scene.Tools);
        Assert.Equal("tool-v1", tool.Description);
        Assert.Equal("tool-v1", tool.Declaration.Description);
        Assert.Equal(result.OperationId, acquired.ExecutionInfo.LastValidationOperationId);
        Assert.Equal(result.CatalogIdentity, acquired.ExecutionInfo.CatalogIdentity);
    }

    [Fact]
    public async Task Refresh_Unchanged_ReusesCatalogButAdvancesValidatedSourceStamp()
    {
        PromptSnapshotAccessor.InstanceCount = 0;
        var source = new MutablePromptSource("scene", "tool", "snapshot-v1");
        await using var provider = BuildProvider(source);
        var refresher = provider.GetRequiredService<IFactory<IRuntimeDescriptionRefresher>>().Create("runtime")!;
        var manager = provider.GetRequiredService<IFactory<RuntimeDescriptionCatalogManager>>().Create("runtime")!;

        await refresher.RefreshAsync();
        var first = await manager.AcquireAsync(null, CancellationToken.None);

        source.Version = "snapshot-v2";
        var result = await refresher.RefreshAsync();
        var second = await manager.AcquireAsync(null, CancellationToken.None);

        Assert.Equal(RuntimeDescriptionRefreshOutcome.Unchanged, result.Outcome);
        Assert.Same(first.Catalog, second.Catalog);
        Assert.Equal(first.Catalog.Identity.CatalogId, second.Catalog.Identity.CatalogId);
        Assert.Equal("snapshot-v2", second.ExecutionInfo.SourceVersion);
        Assert.Equal(result.OperationId, second.ExecutionInfo.LastValidationOperationId);
        Assert.True(second.ExecutionInfo.LastValidatedAt >= first.ExecutionInfo.LastValidatedAt);
        Assert.Equal(2, PromptSnapshotAccessor.InstanceCount);
    }

    [Fact]
    public async Task Refresh_Changed_PublishesNewCatalogAndPreservesPinnedVersion()
    {
        var source = new MutablePromptSource("scene-v1", "tool-v1", "snapshot-v1");
        await using var provider = BuildProvider(source);
        var refresher = provider.GetRequiredService<IFactory<IRuntimeDescriptionRefresher>>().Create("runtime")!;
        var manager = provider.GetRequiredService<IFactory<RuntimeDescriptionCatalogManager>>().Create("runtime")!;

        await refresher.RefreshAsync();
        var first = await manager.AcquireAsync(null, CancellationToken.None);

        source.SceneDescription = "scene-v2";
        source.ToolDescription = "tool-v2";
        source.Version = "snapshot-v2";
        var changed = await refresher.RefreshAsync();
        var latest = await manager.AcquireAsync(null, CancellationToken.None);
        var pinned = await manager.AcquireAsync(first.Catalog.Identity.CatalogId, CancellationToken.None);

        Assert.Equal(RuntimeDescriptionRefreshOutcome.Changed, changed.Outcome);
        Assert.NotEqual(first.Catalog.Identity.CatalogId, latest.Catalog.Identity.CatalogId);
        Assert.Equal(first.Catalog.Identity.CatalogId, pinned.Catalog.Identity.CatalogId);
        Assert.Equal("scene-v1", pinned.PublicView.Scenes.Single().Description);
        Assert.Equal(first.Catalog.Identity.CatalogId, pinned.ExecutionInfo.RequestedCatalogId);
    }

    [Fact]
    public async Task SourceFailure_WithCurrentCatalog_IsFailedAndObservable()
    {
        var source = new MutablePromptSource("scene", "tool", "snapshot-v1");
        await using var provider = BuildProvider(source, settings =>
        {
            settings.FailureMode = RuntimeDescriptionFailureMode.UseFallback;
        });
        var refresher = provider.GetRequiredService<IFactory<IRuntimeDescriptionRefresher>>().Create("runtime")!;
        var manager = provider.GetRequiredService<IFactory<RuntimeDescriptionCatalogManager>>().Create("runtime")!;

        await refresher.RefreshAsync();
        var before = await manager.AcquireAsync(null, CancellationToken.None);
        source.ThrowOnRead = true;

        var failed = await refresher.RefreshAsync();
        var after = await manager.AcquireAsync(null, CancellationToken.None);

        Assert.Equal(RuntimeDescriptionRefreshOutcome.Failed, failed.Outcome);
        Assert.Equal(RuntimeDescriptionRecoverySource.CurrentCatalog, failed.RecoverySource);
        Assert.True(failed.UsedFallback);
        Assert.Equal(before.Catalog.Identity.CatalogId, after.Catalog.Identity.CatalogId);
    }

    [Fact]
    public async Task EveryRequest_ResolvesOncePerRequest_AndUsesRequestLocalCatalog()
    {
        var source = new MutablePromptSource("scene-v1", "tool-v1", "snapshot-v1");
        await using var provider = BuildProvider(source, settings =>
        {
            settings.RefreshMode = RuntimeDescriptionRefreshMode.EveryRequest;
            settings.FailureMode = RuntimeDescriptionFailureMode.Throw;
        });
        var manager = provider.GetRequiredService<IFactory<RuntimeDescriptionCatalogManager>>().Create("runtime")!;

        var first = await manager.AcquireAsync(null, CancellationToken.None);
        source.SceneDescription = "scene-v2";
        source.ToolDescription = "tool-v2";
        source.Version = "snapshot-v2";
        var second = await manager.AcquireAsync(null, CancellationToken.None);

        Assert.True(first.ExecutionInfo.IsRequestLocal);
        Assert.True(second.ExecutionInfo.IsRequestLocal);
        Assert.NotEqual(first.Catalog.Identity.CatalogId, second.Catalog.Identity.CatalogId);
        Assert.Equal(2, PromptSnapshotAccessor.InstanceCount);
        Assert.Null(manager.CurrentCatalog);
    }

    [Fact]
    public async Task ManualRefresh_RejectsDescriptionOverUtf8Limit()
    {
        var source = new MutablePromptSource("12345", "tool", "snapshot-v1");
        await using var provider = BuildProvider(source, settings =>
        {
            settings.MaxDescriptionUtf8Bytes = 4;
            settings.MaxCatalogUtf8Bytes = 64;
        });
        var refresher = provider.GetRequiredService<IFactory<IRuntimeDescriptionRefresher>>().Create("runtime")!;

        var result = await refresher.RefreshAsync();

        Assert.Equal(RuntimeDescriptionRefreshOutcome.Failed, result.Outcome);
        Assert.Equal("source", result.FailureStage);
        Assert.Contains("byte limit", result.ErrorMessage);
    }

    [Fact]
    public async Task RuntimeIdentity_IsAvailableToMainActorAndTerminalResponse()
    {
        var source = new MutablePromptSource("scene-runtime", "tool-runtime", "snapshot-v1");
        var observer = new RuntimeObserver();
        await using var provider = BuildProvider(source, observer: observer);
        var refresher = provider.GetRequiredService<IFactory<IRuntimeDescriptionRefresher>>().Create("runtime")!;
        await refresher.RefreshAsync();
        var sceneManager = provider.GetRequiredService<IFactory<ISceneManager>>().Create("runtime")!;

        var responses = new List<AiSceneResponse>();
        await foreach (var response in sceneManager.ExecuteAsync("hello", settings: new SceneRequestSettings
        {
            ConversationKey = Guid.NewGuid().ToString(),
            CacheBehavior = CacheBehavior.Avoidable
        }))
        {
            responses.Add(response);
        }

        var completed = Assert.Single(responses.Where(x => x.Status == AiResponseStatus.Completed));
        Assert.NotNull(completed.RuntimeDescriptions);
        Assert.NotNull(observer.ExecutionInfo);
        Assert.Equal(completed.RuntimeDescriptions!.CatalogIdentity.CatalogId, observer.ExecutionInfo!.CatalogIdentity.CatalogId);
        Assert.Equal("scene-runtime", observer.SceneDescription);
    }

    [Fact]
    public async Task Refresh_InProgress_DoesNotBlockOrPartiallyUpdateCurrentCatalog()
    {
        var source = new MutablePromptSource("scene-v1", "tool-v1", "snapshot-v1");
        await using var provider = BuildProvider(source);
        var refresher = provider.GetRequiredService<IFactory<IRuntimeDescriptionRefresher>>().Create("runtime")!;
        var manager = provider.GetRequiredService<IFactory<RuntimeDescriptionCatalogManager>>().Create("runtime")!;
        await refresher.RefreshAsync();

        source.SceneDescription = "scene-v2";
        source.ToolDescription = "tool-v2";
        source.Version = "snapshot-v2";
        source.ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.ContinueRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var refresh = refresher.RefreshAsync();
        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var during = await manager.AcquireAsync(null, CancellationToken.None);

        Assert.Equal("scene-v1", during.PublicView.Scenes.Single().Description);
        Assert.Equal("tool-v1", during.PublicView.Scenes.Single().Tools.Single().Description);

        source.ContinueRead.SetResult();
        await refresh;
        var after = await manager.AcquireAsync(null, CancellationToken.None);
        Assert.Equal("scene-v2", after.PublicView.Scenes.Single().Description);
        Assert.Equal("tool-v2", after.PublicView.Scenes.Single().Tools.Single().Description);
    }

    [Fact]
    public async Task Refresh_PropagatesCancellationToDescriptionResolver()
    {
        var source = new MutablePromptSource("scene", "tool", "snapshot-v1")
        {
            ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ContinueRead = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var provider = BuildProvider(source);
        var refresher = provider.GetRequiredService<IFactory<IRuntimeDescriptionRefresher>>().Create("runtime")!;
        using var cancellation = new CancellationTokenSource();

        var refresh = refresher.RefreshAsync(cancellation.Token);
        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
    }

    [Fact]
    public async Task ManualMode_DoesNotRefreshImplicitlyOnAcquire()
    {
        var source = new MutablePromptSource("scene", "tool", "snapshot-v1");
        await using var provider = BuildProvider(source);
        var manager = provider.GetRequiredService<IFactory<RuntimeDescriptionCatalogManager>>().Create("runtime")!;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.AcquireAsync(null, CancellationToken.None));

        Assert.Contains("manual refresh", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, PromptSnapshotAccessor.InstanceCount);
    }

    [Fact]
    public async Task RetentionPruning_MakesOldPinnedCatalogUnavailable()
    {
        var source = new MutablePromptSource("scene-v1", "tool-v1", "snapshot-v1");
        await using var provider = BuildProvider(source, settings => settings.MaxRetainedSnapshots = 1);
        var refresher = provider.GetRequiredService<IFactory<IRuntimeDescriptionRefresher>>().Create("runtime")!;
        var manager = provider.GetRequiredService<IFactory<RuntimeDescriptionCatalogManager>>().Create("runtime")!;
        await refresher.RefreshAsync();
        var first = await manager.AcquireAsync(null, CancellationToken.None);

        source.SceneDescription = "scene-v2";
        source.Version = "snapshot-v2";
        await refresher.RefreshAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.AcquireAsync(first.Catalog.Identity.CatalogId, CancellationToken.None));
    }

    [Fact]
    public async Task ChangeNotification_TriggersBackgroundRefresh()
    {
        var source = new MutablePromptSource("scene-v1", "tool-v1", "snapshot-v1");
        await using var provider = BuildProvider(
            source,
            settings =>
            {
                settings.RefreshMode = RuntimeDescriptionRefreshMode.Background;
                settings.RefreshAtStartup = false;
                settings.BackgroundRefreshInterval = TimeSpan.FromDays(1);
            },
            addChangeTokenSource: true);
        var manager = provider.GetRequiredService<IFactory<RuntimeDescriptionCatalogManager>>().Create("runtime")!;
        var changeSource = provider.GetRequiredService<IFactory<IRuntimeDescriptionChangeTokenSource>>().Create("runtime")!;
        var hostedService = Assert.Single(provider.GetServices<IHostedService>().OfType<RuntimeDescriptionBackgroundService>());
        await hostedService.StartAsync(CancellationToken.None);

        ((TestChangeTokenSource)changeSource).Signal();
        await WaitUntilAsync(() => manager.CurrentCatalog is not null, TimeSpan.FromSeconds(2));
        var acquired = await manager.AcquireAsync(null, CancellationToken.None);

        Assert.Equal("scene-v1", acquired.PublicView.Scenes.Single().Description);
        await hostedService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Refresh_EmitsStructuredLifecycleWithoutDescriptionContent()
    {
        var source = new MutablePromptSource("secret-scene-text", "secret-tool-text", "snapshot-v1");
        await using var provider = BuildProvider(source);
        var refresher = provider.GetRequiredService<IFactory<IRuntimeDescriptionRefresher>>().Create("runtime")!;
        var stopped = new TaskCompletionSource<Activity>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new ActivityListener
        {
            ShouldListenTo = activitySource => activitySource.Name == PlayFrameworkActivitySource.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == PlayFrameworkActivitySource.Activities.RuntimeDescriptionRefresh)
                    stopped.TrySetResult(activity);
            }
        };
        ActivitySource.AddActivityListener(listener);

        await refresher.RefreshAsync();
        var activity = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var eventNames = activity.Events.Select(x => x.Name).ToList();
        var telemetry = string.Join('|', activity.Tags.Select(x => $"{x.Key}={x.Value}"))
            + string.Join('|', activity.Events.SelectMany(x => x.Tags).Select(x => $"{x.Key}={x.Value}"));

        Assert.Contains(PlayFrameworkActivitySource.Events.RuntimeDescriptionRefreshTriggered, eventNames);
        Assert.Contains(PlayFrameworkActivitySource.Events.RuntimeDescriptionRefreshStarted, eventNames);
        Assert.Contains(PlayFrameworkActivitySource.Events.RuntimeDescriptionChangeDetected, eventNames);
        Assert.Contains(PlayFrameworkActivitySource.Events.RuntimeDescriptionCatalogPublished, eventNames);
        Assert.DoesNotContain("secret-scene-text", telemetry, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-tool-text", telemetry, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_MaterializesEndpointClientToolAndCommandDescriptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlayFramework("all-tools", builder =>
        {
            builder.WithRuntimeDescriptions(settings =>
            {
                settings.RefreshMode = RuntimeDescriptionRefreshMode.Manual;
                settings.RefreshAtStartup = false;
                settings.FailureMode = RuntimeDescriptionFailureMode.Throw;
            });
            builder.AddScene("mixed", "mixed scene", scene =>
            {
                scene.WithEndpoint<EndpointMarker>(endpoint => endpoint.WithAction<string>(
                    "endpoint-tool",
                    HttpMethod.Get,
                    "/status",
                    _ => "runtime endpoint"));
                scene.OnClient(client =>
                {
                    client.AddTool(
                        "client-tool",
                        "{\"type\":\"object\"}",
                        _ => "runtime client tool");
                    client.AddCommand(
                        "client-command",
                        _ => "runtime client command");
                });
            });
        });
        await using var provider = services.BuildServiceProvider();
        var refresher = provider.GetRequiredService<IFactory<IRuntimeDescriptionRefresher>>().Create("all-tools")!;
        var manager = provider.GetRequiredService<IFactory<RuntimeDescriptionCatalogManager>>().Create("all-tools")!;

        await refresher.RefreshAsync();
        var acquired = await manager.AcquireAsync(null, CancellationToken.None);
        var tools = acquired.PublicView.Scenes.Single().Tools.ToDictionary(x => x.Name);

        Assert.Equal("runtime endpoint", tools["endpoint-tool"].Description);
        Assert.Equal("runtime client tool", tools["client-tool"].Description);
        Assert.Equal("runtime client command", tools["client-command"].Description);
        Assert.All(tools.Values, tool => Assert.Equal(tool.Description, tool.Declaration.Description));
    }

    [Fact]
    public async Task RestartRecovery_UsesLastKnownGoodSnapshotBeforeStaticFallback()
    {
        SharedSnapshotStore.Clear();
        var initialSource = new MutablePromptSource("dynamic-scene", "dynamic-tool", "snapshot-v1");
        await using (var initialProvider = BuildProvider(initialSource, useSharedSnapshotStore: true))
        {
            var refresher = initialProvider.GetRequiredService<IFactory<IRuntimeDescriptionRefresher>>().Create("runtime")!;
            Assert.Equal(RuntimeDescriptionRefreshOutcome.Changed, (await refresher.RefreshAsync()).Outcome);
        }

        var unavailableSource = new MutablePromptSource("unused-scene", "unused-tool", "snapshot-v2")
        {
            ThrowOnRead = true
        };
        await using var recoveredProvider = BuildProvider(
            unavailableSource,
            settings => settings.FailureMode = RuntimeDescriptionFailureMode.UseFallback,
            useSharedSnapshotStore: true);
        var recoveredRefresher = recoveredProvider.GetRequiredService<IFactory<IRuntimeDescriptionRefresher>>().Create("runtime")!;
        var recoveredManager = recoveredProvider.GetRequiredService<IFactory<RuntimeDescriptionCatalogManager>>().Create("runtime")!;

        var result = await recoveredRefresher.RefreshAsync();
        var acquired = await recoveredManager.AcquireAsync(null, CancellationToken.None);

        Assert.Equal(RuntimeDescriptionRefreshOutcome.Changed, result.Outcome);
        Assert.Equal(RuntimeDescriptionRecoverySource.SnapshotStore, result.RecoverySource);
        Assert.Equal(RuntimeDescriptionSnapshotStoreOutcome.Succeeded, result.SnapshotStoreOutcome);
        Assert.Equal("dynamic-scene", acquired.PublicView.Scenes.Single().Description);
        Assert.True(acquired.ExecutionInfo.UsedFallback);
    }

    [Fact]
    public async Task SnapshotWriteFailure_DoesNotPreventAtomicInMemoryPublication()
    {
        var source = new MutablePromptSource("scene", "tool", "snapshot-v1");
        await using var provider = BuildProvider(source, useFailingSnapshotStore: true);
        var refresher = provider.GetRequiredService<IFactory<IRuntimeDescriptionRefresher>>().Create("runtime")!;
        var manager = provider.GetRequiredService<IFactory<RuntimeDescriptionCatalogManager>>().Create("runtime")!;

        var result = await refresher.RefreshAsync();
        var acquired = await manager.AcquireAsync(null, CancellationToken.None);

        Assert.Equal(RuntimeDescriptionRefreshOutcome.Changed, result.Outcome);
        Assert.Equal(RuntimeDescriptionSnapshotStoreOutcome.Failed, result.SnapshotStoreOutcome);
        Assert.Equal(result.CurrentCatalogId, acquired.Catalog.Identity.CatalogId);
    }

    [Fact]
    public void DistributedStore_RequiresDistributedCacheRegistration()
    {
        var source = new MutablePromptSource("scene", "tool", "snapshot-v1");
        using var provider = BuildProvider(source, settings =>
            settings.SnapshotStoreMode = RuntimeDescriptionSnapshotStoreMode.Distributed);
        var factory = provider.GetRequiredService<IFactory<RuntimeDescriptionCatalogManager>>();

        var exception = Assert.Throws<InvalidOperationException>(() => factory.Create("runtime"));

        Assert.Contains("IDistributedCache", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discovery_ReadsPublishedGlobalCatalogWithoutTriggeringRefresh()
    {
        var source = new MutablePromptSource("runtime-scene", "runtime-tool", "snapshot-v1");
        await using var provider = BuildProvider(source);
        var sceneFactory = provider.GetRequiredService<IFactory<ISceneFactory>>().Create("runtime")!;
        var manager = provider.GetRequiredService<IFactory<RuntimeDescriptionCatalogManager>>().Create("runtime")!;
        var mcpFactory = provider.GetRequiredService<IFactory<IMcpServerManager>>();

        var before = await WebApplicationExtensions.BuildDiscoveryResponseAsync(
            "runtime",
            sceneFactory,
            manager,
            mcpFactory,
            CancellationToken.None);

        Assert.False(before.IsRuntimeResolved);
        Assert.Equal("fallback-scene", before.Scenes.Single().Description);
        Assert.Equal(0, PromptSnapshotAccessor.InstanceCount);

        var refresher = provider.GetRequiredService<IFactory<IRuntimeDescriptionRefresher>>().Create("runtime")!;
        var refreshed = await refresher.RefreshAsync();
        var after = await WebApplicationExtensions.BuildDiscoveryResponseAsync(
            "runtime",
            sceneFactory,
            manager,
            mcpFactory,
            CancellationToken.None);

        Assert.True(after.IsRuntimeResolved);
        Assert.Equal(refreshed.CurrentCatalogId, after.RuntimeDescriptionCatalogId);
        Assert.Equal("snapshot-v1", after.RuntimeDescriptionVersion);
        Assert.Equal("runtime-scene", after.Scenes.Single().Description);
        Assert.Equal(1, PromptSnapshotAccessor.InstanceCount);
    }

    [Fact]
    public async Task BackgroundTrigger_IsSkippedWhileAnotherRefreshOwnsTheFactoryLock()
    {
        var source = new MutablePromptSource("scene-v1", "tool-v1", "snapshot-v1");
        await using var provider = BuildProvider(source);
        var refresher = provider.GetRequiredService<IFactory<IRuntimeDescriptionRefresher>>().Create("runtime")!;
        var manager = provider.GetRequiredService<IFactory<RuntimeDescriptionCatalogManager>>().Create("runtime")!;
        await refresher.RefreshAsync();

        source.SceneDescription = "scene-v2";
        source.ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.ContinueRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeRefresh = refresher.RefreshAsync();
        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var skipped = await manager.RefreshIfIdleAsync(
            RuntimeDescriptionRefreshReason.Timer,
            CancellationToken.None);

        Assert.Equal(RuntimeDescriptionRefreshOutcome.SkippedBusy, skipped.Outcome);
        source.ContinueRead.SetResult();
        await activeRefresh;
    }

    [Fact]
    public async Task FirstRefreshFailure_UsesCompleteStaticFallbackAndReportsIt()
    {
        var source = new MutablePromptSource("unused-scene", "unused-tool", "snapshot-v1")
        {
            ThrowOnRead = true
        };
        await using var provider = BuildProvider(source, settings =>
            settings.FailureMode = RuntimeDescriptionFailureMode.UseFallback);
        var refresher = provider.GetRequiredService<IFactory<IRuntimeDescriptionRefresher>>().Create("runtime")!;
        var manager = provider.GetRequiredService<IFactory<RuntimeDescriptionCatalogManager>>().Create("runtime")!;

        var result = await refresher.RefreshAsync();
        var acquired = await manager.AcquireAsync(null, CancellationToken.None);

        Assert.Equal(RuntimeDescriptionRefreshOutcome.Changed, result.Outcome);
        Assert.Equal(RuntimeDescriptionRecoverySource.StaticFallback, result.RecoverySource);
        Assert.Equal(2, result.FallbackItemCount);
        Assert.Equal("fallback-scene", acquired.PublicView.Scenes.Single().Description);
        Assert.Equal("fallback-tool", acquired.PublicView.Scenes.Single().Tools.Single().Description);
        Assert.True(acquired.ExecutionInfo.UsedFallback);
    }

    private static ServiceProvider BuildProvider(
        MutablePromptSource source,
        Action<RuntimeDescriptionSettings>? configure = null,
        RuntimeObserver? observer = null,
        bool addChangeTokenSource = false,
        bool useSharedSnapshotStore = false,
        bool useFailingSnapshotStore = false)
    {
        PromptSnapshotAccessor.InstanceCount = 0;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(source);
        services.AddScoped<PromptSnapshotAccessor>();
        services.AddSingleton<RuntimeToolService>();
        services.AddSingleton<IChatClient>(new MockChatClient());
        if (observer is not null)
            services.AddSingleton(observer);

        services.AddPlayFramework("runtime", builder =>
        {
            builder.WithRuntimeDescriptions(settings =>
            {
                settings.RefreshMode = RuntimeDescriptionRefreshMode.Manual;
                settings.RefreshAtStartup = false;
                settings.FailureMode = RuntimeDescriptionFailureMode.Throw;
                settings.MissingVersionBehavior = MissingRuntimeDescriptionVersionBehavior.Throw;
                settings.SnapshotStoreMode = RuntimeDescriptionSnapshotStoreMode.Memory;
                configure?.Invoke(settings);
            });

            if (addChangeTokenSource)
                builder.AddRuntimeDescriptionChangeTokenSource<TestChangeTokenSource>();
            if (useSharedSnapshotStore)
                builder.AddRuntimeDescriptionSnapshotStore<SharedSnapshotStore>();
            if (useFailingSnapshotStore)
                builder.AddRuntimeDescriptionSnapshotStore<FailingSnapshotStore>();

            if (observer is not null)
            {
                builder.AddMainActor(context =>
                {
                    var runtimeObserver = context.ServiceProvider.GetRequiredService<RuntimeObserver>();
                    runtimeObserver.ExecutionInfo = context.RuntimeDescriptions;
                    runtimeObserver.SceneDescription = context.RuntimeSceneCatalog?.Scenes.Single().Description;
                    return "runtime observer";
                });
            }

            builder.AddScene(
                "orders",
                static (context, cancellationToken) => context.Services
                    .GetRequiredService<PromptSnapshotAccessor>()
                    .GetSceneAsync(cancellationToken),
                scene => scene.WithService<RuntimeToolService>(tools => tools.WithMethod(
                    service => service.Execute(default!),
                    "execute",
                    static (context, cancellationToken) => context.Services
                        .GetRequiredService<PromptSnapshotAccessor>()
                        .GetToolAsync(cancellationToken),
                    fallbackDescription: "fallback-tool")),
                fallbackDescription: "fallback-scene");
        });

        return services.BuildServiceProvider();
    }

    private sealed class MutablePromptSource
    {
        public MutablePromptSource(string sceneDescription, string toolDescription, string version)
        {
            SceneDescription = sceneDescription;
            ToolDescription = toolDescription;
            Version = version;
        }

        public string SceneDescription { get; set; }
        public string ToolDescription { get; set; }
        public string Version { get; set; }
        public bool ThrowOnRead { get; set; }
        public TaskCompletionSource? ReadStarted { get; set; }
        public TaskCompletionSource? ContinueRead { get; set; }
    }

    private sealed class PromptSnapshotAccessor
    {
        private readonly string _sceneDescription;
        private readonly string _toolDescription;
        private readonly string _version;
        private readonly bool _throwOnRead;
        private readonly TaskCompletionSource? _readStarted;
        private readonly TaskCompletionSource? _continueRead;

        public PromptSnapshotAccessor(MutablePromptSource source)
        {
            Interlocked.Increment(ref InstanceCount);
            _sceneDescription = source.SceneDescription;
            _toolDescription = source.ToolDescription;
            _version = source.Version;
            _throwOnRead = source.ThrowOnRead;
            _readStarted = source.ReadStarted;
            _continueRead = source.ContinueRead;
        }

        public static int InstanceCount;

        public async Task<RuntimeDescriptionValue> GetSceneAsync(CancellationToken cancellationToken)
        {
            _readStarted?.TrySetResult();
            if (_continueRead is not null)
                await _continueRead.Task.WaitAsync(cancellationToken);
            return await GetAsync(_sceneDescription, cancellationToken);
        }

        public Task<RuntimeDescriptionValue> GetToolAsync(CancellationToken cancellationToken)
            => GetAsync(_toolDescription, cancellationToken);

        private Task<RuntimeDescriptionValue> GetAsync(string value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_throwOnRead)
                throw new InvalidOperationException("source unavailable");
            return Task.FromResult(new RuntimeDescriptionValue
            {
                Value = value,
                Version = _version,
                Source = "test"
            });
        }
    }

    private sealed class RuntimeToolService
    {
        public string Execute(string value) => value;
    }

    private sealed class EndpointMarker;

    private sealed class RuntimeObserver
    {
        public RuntimeDescriptionExecutionInfo? ExecutionInfo { get; set; }
        public string? SceneDescription { get; set; }
    }

    private sealed class TestChangeTokenSource : IRuntimeDescriptionChangeTokenSource
    {
        private CancellationTokenSource _source = new();

        public IChangeToken GetChangeToken()
            => new CancellationChangeToken(_source.Token);

        public void Signal()
        {
            var previous = Interlocked.Exchange(ref _source, new CancellationTokenSource());
            previous.Cancel();
            previous.Dispose();
        }
    }

    private sealed class SharedSnapshotStore : IRuntimeDescriptionSnapshotStore
    {
        private static readonly ConcurrentDictionary<string, RuntimeDescriptionSnapshot> s_snapshots = new();
        private static readonly ConcurrentDictionary<string, string> s_latest = new();

        public static void Clear()
        {
            s_snapshots.Clear();
            s_latest.Clear();
        }

        public ValueTask<RuntimeDescriptionSnapshot?> GetLatestAsync(string factoryName, CancellationToken cancellationToken = default)
            => s_latest.TryGetValue(factoryName, out var catalogId)
                ? GetAsync(factoryName, catalogId, cancellationToken)
                : ValueTask.FromResult<RuntimeDescriptionSnapshot?>(null);

        public ValueTask<RuntimeDescriptionSnapshot?> GetAsync(string factoryName, string catalogId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            s_snapshots.TryGetValue($"{factoryName}:{catalogId}", out var snapshot);
            return ValueTask.FromResult<RuntimeDescriptionSnapshot?>(snapshot);
        }

        public ValueTask SaveAsync(string factoryName, RuntimeDescriptionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            s_snapshots[$"{factoryName}:{snapshot.Identity.CatalogId}"] = snapshot;
            s_latest[factoryName] = snapshot.Identity.CatalogId;
            return ValueTask.CompletedTask;
        }

        public ValueTask RefreshLatestExpirationAsync(string factoryName, string catalogId, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class FailingSnapshotStore : IRuntimeDescriptionSnapshotStore
    {
        public ValueTask<RuntimeDescriptionSnapshot?> GetLatestAsync(string factoryName, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<RuntimeDescriptionSnapshot?>(null);

        public ValueTask<RuntimeDescriptionSnapshot?> GetAsync(string factoryName, string catalogId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<RuntimeDescriptionSnapshot?>(null);

        public ValueTask SaveAsync(string factoryName, RuntimeDescriptionSnapshot snapshot, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new InvalidOperationException("snapshot store unavailable"));

        public ValueTask RefreshLatestExpirationAsync(string factoryName, string catalogId, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new InvalidOperationException("snapshot store unavailable"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
            await Task.Delay(10, cancellation.Token);
    }
}
