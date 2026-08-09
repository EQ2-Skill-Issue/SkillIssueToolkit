using System;
using System.IO;
using System.Text.Json;

namespace SkillIssueToolkit.Overlay
{
    // Persisted to %AppData%\SkillIssueToolkit.Overlay\settings.json (or settings-{key}.json for a
    // named overlay instance). Left/Top are an absolute-position fallback used before EQ2
    // is running; LockOffsetX/Y are the position relative to the EQ2 window's top-left
    // corner, used to restore "locked" placement wherever the game window is.
    //
    // An empty/null key maps to the unkeyed "settings.json" path, matching what
    // OverlayHostSettings.cs on the plugin side expects for the DPS overlay; a named
    // instance (e.g. "triggers") gets its own file.
    public class OverlaySettings
    {
        public double Left { get; set; } = 40;
        public double Top { get; set; } = 40;
        public bool LockToWindow { get; set; }
        public double LockOffsetX { get; set; }
        public double LockOffsetY { get; set; }
        public bool ClickThrough { get; set; }
        public bool AllowDragging { get; set; }
        public double ZoomFactor { get; set; } = 1.0;
        public bool HideWhenUnfocused { get; set; } = false;
        public double DragHandleHeight { get; set; } = 12;
        public bool ShowPreview { get; set; }
        public bool Enabled { get; set; } = true;

        public static string SettingsPath(string key) => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SkillIssueToolkit.Overlay",
            string.IsNullOrEmpty(key) ? "settings.json" : $"settings-{key}.json");

        public static OverlaySettings Load(string key)
        {
            try
            {
                var path = SettingsPath(key);
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<OverlaySettings>(json) ?? new OverlaySettings();
                }
            }
            catch
            {
                // Corrupt or unreadable settings file - fall back to defaults rather than
                // crash the app over a saved-preferences problem.
            }

            return new OverlaySettings();
        }

        public void Save(string key)
        {
            try
            {
                var path = SettingsPath(key);
                var dir = Path.GetDirectoryName(path);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(this));
            }
            catch
            {
                // Best-effort - a failed save just means position/settings reset next launch,
                // not worth taking down the overlay over.
            }
        }
    }
}