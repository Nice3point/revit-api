using System.IO.Compression;
using System.Xml.Linq;
using Build.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModularPipelines.Configuration;
using ModularPipelines.Context;
using ModularPipelines.FileSystem;
using ModularPipelines.Git.Extensions;
using ModularPipelines.Modules;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using Shouldly;
using File = ModularPipelines.FileSystem.File;

namespace Build.Modules;

/// <summary>
///     Extract the Revit assemblies of an Autodesk update into the packaged content.
/// </summary>
public sealed class ExtractUpdateModule(IOptions<PackOptions> packOptions, IOptions<UpdateOptions> updateOptions) : Module<RevitUpdateContent>
{
    /// <summary>
    ///     The part of the installer scanned for the archive.
    /// </summary>
    private const int SearchWindow = 16 * 1024 * 1024;

    /// <summary>
    ///     The folder the package maps onto the Revit installation, with the <c>{0}</c> placeholder for the Revit version.
    /// </summary>
    private const string InstallRoot = "VFS/ProgramFilesX64/Autodesk/Revit%20{0}/";

    /// <summary>
    ///     The installer opens with a Windows executable and holds the update behind it as a 7-Zip archive.
    /// </summary>
    private static readonly byte[] ArchiveSignature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    protected override ModuleConfiguration Configure()
    {
        return ModuleConfiguration.Create()
            .WithTimeout(updateOptions.Value.Timeout)
            .Build();
    }

    protected override async Task<RevitUpdateContent?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var url = updateOptions.Value.Url;
        url.ShouldNotBeNullOrWhiteSpace("No update installer was specified to extract");

        var workspace = await context.Files
            .GetFolder(Path.GetTempPath()).GetFolder("revit-update")
            .CreateAsync(cancellationToken);
        workspace.Clean();

        var installer = await DownloadInstallerAsync(context, url, workspace, cancellationToken);
        var package = await ExtractPackageAsync(installer, workspace, cancellationToken);
        await installer.DeleteAsync(cancellationToken);

        var content = context.Git().RootDirectory.GetFolder(packOptions.Value.ContentDirectory);
        var version = ToPackagedVersion(package.Release, package.Build);
        var versionFolder = await content.GetFolder(version).CreateAsync(cancellationToken);
        var assemblies = ExtractAssemblies(package, versionFolder);
        assemblies.ShouldNotBeEmpty($"The {updateOptions.Value.PackageName} package of the installer holds no Revit assemblies");

        await workspace.DeleteAsync(cancellationToken);
        var replacedVersions = RemovePreviousVersions(content, package.Release, version);

