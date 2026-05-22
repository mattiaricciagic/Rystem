using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rystem.PlayFramework.Test.Infrastructure;

namespace Rystem.PlayFramework.Test.Tests;

/// <summary>
/// Tests for conversation caching — verifying that subsequent turns
/// in the same conversation retain full history (no overwrite/context loss).
/// </summary>
public sealed class ConversationCacheTests
{
    #region Unit Tests (SceneContext logic)

    [Fact]
    public void LoadFromStoredConversation_CompletedPhase_ShouldNotRestoreExecutionState()
    {
        // Arrange: a stored conversation with Completed phase
        var stored = new StoredConversation
        {
            ConversationKey = "test-key",
            UserId = "user1",
            IsPublic = false,
            Timestamp = DateTime.UtcNow,
            Messages =
            [
                StoredMessage.FromTrackedMessage(TrackedMessage.CreateInitialContext("System prompt")),
                StoredMessage.FromTrackedMessage(TrackedMessage.CreateUserMessage("First question")),
                StoredMessage.FromTrackedMessage(TrackedMessage.CreateAssistantMessage("First answer")),
            ],
            ExecutionState = new ExecutionState
            {
                Phase = ExecutionPhase.Completed,
                ExecutedSceneOrder = ["scene1"],
                ExecutedTools = ["scene1.tool1.args"],
                SceneResults = new Dictionary<string, string> { ["scene1"] = "result" },
                AccumulatedCost = 0.01m
            }
        };

        var context = new SceneContext
        {
            ServiceProvider = null!,
            Input = MultiModalInput.FromText("Second question"),
            ChatClientManager = null!
        };

        // Act
        context.LoadFromStoredConversation(stored);

        // Assert: messages should be loaded
        Assert.Equal(3, context.ConversationHistory.Count);
        Assert.Equal("InitialContext", context.ConversationHistory[0].Label);
        Assert.Equal("User", context.ConversationHistory[1].Label);
        Assert.Equal("Assistant", context.ConversationHistory[2].Label);

        // Assert: execution state should NOT be restored (Completed = terminal phase)
        Assert.False(context.IsResuming);
        Assert.Null(context.RestoredExecutionState);
        Assert.Empty(context.ExecutedSceneOrder);
        Assert.Empty(context.ExecutedTools);
        Assert.Empty(context.ExecutedScenes);
        Assert.Empty(context.SceneResults);
        Assert.Equal(0m, context.TotalCost);

        // Assert: NO ExecutionCheckpoint message in history
        Assert.DoesNotContain(context.ConversationHistory, m => m.Label == "ExecutionCheckpoint");
    }

    [Fact]
    public void LoadFromStoredConversation_AwaitingClientPhase_ShouldRestoreExecutionState()
    {
        // Arrange: a stored conversation with AwaitingClient phase (genuinely interrupted)
        var stored = new StoredConversation
        {
            ConversationKey = "test-key",
            UserId = "user1",
            IsPublic = false,
            Timestamp = DateTime.UtcNow,
            Messages =
            [
                StoredMessage.FromTrackedMessage(TrackedMessage.CreateInitialContext("System prompt")),
                StoredMessage.FromTrackedMessage(TrackedMessage.CreateUserMessage("Do something")),
            ],
            ExecutionState = new ExecutionState
            {
                Phase = ExecutionPhase.AwaitingClient,
                ExecutedSceneOrder = ["scene1"],
                ExecutedTools = ["scene1.tool1.args"],
                ExecutedScenes = new Dictionary<string, List<SceneRequestContext>>
                {
                    ["scene1"] = [new SceneRequestContext { ToolName = "tool1", Arguments = "args" }]
                },
                SceneResults = new Dictionary<string, string> { ["scene1"] = "partial" },
                AccumulatedCost = 0.005m
            }
        };

        var context = new SceneContext
        {
            ServiceProvider = null!,
            Input = MultiModalInput.FromText("Client response"),
            ChatClientManager = null!
        };

        // Act
        context.LoadFromStoredConversation(stored);

        // Assert: messages loaded
        Assert.Equal(2 + 1, context.ConversationHistory.Count); // 2 messages + ExecutionCheckpoint

        // Assert: execution state IS restored (AwaitingClient = interrupted)
        Assert.True(context.IsResuming);
        Assert.NotNull(context.RestoredExecutionState);
        Assert.Equal(ExecutionPhase.AwaitingClient, context.RestoredExecutionState!.Phase);
        Assert.Contains("scene1", context.ExecutedSceneOrder);
        Assert.Contains("scene1.tool1.args", context.ExecutedTools);
        Assert.Equal(0.005m, context.TotalCost);

        // Assert: ExecutionCheckpoint IS in history
        Assert.Contains(context.ConversationHistory, m => m.Label == "ExecutionCheckpoint");
    }

