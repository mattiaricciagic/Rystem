using System.Reflection;
using System.Text.Json;
using Rystem.PlayFramework.Helpers;

namespace Rystem.PlayFramework.Test.Tests;

/// <summary>
/// Tests for two independent Rystem defects:
/// #2 EndpointHttpTool.BuildUrl crashed on non-string JSON arguments because
///    GetString() throws (the "?? GetRawText()" fallback was never reached).
/// #8 Scene routing function name was not normalized, unlike every other tool,
///    breaking OpenAI's ^[a-zA-Z0-9_.-]{1,64}$ constraint for scenes with
///    spaces/commas in their name.
/// </summary>
public sealed class EndpointAndSceneNamingTests
{
    // ---------------------------------------------------------------------
    // #2 — BuildUrl must not throw on numeric/array arguments
    // ---------------------------------------------------------------------

    [Fact]
    public void BuildUrl_WithNumericRouteAndQueryArguments_DoesNotThrowAndFormatsCorrectly()
    {
        // Arrange: route param + query param, both numeric (the crashing case)
        var config = new EndpointToolConfiguration
        {
            ClientType = typeof(object),
            ToolName = "delete_note",
            Description = "Deletes a note",
            HttpMethod = HttpMethod.Delete,
            RouteTemplate = "/notes/{id}",
            ResponseType = typeof(object),
            QueryParameters =
            [
                new EndpointParameterDefinition { Name = "count", Description = "n", Type = typeof(int) }
            ]
        };
        var tool = new EndpointHttpTool(config);
        var args = Deserialize("""{"id":123,"count":5}""");

        // Act
        var url = InvokeBuildUrl(tool, args);

        // Assert
        Assert.Equal("/notes/123?count=5", url);
    }

    [Fact]
    public void BuildUrl_WithNumericArrayQueryArgument_ExpandsRepeatedParams()
    {
        var config = new EndpointToolConfiguration
        {
            ClientType = typeof(object),
            ToolName = "get_notes",
            Description = "Gets notes",
            HttpMethod = HttpMethod.Get,
            RouteTemplate = "/notes",
            ResponseType = typeof(object),
            QueryParameters =
            [
                new EndpointParameterDefinition { Name = "ids", Description = "ids", Type = typeof(int[]) }
            ]
        };
        var tool = new EndpointHttpTool(config);
        var args = Deserialize("""{"ids":[1,2,3]}""");

        var url = InvokeBuildUrl(tool, args);

        Assert.Equal("/notes?ids=1&ids=2&ids=3", url);
    }

    [Fact]
    public void BuildUrl_WithStringArguments_StillWorks()
    {
        var config = new EndpointToolConfiguration
        {
            ClientType = typeof(object),
            ToolName = "get_order",
            Description = "Gets an order",
            HttpMethod = HttpMethod.Get,
            RouteTemplate = "/orders/{orderId}",
            ResponseType = typeof(object),
            QueryParameters =
            [
                new EndpointParameterDefinition { Name = "region", Description = "r", Type = typeof(string) }
            ]
        };
        var tool = new EndpointHttpTool(config);
        var args = Deserialize("""{"orderId":"abc-123","region":"it"}""");

        var url = InvokeBuildUrl(tool, args);

        Assert.Equal("/orders/abc-123?region=it", url);
    }

    private static Dictionary<string, JsonElement> Deserialize(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static string InvokeBuildUrl(EndpointHttpTool tool, Dictionary<string, JsonElement> args)
    {
        var method = typeof(EndpointHttpTool).GetMethod(
            "BuildUrl", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (string)method.Invoke(tool, [args])!;
    }

    // ---------------------------------------------------------------------
    // #8 — Scene routing function name is normalized and still resolvable
    // ---------------------------------------------------------------------

    [Fact]
    public void Scene_RoutingFunctionName_IsNormalized()
    {
        // Arrange
        var scene = new Scene(new SceneConfiguration
        {
            Name = "Ferie, permessi e assenze",
            Description = "Gestione ferie"
        });

        // Assert: the exposed routing function name is OpenAI-compliant
        var functionName = scene.AiFunction.Name;
        Assert.Equal("Ferie_permessi_e_assenze", functionName);
        Assert.DoesNotContain(',', functionName);
        Assert.DoesNotContain(' ', functionName);

        // The scene's own Name is untouched
        Assert.Equal("Ferie, permessi e assenze", scene.Name);
    }

    [Fact]
    public void MaterializedSceneCatalog_ResolvesSceneByNormalizedAndRawName()
    {
        // Arrange
        var scene = new Scene(new SceneConfiguration
        {
            Name = "Ferie, permessi e assenze",
            Description = "Gestione ferie"
        });

        var catalog = new MaterializedSceneCatalog(
            NewIdentity(),
            [scene],
            new Dictionary<string, string>());

        // Assert: resolvable both by the normalized (LLM-emitted) name and the raw name
        Assert.Same(scene, catalog.TryGetScene("Ferie_permessi_e_assenze"));
        Assert.Same(scene, catalog.TryGetScene("Ferie, permessi e assenze"));
        Assert.Null(catalog.TryGetScene("does-not-exist"));
    }

    private static RuntimeDescriptionCatalogIdentity NewIdentity()
        => new()
        {
            CatalogId = "test",
            TemplateHash = "t",
            ContentHash = "c",
            HashAlgorithm = "sha256-v1",
            LoadedAt = DateTimeOffset.UtcNow,
            PublishedAt = DateTimeOffset.UtcNow
        };
}
