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
}
