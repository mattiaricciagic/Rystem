using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;

namespace Rystem.PlayFramework;

internal sealed class MaterializedSceneCatalog
{
    private readonly IReadOnlyDictionary<string, IScene> _scenesByName;

    public MaterializedSceneCatalog(
        RuntimeDescriptionCatalogIdentity identity,
        IReadOnlyList<IScene> scenes,
        IReadOnlyDictionary<string, string> descriptions)
    {
        Identity = identity;
        Scenes = scenes;
        Descriptions = descriptions;
        SceneDeclarations = new ReadOnlyCollection<AITool>(scenes.Select(x => x.AiTool).ToList());
        _scenesByName = new ReadOnlyDictionary<string, IScene>(
            scenes.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase));
    }

    public RuntimeDescriptionCatalogIdentity Identity { get; }
    public IReadOnlyList<IScene> Scenes { get; }
    public IReadOnlyList<AITool> SceneDeclarations { get; }
    public IReadOnlyDictionary<string, string> Descriptions { get; }

    public IScene? TryGetScene(string name)
        => _scenesByName.GetValueOrDefault(name);

    public RuntimeSceneCatalogView CreateView(RuntimeDescriptionExecutionInfo executionInfo)
    {
        var scenes = Scenes.Select(scene => new RuntimeSceneDescriptor
        {
            Name = scene.Name,
            Description = scene.Description,
            RoutingDeclaration = (AIFunctionDeclaration)scene.AiTool,
            Tools = new ReadOnlyCollection<RuntimeToolDescriptor>(scene.Tools.Select(tool => new RuntimeToolDescriptor
            {
                Name = tool.Name,
                Description = tool.Description,
                Declaration = (AIFunctionDeclaration)tool.ToolDescription
            }).ToList())
        }).ToList();

        return new RuntimeSceneCatalogView
        {
            ExecutionInfo = executionInfo,
            Scenes = new ReadOnlyCollection<RuntimeSceneDescriptor>(scenes)
        };
    }
}

internal sealed record PublishedRuntimeDescriptionState(
    MaterializedSceneCatalog Catalog,
    string? SourceVersion,
    bool HasUniformSourceVersion,
    Guid LastValidationOperationId,
    DateTimeOffset LastValidatedAt,
    RuntimeDescriptionRecoverySource RecoverySource,
    bool UsedFallback);

internal sealed record RuntimeDescriptionAcquisition(
    MaterializedSceneCatalog Catalog,
    RuntimeDescriptionExecutionInfo ExecutionInfo,
    RuntimeSceneCatalogView PublicView);
