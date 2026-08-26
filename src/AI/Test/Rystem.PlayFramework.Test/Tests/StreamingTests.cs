using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Rystem.PlayFramework.Services;
using Rystem.PlayFramework.Services.Helpers;

namespace Rystem.PlayFramework.Test.Tests;

/// <summary>
/// Tests for streaming support in PlayFramework.
/// </summary>
public class StreamingTests : PlayFrameworkTestBase
{
    /// <summary>
    /// Tests that streaming is enabled when EnableStreaming is true.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithStreaming_ReturnsProgressiveChunks()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPlayFramework(builder =>
        {
            builder
                .AddScene("Calculator", "Math operations", sceneBuilder =>
                {
                    sceneBuilder
                        .WithService<ICalculatorService>(serviceBuilder =>
                        {
                            serviceBuilder
                                .WithMethod(x => x.AddAsync(default, default), "add", "Add numbers");
                        });
                });
        });

        services.AddSingleton<ICalculatorService, CalculatorService>();
        services.AddSingleton<IChatClient>(sp => new MockStreamingChatClient());

        var serviceProvider = services.BuildServiceProvider();
        var sceneManager = serviceProvider.GetRequiredService<ISceneManager>();

        var settings = new SceneRequestSettings
        {
            ExecutionMode = SceneExecutionMode.Direct,
            EnableStreaming = true // Enable streaming
        };

        // Act
        var responses = new List<AiSceneResponse>();
        await foreach (var response in sceneManager.ExecuteAsync("Calculate 10 + 5", metadata: null, settings))
        {
            responses.Add(response);
        }

        // Assert
        var streamingResponses = responses.Where(r => r.Status == AiResponseStatus.Streaming).ToList();
        Assert.NotEmpty(streamingResponses); // Should have streaming chunks

        // Check that Message accumulates
        string? previousMessage = null;
        foreach (var streamResponse in streamingResponses)
        {
            if (previousMessage != null)
            {
                // Each message should be longer than the previous (accumulating)
                Assert.True(streamResponse.Message?.Length >= previousMessage.Length,
                    $"Message should accumulate. Previous: '{previousMessage}', Current: '{streamResponse.Message}'");
            }
            previousMessage = streamResponse.Message;

            // Each chunk should have StreamingChunk populated
            Assert.NotNull(streamResponse.StreamingChunk);
        }

        // Final response should have IsStreamingComplete = true
        var finalStreamResponse = responses.LastOrDefault(r => r.IsStreamingComplete);
        Assert.NotNull(finalStreamResponse);
        Assert.Equal(AiResponseStatus.FinalResponse, finalStreamResponse!.Status);
    }

    /// <summary>
    /// Tests that non-streaming mode works as before (no streaming chunks).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithoutStreaming_ReturnsCompleteResponse()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPlayFramework(builder =>
        {
            builder
                .AddScene("Calculator", "Math", sceneBuilder =>
                {
                    sceneBuilder
                        .WithService<ICalculatorService>(serviceBuilder =>
                        {
                            serviceBuilder
                                .WithMethod(x => x.AddAsync(default, default), "add", "Add");
                        });
                });
        });

        services.AddSingleton<ICalculatorService, CalculatorService>();
        services.AddSingleton<IChatClient>(sp => new MockStreamingChatClient());

        var serviceProvider = services.BuildServiceProvider();
        var sceneManager = serviceProvider.GetRequiredService<ISceneManager>();

        var settings = new SceneRequestSettings
        {
            ExecutionMode = SceneExecutionMode.DynamicChaining,
            EnableStreaming = false, // Disable streaming
            MaxDynamicScenes = 1 // Only execute one scene
        };

        // Act
        var responses = new List<AiSceneResponse>();
        await foreach (var response in sceneManager.ExecuteAsync("Calculate 5 + 3", metadata: null, settings))
        {
            responses.Add(response);
        }

        // Assert
        var streamingResponses = responses.Where(r => r.Status == AiResponseStatus.Streaming).ToList();
        Assert.Empty(streamingResponses); // Should NOT have streaming chunks

        // Should have complete response
        var runningResponses = responses.Where(r => r.Status == AiResponseStatus.Running).ToList();
        Assert.NotEmpty(runningResponses);
    }

    /// <summary>
    /// Tests streaming with multiple words/chunks.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithStreaming_AccumulatesMessageCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPlayFramework(builder =>
        {
            builder
                .AddScene("Calculator", "Math", sceneBuilder =>
                {
                    sceneBuilder
                        .WithService<ICalculatorService>(serviceBuilder =>
                        {
                            serviceBuilder
                                .WithMethod(x => x.AddAsync(default, default), "add", "Add");
                        });
                });
        });

        services.AddSingleton<ICalculatorService, CalculatorService>();
        services.AddSingleton<IChatClient>(sp => new MockStreamingChatClient("The result is 15")); // Multi-word response

        var serviceProvider = services.BuildServiceProvider();
        var sceneManager = serviceProvider.GetRequiredService<ISceneManager>();

        var settings = new SceneRequestSettings
        {
            ExecutionMode = SceneExecutionMode.DynamicChaining,
            EnableStreaming = true,
            MaxDynamicScenes = 1 // Only execute one scene
        };

        // Act
        var responses = new List<AiSceneResponse>();
        await foreach (var response in sceneManager.ExecuteAsync("Add numbers", metadata: null, settings))
        {
            responses.Add(response);
        }

        // Assert
        var streamingResponses = responses
            .Where(r => r.Status == AiResponseStatus.Streaming || r.IsStreamingComplete)
            .ToList();

        // Should have 4 chunks: "The", "result", "is", "15"
        Assert.True(streamingResponses.Count >= 4, $"Expected at least 4 chunks, got {streamingResponses.Count}");

        // Final message should be complete
        var lastMessage = streamingResponses.Last().Message;
        Assert.Equal("The result is 15", lastMessage);
    }

    /// <summary>
    /// Tests that streaming respects budget limits.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithStreamingAndBudget_StopsWhenExceeded()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPlayFramework(builder =>
        {
            builder
                .AddScene("Calculator", "Math", sceneBuilder =>
                {
                    sceneBuilder
                        .WithService<ICalculatorService>(serviceBuilder =>
                        {
                            serviceBuilder
                                .WithMethod(x => x.AddAsync(default, default), "add", "Add");
                        });
                });
        });

        services.AddSingleton<ICalculatorService, CalculatorService>();
        services.AddSingleton<IChatClient>(sp => new MockCostTrackingChatClient(500, 500, "USD", 0.1m, 0.2m)); // Costs $0.15 per call

        var serviceProvider = services.BuildServiceProvider();
        var sceneManager = serviceProvider.GetRequiredService<ISceneManager>();

        var settings = new SceneRequestSettings
        {
            ExecutionMode = SceneExecutionMode.DynamicChaining,
            EnableStreaming = true,
            MaxBudget = 0.20m, // Low budget
            MaxDynamicScenes = 1 // Only execute one scene
        };

        // Act
        var responses = new List<AiSceneResponse>();
        await foreach (var response in sceneManager.ExecuteAsync("Add numbers", metadata: null, settings))
        {
            responses.Add(response);
        }

        // Assert
        // May hit budget exceeded during execution
        var hasBudgetExceeded = responses.Any(r => r.Status == AiResponseStatus.BudgetExceeded);
        
        // If budget exceeded, should have some streaming responses before it
        if (hasBudgetExceeded)
        {
            var indexOfBudgetExceeded = responses.FindIndex(r => r.Status == AiResponseStatus.BudgetExceeded);
            Assert.True(indexOfBudgetExceeded > 0, "Should have responses before budget exceeded");
        }
    }

    /// <summary>
    /// Tests optimistic streaming in Direct mode (no function calls).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithOptimisticStreaming_DirectMode_StreamsNatively()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPlayFramework(builder =>
        {
            builder
                .AddScene("Storyteller", "Tell stories", sceneBuilder =>
                {
                    // No tools - pure text response
                });
        });

        services.AddSingleton<IChatClient>(sp => new MockStreamingChatClient("Once upon a time there was a robot"));

        var serviceProvider = services.BuildServiceProvider();
        var sceneManager = serviceProvider.GetRequiredService<ISceneManager>();

        var settings = new SceneRequestSettings
        {
            ExecutionMode = SceneExecutionMode.Direct, // Direct mode - uses optimistic streaming
            EnableStreaming = true
        };

        // Act
        var responses = new List<AiSceneResponse>();
        await foreach (var response in sceneManager.ExecuteAsync("Tell me a story", null, settings))
        {
            responses.Add(response);
        }

        // Assert
        var streamingResponses = responses.Where(r => r.Status == AiResponseStatus.Streaming).ToList();
        Assert.NotEmpty(streamingResponses); // Should have streaming chunks

        // Check that Message accumulates progressively
        string? previousMessage = null;
        foreach (var streamResponse in streamingResponses)
        {
            if (previousMessage != null)
            {
                // Each message should be longer or equal to previous (accumulating)
                Assert.True(streamResponse.Message?.Length >= previousMessage.Length,
                    $"Message should accumulate. Previous: '{previousMessage}', Current: '{streamResponse.Message}'");
            }
            previousMessage = streamResponse.Message;

            // Each chunk should have StreamingChunk populated
            Assert.NotNull(streamResponse.StreamingChunk);
        }

        // Final response should have IsStreamingComplete = true
        var finalStreamResponse = responses.Last(r => r.IsStreamingComplete);
        Assert.NotNull(finalStreamResponse);
    }

    [Fact]
    public async Task ProcessOptimisticStreamAsync_EmptyUpdates_DoesNotStreamThem()
    {
        var helper = CreateStreamingHelper();
        var context = CreateContext(new StubChatClientManager(
            new ChatUpdateWithCost
            {
                Update = new ChatResponseUpdate(ChatRole.Assistant, string.Empty)
            },
            new ChatUpdateWithCost
            {
                Update = new ChatResponseUpdate(ChatRole.Assistant, "Hello")
            },
            new ChatUpdateWithCost
            {
                Update = new ChatResponseUpdate(ChatRole.Assistant, string.Empty)
                {
                    FinishReason = ChatFinishReason.Stop
                },
                IsComplete = true
            }));

        var results = new List<StreamingResult>();
        await foreach (var result in helper.ProcessOptimisticStreamAsync(
            context,
            [],
            new ChatOptions(),
            "test-scene",
            CancellationToken.None))
        {
            results.Add(result);
        }

        Assert.Equal(2, results.Count);
        Assert.Equal("Hello", results[0].StreamChunk);
        Assert.True(results[0].StreamedToUser);
        Assert.Equal("Hello", results[1].FinalMessage?.Text);
        Assert.Null(results[1].StreamChunk);
    }

    [Fact]
    public async Task ProcessChunkAsync_EmptyUpdates_OnlyReturnsTerminalUpdate()
    {
        var helper = CreateStreamingHelper();
        var context = CreateContext(new StubChatClientManager());

        var emptyResults = new List<AiSceneResponse>();
        await foreach (var result in helper.ProcessChunkAsync(
            new ChatResponseUpdate(ChatRole.Assistant, string.Empty),
            "test-scene",
            context))
        {
            emptyResults.Add(result);
        }

        var textResults = new List<AiSceneResponse>();
        await foreach (var result in helper.ProcessChunkAsync(
            new ChatResponseUpdate(ChatRole.Assistant, "Hello"),
            "test-scene",
            context))
        {
            textResults.Add(result);
        }

        var terminalResults = new List<AiSceneResponse>();
        await foreach (var result in helper.ProcessChunkAsync(
            new ChatResponseUpdate(ChatRole.Assistant, string.Empty)
            {
                FinishReason = ChatFinishReason.Stop
            },
            "test-scene",
            context))
        {
            terminalResults.Add(result);
        }

        Assert.Empty(emptyResults);
        var textResult = Assert.Single(textResults);
        Assert.Equal("Hello", textResult.StreamingChunk);
        Assert.False(textResult.IsStreamingComplete);
        var terminalResult = Assert.Single(terminalResults);
        Assert.Equal(string.Empty, terminalResult.StreamingChunk);
        Assert.Equal("Hello", terminalResult.Message);
        Assert.True(terminalResult.IsStreamingComplete);
    }

    private static StreamingHelper CreateStreamingHelper()
    {
        var toolExecutionManager = new ToolExecutionManager(
            NullLogger<ToolExecutionManager>.Instance,
            new ClientInteractionHandler(NullLogger<ClientInteractionHandler>.Instance));

        return new StreamingHelper(
            NullLogger<StreamingHelper>.Instance,
            new ResponseHelper(),
            toolExecutionManager);
    }

    private static SceneContext CreateContext(IChatClientManager chatClientManager)
        => new()
        {
            ServiceProvider = new ServiceCollection().BuildServiceProvider(),
            Input = MultiModalInput.FromText("test"),
            ChatClientManager = chatClientManager
        };
}

