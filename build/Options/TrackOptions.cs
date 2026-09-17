namespace Build.Options;

[PublicAPI]
public sealed record TrackOptions
{
    /// <summary>
    ///     The release notes index of a Revit version.
    /// </summary>
    /// <remarks>The <c>{0}</c> placeholder is replaced with the Revit version.</remarks>
    public string ReleaseNotesUrl { get; init; } = "https://help.autodesk.com/cloudhelp/{0}/ENU/RevitReleaseNotes/files/RevitReleaseNotes_{0}updates_html.html";

    /// <summary>
    ///     The labels of a reported update.
    /// </summary>
    public string[] IssueLabels { get; init; } = [];
}
