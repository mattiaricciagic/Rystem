using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Rystem.PlayFramework.Test.Tests;

public class TokenPropagationTests
{
    [Fact]
    public async Task FinalResponse_And_Completed_ShouldCarryTokenCounters()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPlayFramework(builder =>
        {
            builder
                .WithExecutionMode(SceneExecutionMode.Direct)
                .AddScene("Calculator", "Simple calculator", sceneBuilder =>
                {
                    sceneBuilder.WithService<ITokenMathService>(serviceBuilder =>
                    {
                        serviceBuilder.WithMethod(x => x.AddAsync(default, default), "add", "Add two numbers");
                    });
                });
        });

        services.AddSingleton<ITokenMathService, TokenMathService>();
        services.AddSingleton<IChatClient>(new MockTokenChatClient(
            sceneInputTokens: 100,
            sceneOutputTokens: 20,
            sceneCachedInputTokens: 30));

        var provider = services.BuildServiceProvider();
        var sceneManager = provider.GetRequiredService<ISceneManager>();

        var responses = new List<AiSceneResponse>();
        await foreach (var response in sceneManager.ExecuteAsync(
            message: "quanto fa 2 + 2?",
            settings: new SceneRequestSettings
            {
                ExecutionMode = SceneExecutionMode.Direct,
                EnableStreaming = false
            }))
        {
            responses.Add(response);
        }

        var finalResponse = responses.Last(r => r.Status == AiResponseStatus.FinalResponse);
        var completed = responses.Last(r => r.Status == AiResponseStatus.Completed);

        Assert.Equal(100, finalResponse.InputTokens);
        Assert.Equal(20, finalResponse.OutputTokens);
        Assert.Equal(30, finalResponse.CachedInputTokens);
        Assert.Equal(150, finalResponse.TotalTokens);