    [Fact]
    public void LoadFromStoredConversation_ExecutingScenePhase_ShouldRestoreExecutionState()
    {
        // Arrange
        var stored = new StoredConversation
        {
            ConversationKey = "key",
            UserId = null,
            IsPublic = true,
            Timestamp = DateTime.UtcNow,
            Messages =
            [
                StoredMessage.FromTrackedMessage(TrackedMessage.CreateInitialContext("ctx")),
                StoredMessage.FromTrackedMessage(TrackedMessage.CreateUserMessage("q")),
            ],
            ExecutionState = new ExecutionState
            {
                Phase = ExecutionPhase.ExecutingScene,
                CurrentSceneName = "active-scene",
                ExecutedSceneOrder = [],
                ExecutedTools = []
            }
        };

        var context = new SceneContext
        {
            ServiceProvider = null!,
            Input = MultiModalInput.FromText("continue"),
            ChatClientManager = null!
        };

        // Act
        context.LoadFromStoredConversation(stored);

        // Assert: IS resuming for ExecutingScene
        Assert.True(context.IsResuming);
        Assert.NotNull(context.RestoredExecutionState);
        Assert.Equal(ExecutionPhase.ExecutingScene, context.RestoredExecutionState!.Phase);
    }

    [Fact]
    public void LoadFromStoredConversation_ChainingPhase_ShouldRestoreExecutionState()
    {
        // Arrange
        var stored = new StoredConversation
        {
            ConversationKey = "key",
            UserId = null,
            IsPublic = true,
            Timestamp = DateTime.UtcNow,
            Messages =
            [
                StoredMessage.FromTrackedMessage(TrackedMessage.CreateInitialContext("ctx")),
            ],
            ExecutionState = new ExecutionState
            {
                Phase = ExecutionPhase.Chaining,
                ExecutedSceneOrder = ["s1"],
                ExecutedTools = ["s1.t1.null"]
            }
        };

        var context = new SceneContext
        {
            ServiceProvider = null!,
            Input = MultiModalInput.FromText("next"),
            ChatClientManager = null!
        };

        // Act
        context.LoadFromStoredConversation(stored);

        // Assert
        Assert.True(context.IsResuming);
        Assert.Equal(ExecutionPhase.Chaining, context.RestoredExecutionState!.Phase);
    }

    [Theory]
    [InlineData(ExecutionPhase.Completed)]
    [InlineData(ExecutionPhase.CompletedNoResponse)]
    [InlineData(ExecutionPhase.BudgetExceeded)]
    [InlineData(ExecutionPhase.SceneNotFound)]
    [InlineData(ExecutionPhase.TooManyToolRequests)]
    [InlineData(ExecutionPhase.Break)]
    [InlineData(ExecutionPhase.NotStarted)]
    [InlineData(ExecutionPhase.Initialized)]
    [InlineData(ExecutionPhase.SceneSelected)]
    [InlineData(ExecutionPhase.SceneCompleted)]
    public void LoadFromStoredConversation_TerminalOrNonInterruptedPhase_ShouldNotRestore(ExecutionPhase phase)
    {
        // Arrange
        var stored = new StoredConversation
        {
            ConversationKey = "key",
            UserId = null,
            IsPublic = true,
            Timestamp = DateTime.UtcNow,
            Messages =
            [
                StoredMessage.FromTrackedMessage(TrackedMessage.CreateUserMessage("msg")),
            ],
            ExecutionState = new ExecutionState
            {
                Phase = phase,
                ExecutedSceneOrder = ["old-scene"],
                ExecutedTools = ["old-tool"]
            }
        };

        var context = new SceneContext
        {
            ServiceProvider = null!,
            Input = MultiModalInput.FromText("new msg"),
            ChatClientManager = null!
        };

        // Act
        context.LoadFromStoredConversation(stored);

        // Assert: NOT resuming for terminal/non-interrupted phases
        Assert.False(context.IsResuming);
        Assert.Null(context.RestoredExecutionState);
        Assert.Empty(context.ExecutedSceneOrder);
        Assert.Empty(context.ExecutedTools);
        Assert.DoesNotContain(context.ConversationHistory, m => m.Label == "ExecutionCheckpoint");
    }

