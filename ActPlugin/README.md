# Skill Issue Toolkit - ACT Plugin

A combat overlay, trigger/alert system, and countdown timers for EverQuest 2, built on
Advanced Combat Tracker (ACT).

```
EQ2 game
   │
   ▼
ACT + EQ2 parsing plugin
   ▼
SkillIssueToolkit.ActPlugin (this project) - reads ACT's combat data, runs an HTTP+WebSocket server
   ▼
SkillIssueToolkit.Overlay.exe - one window per overlay page
   │
   ├── dps-meter.html
   ├── triggers.html
   └── timers.html
```

*[screenshot: the ACT Plugins tab showing this plugin loaded and running]*

## Setup

1. In `SkillIssueToolkit.ActPlugin.csproj`, point the `<HintPath>` under
   `<Reference Include="Advanced Combat Tracker">` at your own `Advanced Combat Tracker.exe`.
2. Build both projects (`../publish.ps1` builds and packages both in one step).
3. In ACT: Plugins tab → Browse → select `SkillIssueToolkit.ActPlugin.dll` → Add/Enable Plugin.
4. The overlay windows appear automatically. `eq2overlay-debug.log` (next to the plugin DLL)
   has diagnostic output if something doesn't come up.

## DPS Meter

Per-encounter breakdown for every ally in the fight: damage, DPS, healing, HPS, power
fed/drained, max hit, cures, and deaths. Click a column header to sort by it.

A pill in the header switches between **Encounter** mode (resets at the start of each new
fight) and **Zone** mode (accumulates across every fight since the last zone change).

*[screenshot: DPS meter with rows populated]*

## Automatic class detection

Class icons are resolved automatically via Daybreak's Census API. The server/world is
read from ACT's own active log file path, so no configuration is required for this to work.

- Results are cached in memory for the current plugin session only, never written to disk -
  a session restart is the only way the cache clears.
- Lookups are queued and rate-limited to 10 per minute (matching Census's own published
  limit for its shared "example" service ID), using a rolling 60-second window.
- A failed lookup is retried later rather than dropped. A confirmed absence of data for a
  name is cached so that name isn't queried again this session.
- For a raid where nobody has been looked up yet this session, expect up to about two
  minutes for everyone's class to resolve in the worst case, since lookups begin the moment
  someone appears in combat rather than when they join the group. Every fight after the
  first is instant, since results are already cached.
- A personal Census service ID can be set in the settings tab for higher throughput. Do not
  share a personal service ID with anyone else - Census's own policy prohibits it.

## Trigger alerts

Regex-matched log-line alerts in three severity tiers: **Alarm** (large, red), **Alert**
(medium, gold), **Info** (small, green).

*[screenshot: triggers.html showing one or more severity tiers]*

Rules come from two files, both siblings of the plugin DLL (not inside `Overlays/`):

- `eq2overlay-triggers.default.json` - SkillIssueToolkit's own bundled triggers. Auto-updated
  from GitHub on startup (and whenever you click **Check for Trigger Updates**), so a guild
  or group using this plugin picks up new/changed triggers without anyone redistributing
  files. Not meant to be hand-edited - it gets overwritten on every successful refresh. Turn
  this off in the settings tab if you'd rather manage it yourself.
- `eq2overlay-triggers.custom.json` - your own additions. Never touched by the auto-update;
  edit this one directly for anything personal or guild-specific that isn't in the default set.

Every rule is tagged internally with which file it came from (`Default` or `Custom`), which
also means a default rule and a custom rule can share a `Name` without their cooldowns
colliding. Fields:

