using System;
using Newtonsoft.Json;

namespace SkillIssueToolkit.ActPlugin
{
    // A single rule: if Pattern matches a raw log line, fire a text alert. Only Name and
    // Pattern are required - everything else is optional. Countdown timers are no longer
    // authored here - those are rendered from ACT's own native Spell Timers (see
    // AlarmTimerBridge) instead of a custom timer engine.
    public class NotificationRule
    {
        // Which rule file this came from ("Default" or "Custom") - not read from the JSON
        // itself, set by NotificationSourceManager after loading each file. Combined with
        // Name to form a stable key for cooldown tracking and per-rule disable, so a default
        // rule and a custom rule sharing a Name don't collide.
        [JsonIgnore]
        public string Source { get; set; }

        public string Name { get; set; }
        public string Pattern { get; set; }

        // Text alert to show.
        public string DisplayText { get; set; }

        // "Alarm" | "Alert" | "Info", case-insensitive. Falls back to "Alert" if unset or
        // unrecognized. Only matters when DisplayText is set.
        public string Severity { get; set; }

        // Restricts the rule to one zone (exact match against the current zone,
        // case-insensitive). Unset means always active.
        public string Zone { get; set; }

        // Cooldown - once fired, won't fire again for this many seconds even if the pattern
        // matches again. 0 means no cooldown.
        public double SuppressSeconds { get; set; }

        // Delays the alert by this many seconds after the match instead of firing instantly.
        // 0 means immediate. Once scheduled it always fires - no cancellation.
        public double DelaySeconds { get; set; }

        // Names a capture group from this rule's Pattern that must equal the player's own
        // character name for the rule to fire at all. Unset means no restriction.
        public string RequirePlayerMatch { get; set; }
    }

    public class NotificationFiredEvent
    {
        public string RuleName { get; set; }
        public string DisplayText { get; set; }
        public string Severity { get; set; }
        public DateTime FiredAt { get; set; }
    }
}
