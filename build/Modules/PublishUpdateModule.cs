using System.Text.RegularExpressions;
using Build.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModularPipelines.Attributes;
using ModularPipelines.Context;
using ModularPipelines.Git.Extensions;
using ModularPipelines.Git.Options;
using ModularPipelines.GitHub.Attributes;
using ModularPipelines.GitHub.Extensions;
using ModularPipelines.Modules;
using Octokit;
using Shouldly;
using Sourcy.DotNet;
using File = ModularPipelines.FileSystem.File;

namespace Build.Modules;

/// <summary>
///     Open a pull request with the extracted content of an Autodesk update.
/// </summary>
[SkipIfNoGitHubToken]
[DependsOn<ExtractUpdateModule>]
public sealed partial class PublishUpdateModule(IOptions<PackOptions> packOptions) : Module<PullRequest>
{
    /// <summary>
    ///     The GraphQL endpoint of GitHub, relative to the address the client holds.
    /// </summary>
    private static readonly Uri GraphQlEndpoint = new("graphql", UriKind.Relative);

    protected override async Task<PullRequest?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var extractionResult = await context.GetModule<ExtractUpdateModule>();
        var content = extractionResult.ValueOrDefault!;
        var branch = $"content/revit-{content.Version.Replace('.', '-')}";

        var settingsFile = await PinReleaseAsync(context, content, cancellationToken);
        await CommitAsync(context, content, settingsFile, branch, cancellationToken);

        var pullRequest = await CreatePullRequestAsync(context, content, branch);
        await LinkIssueAsync(context, content, pullRequest, cancellationToken);

        return pullRequest;
    }

    /// <summary>
    ///     Reference the pull request from the issue the update was reported under.
    /// </summary>
    /// <remarks>The reference fills the Development section of the issue, and the title is what ties the two together.</remarks>
    private static async Task LinkIssueAsync(IModuleContext context, RevitUpdateContent content, PullRequest pullRequest, CancellationToken cancellationToken)
    {
        var repositoryInfo = context.GitHub().RepositoryInfo;
        var title = TrackUpdatesModule.CreateIssueTitle(content.Release, content.Version, content.ReplacedVersions.Length > 0);
        var reportedIssues = await context.GitHub().Client.Issue.GetAllForRepository(repositoryInfo.Owner, repositoryInfo.RepositoryName, new RepositoryIssueRequest
        {
            State = ItemStateFilter.Open
        });

        var issue = reportedIssues.FirstOrDefault(reportedIssue => reportedIssue.Title == title);
        if (issue is null)
        {
            context.Logger.LogInformation("No open issue reports the {Version} update", content.Version);
            return;
        }

        var mutation = $$"""
                         mutation {
                           addCloseIssueReferences(input: {issueId: "{{issue.NodeId}}", pullRequestIds: ["{{pullRequest.NodeId}}"]}) {
                             clientMutationId
                           }
                         }
                         """;

        var response = await context.GitHub().Client.Connection.Post<GraphQlResponse>(GraphQlEndpoint, new
        {
            query = mutation
        }, "application/json", "application/json", parameters: null, cancellationToken);

        var errors = response.Body.Errors;
        if (errors is { Length: > 0 })
        {
            throw new InvalidOperationException($"Referencing issue #{issue.Number} failed: {string.Join("; ", errors.Select(error => error.Message))}");
        }

        context.Logger.LogInformation("Referenced the pull request from issue #{Number}", issue.Number);
    }

    /// <summary>
    ///     Point the release settings at the packaged update.
    /// </summary>
    /// <remarks>
    ///     A tag on the merged update packs the version folder of the update and publishes its build as the release notes.
    ///     The settings file keeps its layout, and only the two values change.
    /// </remarks>
    /// <returns>The build settings file holding the release settings.</returns>
    private static async Task<File> PinReleaseAsync(IModuleContext context, RevitUpdateContent content, CancellationToken cancellationToken)
    {
        var settingsFile = context.Files.GetFile(Path.Combine(Projects.Build.DirectoryName!, "appsettings.json"));
        var settings = await settingsFile.ReadAsync(cancellationToken);

        settings = ReplaceSetting(settings, PinnedDllVersionRegex(), content.Version);
        settings = ReplaceSetting(settings, ChangelogRegex(), CreateCommitMessage(content));

        await settingsFile.WriteAsync(settings, cancellationToken);
        return settingsFile;
    }

    [Pure]
    private static string ReplaceSetting(string settings, Regex settingRegex, string value)
    {
        settingRegex.Count(settings).ShouldBe(1, $"The build settings hold no single value matching {settingRegex}");
        return settingRegex.Replace(settings, _ => value);
    }

    [GeneratedRegex($"""(?<="{nameof(PackOptions.PinnedDllVersion)}"\s*:\s*")[^"]*""")]
    private static partial Regex PinnedDllVersionRegex();

    [GeneratedRegex($"""(?<="{nameof(PublishOptions.Changelog)}"\s*:\s*")[^"]*""")]
    private static partial Regex ChangelogRegex();

    /// <summary>
    ///     Commit the packaged content and the release settings onto a branch of its own.
    /// </summary>
    private async Task CommitAsync(IModuleContext context, RevitUpdateContent content, File settingsFile, string branch, CancellationToken cancellationToken)
    {
        await context.Git().Commands.Checkout(new GitCheckoutOptions(branch, true), token: cancellationToken);

        await context.Git().Commands.Add(new GitAddOptions
        {
            All = true,
            Arguments = ["--", context.Git().RootDirectory.GetFolder(packOptions.Value.ContentDirectory).Path, settingsFile.Path]
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
            Draft = false
        };

        try
        {
            return await context.GitHub().Client.PullRequest.Create(repositoryInfo.Owner, repositoryInfo.RepositoryName, pullRequest);
        }
        catch (ApiException exception)
        {
            var createdPullRequest = await FindPullRequestAsync(context, branch);
            if (createdPullRequest is null)
            {
                throw;
            }

            context.Logger.LogInformation("GitHub returns {Status} and holds the pull request #{Number}", exception.HttpResponse.StatusCode, createdPullRequest.Number);
            return createdPullRequest;
        }
    }

    /// <summary>
    ///     Read the open pull request of the branch.
    /// </summary>
    /// <remarks>
    ///     A content update carries tens of megabytes, and GitHub answers it with a gateway error
    ///     once the pull request stands. The branch is what identifies it afterwards.
    /// </remarks>
    /// <returns><c>null</c> when the branch carries no pull request.</returns>
    private static async Task<PullRequest?> FindPullRequestAsync(IModuleContext context, string branch)
    {
        var repositoryInfo = context.GitHub().RepositoryInfo;
        var openPullRequests = await context.GitHub().Client.PullRequest.GetAllForRepository(repositoryInfo.Owner, repositoryInfo.RepositoryName, new PullRequestRequest
        {
            State = ItemStateFilter.Open,
            Head = $"{repositoryInfo.Owner}:{branch}"
        });

        return openPullRequests.FirstOrDefault(openPullRequest => openPullRequest.Head.Ref == branch);
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

    [PublicAPI]
    private sealed record GraphQlResponse
    {
        public GraphQlError[]? Errors { get; init; }
    }

    [PublicAPI]
    private sealed record GraphQlError
    {
        public string? Message { get; init; }
    }
}
