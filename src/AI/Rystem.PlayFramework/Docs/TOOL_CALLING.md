# Tool calling in PlayFramework

Tool calling is the mechanism by which PlayFramework exposes C# methods, HTTP endpoints, MCP servers, and client-side functions to the language model. The model selects and invokes tools; PlayFramework executes them and feeds the results back into the conversation.

## Tool types

| Type | Registration | Description |
|---|---|---|
| Service tool | `WithService<T>(tools => ...)` | C# service methods resolved from DI |
| Endpoint tool | `WithEndpoint<TClient>(ep => ...)` | HTTP actions via named `IHttpClientFactory` |
| MCP tool | `WithMcpServer(name)` | External MCP server (tools fetched at runtime) |
| Client tool | `OnClient(client => ...)` | Browser or mobile functions (bidirectional) |

All four types appear in `AiSceneResponse.FunctionName` / `FunctionArguments` and in the discovery endpoint.

## Service tools

Register typed service methods on a scene using `WithService<TService>`:

```csharp
builder.Services.AddSingleton<ICalculatorService, CalculatorService>();

builder.Services.AddPlayFramework("default", framework =>
{
    framework
        .WithChatClient("default")
        .AddScene("Calculator", "Arithmetic operations", scene =>
        {
            scene.WithService<ICalculatorService>(tools =>
            {
                tools
                    .WithMethod<double>(x => x.Add(default, default), "Add", "Add two numbers")
                    .WithMethod<double>(x => x.Multiply(default, default), "Multiply", "Multiply two numbers")
                    .WithMethod<double>(x => x.Subtract(default, default), "Subtract", "Subtract two numbers");
            });
        });
});
```

The expression `x => x.Add(default, default)` extracts the `MethodInfo` from the lambda at startup. `default` arguments are ignored; only the method signature matters. Tool names are normalized (spaces and hyphens become underscores).

## Endpoint tools

Use `WithEndpoint<TClient>` to expose an HTTP service as a set of AI-callable actions. Register the named HTTP client on the PlayFramework builder first:

```csharp
builder.Services.AddPlayFramework("default", framework =>
{
    framework
        .WithChatClient("default")
        .WithHttpClient<IOrderServiceClient>(c =>
        {
            c.BaseAddress = new Uri("http://order-service:5001/api");
        })
        .AddScene("Orders", "Order management", scene =>
        {
            scene.WithEndpoint<IOrderServiceClient>(ep => ep
                .WithAction<Order>(
                    "GetOrder",
                    HttpMethod.Get,
                    "/orders/{orderId}",
                    "Retrieve an order by its ID")
                .WithAction<CreateOrderRequest, Order>(
                    "CreateOrder",
                    HttpMethod.Post,
                    "/orders",
                    "Create a new order")
                .WithAction<PagedResult<Order>>(
                    "ListOrders",
                    HttpMethod.Get,
                    "/orders",
                    "List all orders")
                .WithParameter("status", "Filter by order status"));
        });
});
```

Route template placeholders (`{orderId}`) are automatically exposed as required AI parameters. Optional query parameters are added with `.WithParameter(...)`.

## MCP tools

Connect a scene to a registered MCP server:

```csharp
builder.Services.AddPlayFramework("default", framework =>
{
    framework
        .WithChatClient("default")
        .AddScene("Dev Tools", "Development utilities", scene =>
        {
            scene.WithMcpServer("mcp-server-name");
        });
});
```

The MCP server name must match a registered `IMcpServerManager` factory entry. MCP tools are fetched at runtime from the server's tool list and are merged with any other scene tools.

## Client tools

`OnClient(...)` asks the browser or mobile client to execute something locally, then resume the conversation with the result:

```csharp
builder.Services.AddPlayFramework("default", framework =>
{
    framework
        .WithChatClient("default")
        .AddCache(cache => cache.WithMemory().WithExpiration(TimeSpan.FromMinutes(10)))
        .AddScene("Browser Assistant", "Needs browser-side tools", scene =>
        {
            scene.OnClient(client =>
            {
                client
                    .AddTool("getCurrentLocation", "Get the user's current location", timeoutSeconds: 15)
                    .AddCommand("trackAnalytics", "Track an analytics event", timeoutSeconds: 5);
            });
        });
});
```

When the model calls a client tool, PlayFramework:
1. yields a `ClientInteraction` status response carrying the tool call
2. pauses the conversation in cache
3. waits for the client to send back `clientInteractionResults` with a `conversationKey`

## Execution loop

The multi-turn tool execution loop runs inside each scene:

1. scene actors execute to inject dynamic system messages
2. all scene tools are registered with the chat client options
3. the model is called; if it returns tool calls, each tool is executed and the result is fed back
4. the loop repeats until the model returns a text-only response or reaches the iteration limit
5. every step yields an `AiSceneResponse` item into the stream

```csharp
await foreach (var step in sceneManager.ExecuteAsync("What is 12 * 7?", settings: settings))
{
    // step.Status can be: ExecutingScene, FunctionRequest, FunctionCompleted,
    //                      Running, Completed, Error, BudgetExceeded, ...
    Console.WriteLine($"[{step.Status}] {step.Message}");

    if (step.FunctionName is not null)
    {
        // step.FunctionArguments is always valid JSON (or "{}") when FunctionName is set
        Console.WriteLine($"  Tool: {step.FunctionName}, Args: {step.FunctionArguments}");
    }
}
```

