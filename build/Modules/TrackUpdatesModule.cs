using System.Globalization;
using System.Text.RegularExpressions;
using Build.Options;
using EnumerableAsyncProcessor.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModularPipelines.Context;
using ModularPipelines.Git.Extensions;
using ModularPipelines.GitHub.Attributes;
using ModularPipelines.GitHub.Extensions;
using ModularPipelines.Modules;
using Octokit;
using Shouldly;

namespace Build.Modules;

/// <summary>
///     Report the Revit versions with a newer update published by Autodesk.
/// </summary>
[SkipIfNoGitHubToken]
public sealed partial class TrackUpdatesModule(IOptions<PackOptions> packOptions, IOptions<TrackOptions> trackOptions) : Module<Issue[]>
{
    /// <summary>
    ///     Compares versions by the numeric value of each digit group.
    /// </summary>
    /// <example>2026.4.10 is greater than 2026.4.2</example>
    private static readonly StringComparer VersionComparer = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.NumericOrdering);

    protected override async Task<Issue[]?> ExecuteAsync(IModuleContext context, CancellationToken cancellationToken)
    {
        var trackedVersions = ResolveTrackedVersions(context);
        trackedVersions.ShouldNotBeEmpty("No packaged Revit versions were found to track");

        var publishedUpdates = await trackedVersions
            .SelectAsync(async version => await FetchLatestUpdateAsync(context, version, cancellationToken), cancellationToken)
            .ProcessInParallel();

        var updates = publishedUpdates.OfType<RevitUpdate>().ToArray();
        foreach (var update in updates.Where(update => !IsOutdated(update)))
        {
            context.Logger.LogInformation("Revit {Version} is up to date with the {Release} update", update.Version, update.Release);
        }

        return await ReportUpdatesAsync(context, updates.Where(IsOutdated).ToArray());
    }

    /// <summary>
    ///     Resolve the latest packaged version of each Revit version, followed by the version Autodesk releases next.
    /// </summary>
    private RevitVersion[] ResolveTrackedVersions(IModuleContext context)
    {
        var packagedVersions = context.Git().RootDirectory
            .GetFolder(packOptions.Value.ContentDirectory)
            .ListFolders()
            .Select(folder => folder.Name)
            .GroupBy(version => version[..4])
            .Select(versions => new RevitVersion
            {
                Version = versions.Key,
                PackagedVersion = versions.Max(VersionComparer)!
            })
            .ToArray();

        var latestVersion = packagedVersions.MaxBy(version => version.Version, VersionComparer)!;
        var nextVersion = int.Parse(latestVersion.Version, CultureInfo.InvariantCulture) + 1;

        return
        [
            .. packagedVersions,
            new RevitVersion
            {
                Version = nextVersion.ToString(CultureInfo.InvariantCulture),
                PackagedVersion = null
            }
        ];
    }

    /// <summary>
    ///     Read the latest update Autodesk published for the specified Revit version.
    /// </summary>
    /// <returns><c>null</c> when Autodesk publishes no release notes for that version.</returns>
    private async Task<RevitUpdate?> FetchLatestUpdateAsync(IModuleContext context, RevitVersion trackedVersion, CancellationToken cancellationToken)
    {
        var indexUrl = string.Format(trackOptions.Value.ReleaseNotesUrl, trackedVersion.Version);
        var indexPage = await ReadPageAsync(context, indexUrl, cancellationToken);
        if (indexPage is null)
        {
            context.Logger.LogInformation("Revit {Version} has no release notes", trackedVersion.Version);
            return null;
        }

        var releaseLinks = ReleaseLinkRegex().Matches(indexPage);
        if (releaseLinks.Count == 0)
        {
            return null;
        }

        var latestLink = releaseLinks.MaxBy(ToRelease, VersionComparer)!;
        var releaseUrl = indexUrl[..(indexUrl.LastIndexOf('/') + 1)] + latestLink.Value;
        var releasePage = await ReadPageAsync(context, releaseUrl, cancellationToken);
        if (releasePage is null)
        {
            return null;
        }

        var releaseDate = ReleaseDateRegex().Match(releasePage);
        var build = BuildRegex().Match(releasePage).Groups["build"].Value;
        return new RevitUpdate
        {
            Version = trackedVersion.Version,
            Release = ToRelease(latestLink),
            ReleaseVersion = ToPackagedVersion(trackedVersion.Version, build),
            ReleaseDate = releaseDate.Success ? releaseDate.Groups["date"].Value.Trim() : null,
            ReleaseNotesUrl = releaseUrl,
            Build = build,
            PackagedVersion = trackedVersion.PackagedVersion
        };
    }

    /// <summary>
    ///     Download the release notes page.
    /// </summary>
    /// <returns><c>null</c> when the page does not exist.</returns>
    private static async Task<string?> ReadPageAsync(IModuleContext context, string url, CancellationToken cancellationToken)
    {
        var response = await context.Network.Http.SendAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>
    ///     Extract the release from a release notes link.
    /// </summary>
    /// <example>2026updates/RevitReleaseNotes_2026updates_2026_5_html.html resolves as 2026.5</example>
    private static string ToRelease(Match releaseLink)
    {
        return releaseLink.Groups["release"].Value.Replace('_', '.');
    }

    /// <summary>
    ///     Translate the build of an update into the version the packages follow.
    /// </summary>
    /// <example>Version 2026 and build 26.5.0.55 resolves as 2026.5.0</example>
    private static string ToPackagedVersion(string version, string build)
    {
        var buildParts = build.Split('.');
        return $"{version}.{buildParts[1]}.{buildParts[2]}";
    }

    /// <summary>
    ///     Determines whether the packaged version is older than the published update.
    /// </summary>
    private static bool IsOutdated(RevitUpdate update)
    {
        return update.PackagedVersion is null || VersionComparer.Compare(update.ReleaseVersion, update.PackagedVersion) > 0;
    }

    /// <summary>
    ///     Create an issue for every update that has none.
    /// </summary>
    private async Task<Issue[]> ReportUpdatesAsync(IModuleContext context, RevitUpdate[] updates)
    {
        var repositoryInfo = context.GitHub().RepositoryInfo;
        var reportedIssues = await context.GitHub().Client.Issue.GetAllForRepository(repositoryInfo.Owner, repositoryInfo.RepositoryName, new RepositoryIssueRequest
        {
            State = ItemStateFilter.All
        });

        var reportedTitles = reportedIssues.Select(issue => issue.Title).ToHashSet();
        var createdIssues = new List<Issue>();

        foreach (var update in updates)
        {
            var title = CreateIssueTitle(update.Version, update.ReleaseVersion, update.PackagedVersion is not null);
            if (!reportedTitles.Add(title))
            {
                context.Logger.LogInformation("The {Release} update is already reported", update.Release);
                continue;
            }

            context.Logger.LogInformation("Reporting the {Release} update", update.Release);
            createdIssues.Add(await context.GitHub().Client.Issue.Create(repositoryInfo.Owner, repositoryInfo.RepositoryName, CreateIssue(update, title)));
        }

        return createdIssues.ToArray();
    }

    private NewIssue CreateIssue(RevitUpdate update, string title)
    {
        var issue = new NewIssue(title)
        {
            Body = CreateIssueBody(update)
        };

        foreach (var label in trackOptions.Value.IssueLabels)
        {
            issue.Labels.Add(label);
        }

        return issue;
    }

    /// <summary>
    ///     The title the issue of an update carries.
    /// </summary>
    /// <remarks>The pull request of the update finds its issue under this title.</remarks>
    internal static string CreateIssueTitle(string version, string packageVersion, bool isPackaged)
    {
        return isPackaged
            ? $"Update the Revit {version} packages to {packageVersion}"
            : $"Add the Revit {version} packages";
    }

    private static string CreateIssueBody(RevitUpdate update)
    {
        var release = update.ReleaseDate is null
            ? $"Autodesk released the Revit {update.Release} update."
            : $"Autodesk released the Revit {update.Release} update on {update.ReleaseDate}.";

        string[] summary = update.PackagedVersion is null
            ? [release, $"Revit {update.Version} is not packaged yet."]
            : [release];

        string[] versions = update.PackagedVersion is null
            ? [$"- New version: {update.ReleaseVersion}"]
            : [$"- Current version: {update.PackagedVersion}", $"- New version: {update.ReleaseVersion}"];

        return $"""
                {string.Join('\n', summary)}

                {string.Join('\n', versions)}
                - Build: {update.Build}
                - [Release notes]({update.ReleaseNotesUrl})
                """;
    }

    /// <summary>
    ///     Matches an update listed in the release notes index.
    /// </summary>
    /// <example>2026updates/RevitReleaseNotes_2026updates_2026_5_html.html</example>
    [GeneratedRegex(@"(?<version>\d{4})updates/RevitReleaseNotes_\k<version>updates_(?<release>\d+(?:_\d+)*)_html\.html")]
    private static partial Regex ReleaseLinkRegex();

    /// <summary>
    ///     Matches the day the update was published.
    /// </summary>
    /// <example>Release Date: August 6, 2026</example>
    [GeneratedRegex(@"Release Date:(?<date>[^<]+)<")]
    private static partial Regex ReleaseDateRegex();

    /// <summary>
    ///     Matches the build Revit reports once the update is applied.
    /// </summary>
    /// <example>26.5.0.55</example>
    [GeneratedRegex(@"<li>(?<build>\d+(?:\.\d+){3})</li>")]
    private static partial Regex BuildRegex();

    private sealed record RevitVersion
    {
        /// <summary>
        ///     The Revit version.
        /// </summary>
        /// <example>2026</example>
        public required string Version { get; init; }

        /// <summary>
        ///     The latest version packaged for it, absent while the repository packages none.
        /// </summary>
        /// <example>2026.4.10</example>
        public required string? PackagedVersion { get; init; }
    }
}