        Assert.Equal(finalResponse.InputTokens, completed.InputTokens);
        Assert.Equal(finalResponse.OutputTokens, completed.OutputTokens);
        Assert.Equal(finalResponse.CachedInputTokens, completed.CachedInputTokens);
        Assert.Equal(finalResponse.TotalTokens, completed.TotalTokens);
        Assert.Equal("mock-model", finalResponse.ModelName);
        Assert.Equal("mock-model", completed.ModelName);
    }

    [Fact]
    public void ComputeTotalTokens_WhenOnlyCachedTokensAreProvided_ShouldReturnCachedValue()
    {
        var responseHelperType = typeof(AiSceneResponse).Assembly
            .GetType("Rystem.PlayFramework.Services.Helpers.ResponseHelper");
        Assert.NotNull(responseHelperType);

        var computeTotalTokensMethod = responseHelperType!.GetMethod(
            "ComputeTotalTokens",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(computeTotalTokensMethod);

        var result = computeTotalTokensMethod!.Invoke(null, [null, null, 42]);

        Assert.Equal(42, Assert.IsType<int>(result));
    }

    [Fact]
    public async Task DirectMode_TextOnlyPath_ShouldCarryTokensOnCompleted()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPlayFramework(builder =>
        {
            builder.WithExecutionMode(SceneExecutionMode.Direct)
                .AddScene("Calculator", "Simple calculator", sceneBuilder =>
                {
                    sceneBuilder.WithService<ITokenMathService>(serviceBuilder =>
                    {
                        serviceBuilder.WithMethod(x => x.AddAsync(default, default), "add", "Add two numbers");
                    });
                });
        });

        services.AddSingleton<ITokenMathService, TokenMathService>();
        services.AddSingleton<IChatClient>(new MockDirectTextOnlyChatClient(
            inputTokens: 77,
            outputTokens: 11,
            cachedInputTokens: 5));

        var provider = services.BuildServiceProvider();
        var sceneManager = provider.GetRequiredService<ISceneManager>();

        var responses = new List<AiSceneResponse>();
        await foreach (var response in sceneManager.ExecuteAsync(
            message: "hello",
            settings: new SceneRequestSettings
            {
                ExecutionMode = SceneExecutionMode.Direct,
                EnableStreaming = false
            }))
        {
            responses.Add(response);
        }

        var completed = responses.Last(r => r.Status == AiResponseStatus.Completed);

        Assert.Equal(77, completed.InputTokens);
        Assert.Equal(11, completed.OutputTokens);
        Assert.Equal(5, completed.CachedInputTokens);
        Assert.Equal(93, completed.TotalTokens);
        Assert.Equal("mock-model", completed.ModelName);

        var finalResponse = responses.Last(r => r.Status == AiResponseStatus.FinalResponse);
        Assert.Equal("mock-model", finalResponse.ModelName);
    }

    [Fact]
    public async Task SceneMode_TextOnlyPath_ShouldCarryModelNameOnFinalAndCompleted()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPlayFramework(builder =>
        {
            builder.WithExecutionMode(SceneExecutionMode.Scene)
                .AddScene("Calculator", "Simple calculator", sceneBuilder =>
                {
                    sceneBuilder.WithService<ITokenMathService>(serviceBuilder =>
                    {
                        serviceBuilder.WithMethod(x => x.AddAsync(default, default), "add", "Add two numbers");
                    });
                });
        });

        services.AddSingleton<ITokenMathService, TokenMathService>();
        services.AddSingleton<IChatClient>(new MockDirectTextOnlyChatClient(
            inputTokens: 13,
            outputTokens: 7,
            cachedInputTokens: 2,
            modelId: "model-scene-mode"));

        var provider = services.BuildServiceProvider();
        var sceneManager = provider.GetRequiredService<ISceneManager>();

        var responses = new List<AiSceneResponse>();
        await foreach (var response in sceneManager.ExecuteAsync(
            message: "hello",
            settings: new SceneRequestSettings
            {
                ExecutionMode = SceneExecutionMode.Scene,
                SceneName = "Calculator",
                EnableStreaming = false
            }))
        {
            responses.Add(response);
        }

        var finalResponse = responses.Last(r => r.Status == AiResponseStatus.FinalResponse);
        var completed = responses.Last(r => r.Status == AiResponseStatus.Completed);

        Assert.Equal("model-scene-mode", finalResponse.ModelName);
        Assert.Equal("model-scene-mode", completed.ModelName);
    }

    [Fact]
    public async Task Completed_ShouldKeepModelNameNull_WhenProviderDoesNotReturnIt()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddPlayFramework(builder =>
        {
            builder.WithExecutionMode(SceneExecutionMode.Direct)
                .AddScene("Calculator", "Simple calculator", sceneBuilder =>
                {
                    sceneBuilder.WithService<ITokenMathService>(serviceBuilder =>
                    {
                        serviceBuilder.WithMethod(x => x.AddAsync(default, default), "add", "Add two numbers");
                    });
                });
        });

        services.AddSingleton<ITokenMathService, TokenMathService>();
        services.AddSingleton<IChatClient>(new MockDirectTextOnlyChatClient(
            inputTokens: 9,
            outputTokens: 4,
            cachedInputTokens: 1,
            modelId: null));

        var provider = services.BuildServiceProvider();
        var sceneManager = provider.GetRequiredService<ISceneManager>();

        var responses = new List<AiSceneResponse>();
        await foreach (var response in sceneManager.ExecuteAsync(
            message: "hello",
            settings: new SceneRequestSettings
            {
                ExecutionMode = SceneExecutionMode.Direct,
                EnableStreaming = false
            }))
        {
            responses.Add(response);
        }

        var finalResponse = responses.Last(r => r.Status == AiResponseStatus.FinalResponse);
        var completed = responses.Last(r => r.Status == AiResponseStatus.Completed);

        Assert.Null(finalResponse.ModelName);
        Assert.Null(completed.ModelName);
    }

    private interface ITokenMathService
    {
        Task<double> AddAsync(double a, double b);
    }

    private sealed class TokenMathService : ITokenMathService
    {
        public Task<double> AddAsync(double a, double b) => Task.FromResult(a + b);
    }

    private sealed class MockTokenChatClient : IChatClient
    {
        private readonly int _sceneInputTokens;
        private readonly int _sceneOutputTokens;
        private readonly int _sceneCachedInputTokens;
        private int _callCount;

        public MockTokenChatClient(int sceneInputTokens, int sceneOutputTokens, int sceneCachedInputTokens)
        {
            _sceneInputTokens = sceneInputTokens;
            _sceneOutputTokens = sceneOutputTokens;
            _sceneCachedInputTokens = sceneCachedInputTokens;
        }

        public ChatClientMetadata Metadata => new("mock-token-client", null, "mock-1.0");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _callCount++;

            var responseMessage = _callCount == 1
                ? BuildSceneSelectionMessage(options)
                : new ChatMessage(ChatRole.Assistant, "Risultato: 4");

            var usage = _callCount == 1
                ? new UsageDetails
                {
                    InputTokenCount = 1,
                    OutputTokenCount = 1,
                    CachedInputTokenCount = 0,
                    TotalTokenCount = 2
                }
                : new UsageDetails
                {
                    InputTokenCount = _sceneInputTokens,
                    OutputTokenCount = _sceneOutputTokens,
                    CachedInputTokenCount = _sceneCachedInputTokens,
                    TotalTokenCount = _sceneInputTokens + _sceneOutputTokens + _sceneCachedInputTokens
                };

            var response = new ChatResponse([responseMessage])
            {
                ModelId = "mock-model",
                Usage = usage
            };

            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "unused in this test")
            {
                FinishReason = ChatFinishReason.Stop
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private static ChatMessage BuildSceneSelectionMessage(ChatOptions? options)
        {
            var selectedScene = options?.Tools?.FirstOrDefault();
            var selectedSceneName = selectedScene?.GetType().GetProperty("Name")?.GetValue(selectedScene)?.ToString() ?? "Calculator";

            var functionCall = new FunctionCallContent(
                callId: Guid.NewGuid().ToString(),
                name: selectedSceneName,
                arguments: new Dictionary<string, object?>());

            var message = new ChatMessage(ChatRole.Assistant, string.Empty);
            message.Contents.Add(functionCall);
            return message;
        }
    }

    private sealed class MockDirectTextOnlyChatClient : IChatClient
    {
        private readonly int _inputTokens;
        private readonly int _outputTokens;
        private readonly int _cachedInputTokens;
        private readonly string? _modelId;

        public MockDirectTextOnlyChatClient(int inputTokens, int outputTokens, int cachedInputTokens, string? modelId = "mock-model")
        {
            _inputTokens = inputTokens;
            _outputTokens = outputTokens;
            _cachedInputTokens = cachedInputTokens;
            _modelId = modelId;
        }

        public ChatClientMetadata Metadata => new("mock-direct-text-only-client", null, "mock-1.0");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "Plain text answer")])
            {
                ModelId = _modelId,
                Usage = new UsageDetails
                {
                    InputTokenCount = _inputTokens,
                    OutputTokenCount = _outputTokens,
                    CachedInputTokenCount = _cachedInputTokens,
                    TotalTokenCount = _inputTokens + _outputTokens + _cachedInputTokens
                }
            };

            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "unused in this test")
            {
                FinishReason = ChatFinishReason.Stop
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