    [Fact]
    public void LoadFromStoredConversation_CompletedPhase_PreservesAllMessageHistory()
    {
        // Arrange: multi-turn conversation saved after completion
        var stored = new StoredConversation
        {
            ConversationKey = "multi-turn",
            UserId = "u1",
            IsPublic = false,
            Timestamp = DateTime.UtcNow,
            Messages =
            [
                StoredMessage.FromTrackedMessage(TrackedMessage.CreateInitialContext("System prompt")),
                StoredMessage.FromTrackedMessage(TrackedMessage.CreateUserMessage("Turn 1 question")),
                StoredMessage.FromTrackedMessage(TrackedMessage.CreateAssistantMessage("Turn 1 answer")),
                StoredMessage.FromTrackedMessage(TrackedMessage.CreateUserMessage("Turn 2 question")),
                StoredMessage.FromTrackedMessage(TrackedMessage.CreateAssistantMessage("Turn 2 answer")),
            ],
            ExecutionState = new ExecutionState { Phase = ExecutionPhase.Completed }
        };

        var context = new SceneContext
        {
            ServiceProvider = null!,
            Input = MultiModalInput.FromText("Turn 3 question"),
            ChatClientManager = null!
        };

        // Act
        context.LoadFromStoredConversation(stored);

        // Assert: ALL 5 messages from cache are preserved
        Assert.Equal(5, context.ConversationHistory.Count);

        // After adding new user message, history should have 6
        context.AddUserMessage(context.Input);
        Assert.Equal(6, context.ConversationHistory.Count);

        // Verify messages are in correct order
        Assert.Equal("InitialContext", context.ConversationHistory[0].Label);
        Assert.Equal("Turn 1 question", context.ConversationHistory[1].Message.Text);
        Assert.Equal("Turn 1 answer", context.ConversationHistory[2].Message.Text);
        Assert.Equal("Turn 2 question", context.ConversationHistory[3].Message.Text);
        Assert.Equal("Turn 2 answer", context.ConversationHistory[4].Message.Text);
        Assert.Equal("Turn 3 question", context.ConversationHistory[5].Message.Text);
    }

    [Fact]
    public void LoadFromStoredConversation_CompletedPhase_GetMessagesForLLM_IncludesFullHistory()
    {
        // Arrange
        var stored = new StoredConversation
        {
            ConversationKey = "llm-test",
            UserId = null,
            IsPublic = true,
            Timestamp = DateTime.UtcNow,
            Messages =
            [
                StoredMessage.FromTrackedMessage(TrackedMessage.CreateInitialContext("You are a helpful assistant")),
                StoredMessage.FromTrackedMessage(TrackedMessage.CreateUserMessage("What is 2+2?")),
                StoredMessage.FromTrackedMessage(TrackedMessage.CreateAssistantMessage("4")),
            ],
            ExecutionState = new ExecutionState { Phase = ExecutionPhase.Completed }
        };

        var context = new SceneContext
        {
            ServiceProvider = null!,
            Input = MultiModalInput.FromText("And 3+3?"),
            ChatClientManager = null!
        };

        // Act
        context.LoadFromStoredConversation(stored);
        context.AddUserMessage(context.Input);

        var messagesForLlm = context.GetMessagesForLLM();

        // Assert: LLM should receive full conversation including previous turns
        Assert.Equal(4, messagesForLlm.Count);
        Assert.Equal(ChatRole.System, messagesForLlm[0].Role);
        Assert.Equal(ChatRole.User, messagesForLlm[1].Role);
        Assert.Equal(ChatRole.Assistant, messagesForLlm[2].Role);
        Assert.Equal(ChatRole.User, messagesForLlm[3].Role);
        Assert.Equal("And 3+3?", messagesForLlm[3].Text);

        // Critical: NO ExecutionCheckpoint system message interfering
        Assert.DoesNotContain(messagesForLlm, m =>
            m.Role == ChatRole.System && m.Text != null && m.Text.Contains("Execution Checkpoint"));
    }

    #endregion

    #region Integration Tests (full pipeline with cache)

