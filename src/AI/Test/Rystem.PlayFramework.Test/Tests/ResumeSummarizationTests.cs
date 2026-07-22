using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Rystem.PlayFramework.Test.Tests;

/// <summary>
/// Tests for the resume-summarization path, covering the fixes for:
/// #1 AiSceneResponse.SceneName is populated for the summarizer (context/routing preservation),
/// #2 ISummarizer.ShouldSummarize is actually invoked by the runtime,
/// #3 CharacterThreshold is honored (consequence of #2),
/// #4 SceneRequestSettings.EnableSummarization gates summarization per request.
/// </summary>
public sealed class ResumeSummarizationTests
{
    // ---------------------------------------------------------------------
    // #1 — DefaultSummarizer must surface the active scene in the prompt
    // ---------------------------------------------------------------------

    [Fact]
    public async Task DefaultSummarizer_WhenSceneNamePresent_IncludesSceneInPrompt()
    {
        // Arrange
        var captured = new List<string>();
        var chatClient = new CapturingMockChatClient(captured);
        var settings = new PlayFrameworkSettings();
        var summarizer = new DefaultSummarizer(chatClient, settings, NullLogger<DefaultSummarizer>.Instance);

        var responses = new List<AiSceneResponse>
        {
            new() { Message = "Cerco la commessa Zeta", SceneName = "inserisci-o-modifica-ore-timesheet" },
            new() { Message = "Nessun risultato trovato", SceneName = "inserisci-o-modifica-ore-timesheet" }
        };

        // Act
        await summarizer.SummarizeAsync(responses);

        // Assert: the scene is present both as a per-message tag and as an explicit instruction
        Assert.Contains(captured, text => text.Contains("[Scene: inserisci-o-modifica-ore-timesheet]"));
        Assert.Contains(captured, text => text.Contains("'inserisci-o-modifica-ore-timesheet' scene"));
    }

    [Fact]
    public async Task DefaultSummarizer_WhenSceneNameMissing_DoesNotEmitSceneTag()
    {
        // Arrange
        var captured = new List<string>();
        var chatClient = new CapturingMockChatClient(captured);
        var settings = new PlayFrameworkSettings();
        var summarizer = new DefaultSummarizer(chatClient, settings, NullLogger<DefaultSummarizer>.Instance);

        var responses = new List<AiSceneResponse>
        {
            new() { Message = "Un messaggio senza scena" }
        };

        // Act
        await summarizer.SummarizeAsync(responses);

        // Assert
        Assert.DoesNotContain(captured, text => text.Contains("[Scene:"));
    }

    // ---------------------------------------------------------------------
    // #3 — CharacterThreshold alone must be able to trigger summarization
    // ---------------------------------------------------------------------

    [Fact]
    public void ShouldSummarize_CharacterThresholdAlone_TriggersEvenWhenResponseCountIsLow()
    {
        // Arrange: response count can never be reached, but characters exceed the tiny threshold
        var settings = new PlayFrameworkSettings();
        settings.Summarization.Enabled = true;
        settings.Summarization.ResponseCountThreshold = int.MaxValue;
        settings.Summarization.CharacterThreshold = 1;

        var summarizer = new DefaultSummarizer(
            new CapturingMockChatClient([]), settings, NullLogger<DefaultSummarizer>.Instance);

        var responses = new List<AiSceneResponse>
        {
            new() { Message = new string('x', 5_000) }
        };

        // Act + Assert
        Assert.True(summarizer.ShouldSummarize(responses));
    }

    [Fact]
    public void ShouldSummarize_WhenDisabled_ReturnsFalse()
    {
        var settings = new PlayFrameworkSettings();
        settings.Summarization.Enabled = false;
        settings.Summarization.ResponseCountThreshold = 1;
        settings.Summarization.CharacterThreshold = 1;

        var summarizer = new DefaultSummarizer(
            new CapturingMockChatClient([]), settings, NullLogger<DefaultSummarizer>.Instance);

        var responses = new List<AiSceneResponse> { new() { Message = "anything" } };

        Assert.False(summarizer.ShouldSummarize(responses));
    }

