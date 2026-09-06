namespace Build.Options;

[PublicAPI]
public sealed record PublishOptions
{
    public string? Changelog { get; init; }
    public string Version { get; init; } = string.Empty;
}