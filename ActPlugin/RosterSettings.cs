using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SkillIssueToolkit.ActPlugin
{
    /// <summary>
    /// A user-maintained roster mapping character name to class (e.g. "Gabriel=Paladin").
    /// ACT has no auto-detection for this - CombatantData has no class/archetype property,
    /// and CombatantData.Tags is empty for it too - so the player enters it manually.
    /// </summary>
    public class RosterSettings
    {
        // Case-insensitive on lookup - stored as typed, compared ignoring case.
        public Dictionary<string, string> CharacterClasses { get; set; } = new Dictionary<string, string>();

        public static string RosterPath(string pluginDir) => Path.Combine(pluginDir, "eq2overlay-roster.json");

        public static RosterSettings Load(string pluginDir)
        {
            try
            {
                var path = RosterPath(pluginDir);
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<RosterSettings>(json) ?? new RosterSettings();
                }
            }
            catch
            {
                // Corrupt/unreadable - fall back to an empty roster rather than fail plugin init.
            }

            return new RosterSettings();
        }

        public void Save(string pluginDir)
        {
            try
            {
                File.WriteAllText(RosterPath(pluginDir), Newtonsoft.Json.JsonConvert.SerializeObject(this));
            }
            catch
            {
                // Best-effort - a failed save just means the roster resets next load.
            }
        }

        public string LookupClass(string characterName)
        {
            foreach (var kvp in CharacterClasses)
            {
                if (string.Equals(kvp.Key, characterName, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }
            return null;
        }

        // Parses the simple "Name=Class" per-line text format used in the settings UI.
        public static RosterSettings ParseText(string text)
        {
            var roster = new RosterSettings();
            foreach (var line in (text ?? "").Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                var parts = trimmed.Split(new[] { '=' }, 2);
                if (parts.Length != 2) continue;

                var name = parts[0].Trim();
                var className = parts[1].Trim();
                if (name.Length > 0 && className.Length > 0)
                    roster.CharacterClasses[name] = className;
            }
            return roster;
        }

        // Reverses ParseText, for populating the settings textbox from a loaded roster.
        public string ToText()
        {
            return string.Join("\n", CharacterClasses.Select(kvp => kvp.Key + "=" + kvp.Value));
        }
    }
}
