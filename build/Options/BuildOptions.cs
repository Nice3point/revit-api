namespace Build.Options;

[PublicAPI]
public sealed record BuildOptions
{
    public string OutputDirectory { get; init; } = "output";
}