namespace Build.Options;

[PublicAPI]
public sealed record UpdateOptions
{
    /// <summary>
    ///     The Autodesk update installer to extract.
    /// </summary>
    /// <example>https://up1.autodesk.com/prd/2026/RVT/8C95356B-C1ED-35EE-9E4E-BCD718965B71/Revit_2026_5_0.exe</example>
    public string? Url { get; init; }

    /// <summary>
    ///     The time the extraction is given before the pipeline abandons it.
    /// </summary>
    /// <remarks>A download stalled halfway holds no deadline of its own.</remarks>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    ///     The package of the installer holding the Revit assemblies.
    /// </summary>
    public string PackageName { get; init; } = "RCPCOM";

    /// <summary>
    ///     The assemblies taken from the package.
    /// </summary>
    /// <remarks>The <c>.dll</c> and the <c>.xml</c> of each name are taken, whichever of them the package holds.</remarks>
    public string[] Assemblies { get; init; } = [];
}
