using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SkillIssueToolkit.ActPlugin
{
    // Local, offline, instant class resolution from an ability/spell/combat-art name seen on
    // a combat log line - a supplement to CensusClassLookup, not a replacement. Census gives a
    // definitive class from an authoritative source but fails whenever a player has opted their
    // character out of Census participation, or when the shared rate-limited "example" service
    // ID gets throttled during busy raids. This class sidesteps both problems entirely: no
    // network call, no rate limit, works even for Census-invisible characters - at the cost of
    // only knowing whatever abilities are in eq2overlay-class-abilities.json.
    //
    // Deliberately only maps abilities/procs that are unique to ONE specific class (e.g.
    // Paladin), never anything archetype-wide (Fighter/Priest/Mage/Scout) or otherwise shared
    // across multiple classes - a shared ability would risk confidently attributing a player to
    // the wrong specific class, which is worse than not resolving them at all (Census, or simply
    // an unresolved class, can still fill the gap later).
    public class ClassAbilityLookup
    {
        private const string FileName = "eq2overlay-class-abilities.json";

        private readonly Dictionary<string, string> _abilityToClass;
        private readonly Action<string> _log;

        public ClassAbilityLookup(string pluginDir, Action<string> log)
        {
            _log = log;
            _abilityToClass = Load(pluginDir, log);
        }

        public static string DataPath(string pluginDir) => Path.Combine(pluginDir, FileName);

        private static Dictionary<string, string> Load(string pluginDir, Action<string> log)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = DataPath(pluginDir);
                if (!File.Exists(path))
                {
                    log?.Invoke("ClassAbilityLookup: " + FileName + " not found - class-specific ability matching disabled");
                    return result;
                }

                var json = File.ReadAllText(path);
                var root = JObject.Parse(json);
                foreach (var prop in root.Properties())
                {
                    // "_comment" and any other non-string-value entries are metadata, not
                    // ability names - skip anything that isn't a plain string value.
                    if (prop.Name.StartsWith("_", StringComparison.Ordinal)) continue;
                    if (prop.Value.Type != JTokenType.String) continue;

                    result[prop.Name] = prop.Value.Value<string>();
                }

                log?.Invoke("ClassAbilityLookup: loaded " + result.Count + " class-specific abilities");
            }
            catch (Exception ex)
            {
                log?.Invoke("ClassAbilityLookup: failed to load " + FileName + " - " + ex.Message);
            }

            return result;
        }

        // Never blocks, never queues anything - just a dictionary lookup. Returns null if the
        // ability isn't in the data file (unknown, or intentionally excluded because it isn't
        // class-unique).
        public string TryGetClass(string abilityName)
        {
            if (string.IsNullOrWhiteSpace(abilityName)) return null;
            return _abilityToClass.TryGetValue(abilityName, out var className) ? className : null;
        }
    }
}
