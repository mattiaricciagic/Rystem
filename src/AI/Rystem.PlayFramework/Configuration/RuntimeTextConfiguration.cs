namespace Rystem.PlayFramework;

internal sealed class RuntimeTextConfiguration
{
    public string? FallbackValue { get; init; }
    public required Func<RuntimeDescriptionContext, CancellationToken, ValueTask<RuntimeDescriptionValue>> Resolver { get; init; }

    public static RuntimeTextConfiguration From(
        Func<RuntimeDescriptionContext, string> resolver,
        string? fallbackValue)
        => new()
        {
            FallbackValue = fallbackValue,
            Resolver = (context, _) => ValueTask.FromResult(new RuntimeDescriptionValue
            {
                Value = resolver(context)
            })
        };

    public static RuntimeTextConfiguration From(
        Func<RuntimeDescriptionContext, CancellationToken, Task<string>> resolver,
        string? fallbackValue)
        => new()
        {
            FallbackValue = fallbackValue,
            Resolver = async (context, cancellationToken) => new RuntimeDescriptionValue
            {
                Value = await resolver(context, cancellationToken).ConfigureAwait(false)
            }
        };

    public static RuntimeTextConfiguration From(
        Func<RuntimeDescriptionContext, CancellationToken, Task<RuntimeDescriptionValue>> resolver,
        string? fallbackValue)
        => new()
        {
            FallbackValue = fallbackValue,
            Resolver = async (context, cancellationToken) =>
                await resolver(context, cancellationToken).ConfigureAwait(false)
        };
}
