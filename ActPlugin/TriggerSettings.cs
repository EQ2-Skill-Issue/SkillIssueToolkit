using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace SkillIssueToolkit.ActPlugin
{
    // Persists the default-triggers source URL and the set of individually disabled rules
    // (identified by "Source:Name", e.g. "Default:YouDied") so a user can turn off one
    // trigger without editing any JSON rule file by hand.
    public class TriggerSettings
    {
        public string DefaultTriggersUrl { get; set; } =
            "https://raw.githubusercontent.com/EQ2-Skill-Issue/SkillIssueToolkit/main/SkillIssueToolkit.ActPlugin/eq2overlay-triggers.default.json";

        public bool AutoUpdateDefaultTriggers { get; set; } = true;

        public List<string> DisabledTriggerKeys { get; set; } = new List<string>();

        public bool IsDisabled(string source, string name) =>
            DisabledTriggerKeys.Contains(MakeKey(source, name), StringComparer.OrdinalIgnoreCase);

        public void SetDisabled(string source, string name, bool disabled)
        {
            var key = MakeKey(source, name);
            var existing = DisabledTriggerKeys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

            if (disabled && existing == null)
            {
                DisabledTriggerKeys.Add(key);
            }
            else if (!disabled && existing != null)
            {
                DisabledTriggerKeys.Remove(existing);
            }
        }

        public static string MakeKey(string source, string name) => source + ":" + name;

        private static string SettingsPath(string pluginDir) => Path.Combine(pluginDir, "eq2overlay-trigger-settings.json");

        public static TriggerSettings Load(string pluginDir)
        {
            try
            {
                var path = SettingsPath(pluginDir);
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return JsonConvert.DeserializeObject<TriggerSettings>(json) ?? new TriggerSettings();
                }
            }
            catch
            {
                // Corrupt or unreadable - fall back to defaults rather than fail plugin init.
            }

            return new TriggerSettings();
        }

        public void Save(string pluginDir)
        {
            try
            {
                File.WriteAllText(SettingsPath(pluginDir), JsonConvert.SerializeObject(this));
            }
            catch
            {
                // Best-effort - a failed save just means these settings reset next load.
            }
        }
    }
}