    // ---------------------------------------------------------------------
    // #1 + #2 — End-to-end: ShouldSummarize is invoked and SceneName reaches
    //           the summarizer across a second turn.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task SecondTurn_Summarization_InvokesShouldSummarize_AndPropagatesActiveScene()
    {
        // Arrange
        var probe = new SummarizerProbe();
        var services = BuildServices(probe);
        var serviceProvider = services.BuildServiceProvider();
        var sceneManager = serviceProvider.GetRequiredService<ISceneManager>();

        var conversationKey = Guid.NewGuid().ToString();

        SceneRequestSettings Settings() => new()
        {
            ExecutionMode = SceneExecutionMode.Scene,
            SceneName = "inserisci-o-modifica-ore-timesheet",
            ConversationKey = conversationKey
        };

        // Turn 1: forces the scene, whose actor produces a stored "SceneActor:{scene}" message.
        await foreach (var _ in sceneManager.ExecuteAsync("Voglio inserire delle ore", metadata: null, Settings())) { }
        probe.Reset();

        // Act — Turn 2: summarization runs during initialization, before scene execution.
        await foreach (var _ in sceneManager.ExecuteAsync("La commessa si chiama Zeta", metadata: null, Settings())) { }

        // Assert
        Assert.True(probe.ShouldSummarizeCalls > 0, "ShouldSummarize must be invoked by the runtime (fix #2).");
        Assert.True(probe.SummarizeCalls > 0, "SummarizeAsync must run when ShouldSummarize returns true.");
        Assert.Contains(
            probe.LastResponses,
            r => r.SceneName == "inserisci-o-modifica-ore-timesheet");
    }

    // ---------------------------------------------------------------------
    // #4 — EnableSummarization = false must skip summarization for the request
    // ---------------------------------------------------------------------

    [Fact]
    public async Task SecondTurn_WhenEnableSummarizationFalse_SkipsSummarization()
    {
        // Arrange
        var probe = new SummarizerProbe();
        var services = BuildServices(probe);
        var serviceProvider = services.BuildServiceProvider();
        var sceneManager = serviceProvider.GetRequiredService<ISceneManager>();

        var conversationKey = Guid.NewGuid().ToString();

        SceneRequestSettings Settings(bool enableSummarization) => new()
        {
            ExecutionMode = SceneExecutionMode.Scene,
            SceneName = "inserisci-o-modifica-ore-timesheet",
            ConversationKey = conversationKey,
            EnableSummarization = enableSummarization
        };

        await foreach (var _ in sceneManager.ExecuteAsync("Voglio inserire delle ore", metadata: null, Settings(true))) { }
        probe.Reset();

        // Act — summarization disabled for this request
        await foreach (var _ in sceneManager.ExecuteAsync("La commessa si chiama Zeta", metadata: null, Settings(false))) { }

        // Assert
        Assert.Equal(0, probe.ShouldSummarizeCalls);
        Assert.Equal(0, probe.SummarizeCalls);
    }

    private static ServiceCollection BuildServices(SummarizerProbe probe)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddDistributedMemoryCache();
        services.AddChatClient<CapturingMockChatClient>(name: null);
        services.AddSingleton(new List<string>());
        services.AddSingleton(probe);

        services.AddPlayFramework(builder =>
        {
            builder.AddScene(
                "inserisci-o-modifica-ore-timesheet",
                "Insert or modify timesheet hours",
                sceneBuilder => sceneBuilder.WithActors(actorBuilder =>
                    actorBuilder.AddActor("Collect the commessa and the hours before confirming.")));
            builder.AddCustomSummarizer<RecordingSummarizer>();
            builder.AddCache(cacheBuilder =>
                cacheBuilder.WithDistributed().WithExpiration(TimeSpan.FromMinutes(5)));
        });

        return services;
    }
}

/// <summary>
/// Shared recorder for summarizer interactions across turns.
/// </summary>
internal sealed class SummarizerProbe
{
    public int ShouldSummarizeCalls { get; set; }
    public int SummarizeCalls { get; set; }
    public List<AiSceneResponse> LastResponses { get; set; } = [];

    public void Reset()
    {
        ShouldSummarizeCalls = 0;
        SummarizeCalls = 0;
        LastResponses = [];
    }
}

/// <summary>
/// Custom summarizer that records how the runtime interacts with it,
/// while always requesting summarization so the path is exercised deterministically.
/// </summary>
internal sealed class RecordingSummarizer : ISummarizer
{
    private readonly SummarizerProbe _probe;

    public RecordingSummarizer(SummarizerProbe probe)
    {
        _probe = probe;
    }

    public bool ShouldSummarize(List<AiSceneResponse> responses)
    {
        _probe.ShouldSummarizeCalls++;
        _probe.LastResponses = responses;
        return true;
    }

    public Task<string> SummarizeAsync(List<AiSceneResponse> responses, CancellationToken cancellationToken = default)
    {
        _probe.SummarizeCalls++;
        _probe.LastResponses = responses;
        return Task.FromResult("Summary generated for cached conversation.");
    }
}
