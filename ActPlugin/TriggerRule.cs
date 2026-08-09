using System;
using Newtonsoft.Json;

namespace SkillIssueToolkit.ActPlugin
{
    // A single rule: if Pattern matches a raw log line, fire an alert and/or start a
    // countdown timer. Only Name and Pattern are required - everything else is optional.
    public class TriggerRule
    {
        // Which rule file this came from ("Default" or "Custom") - not read from the JSON
        // itself, set by TriggerSourceManager after loading each file. Combined with Name to
        // form a stable key for cooldown tracking and per-rule disable, so a default rule and
        // a custom rule sharing a Name don't collide.
        [JsonIgnore]
        public string Source { get; set; }

        public string Name { get; set; }
        public string Pattern { get; set; }

        // Text alert to show. Leave unset for a rule that only starts a timer.
        public string DisplayText { get; set; }

        // "Alarm" | "Alert" | "Info", case-insensitive. Falls back to "Alert" if unset or
        // unrecognized. Only matters when DisplayText is set.
        public string Severity { get; set; }

        // Length of the timer bar this rule starts, in seconds. 0 means no timer.
        public double TimerDurationSeconds { get; set; }

        // Bar label - defaults to Name if unset. Supports {groupName} interpolation same as
        // DisplayText.
        public string TimerLabel { get; set; }

        // Hex color override for the bar (e.g. "#ff4136"). Unset uses the default.
        public string TimerColor { get; set; }

        // Once the timer hits zero, keeps it visible in an overdue state (pulsing, showing
        // elapsed time) for this many extra seconds instead of disappearing instantly. 0
        // (default) disappears immediately. -1 means it never disappears on its own - only
        // clears when this same rule fires again.
        public double TimerOverdueLingerSeconds { get; set; }

        // Restricts the rule to one zone (exact match against the current zone,
        // case-insensitive). Unset means always active.
        public string Zone { get; set; }

        // Cooldown - once fired, won't fire again for this many seconds even if the pattern
        // matches again. Applies to both the text alert and the timer together. 0 means no
        // cooldown.
        public double SuppressSeconds { get; set; }

        // Delays the alert/timer by this many seconds after the match instead of firing
        // instantly. 0 means immediate. Once scheduled it always fires - no cancellation.
        public double DelaySeconds { get; set; }

        // Names a capture group from this rule's Pattern that must equal the player's own
        // character name for the rule to fire at all. Unset means no restriction.
        public string RequirePlayerMatch { get; set; }
    }

    public class TriggerFiredEvent
    {
        public string RuleName { get; set; }
        public string DisplayText { get; set; }
        public string Severity { get; set; }
        public DateTime FiredAt { get; set; }
    }

    // InstanceId is per individual timer start, not per rule - the same rule matching
    // multiple times concurrently produces multiple instances, tracked and expiring
    // independently. The overlay groups same-named instances visually.
    public class TimerStartedEvent
    {
        public string InstanceId { get; set; }
        public string RuleName { get; set; }
        public string Label { get; set; }
        public string Color { get; set; }
        public double DurationSeconds { get; set; }
        public double OverdueLingerSeconds { get; set; }
        public DateTime StartedAt { get; set; }
    }
}