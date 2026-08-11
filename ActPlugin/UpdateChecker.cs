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
                SemVer releaseVersion = null;
                foreach (var candidate in releases)
                {
                    if (candidate.Draft || string.IsNullOrWhiteSpace(candidate.TagName))
                    {
                        continue;
                    }

                    if (!SemVer.TryParse(candidate.TagName.TrimStart('v', 'V'), out var candidateVersion))
                    {
                        continue;
                    }

                    if (release == null || candidateVersion.CompareTo(releaseVersion) > 0)
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
                if (!SemVer.TryParse(currentVersionText, out var currentVersion))
                {
                    log?.Invoke("UpdateChecker: could not parse current version '" + currentVersionText + "' - skipping");
                    return null;
                }

                if (latestVersion.CompareTo(currentVersion) <= 0)
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

        // Minimal SemVer 2.0 comparer - just enough for this repo's own X.Y.Z-beta.N tags.
        // System.Version can't represent a prerelease suffix at all, which is why the previous
        // version of this check stripped "-beta.N" entirely before comparing: that made every
        // beta of the same X.Y.Z core compare as equal, so "1.0.0-beta.3" (current) vs
        // "1.0.0-beta.4" (latest on GitHub) were never actually recognized as different
        // versions. Build metadata (a "+..." suffix, e.g. from the current assembly's git
        // commit hash) is parsed but explicitly ignored in comparisons per the SemVer spec -
        // it never affects precedence.
        private sealed class SemVer : IComparable<SemVer>
        {
            private readonly Version _core;
            private readonly string[] _prerelease; // empty array = no prerelease (a full release)

            private SemVer(Version core, string[] prerelease)
            {
                _core = core;
                _prerelease = prerelease;
            }

            public static bool TryParse(string versionText, out SemVer version)
            {
                version = null;
                if (string.IsNullOrWhiteSpace(versionText)) return false;

                // Strip build metadata ("+...") first - it has no bearing on precedence at all.
                var plusIndex = versionText.IndexOf('+');
                var withoutMetadata = plusIndex >= 0 ? versionText.Substring(0, plusIndex) : versionText;

                var dashIndex = withoutMetadata.IndexOf('-');
                var coreText = dashIndex >= 0 ? withoutMetadata.Substring(0, dashIndex) : withoutMetadata;
                var prereleaseText = dashIndex >= 0 ? withoutMetadata.Substring(dashIndex + 1) : null;

                if (!Version.TryParse(coreText, out var core)) return false;

                var prerelease = string.IsNullOrEmpty(prereleaseText)
                    ? Array.Empty<string>()
                    : prereleaseText.Split('.');

                version = new SemVer(core, prerelease);
                return true;
            }

            public int CompareTo(SemVer other)
            {
                var coreCompare = _core.CompareTo(other._core);
                if (coreCompare != 0) return coreCompare;

                // Per SemVer: a version with no prerelease outranks one with a prerelease of
                // the same core (e.g. "1.0.0" > "1.0.0-beta.4").
                if (_prerelease.Length == 0 && other._prerelease.Length == 0) return 0;
                if (_prerelease.Length == 0) return 1;
                if (other._prerelease.Length == 0) return -1;

                var count = Math.Max(_prerelease.Length, other._prerelease.Length);
                for (var i = 0; i < count; i++)
                {
                    if (i >= _prerelease.Length) return -1; // fewer identifiers = lower precedence
                    if (i >= other._prerelease.Length) return 1;

                    var a = _prerelease[i];
                    var b = other._prerelease[i];

                    var aIsNumeric = int.TryParse(a, out var aNum);
                    var bIsNumeric = int.TryParse(b, out var bNum);

                    int identifierCompare;
                    if (aIsNumeric && bIsNumeric)
                    {
                        identifierCompare = aNum.CompareTo(bNum);
                    }
                    else if (aIsNumeric != bIsNumeric)
                    {
                        // Numeric identifiers always have lower precedence than alphanumeric ones.
                        identifierCompare = aIsNumeric ? -1 : 1;
                    }
                    else
                    {
                        identifierCompare = string.CompareOrdinal(a, b);
                    }

                    if (identifierCompare != 0) return identifierCompare;
                }

                return 0;
            }
        }
    }
}