internal sealed class StubChatClientManager(params ChatUpdateWithCost[] updates) : IChatClientManager
{
    public string? ModelId => "stub-model";
    public string Currency => "USD";

    public Task<ChatResponseWithCost> GetResponseAsync(
        List<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async IAsyncEnumerable<ChatUpdateWithCost> GetStreamingResponseAsync(
        List<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }

        await Task.CompletedTask;
    }
}

/// <summary>
/// Mock chat client that supports streaming responses.
/// </summary>
internal class MockStreamingChatClient : IChatClient
{
    private readonly string _responseText;

    public MockStreamingChatClient(string responseText = "The answer is 15")
    {
        _responseText = responseText;
    }

    public ChatClientMetadata Metadata => new("mock-streaming-client", null, "mock-1.0");

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(10, cancellationToken);

        var responseMessage = new ChatMessage(ChatRole.Assistant, _responseText);

        // Simulate function calls if tools are available
        if (options?.Tools?.Count > 0)
        {
            var tool = options.Tools.First();
            var functionCall = new FunctionCallContent(
                callId: Guid.NewGuid().ToString(),
                name: tool.GetType().GetProperty("Name")?.GetValue(tool)?.ToString() ?? "unknown",
                arguments: new Dictionary<string, object?> { ["a"] = 10, ["b"] = 5 });

            responseMessage.Contents.Add(functionCall);
        }

        return new ChatResponse([responseMessage])
        {
            ModelId = "mock-model"
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Split response into words and stream them
        var words = _responseText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < words.Length; i++)
        {
            await Task.Delay(5, cancellationToken); // Simulate streaming delay

            var isLast = i == words.Length - 1;
            var text = i == 0 ? words[i] : $" {words[i]}";

            yield return new ChatResponseUpdate(ChatRole.Assistant, text)
            {
                ModelId = "mock-model",
                FinishReason = isLast ? ChatFinishReason.Stop : null
            };
        }
    }

    public void Dispose() { }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
}