    [Fact]
    public async Task SecondTurn_WithCache_ShouldRetainConversationHistory()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.AddConsole();
            logging.SetMinimumLevel(LogLevel.Debug);
        });

        // Use memory cache (simulates distributed cache behavior)
        services.AddDistributedMemoryCache();
        services.AddChatClient<MockChatClient>(name: null);

        services.AddPlayFramework(builder =>
        {
            builder.AddScene("TestScene", "A test scene that echoes", _ => { });

            builder.AddCache(cacheBuilder =>
            {
                cacheBuilder
                    .WithDistributed()
                    .WithExpiration(TimeSpan.FromMinutes(5));
            });
        });

        var serviceProvider = services.BuildServiceProvider();
        var sceneManager = serviceProvider.GetRequiredService<ISceneManager>();

        var conversationKey = Guid.NewGuid().ToString();
        var settings = new SceneRequestSettings
        {
            ExecutionMode = SceneExecutionMode.Direct,
            ConversationKey = conversationKey
        };

        // Act - First turn
        var responses1 = new List<AiSceneResponse>();
        await foreach (var response in sceneManager.ExecuteAsync("First message", metadata: null, settings))
        {
            responses1.Add(response);
        }

        // Act - Second turn (same conversation key)
        var responses2 = new List<AiSceneResponse>();
        await foreach (var response in sceneManager.ExecuteAsync("Second message", metadata: null, settings))
        {
            responses2.Add(response);
        }

        // Assert: both turns should complete
        Assert.Contains(responses1, r => r.Status == AiResponseStatus.Completed);
        Assert.Contains(responses2, r => r.Status == AiResponseStatus.Completed);

        // Assert: second turn should have a cache-loading status
        Assert.Contains(responses2, r => r.Status == AiResponseStatus.LoadingCache);
    }

    [Fact]
    public async Task ThirdTurn_WithCache_ShouldHaveAllPreviousMessages()
    {
        // Arrange - use a chat client that captures messages
        var capturedMessages = new List<string>();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug));
        services.AddDistributedMemoryCache();

        // Register a capturing mock
        services.AddChatClient<CapturingMockChatClient>(name: null);
        services.AddSingleton(capturedMessages);

        services.AddPlayFramework(builder =>
        {
            builder.AddScene("TestScene", "A test scene", _ => { });
            builder.AddCache(cacheBuilder =>
            {
                cacheBuilder.WithDistributed().WithExpiration(TimeSpan.FromMinutes(5));
            });
        });

        var serviceProvider = services.BuildServiceProvider();
        var sceneManager = serviceProvider.GetRequiredService<ISceneManager>();

        var conversationKey = Guid.NewGuid().ToString();
        var settings = new SceneRequestSettings
        {
            ExecutionMode = SceneExecutionMode.Direct,
            ConversationKey = conversationKey
        };

        // Act - Three turns
        await foreach (var _ in sceneManager.ExecuteAsync("Message 1", metadata: null, settings)) { }
        capturedMessages.Clear();

        await foreach (var _ in sceneManager.ExecuteAsync("Message 2", metadata: null, settings)) { }
        capturedMessages.Clear();

        await foreach (var _ in sceneManager.ExecuteAsync("Message 3", metadata: null, settings)) { }

        // Assert: On the third turn, LLM should receive messages from all previous turns
        // The captured messages on turn 3 should contain references to previous messages
        Assert.Contains(capturedMessages, m => m.Contains("Message 1"));
        Assert.Contains(capturedMessages, m => m.Contains("Message 2"));
        Assert.Contains(capturedMessages, m => m.Contains("Message 3"));

        // Should NOT contain ExecutionCheckpoint messages
        Assert.DoesNotContain(capturedMessages, m => m.Contains("Execution Checkpoint"));
        Assert.DoesNotContain(capturedMessages, m => m.Contains("Do not repeat already completed actions"));
    }

    #endregion
}

/// <summary>
/// Mock chat client that captures all message texts sent to it for assertions.
/// </summary>
internal sealed class CapturingMockChatClient : IChatClient
{
    private readonly List<string> _capturedMessages;

    public CapturingMockChatClient(List<string> capturedMessages)
    {
        _capturedMessages = capturedMessages;
    }

    public ChatClientMetadata Metadata => new("capturing-mock", new Uri("http://localhost"), "mock-model");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var msg in messages)
        {
            if (msg.Text != null)
                _capturedMessages.Add(msg.Text);
        }

        var response = new ChatResponse(
            [new ChatMessage(ChatRole.Assistant, "Mock response")]
        )
        {
            ModelId = "mock-model",
            Usage = new UsageDetails { InputTokenCount = 50, OutputTokenCount = 50 }
        };

        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return GetStreamingCore(messages, cancellationToken);
    }

    private async IAsyncEnumerable<ChatResponseUpdate> GetStreamingCore(
        IEnumerable<ChatMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var msg in messages)
        {
            if (msg.Text != null)
                _capturedMessages.Add(msg.Text);
        }
        await Task.Delay(1, ct);
        yield return new ChatResponseUpdate(ChatRole.Assistant, "Mock streaming response");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
