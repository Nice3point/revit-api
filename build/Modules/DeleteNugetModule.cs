using System.Text;
using Build.Options;
using EnumerableAsyncProcessor.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Extensions;
using ModularPipelines.DotNet.Options;
using ModularPipelines.Git.Extensions;
using ModularPipelines.Git.Options;
using ModularPipelines.Models;
using ModularPipelines.Modules;
using ModularPipelines.Options;
using Shouldly;

namespace Build.Modules;

/// <summary>
///     Delete the NuGet packages published from the specified git reference.
/// </summary>
public sealed class DeleteNugetModule(IOptions<DeleteOptions> deleteOptions, IOptions<NuGetOptions> nuGetOptions) : Module<CommandResult[]?>
{
    protected override async Task<CommandResult[]?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var reference = deleteOptions.Value.Ref;
        reference.ShouldNotBeNullOrWhiteSpace("No git reference was specified to delete");

        var packOptions = await ReadPackOptionsAsync(context, reference, cancellationToken);
        var version = string.IsNullOrEmpty(packOptions.PinnedDllVersion) ? reference : packOptions.PinnedDllVersion;
        var contentFolder = $"{packOptions.ContentDirectory.Replace('\\', '/').TrimEnd('/')}/{version}";

        var targetFiles = (await ListFilesAsync(context, reference, contentFolder, cancellationToken))
            .Where(file => Path.GetExtension(file) == ".dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => string.IsNullOrEmpty(packOptions.PinnedDllName) || name == packOptions.PinnedDllName)
            .ToArray();

        targetFiles.ShouldNotBeEmpty($"No NuGet packages were found to delete at: {reference}:{contentFolder}");

        return await targetFiles
            .SelectAsync(async name => await context.DotNet().Nuget.Delete(new DotNetNugetDeleteOptions
                {
                    PackageName = $"Nice3point.Revit.Api.{name}",
                    Version = version,
                    ApiKey = nuGetOptions.Value.ApiKey,
                    Source = nuGetOptions.Value.Source,
                    NonInteractive = true
                }, cancellationToken: cancellationToken),
                cancellationToken)
            .ProcessInParallel();
    }

    /// <summary>
    ///     Read the pack options from the settings file stored at the specified git reference.
    /// </summary>
    private static async Task<PackOptions> ReadPackOptionsAsync(IModuleContext context, string reference, CancellationToken cancellationToken)
    {
        var showResult = await context.Git().Commands.Show(
            new GitShowOptions
            {
                Arguments = [$"{reference}:build/appsettings.json"]
            },
            new CommandExecutionOptions
            {
                LogSettings = CommandLoggingOptions.Silent
            },
            cancellationToken);

        using var settingsStream = new MemoryStream(Encoding.UTF8.GetBytes(showResult.StandardOutput));
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(settingsStream)
            .Build();

        return configuration.GetSection("Pack").Get<PackOptions>() ?? new PackOptions();
    }

    /// <summary>
    ///     List the files of the folder stored at the specified git reference.
    /// </summary>
    private static async Task<string[]> ListFilesAsync(IModuleContext context, string reference, string folder, CancellationToken cancellationToken)
    {
        var treeResult = await context.Git().Commands.LsTree(
            new GitLsTreeOptions
            {
                NameOnly = true,
                FullTree = true,
                Arguments = [reference, $"{folder}/"]
            },
            new CommandExecutionOptions
            {
                LogSettings = CommandLoggingOptions.Silent
            },
            cancellationToken);

        return treeResult.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
