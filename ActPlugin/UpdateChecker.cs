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
        private const string LatestReleaseUrl = "https://api.github.com/repos/EQ2-Skill-Issue/SkillIssueToolkit/releases/latest";

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

        // Returns an UpdateResult if GitHub has a newer non-draft, non-prerelease release than
        // the currently running version, or null if there's nothing newer (or nothing at all -
        // e.g. no releases have been published yet, which GitHub reports as a 404).
        public static async Task<UpdateResult> CheckForUpdateAsync(Action<string> log)
        {
            try
            {
                var response = await Http.GetAsync(LatestReleaseUrl);
                if (!response.IsSuccessStatusCode)
                {
                    // 404 is expected until the first GitHub release is published.
                    log?.Invoke("UpdateChecker: latest release request returned " + (int)response.StatusCode + " - skipping update check");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var release = JsonConvert.DeserializeObject<GitHubRelease>(json);
                if (release == null || release.Draft || release.Prerelease || string.IsNullOrWhiteSpace(release.TagName))
                {
                    return null;
                }

                var latestVersionText = release.TagName.TrimStart('v', 'V');
                if (!TryParseCoreVersion(latestVersionText, out var latestVersion))
                {
                    log?.Invoke("UpdateChecker: could not parse release tag '" + release.TagName + "' as a version - skipping");
                    return null;
                }

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
