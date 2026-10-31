// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace XboxDevCLI;

/// <summary>
/// The web addresses the CLI prints or opens.
/// </summary>
/// <remarks>
/// Released builds use <see cref="Release"/>. A build can link somewhere else instead by
/// supplying a <c>Development</c> set and defining <c>XBOX_DEVELOPMENT_LINKS</c>; see
/// <c>XboxDevCLI.csproj</c>.
/// </remarks>
internal static partial class ProjectLinks
{
#if XBOX_DEVELOPMENT_LINKS
    internal const string Home = Development.Home;
    internal const string Documentation = Development.Documentation;
    internal const string TelemetryPolicy = Development.TelemetryPolicy;
    internal const string NewIssue = Development.NewIssue;
    internal const string Releases = Development.Releases;
    internal const string InstallationGuide = Development.InstallationGuide;
    internal const string ReleaseTags = Development.ReleaseTags;
    internal const string LatestReleaseApi = Development.LatestReleaseApi;
#else
    internal const string Home = Release.Home;
    internal const string Documentation = Release.Documentation;
    internal const string TelemetryPolicy = Release.TelemetryPolicy;
    internal const string NewIssue = Release.NewIssue;
    internal const string Releases = Release.Releases;
    internal const string InstallationGuide = Release.InstallationGuide;
    internal const string ReleaseTags = Release.ReleaseTags;
    internal const string LatestReleaseApi = Release.LatestReleaseApi;
#endif

    /// <summary>
    /// The links in released builds. Most are aka.ms links, so their targets can change without
    /// a new release.
    /// </summary>
    internal static class Release
    {
        internal const string Home = "https://aka.ms/xbox-cli";
        internal const string Documentation = "https://aka.ms/xbox-cli/docs";
        internal const string TelemetryPolicy = "https://aka.ms/xbox-cli/telemetry";

        /// <summary>
        /// A new issue. <c>xbox feedback</c> adds the issue form's query parameters, which the
        /// aka.ms link passes through.
        /// </summary>
        internal const string NewIssue = "https://aka.ms/xbox-cli/feedback";

        internal const string Releases = "https://aka.ms/xbox-cli/releases";
        internal const string InstallationGuide = "https://aka.ms/xbox-cli/install";

        /// <summary>
        /// The page of a release, followed by <c>/</c> and its tag. A redirect wouldn't carry the
        /// tag, so this links to GitHub directly.
        /// </summary>
        internal const string ReleaseTags = "https://github.com/microsoft/XBOX-Developer-CLI/releases/tag";

        /// <summary>
        /// The latest release's metadata, read by <c>xbox upgrade</c>. The check refuses
        /// redirects, so this links to GitHub directly.
        /// </summary>
        internal const string LatestReleaseApi =
            "https://api.github.com/repos/microsoft/XBOX-Developer-CLI/releases/latest";
    }
}
