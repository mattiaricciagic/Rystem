using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Rystem.PlayFramework.Configuration;
using Rystem.PlayFramework.Services;
using Rystem.PlayFramework.Services.Helpers;
using System.Text.Json;

namespace Rystem.PlayFramework.Test.Tests;

public sealed class FunctionArgumentsTests
{
    [Fact]
    public async Task SceneTool_StartedAndCompletedExposeTheExecutedJson()
    {
        var tool = new CapturingSceneTool("lookup");
        var arguments = new Dictionary<string, object?>
        {
            ["customer"] = new Dictionary<string, object?>
            {
                ["id"] = 42,
                ["active"] = true
            },
            ["tags"] = new[] { "one", "two" },
            ["optional"] = null
        };

        var results = await ExecuteAsync(
            new FunctionCallContent("call-1", tool.Name, arguments),
            sceneTools: [tool]);

        var started = Assert.Single(results, x => x.Status == ToolExecutionStatus.Started);
        var completed = Assert.Single(results, x => x.Status == ToolExecutionStatus.Completed);

        AssertJsonEqual(tool.ExecutedArguments, started.FunctionArguments);
        AssertJsonEqual(tool.ExecutedArguments, completed.FunctionArguments);
        Assert.Equal(42, JsonDocument.Parse(completed.FunctionArguments!).RootElement
            .GetProperty("customer").GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task SceneTool_WithoutArgumentsExposesEmptyJsonObject()
    {
        var tool = new CapturingSceneTool("no_args");

        var results = await ExecuteAsync(
            new FunctionCallContent("call-2", tool.Name, arguments: null),
            sceneTools: [tool]);

        Assert.All(results, result => Assert.Equal("{}", result.FunctionArguments));
        Assert.Equal("{}", tool.ExecutedArguments);
    }

    [Fact]
    public async Task McpTool_StartedAndCompletedExposeOriginalArguments()
    {
        string? invokedCity = null;
        var mcpTool = AIFunctionFactory.Create(
            (string city) =>
            {
                invokedCity = city;
                return "sunny";
            },
            new AIFunctionFactoryOptions
            {
                Name = "weather",
                Description = "Gets weather"
            });

        var results = await ExecuteAsync(
            new FunctionCallContent(
                "call-3",
                "weather",
                new Dictionary<string, object?> { ["city"] = "Rome" }),
            mcpTools: [mcpTool]);

        Assert.Equal("Rome", invokedCity);
        Assert.All(results, result =>
        {
            Assert.NotNull(result.FunctionArguments);
            using var json = JsonDocument.Parse(result.FunctionArguments);
            Assert.Equal("Rome", json.RootElement.GetProperty("city").GetString());
        });
    }

    [Fact]
    public async Task FailedTool_PreservesArgumentsOnStartedAndError()
    {
        var tool = new CapturingSceneTool("fail", new InvalidOperationException("boom"));

        var results = await ExecuteAsync(
            new FunctionCallContent(
                "call-4",
                tool.Name,
                new Dictionary<string, object?> { ["attempt"] = 3 }),
            sceneTools: [tool]);

        var started = Assert.Single(results, x => x.Status == ToolExecutionStatus.Started);
        var error = Assert.Single(results, x => x.Status == ToolExecutionStatus.Error);
        AssertJsonEqual(started.FunctionArguments, error.FunctionArguments);
    }

    [Fact]
    public async Task ClientTool_ExposesArgumentsAndPersistsThemForResume()
    {
        var call = new FunctionCallContent(
            "call-5",
            "show_confirmation",
            new Dictionary<string, object?> { ["message"] = "Continue?" });
        var definition = new ClientInteractionDefinition
        {
            ToolName = "show_confirmation",
            Description = "Shows a confirmation",
            TimeoutSeconds = 30
        };

        var context = CreateContext();
        var results = await ExecuteAsync(
            call,
            clientInteractions: [definition],
            context: context);

        var pending = Assert.Single(results);
        Assert.Equal(ToolExecutionStatus.AwaitingClient, pending.Status);
        Assert.NotNull(pending.FunctionArguments);
        using (var json = JsonDocument.Parse(pending.FunctionArguments))
        {
            Assert.Equal("Continue?", json.RootElement.GetProperty("message").GetString());
        }

        var batchJson = Assert.IsType<string>(
            context.Properties[ToolExecutionManager.ClientInteractionBatchKey]);
        var batch = JsonSerializer.Deserialize<ClientInteractionBatch>(batchJson);
        Assert.Equal(pending.FunctionArguments, Assert.Single(batch!.Interactions).FunctionArguments);
    }

    [Fact]
    public void AggregateFunctionRequest_HasNoFunctionIdentityOrArguments()
    {
        var context = CreateContext();
        var helper = new ResponseHelper();

        var response = helper.CreateAndTrackResponse(
            context,
            AiResponseStatus.FunctionRequest,
            message: "LLM returned 2 function call(s)");

        Assert.Null(response.FunctionName);
        Assert.Null(response.FunctionArguments);
    }

    private static async Task<List<ToolExecutionResult>> ExecuteAsync(
        FunctionCallContent call,
        List<ISceneTool>? sceneTools = null,
        List<AIFunction>? mcpTools = null,
        IReadOnlyList<ClientInteractionDefinition>? clientInteractions = null,
        SceneContext? context = null)
    {
        var manager = new ToolExecutionManager(
            NullLogger<ToolExecutionManager>.Instance,
            new ClientInteractionHandler(NullLogger<ClientInteractionHandler>.Instance));
        var results = new List<ToolExecutionResult>();

        await foreach (var result in manager.ExecuteToolCallsAsync(
            context ?? CreateContext(),
            [call],
            sceneTools ?? [],
            mcpTools ?? [],
            clientInteractions,
            "test-scene"))
        {
            results.Add(result);
        }

        return results;
    }

    private static SceneContext CreateContext()
    {
        return new SceneContext
        {
            ServiceProvider = new ServiceCollection().BuildServiceProvider(),
            Input = MultiModalInput.FromText("test"),
            ChatClientManager = null!
        };
    }

    private static void AssertJsonEqual(string? expected, string? actual)
    {
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        using var expectedJson = JsonDocument.Parse(expected);
        using var actualJson = JsonDocument.Parse(actual);
        Assert.True(JsonElement.DeepEquals(expectedJson.RootElement, actualJson.RootElement));
    }

    private sealed class CapturingSceneTool : ISceneTool
    {
        private readonly Exception? _exception;

        public CapturingSceneTool(string name, Exception? exception = null)
        {
            Name = name;
            _exception = exception;
            using var schema = JsonDocument.Parse("{}");
            ToolDescription = AIFunctionFactory.CreateDeclaration(
                name,
                "Test tool",
                schema.RootElement.Clone());
        }

        public string Name { get; }
        public string Description => "Test tool";
        public AITool ToolDescription { get; }
        public string? ExecutedArguments { get; private set; }

        public Task<object?> ExecuteAsync(
            string arguments,
            SceneContext context,
            CancellationToken cancellationToken)
        {
            ExecutedArguments = arguments;
            if (_exception is not null)
                throw _exception;

            return Task.FromResult<object?>("ok");
        }
    }
}
