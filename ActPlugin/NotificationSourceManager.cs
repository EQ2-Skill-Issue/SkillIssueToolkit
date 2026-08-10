using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SkillIssueToolkit.ActPlugin
{
    // Loads and merges the two notification rule sources:
    //  - "Default": SkillIssueToolkit's own bundled notifications, refreshed from a remote
    //    URL on startup and cached to disk so the plugin still has rules if the fetch fails
    //    (no internet, GitHub down, etc). Never edited by hand.
    //  - "Custom": the user's own local additions, never touched by the remote fetch.
    //
    // Each loaded rule gets tagged with its Source (see NotificationRule.Source) so
    // cooldowns, the settings UI, and per-rule disable can tell same-named rules from
    // different files apart.
    public static class NotificationSourceManager
    {
        private const string DefaultRulesFileName = "eq2overlay-notifications.default.json";
        private const string CustomRulesFileName = "eq2overlay-notifications.custom.json";

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        static NotificationSourceManager()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("SkillIssueToolkit-ActPlugin");
        }

        public static string DefaultRulesPath(string pluginDir) => Path.Combine(pluginDir, DefaultRulesFileName);

        public static string CustomRulesPath(string pluginDir) => Path.Combine(pluginDir, CustomRulesFileName);

        // Fetches the default rules from settings.DefaultNotificationsUrl and overwrites the
        // cached copy on disk - only if the response parses as a valid rule list, so a bad
        // fetch (network blip, malformed JSON) never clobbers the last known good cache.
        // Call this before LoadAll if AutoUpdateDefaultNotifications is on; safe to skip otherwise.
        public static async Task<bool> RefreshDefaultRulesAsync(string pluginDir, NotificationSettings settings, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(settings.DefaultNotificationsUrl)) return false;

            try
            {
                var response = await Http.GetAsync(settings.DefaultNotificationsUrl);
                if (!response.IsSuccessStatusCode)
                {
                    log?.Invoke("NotificationSourceManager: default notification fetch failed with " + (int)response.StatusCode + " - keeping cached copy");
                    return false;
                }

                var json = await response.Content.ReadAsStringAsync();
                var parsed = JsonConvert.DeserializeObject<List<NotificationRule>>(json);
                if (parsed == null)
                {
                    log?.Invoke("NotificationSourceManager: default notification fetch returned unparseable JSON - keeping cached copy");
                    return false;
                }

                File.WriteAllText(DefaultRulesPath(pluginDir), json);
                log?.Invoke("NotificationSourceManager: refreshed default notifications (" + parsed.Count + " rule(s))");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke("NotificationSourceManager: default notification fetch failed - keeping cached copy: " + ex.Message);
                return false;
            }
        }

        // Loads the cached default file and the local custom file, tags each rule with its
        // Source, and drops any rule the user has individually disabled in NotificationSettings.
        public static List<NotificationRule> LoadAll(string pluginDir, NotificationSettings settings, Action<string> log)
        {
            var defaultRules = LoadFile(DefaultRulesPath(pluginDir), "Default", log);
            var customRules = LoadFile(CustomRulesPath(pluginDir), "Custom", log);

            var all = defaultRules.Concat(customRules)
                .Where(r => !settings.IsDisabled(r.Source, r.Name))
                .ToList();

            log?.Invoke("NotificationSourceManager: loaded " + defaultRules.Count + " default + " + customRules.Count +
                " custom rule(s), " + all.Count + " active after disabled filtering");

            return all;
        }

        // Also returns disabled rules, tagged, for the settings UI's per-rule checkbox list -
        // LoadAll alone can't show a rule the user has already turned off.
        public static List<NotificationRule> LoadAllIncludingDisabled(string pluginDir, Action<string> log)
        {
            var defaultRules = LoadFile(DefaultRulesPath(pluginDir), "Default", log);
            var customRules = LoadFile(CustomRulesPath(pluginDir), "Custom", log);
            return defaultRules.Concat(customRules).ToList();
        }

        private static List<NotificationRule> LoadFile(string path, string source, Action<string> log)
        {
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var rules = JsonConvert.DeserializeObject<List<NotificationRule>>(json) ?? new List<NotificationRule>();
                    foreach (var rule in rules) rule.Source = source;
                    return rules;
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("NotificationSourceManager: failed to load " + path + " - " + ex.Message);
            }

            return new List<NotificationRule>();
        }
    }
}
