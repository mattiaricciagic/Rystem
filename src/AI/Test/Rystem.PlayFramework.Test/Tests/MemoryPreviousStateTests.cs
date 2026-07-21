using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Rystem.PlayFramework.Test.Tests;

/// <summary>
/// Tests for defect #7: the long-term memory re-synthesis used to always receive
/// previousMemory = null, preventing incremental accumulation of Summary/ImportantFacts.
/// After the fix, the memory loaded during initialization is preserved on SceneContext
/// and passed back to IMemory.SummarizeAsync on the next turn.
/// </summary>
public sealed class MemoryPreviousStateTests
{
    [Fact]
    public async Task SecondTurn_MemoryReSynthesis_ReceivesPreviousMemory()
    {
        // Arrange
        var probe = new MemoryProbe();
        var services = BuildServices(probe);
        var serviceProvider = services.BuildServiceProvider();
        var sceneManager = serviceProvider.GetRequiredService<ISceneManager>();

        var conversationKey = Guid.NewGuid().ToString();

        SceneRequestSettings Settings() => new()
        {
            ExecutionMode = SceneExecutionMode.Scene,
            SceneName = "TestScene",
            ConversationKey = conversationKey
        };

        // Act — two consecutive turns on the same conversation key.
        await foreach (var _ in sceneManager.ExecuteAsync("Primo turno", metadata: null, Settings())) { }
        await foreach (var _ in sceneManager.ExecuteAsync("Secondo turno", metadata: null, Settings())) { }

        // Assert
        Assert.Equal(2, probe.ObservedPreviousMemory.Count);

        // Turn 1: nothing stored yet, so previousMemory is null (expected).
        Assert.Null(probe.ObservedPreviousMemory[0]);

        // Turn 2: the memory produced in turn 1 must be passed back (the actual fix).
        var secondTurnPrevious = probe.ObservedPreviousMemory[1];
        Assert.NotNull(secondTurnPrevious);
        Assert.Equal(1, secondTurnPrevious!.ConversationCount);
        Assert.True(
            secondTurnPrevious.ImportantFacts.ContainsKey("turn"),
            "Previous memory must carry forward the facts accumulated in turn 1.");
    }

    [Fact]
    public async Task Memory_AccumulatesConversationCount_AcrossThreeTurns()
    {
        // Arrange
        var probe = new MemoryProbe();
        var services = BuildServices(probe);
        var serviceProvider = services.BuildServiceProvider();
        var sceneManager = serviceProvider.GetRequiredService<ISceneManager>();

        var conversationKey = Guid.NewGuid().ToString();

        SceneRequestSettings Settings() => new()
        {
            ExecutionMode = SceneExecutionMode.Scene,
            SceneName = "TestScene",
            ConversationKey = conversationKey
        };

        // Act
        await foreach (var _ in sceneManager.ExecuteAsync("Turno 1", metadata: null, Settings())) { }
        await foreach (var _ in sceneManager.ExecuteAsync("Turno 2", metadata: null, Settings())) { }
        await foreach (var _ in sceneManager.ExecuteAsync("Turno 3", metadata: null, Settings())) { }

        // Assert: incremental accumulation, not a reset each cycle.
        Assert.Equal(3, probe.ObservedPreviousMemory.Count);
        Assert.Null(probe.ObservedPreviousMemory[0]);
        Assert.Equal(1, probe.ObservedPreviousMemory[1]!.ConversationCount);
        Assert.Equal(2, probe.ObservedPreviousMemory[2]!.ConversationCount);
    }

    private static ServiceCollection BuildServices(MemoryProbe probe)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddDistributedMemoryCache();
        services.AddChatClient<CapturingMockChatClient>(name: null);
        services.AddSingleton(new List<string>());
        services.AddSingleton(probe);

        services.AddPlayFramework(builder =>
        {
            builder.AddScene("TestScene", "A test scene", _ => { });
            builder.WithMemory(memory => memory
                .WithCustomMemory<RecordingMemory>()
                .WithCustomStorage<RecordingMemoryStorage>());
        });

        return services;
    }
}

/// <summary>
/// Shared state between the recording memory and its storage, plus the observed
/// previousMemory argument for each summarization call.
/// </summary>
internal sealed class MemoryProbe
{
    public ConcurrentDictionary<string, ConversationMemory> Store { get; } = new();
    public List<ConversationMemory?> ObservedPreviousMemory { get; } = [];
}

/// <summary>
/// Records the previousMemory argument received on each summarization and produces an
/// incremented memory persisted to the shared store (so the next turn can load it).
/// </summary>
internal sealed class RecordingMemory : IMemory
{
    private readonly MemoryProbe _probe;

    public RecordingMemory(MemoryProbe probe)
    {
        _probe = probe;
    }

    public bool FactoryNameAlreadySetup { get; set; }
    public void SetFactoryName(AnyOf<string?, Enum>? name) { }

    public Task<ConversationMemory> SummarizeAsync(
        ConversationMemory? previousMemory,
        string startingMessage,
        IReadOnlyList<ChatMessage> conversationMessages,
        IReadOnlyDictionary<string, object>? metadata,
        SceneRequestSettings? settings,
        IChatClientManager chatClientManager,
        CancellationToken cancellationToken = default)
    {
        _probe.ObservedPreviousMemory.Add(previousMemory);

        var updated = new ConversationMemory
        {
            Summary = $"{previousMemory?.Summary} | {startingMessage}".Trim(' ', '|'),
            ImportantFacts = new Dictionary<string, object>(previousMemory?.ImportantFacts ?? [])
            {
                ["turn"] = startingMessage
            },
            ConversationCount = (previousMemory?.ConversationCount ?? 0) + 1,
            LastUpdated = DateTime.UtcNow
        };

        var key = settings?.ConversationKey ?? string.Empty;
        _probe.Store[key] = updated;

        return Task.FromResult(updated);
    }
}

/// <summary>
/// Storage backed by the shared probe store, so what RecordingMemory saves is what
/// the runtime loads on the following turn.
/// </summary>
internal sealed class RecordingMemoryStorage : IMemoryStorage
{
    private readonly MemoryProbe _probe;

    public RecordingMemoryStorage(MemoryProbe probe)
    {
        _probe = probe;
    }

    public bool FactoryNameAlreadySetup { get; set; }
    public void SetFactoryName(AnyOf<string?, Enum>? name) { }

    public Task<ConversationMemory?> GetAsync(
        string conversationKey,
        IReadOnlyDictionary<string, object>? metadata,
        SceneRequestSettings? settings,
        CancellationToken cancellationToken = default)
    {
        _probe.Store.TryGetValue(conversationKey, out var memory);
        return Task.FromResult<ConversationMemory?>(memory);
    }

    public Task SetAsync(
        string conversationKey,
        ConversationMemory memory,
        IReadOnlyDictionary<string, object>? metadata,
        SceneRequestSettings? settings,
        CancellationToken cancellationToken = default)
    {
        _probe.Store[conversationKey] = memory;
        return Task.CompletedTask;
    }
}
