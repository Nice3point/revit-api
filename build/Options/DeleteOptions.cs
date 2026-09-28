namespace Build.Options;

[PublicAPI]
public sealed record DeleteOptions
{
    /// <summary>
    ///     The git reference of the release to delete.
    /// </summary>
    /// <example>2027.3.0</example>
    public string Ref { get; init; } = string.Empty;
}
