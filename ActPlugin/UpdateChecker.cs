using System;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SkillIssueToolkit.ActPlugin
{
    // Checks GitHub Releases for a newer version of the plugin than the one currently loaded.
    // Read-only - never downloads or installs anything, just reports what it finds so the
    // settings UI can show a "new version available" link.
    public static class UpdateChecker
    {
        // GitHub's own /releases/latest only ever returns the newest release with
        // prerelease == false - if every published release is flagged prerelease, that
        // endpoint 404s outright (documented GitHub behavior, not a bug in how it's called
        // here). Using the plain list endpoint instead and picking the highest-versioned
        // non-draft release ourselves works regardless of whether releases are marked as
        // "Latest" on GitHub.
        private const string ReleasesListUrl = "https://api.github.com/repos/EQ2-Skill-Issue/SkillIssueToolkit/releases";

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        static UpdateChecker()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("SkillIssueToolkit-ActPlugin");
            Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        public class UpdateResult
        {
            public string LatestVersion;
            public string HtmlUrl;
        }

        private class GitHubRelease
        {
            [JsonProperty("tag_name")]
            public string TagName;

            [JsonProperty("html_url")]
            public string HtmlUrl;

            [JsonProperty("draft")]
            public bool Draft;

            [JsonProperty("prerelease")]
            public bool Prerelease;
        }

        // Returns the current assembly's informational/file version, e.g. "1.0.0" - set via
        // <Version> in the csproj.
        public static string GetCurrentVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var infoAttr = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (!string.IsNullOrWhiteSpace(infoAttr?.InformationalVersion))
            {
                return infoAttr.InformationalVersion;
            }

            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        // Returns an UpdateResult if GitHub has a newer non-draft release than the currently
        // running version, or null if there's nothing newer (or nothing at all - e.g. no
        // releases have been published yet, which GitHub reports as a 404). Prereleases are
        // included in the comparison since this repo currently publishes every release as a
        // prerelease - excluding them here would mean update checks never find anything.
        public static async Task<UpdateResult> CheckForUpdateAsync(Action<string> log)
        {
            try
            {
                var response = await Http.GetAsync(ReleasesListUrl);
                if (!response.IsSuccessStatusCode)
                {
                    // 404 is expected until the first GitHub release is published.
                    log?.Invoke("UpdateChecker: releases list request returned " + (int)response.StatusCode + " - skipping update check");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var releases = JsonConvert.DeserializeObject<GitHubRelease[]>(json);
                if (releases == null || releases.Length == 0)
                {
                    return null;
                }

                GitHubRelease release = null;
                Version releaseVersion = null;
                foreach (var candidate in releases)
                {
                    if (candidate.Draft || string.IsNullOrWhiteSpace(candidate.TagName))
                    {
                        continue;
                    }

                    if (!TryParseCoreVersion(candidate.TagName.TrimStart('v', 'V'), out var candidateVersion))
                    {
                        continue;
                    }

                    if (release == null || candidateVersion > releaseVersion)
                    {
                        release = candidate;
                        releaseVersion = candidateVersion;
                    }
                }

                if (release == null)
                {
                    return null;
                }

                var latestVersionText = release.TagName.TrimStart('v', 'V');
                var latestVersion = releaseVersion;

                var currentVersionText = GetCurrentVersion();
                if (!TryParseCoreVersion(currentVersionText, out var currentVersion))
                {
                    log?.Invoke("UpdateChecker: could not parse current version '" + currentVersionText + "' - skipping");
                    return null;
                }

                if (latestVersion <= currentVersion)
                {
                    log?.Invoke("UpdateChecker: running latest version (" + currentVersionText + ")");
                    return null;
                }

                log?.Invoke("UpdateChecker: newer version available - " + latestVersionText + " (current: " + currentVersionText + ")");
                return new UpdateResult { LatestVersion = latestVersionText, HtmlUrl = release.HtmlUrl };
            }
            catch (Exception ex)
            {
                log?.Invoke("UpdateChecker: update check failed - " + ex.Message);
                return null;
            }
        }

        // System.Version has no concept of SemVer pre-release suffixes (e.g. "-beta.1"), so
        // strip everything from the first '-' onward before parsing. Fine for comparison
        // purposes here since pre-release/draft releases are already filtered out above -
        // this only ever needs to compare stable X.Y.Z tags against the current version's
        // core X.Y.Z, even while the current build is itself still a beta.
        private static bool TryParseCoreVersion(string versionText, out Version version)
        {
            var dashIndex = versionText.IndexOf('-');
            var coreVersionText = dashIndex >= 0 ? versionText.Substring(0, dashIndex) : versionText;
            return Version.TryParse(coreVersionText, out version);
        }
    }
}