## FunctionArguments in responses

Starting with `10.0.11-beta.23`, `AiSceneResponse.FunctionArguments` is populated for every per-tool event:

- `FunctionRequest` (tool is about to be called)
- `FunctionCompleted` (tool result returned to the model)
- per-tool error events

When `FunctionName` is non-null, `FunctionArguments` is a valid JSON document. A call with no parameters produces `"{}"`, never `null`.

PlayFramework also emits an aggregate `FunctionRequest` item when the model returns multiple tool calls at once. That item has `FunctionName = null` and `FunctionArguments = null`; it should not be treated as an individual tool call.

```csharp
var toolEvents = responses.Where(r =>
    r.FunctionName is not null &&
    r.Status is AiResponseStatus.FunctionRequest or AiResponseStatus.FunctionCompleted);

foreach (var ev in toolEvents)
{
    using var doc = JsonDocument.Parse(ev.FunctionArguments!);
    // ev.FunctionName identifies the tool; doc.RootElement contains the arguments
}
```

## Runtime descriptions for tool descriptions

Tool descriptions can be resolved from application services at runtime, without rebuilding the DI container. This requires `WithRuntimeDescriptions(...)` on the PlayFramework builder.

See [RUNTIME_DESCRIPTIONS.md](RUNTIME_DESCRIPTIONS.md) for the complete reference. Quick example:

```csharp
services.AddScoped<IAiPromptSnapshot, AiPromptSnapshot>();

services.AddPlayFramework("default", framework =>
{
    framework.WithRuntimeDescriptions(settings =>
    {
        settings.RefreshMode = RuntimeDescriptionRefreshMode.Background;
        settings.BackgroundRefreshInterval = TimeSpan.FromMinutes(5);
    });

    framework.AddScene(
        "orders",
        async (ctx, ct) => await ctx.Services
            .GetRequiredService<IAiPromptSnapshot>()
            .GetSceneDescriptionAsync(ct),
        scene => scene.WithService<IOrderService>(tools => tools.WithMethod(
            svc => svc.SearchAsync(default!, default),
            "search_orders",
            async (ctx, ct) => await ctx.Services
                .GetRequiredService<IAiPromptSnapshot>()
                .GetSearchToolDescriptionAsync(ct),
            fallbackDescription: "Search orders")),
        fallbackDescription: "Manage orders");
});
```

The same three async overloads are available for `ServiceToolBuilder.WithMethod`, `EndpointToolBuilder.WithAction`, and `ClientInteractionBuilder.AddTool` / `AddCommand`.

## Forcing specific tools

Use `SceneRequestSettings.ForcedTools` to expose only a subset of tools to the model for a request:

```csharp
var settings = new SceneRequestSettings
{
    ExecutionMode = SceneExecutionMode.Scene,
    SceneName = "Calculator",
    ForcedTools =
    [
        new ForcedToolRequest
        {
            SceneName = "Calculator",
            ToolName = "Add",
            SourceType = PlayFrameworkToolSourceType.Service,
            SourceName = "ICalculatorService",
            MemberName = "Add"
        }
    ]
};
```

Supported `SourceType` values: `Service`, `Client`, `Mcp`, `Endpoint`, `Other`.

If a forced tool is not found in the scene, execution stops with an error response. When exactly one forced tool remains pending, PlayFramework sets `ChatToolMode` to force that tool call.

## Auto-generated description from tools

`WithDescriptionFromTools()` on a scene builder makes PlayFramework append the normalized tool names to the scene description automatically. Useful when you want the LLM to have a richer routing hint without writing a manual description:

```csharp
scene.WithDescriptionFromTools();
```

## Discovery

The discovery endpoint lists all registered tools grouped by source:

```
GET /api/ai/{factoryName}/discovery
```

Each tool entry includes `sceneName`, `toolName`, `description`, `sourceType`, `sourceName`, and `memberName`. Use discovery to build a tool picker UI and then supply the selected values to `ForcedTools`.

When runtime descriptions are active, each scene in the discovery response includes `IsRuntimeResolved`, `RuntimeDescriptionCatalogId`, and `RuntimeDescriptionVersion`. Discovery reads the current globally published catalog and does not trigger a refresh.

## Response fields for tool events

| Field | Present when | Description |
|---|---|---|
| `FunctionName` | per-tool event | normalized tool name |
| `FunctionArguments` | `FunctionName != null` | valid JSON, `"{}"` for no-arg calls |
| `SceneName` | execution events | scene being executed |
| `Status` | always | `FunctionRequest`, `FunctionCompleted`, `Running`, `Completed`, `Error`, … |
| `InputTokens` | LLM calls | input tokens used |
| `OutputTokens` | LLM calls | output tokens generated |
| `CachedInputTokens` | LLM calls | cached input tokens (prompt cache hits) |
| `Cost` | LLM calls (when adapter has pricing) | cost of this call |
| `TotalCost` | always | cumulative cost across the request |
| `ModelName` | LLM calls | model/deployment used for this call |
| `RuntimeDescriptions` | first execution response | catalog identity and acquisition details |