        context.Logger.LogInformation("Extracted {Count} files of Revit {Version} from build {Build}", assemblies.Length, version, package.Build);
        return new RevitUpdateContent
        {
            Release = package.Release,
            Version = version,
            Build = package.Build,
            Url = url,
            Assemblies = assemblies,
            ReplacedVersions = replacedVersions
        };
    }

    /// <summary>
    ///     Download the update installer.
    /// </summary>
    /// <remarks>
    ///     The installer runs to several gigabytes, past the two the response buffer holds,
    ///     and the body reaches the disk as it arrives.
    /// </remarks>
    private static async Task<File> DownloadInstallerAsync(IModuleContext context, string url, Folder workspace, CancellationToken cancellationToken)
    {
        var address = new Uri(url);
        var installer = workspace.GetFile(Path.GetFileName(address.AbsolutePath));
        context.Logger.LogInformation("Downloading {Url}", url);

        using var response = await context.Network.Http.HttpClient.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await installer.WriteAsync(responseStream, cancellationToken);

        return installer;
    }

    /// <summary>
    ///     Take the package holding the Revit assemblies out of the installer.
    /// </summary>
    private async Task<RevitPackage> ExtractPackageAsync(File installer, Folder workspace, CancellationToken cancellationToken)
    {
        var packageName = updateOptions.Value.PackageName;
        await using var installerStream = installer.GetStream(FileAccess.Read);
        installerStream.Position = FindArchiveOffset(installerStream);

        using var installerArchive = SevenZipArchive.Open(installerStream);
        var manifest = ReadManifest(installerArchive, $"/pkg.{packageName}.xml");
        var archive = workspace.GetFile($"{packageName}.adix");

        var adixStream = await FindEntry(installerArchive, $"/{packageName}.adix").OpenEntryStreamAsync(cancellationToken);
        await using (var packageStream = adixStream)
        {
            await archive.WriteAsync(packageStream, cancellationToken);
        }

        return new RevitPackage
        {
            Release = ReadValue(manifest, "Release"),
            Build = ReadValue(manifest, "BuildNumber"),
            Archive = archive
        };
    }

    /// <summary>
    ///     Locate the archive behind the executable.
    /// </summary>
    private static long FindArchiveOffset(Stream installer)
    {
        var buffer = new byte[SearchWindow];
        var length = installer.ReadAtLeast(buffer, buffer.Length, false);
        var offset = buffer.AsSpan(0, length).IndexOf(ArchiveSignature);

        offset.ShouldBePositive("The installer holds no archive");
        return offset;
    }

    /// <summary>
    ///     Read the identity of the package declared by the installer.
    /// </summary>
    private static XElement ReadManifest(SevenZipArchive installer, string entryName)
    {
        using var manifestStream = FindEntry(installer, entryName).OpenEntryStream();
        return XDocument.Load(manifestStream)
            .Descendants()
            .Single(element => element.Name.LocalName == "Identity");
    }

    private static IArchiveEntry FindEntry(SevenZipArchive installer, string entryName)
    {
        return installer.Entries.Single(entry => entry.Key is not null && entry.Key.EndsWith(entryName, StringComparison.Ordinal));
    }

    private static string ReadValue(XElement manifest, string name)
    {
        return manifest.Elements().Single(element => element.Name.LocalName == name).Value;
    }

    /// <summary>
    ///     Translate the build of the update into the version the packages follow.
    /// </summary>
    /// <example>Release 2026 and build 26.5.0.55 become 2026.5.0</example>
    private static string ToPackagedVersion(string release, string build)
    {
        var buildParts = build.Split('.');
        return $"{release}.{buildParts[1]}.{buildParts[2]}";
    }

    /// <summary>
    ///     Write the assemblies of the package into the target folder.
    /// </summary>
    private string[] ExtractAssemblies(RevitPackage package, Folder target)
    {
        var names = updateOptions.Value.Assemblies
            .SelectMany(assembly => new[] { $"{assembly}.dll", $"{assembly}.xml" })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var installRoot = string.Format(InstallRoot, package.Release);
        using var archive = ZipFile.OpenRead(package.Archive.Path);
        var entries = archive.Entries
            .Where(entry => entry.FullName == installRoot + entry.Name)
            .Where(entry => names.Contains(entry.Name))
            .ToArray();

        foreach (var entry in entries)
        {
            entry.ExtractToFile(target.GetFile(entry.Name).Path, true);
        }

        return entries.Select(entry => entry.Name).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    ///     Delete the versions this update replaces within the same Revit version.
    /// </summary>
    private static string[] RemovePreviousVersions(Folder content, string release, string version)
    {
        var previousFolders = content
            .ListFolders()
            .Where(folder => folder.Name.StartsWith($"{release}.", StringComparison.Ordinal))
            .Where(folder => folder.Name != version)
            .ToArray();

        foreach (var folder in previousFolders)
        {
            folder.Delete();
        }

        return previousFolders.Select(folder => folder.Name).Order(StringComparer.Ordinal).ToArray();
    }

    private sealed record RevitPackage
    {
        /// <summary>
        ///     The Revit version the package installs.
        /// </summary>
        /// <example>2026</example>
        public required string Release { get; init; }

        /// <summary>
        ///     The build Revit reports once the update is applied.
        /// </summary>
        /// <example>26.5.0.55</example>
        public required string Build { get; init; }

        /// <summary>
        ///     The package holding the Revit installation files.
        /// </summary>
        public required File Archive { get; init; }
    }
}

public sealed record RevitUpdateContent
{
    /// <summary>
    ///     The Revit version of the update.
    /// </summary>
    /// <example>2026</example>
    public required string Release { get; init; }

    /// <summary>
    ///     The version the assemblies are packaged under.
    /// </summary>
    /// <example>2026.5.0</example>
    public required string Version { get; init; }

    /// <summary>
    ///     The build Revit reports once the update is applied.
    /// </summary>
    /// <example>26.5.0.55</example>
    public required string Build { get; init; }

    /// <summary>
    ///     The installer the assemblies come from.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    ///     The files written into the packaged content.
    /// </summary>
    public required string[] Assemblies { get; init; }

    /// <summary>
    ///     The versions of the same Revit version removed by this update.
    /// </summary>
    /// <example>2026.4.10</example>
    public required string[] ReplacedVersions { get; init; }
}
