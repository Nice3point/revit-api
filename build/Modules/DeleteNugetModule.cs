using Build.Options;
using EnumerableAsyncProcessor.Extensions;
using Microsoft.Extensions.Options;
using ModularPipelines.Context;
using ModularPipelines.DotNet.Extensions;
using ModularPipelines.DotNet.Options;
using ModularPipelines.Git.Extensions;
using ModularPipelines.Models;
using ModularPipelines.Modules;
using Shouldly;

namespace Build.Modules;

/// <summary>
///     Delete the NuGet package.
/// </summary>
public sealed class DeleteNugetModule(IOptions<PublishOptions> publishOptions, IOptions<PackOptions> packOptions, IOptions<NuGetOptions> nuGetOptions) : Module<CommandResult[]?>
{
    protected override async Task<CommandResult[]?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        publishOptions.Value.Version.ShouldNotBeEmpty("No NuGet version was specified to delete");
        
        var targetFiles = context.Git().RootDirectory
            .GetFolder(packOptions.Value.ContentDirectory)
            .GetFiles(file => file.Extension == ".dll")
            .DistinctBy(file => file.Name)
            .ToArray();

        targetFiles.ShouldNotBeEmpty("No NuGet packages were found to delete");

        return await targetFiles
            .SelectAsync(async file => await context.DotNet().Nuget.Delete(new DotNetNugetDeleteOptions
                {
                    PackageName = $"Nice3point.Revit.Api.{file.NameWithoutExtension}",
                    Version = publishOptions.Value.Version,
                    ApiKey = nuGetOptions.Value.ApiKey,
                    Source = nuGetOptions.Value.Source,
                    NonInteractive = true
                }, cancellationToken: cancellationToken),
                cancellationToken)
            .ProcessInParallel();
    }
}