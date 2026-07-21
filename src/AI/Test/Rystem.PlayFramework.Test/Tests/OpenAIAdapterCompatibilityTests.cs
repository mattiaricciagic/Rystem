using Microsoft.Extensions.AI;
using OpenAI.Chat;
using System.Text.Json;

namespace Rystem.PlayFramework.Test;

public sealed class OpenAIAdapterCompatibilityTests
{
    private const string ToolSchema = """
        {
          "type": "object",
          "properties": {
            "city": {
              "type": "string",
              "description": "City to search"
            }
          },
          "required": ["city"],
          "additionalProperties": false
        }
        """;

    [Fact]
    public void AzureTestAdapter_PreservesDeclarationMetadataAndSchema()
    {
        var declaration = CreateDeclaration();
        var options = AzureOpenAIChatClientAdapter.CreateChatCompletionOptions(new ChatOptions
        {
            Tools = [declaration]
        });

        var tool = Assert.Single(options.Tools);
        Assert.Equal("search_weather", tool.FunctionName);
        Assert.Equal("Searches the weather for a city", tool.FunctionDescription);
        Assert.NotNull(tool.FunctionParameters);

        using var expectedSchema = JsonDocument.Parse(ToolSchema);
        using var actualSchema = JsonDocument.Parse(tool.FunctionParameters.ToString());
        Assert.True(JsonElement.DeepEquals(expectedSchema.RootElement, actualSchema.RootElement));
    }

    [Fact]
    public void AzureTestAdapter_StillSupportsInvocableFunctions()
    {
        var function = AIFunctionFactory.Create(
            (string city) => city,
            new AIFunctionFactoryOptions
            {
                Name = "search_weather",
                Description = "Searches the weather for a city"
            });

        var options = AzureOpenAIChatClientAdapter.CreateChatCompletionOptions(new ChatOptions
        {
            Tools = [function]
        });

        var tool = Assert.Single(options.Tools);
        Assert.Equal(function.Name, tool.FunctionName);
        Assert.Equal(function.Description, tool.FunctionDescription);
        Assert.NotNull(tool.FunctionParameters);
    }

    [Fact]
    public void OfficialOpenAIAdapter_ConvertsDeclarationForChatCompletions()
    {
        var declaration = CreateDeclaration();

        var tool = declaration.AsOpenAIChatTool();

        Assert.Equal(declaration.Name, tool.FunctionName);
        Assert.Equal(declaration.Description, tool.FunctionDescription);
        Assert.NotNull(tool.FunctionParameters);

        using var expectedSchema = JsonDocument.Parse(ToolSchema);
        using var actualSchema = JsonDocument.Parse(tool.FunctionParameters.ToString());
        Assert.True(JsonElement.DeepEquals(expectedSchema.RootElement, actualSchema.RootElement));
    }

    [Fact]
#pragma warning disable OPENAI001
    public void OfficialOpenAIAdapter_ConvertsDeclarationForResponsesApi()
    {
        var declaration = CreateDeclaration();

        var tool = OpenAI.Responses.MicrosoftExtensionsAIResponsesExtensions
            .AsOpenAIResponseTool(declaration);

        Assert.NotNull(tool);
    }
#pragma warning restore OPENAI001

    private static AIFunctionDeclaration CreateDeclaration()
    {
        using var schema = JsonDocument.Parse(ToolSchema);
        return AIFunctionFactory.CreateDeclaration(
            "search_weather",
            "Searches the weather for a city",
            schema.RootElement.Clone());
    }
}
