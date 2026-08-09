using System.IO;
using Newtonsoft.Json;

namespace SkillIssueToolkit.ActPlugin
{
    public class PluginSettings
    {
        public int Port { get; set; } = 5000;
        // Daybreak's shared "example" ID is throttled 10 req/min per client IP address, not
        // a pool shared across everyone using it - fine for normal use. Register a personal
        // one only if you need more headroom, and never distribute it to others - Daybreak's
        // policy prohibits sharing a registered service ID.
        public string CensusServiceId { get; set; } = "example";

        public static PluginSettings Load(string pluginDir)
        {
            var path = Path.Combine(pluginDir, "eq2overlay-settings.json");
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return JsonConvert.DeserializeObject<PluginSettings>(json) ?? new PluginSettings();
                }
            }
            catch
            {
                // Corrupt or unreadable - fall back to defaults rather than fail plugin init
                // over a settings-file problem.
            }

            return new PluginSettings();
        }

        public void Save(string pluginDir)
        {
            try
            {
                var path = Path.Combine(pluginDir, "eq2overlay-settings.json");
                File.WriteAllText(path, JsonConvert.SerializeObject(this));
            }
            catch
            {
                // Best-effort - a failed save just means the port resets to default next load.
            }
        }
    }
}