| Field | Purpose |
|---|---|
| `Pattern` | Regex matched against every raw log line, chat included |
| `DisplayText` | The alert text. `{groupName}` inserts a named capture group's matched value |
| `Severity` | `Alarm` \| `Alert` \| `Info` (case-insensitive; defaults to `Alert` if missing or invalid) |
| `Zone` | Restricts the rule to one zone, by exact match against the current zone. Unset = always active |
| `SuppressSeconds` | Cooldown - the rule won't fire again for this many seconds |
| `DelaySeconds` | Fires this many seconds after the match instead of instantly |
| `RequirePlayerMatch` | Names a capture group that must equal your own character name for the rule to fire |

After editing the custom rules file, click **Reload From Disk** in the settings tab (or
restart ACT) to apply the changes. The settings tab also lists every loaded trigger,
regardless of which file it came from, with a checkbox to disable any individual one
without editing JSON.

`http://localhost:5000/test.html` fires any line - typed or from a set of grouped samples -
through the live matching engine, for testing without needing to reproduce something in-game.

## Timer bars

Countdown bars for recurring abilities. A rule becomes a timer by setting a duration,
independent of whether it also shows a text alert. Multiple concurrent instances of the
same named timer collapse into one bar showing the soonest-to-expire time and a count
("x2"), sorted soonest-first.

*[screenshot: timers.html with a couple of bars]*

| Field | Purpose |
|---|---|
| `TimerDurationSeconds` | Length of the countdown |
| `TimerLabel` | Bar text - defaults to the rule's `Name` if unset. Supports `{groupName}` interpolation |
| `TimerColor` | Hex color override for the bar. Unset uses the default color |
| `TimerOverdueLingerSeconds` | Keeps the bar visible (pulsing, showing elapsed overdue time) for this many extra seconds after it hits zero, instead of disappearing instantly. **-1** keeps it visible indefinitely, until that same timer starts again |

All active bars clear automatically when combat ends, and can be cleared manually from
`test.html`.

## Settings tab

One labeled group per overlay, in ACT's own settings UI for this plugin.

*[screenshot: the settings tab]*

| Setting | Effect |
|---|---|
| Enable this overlay | Shows/hides the overlay's window. Live in both directions - no relaunch needed |
| Lock to EQ2 window | Overlay follows the game window's position |
| Click-through | Mouse clicks pass through to the game (`Ctrl+Alt+L` also toggles this for the DPS meter) |
| Allow dragging | Adds a small grip strip for repositioning; height is adjustable |
| Hide when EQ2 loses focus | Auto-hides on alt-tab |
| Zoom | Resizes the overlay's content (`Ctrl+Alt+=` / `Ctrl+Alt+-` / `Ctrl+Alt+0` also work) |
| Show preview content | Shows placeholder content even when nothing's active, for positioning an otherwise-invisible overlay |

`Relaunch Overlay` restarts the overlay process entirely. `Close Overlay` closes it without
restarting.

## Files

- `SkillIssueToolkit.ActPlugin.csproj` - the plugin project (targets net48)
- `Plugin.cs` - `IActPluginV1` entry point, settings UI
- `Models.cs` - DPS meter JSON shape
- `OverlayServer.cs` - HTTP static file serving, WebSocket broadcast, `/test-line`, `/clear-timers`
- `TriggerRule.cs` / `TriggerEngine.cs` - trigger/timer rule model and matching engine
- `TriggerSourceManager.cs` - loads/merges the default (remote) and custom (local) rule files
- `TriggerSettings.cs` - default-triggers URL, auto-update toggle, per-rule disable list
- `CensusClassLookup.cs` - Census lookup queue
- `PluginSettings.cs` - plugin-wide settings (port, Census service ID)
- `OverlayHostSettings.cs` - per-overlay settings
- `Overlays/dps-meter.html`, `Overlays/triggers.html`, `Overlays/timers.html` - overlay pages
- `Overlays/test.html` - trigger/timer tester
- `Overlays/common.js` / `Overlays/common.css` - shared overlay code and theme
- `eq2overlay-triggers.default.json` - bundled/auto-updated trigger rules
- `eq2overlay-triggers.custom.json` - your own local trigger rules