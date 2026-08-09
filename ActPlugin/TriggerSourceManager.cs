using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SkillIssueToolkit.ActPlugin
{
    // Loads and merges the two trigger rule sources:
    //  - "Default": SkillIssueToolkit's own bundled triggers, refreshed from a remote URL on
    //    startup and cached to disk so the plugin still has rules if the fetch fails (no
    //    internet, GitHub down, etc). Never edited by hand.
    //  - "Custom": the user's own local additions, never touched by the remote fetch.
    //
    // Each loaded rule gets tagged with its Source (see TriggerRule.Source) so cooldowns,
    // the settings UI, and per-rule disable can tell same-named rules from different files
    // apart.
    public static class TriggerSourceManager
    {
        private const string DefaultRulesFileName = "eq2overlay-triggers.default.json";
        private const string CustomRulesFileName = "eq2overlay-triggers.custom.json";

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        static TriggerSourceManager()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("SkillIssueToolkit-ActPlugin");
        }

        public static string DefaultRulesPath(string pluginDir) => Path.Combine(pluginDir, DefaultRulesFileName);

        public static string CustomRulesPath(string pluginDir) => Path.Combine(pluginDir, CustomRulesFileName);

        // Fetches the default rules from settings.DefaultTriggersUrl and overwrites the
        // cached copy on disk - only if the response parses as a valid rule list, so a bad
        // fetch (network blip, malformed JSON) never clobbers the last known good cache.
        // Call this before LoadAll if AutoUpdateDefaultTriggers is on; safe to skip otherwise.
        public static async Task<bool> RefreshDefaultRulesAsync(string pluginDir, TriggerSettings settings, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(settings.DefaultTriggersUrl)) return false;

            try
            {
                var response = await Http.GetAsync(settings.DefaultTriggersUrl);
                if (!response.IsSuccessStatusCode)
                {
                    log?.Invoke("TriggerSourceManager: default trigger fetch failed with " + (int)response.StatusCode + " - keeping cached copy");
                    return false;
                }

                var json = await response.Content.ReadAsStringAsync();
                var parsed = JsonConvert.DeserializeObject<List<TriggerRule>>(json);
                if (parsed == null)
                {
                    log?.Invoke("TriggerSourceManager: default trigger fetch returned unparseable JSON - keeping cached copy");
                    return false;
                }

                File.WriteAllText(DefaultRulesPath(pluginDir), json);
                log?.Invoke("TriggerSourceManager: refreshed default triggers (" + parsed.Count + " rule(s))");
                return true;
            }
            catch (Exception ex)
            {
                log?.Invoke("TriggerSourceManager: default trigger fetch failed - keeping cached copy: " + ex.Message);
                return false;
            }
        }

        // Loads the cached default file and the local custom file, tags each rule with its
        // Source, and drops any rule the user has individually disabled in TriggerSettings.
        public static List<TriggerRule> LoadAll(string pluginDir, TriggerSettings settings, Action<string> log)
        {
            var defaultRules = LoadFile(DefaultRulesPath(pluginDir), "Default", log);
            var customRules = LoadFile(CustomRulesPath(pluginDir), "Custom", log);

            var all = defaultRules.Concat(customRules)
                .Where(r => !settings.IsDisabled(r.Source, r.Name))
                .ToList();

            log?.Invoke("TriggerSourceManager: loaded " + defaultRules.Count + " default + " + customRules.Count +
                " custom rule(s), " + all.Count + " active after disabled filtering");

            return all;
        }

        // Also returns disabled rules, tagged, for the settings UI's per-rule checkbox list -
        // LoadAll alone can't show a rule the user has already turned off.
        public static List<TriggerRule> LoadAllIncludingDisabled(string pluginDir, Action<string> log)
        {
            var defaultRules = LoadFile(DefaultRulesPath(pluginDir), "Default", log);
            var customRules = LoadFile(CustomRulesPath(pluginDir), "Custom", log);
            return defaultRules.Concat(customRules).ToList();
        }

        private static List<TriggerRule> LoadFile(string path, string source, Action<string> log)
        {
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var rules = JsonConvert.DeserializeObject<List<TriggerRule>>(json) ?? new List<TriggerRule>();
                    foreach (var rule in rules) rule.Source = source;
                    return rules;
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("TriggerSourceManager: failed to load " + path + " - " + ex.Message);
            }

            return new List<TriggerRule>();
        }
    }
}
