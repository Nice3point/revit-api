using Build.Options;
using Microsoft.Extensions.Options;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.Git.Extensions;
using ModularPipelines.Git.Options;
using ModularPipelines.GitHub.Attributes;
using ModularPipelines.GitHub.Extensions;
using ModularPipelines.Modules;
using Octokit;

namespace Build.Modules;

/// <summary>
///     Open a pull request with the extracted content of an Autodesk update.
/// </summary>
[SkipIfNoGitHubToken]
[DependsOn<ExtractUpdateModule>]
public sealed class PublishUpdateModule(IOptions<PackOptions> packOptions) : Module<PullRequest>
{
    protected override async Task<PullRequest?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var extractionResult = await context.GetModule<ExtractUpdateModule>();
        var content = extractionResult.ValueOrDefault!;
        var branch = $"content/revit-{content.Version.Replace('.', '-')}";

        await CommitAsync(context, content, branch, cancellationToken);

        return await CreatePullRequestAsync(context, content, branch);
    }

    /// <summary>
    ///     Commit the packaged content onto a branch of its own.
    /// </summary>
    private async Task CommitAsync(IModuleContext context, RevitUpdateContent content, string branch, CancellationToken cancellationToken)
    {
        await context.Git().Commands.Checkout(new GitCheckoutOptions(branch, true), token: cancellationToken);

        await context.Git().Commands.Add(new GitAddOptions
        {
            All = true,
            Arguments = ["--", context.Git().RootDirectory.GetFolder(packOptions.Value.ContentDirectory).Path]
        }, token: cancellationToken);

        await context.Git().Commands.Commit(new GitCommitOptions
        {
            Message = CreateCommitMessage(content)
        }, token: cancellationToken);

        await context.Git().Commands.Push(new GitPushOptions
        {
            SetUpstream = true,
            Arguments = ["origin", branch]
        }, token: cancellationToken);
    }

    private static async Task<PullRequest> CreatePullRequestAsync(IModuleContext context, RevitUpdateContent content, string branch)
    {
        var repositoryInfo = context.GitHub().RepositoryInfo;
        var pullRequest = new NewPullRequest(CreateCommitMessage(content), branch, context.Git().Information.DefaultBranchName)
        {
            Body = CreatePullRequestBody(content),
            Draft = true
        };

        return await context.GitHub().Client.PullRequest.Create(repositoryInfo.Owner, repositoryInfo.RepositoryName, pullRequest);
    }

    /// <summary>
    ///     The header the repository gives a content update.
    /// </summary>
    /// <example>Build 26.5.0.55</example>
    private static string CreateCommitMessage(RevitUpdateContent content)
    {
        return $"Build {content.Build}";
    }

    private static string CreatePullRequestBody(RevitUpdateContent content)
    {
        var isFirstVersion = content.ReplacedVersions.Length == 0;

        string[] summary = isFirstVersion
            ?
            [
                $"Adds the Revit {content.Release} packages.",
                $"The target framework of Revit {content.Release} is unmapped. Packing the version fails until it is set."
            ]
            : [$"Updates the Revit {content.Release} packages."];

        string[] versions = isFirstVersion
            ? [$"- New version: {content.Version}"]
            : [$"- Current version: {string.Join(" and ", content.ReplacedVersions)}", $"- New version: {content.Version}"];

        return $"""
                {string.Join('\n', summary)}

                {string.Join('\n', versions)}
                - Build: {content.Build}
                - Files: {content.Assemblies.Length}
                - [Installer]({content.Url})
                """;
    }
}
