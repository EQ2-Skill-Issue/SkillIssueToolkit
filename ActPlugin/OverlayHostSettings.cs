using System;
using System.IO;
using Newtonsoft.Json;

namespace SkillIssueToolkit.ActPlugin
{
    // Same shape as SkillIssueToolkit.Overlay's OverlaySettings class, duplicated rather than
    // shared since these are separate assemblies (this one targets net48 for ACT, the
    // overlay host targets net10.0-windows).
    //
    // This plugin only sets LockToWindow/ClickThrough/AllowDragging - it leaves
    // Left/Top/LockOffsetX/Y alone since only SkillIssueToolkit.Overlay knows its own window
    // position (see its CheckForExternalSettingsChanges).
    //
    // An empty/null key maps to the unkeyed "settings.json" path (the DPS overlay); a named
    // key (e.g. "notifications") maps to that overlay's own file.
    public class OverlayHostSettings
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

        public static OverlayHostSettings Load(string key)
        {
            try
            {
                var path = SettingsPath(key);
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return JsonConvert.DeserializeObject<OverlayHostSettings>(json) ?? new OverlayHostSettings();
                }
            }
            catch
            {
                // Corrupt/unreadable, or SkillIssueToolkit.Overlay has never run yet - fall back to
                // defaults rather than fail the plugin's settings UI over it.
            }

            return new OverlayHostSettings();
        }

        public void Save(string key)
        {
            try
            {
                var path = SettingsPath(key);
                var dir = Path.GetDirectoryName(path);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonConvert.SerializeObject(this));
            }
            catch
            {
                // Best-effort - a failed save just means the toggle didn't take effect.
            }
        }
    }
}