public sealed record RevitUpdate
{
    /// <summary>
    ///     The Revit version of the update.
    /// </summary>
    /// <example>2026</example>
    public required string Version { get; init; }

    /// <summary>
    ///     The release published by Autodesk.
    /// </summary>
    /// <example>
    ///     2026.5 <br />
    ///     2026.4.1
    /// </example>
    public required string Release { get; init; }

    /// <summary>
    ///     The release in the version scheme the packages follow.
    /// </summary>
    /// <example>2026.5.0</example>
    public required string ReleaseVersion { get; init; }

    /// <summary>
    ///     The day Autodesk published the update.
    /// </summary>
    /// <remarks>The release notes of the older Revit versions omit the date.</remarks>
    /// <example>August 6, 2026</example>
    public required string? ReleaseDate { get; init; }

    /// <summary>
    ///     The release notes of the update.
    /// </summary>
    public required string ReleaseNotesUrl { get; init; }

    /// <summary>
    ///     The build Revit reports once the update is applied.
    /// </summary>
    /// <example>26.5.0.55</example>
    public required string Build { get; init; }

    /// <summary>
    ///     The latest packaged version of this Revit version, absent while the repository packages none.
    /// </summary>
    /// <example>2026.4.10</example>
    public required string? PackagedVersion { get; init; }
}
