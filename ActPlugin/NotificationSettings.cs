using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace SkillIssueToolkit.ActPlugin
{
    // Persists the default-notifications source URL and the set of individually disabled
    // rules (identified by "Source:Name", e.g. "Default:YouDied") so a user can turn off one
    // notification without editing any JSON rule file by hand.
    public class NotificationSettings
    {
        public string DefaultNotificationsUrl { get; set; } =
            "https://raw.githubusercontent.com/EQ2-Skill-Issue/SkillIssueToolkit/refs/heads/main/ActPlugin/eq2overlay-notifications.default.json";

        public bool AutoUpdateDefaultNotifications { get; set; } = true;

        public List<string> DisabledNotificationKeys { get; set; } = new List<string>();

        public bool IsDisabled(string source, string name) =>
            DisabledNotificationKeys.Contains(MakeKey(source, name), StringComparer.OrdinalIgnoreCase);

        public void SetDisabled(string source, string name, bool disabled)
        {
            var key = MakeKey(source, name);
            var existing = DisabledNotificationKeys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

            if (disabled && existing == null)
            {
                DisabledNotificationKeys.Add(key);
            }
            else if (!disabled && existing != null)
            {
                DisabledNotificationKeys.Remove(existing);
            }
        }

        public static string MakeKey(string source, string name) => source + ":" + name;

        private static string SettingsPath(string pluginDir) => Path.Combine(pluginDir, "eq2overlay-notification-settings.json");

        public static NotificationSettings Load(string pluginDir)
        {
            try
            {
                var path = SettingsPath(pluginDir);
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return JsonConvert.DeserializeObject<NotificationSettings>(json) ?? new NotificationSettings();
                }
            }
            catch
            {
                // Corrupt or unreadable - fall back to defaults rather than fail plugin init.
            }

            return new NotificationSettings();
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
