# Skill Issue Toolkit - Overlay Host (WPF + WebView2)

The piece that renders overlay pages (served by `SkillIssueToolkit.ActPlugin`) as
always-on-top, transparent windows over the game, rather than plain browser tabs.

```
SkillIssueToolkit.ActPlugin (already running, serving http://localhost:5000/)
        │
        ▼
SkillIssueToolkit.Overlay.exe  →  one MainWindow per overlay page (see App.xaml.cs)
                                      →  transparent WPF window  →  WebView2  →  page
```

## Multiple overlay windows

`App.xaml.cs`'s `Overlays` array is the single source of truth for which pages get their
own window - each entry is `(settingsKey, htmlFile, fallbackLeft, fallbackTop, ...)`. Today
that's the DPS meter (`dps-meter.html`, unkeyed settings file for backward compatibility),
notifications (`notifications.html`, `"notifications"` key), and timer bars
(`timers.html`, `"timers"` key, sourced from ACT's own native Spell Timers), each with its
own settings file. Adding another overlay page is one line in that array, not a new class.

## Per-window settings

Each window persists independently to `%AppData%\SkillIssueToolkit.Overlay\settings.json`
(the DPS overlay, unkeyed) or `settings-{key}.json` (any other overlay, e.g.
`settings-notifications.json`, `settings-timers.json`). The ACT plugin's own settings tab
edits these same files directly and this app polls for external changes every ~300ms, so a
change made in ACT takes effect within about a third of a second without restarting anything.

Per window, independently:

- **Lock to EQ2 window** - follows the game window's position at a fixed offset, useful for
  windowed play where you move the game around. Cactbot/OverlayPlugin overlays don't do
  this (fixed-position floating windows only).
- **Click-through** (`Ctrl+Alt+L` global hotkey, or the checkbox) - passes every mouse
  click straight to the game underneath, including right-clicks, so the context menu
  becomes unreachable by clicking while it's on. The hotkey is the only way back out.
- **Allow dragging** - adds a small, constant-opacity grip strip in its own reserved row
  above the content, letting you reposition the window without turning off click-through
  first. Lives in its own row instead of overlapping WebView2 - WebView2 is a native HWND
  and nothing reliably renders "on top of" it regardless of declared z-order (see
  MainWindow.xaml.cs for what didn't work before landing on this).
- **Zoom** (`Ctrl+Alt+=` / `Ctrl+Alt+-` / `Ctrl+Alt+0`, or the Zoom % field in ACT's
  settings) - uses the CSS `zoom` property against the page (not `transform: scale`, which
  doesn't reflow layout) - safe here specifically because this is always Chromium
  (WebView2), not a general public website.
- **Hide when EQ2 loses focus** - on by default, matching Cactbot/OverlayPlugin's own
  auto-hide behavior; can be turned off if you want the overlay visible regardless of what
  window currently has focus.

## Auto-sizing to content

Each overlay page reports its own actual rendered width and height (see
`reportContentSize()`/`watchContentSize()` in `common.js`) via a `ResizeObserver`, and this
app resizes the window to match - clamped between `MinContentWidth`/`MaxContentWidth` and
`MinContentHeight`/`MaxContentHeight` in `MainWindow.xaml.cs`. Without this, WebView2's
fixed-size bounds would claim mouse input across the whole rectangle even where nothing is
visibly rendered (e.g. "Waiting for combat" with zero rows), blocking clicks on the game
underneath in that dead space.

## Requirements

- The **WebView2 Runtime** needs to be installed on the machine. Most Windows 10/11 PCs
  already have it (it ships with Edge), so this is often a non-issue - but if a window
  shows blank/fails to load, this is the first thing to check.
- **SkillIssueToolkit.ActPlugin must already be running** (loaded in ACT) before or while
  this starts, since it's what serves every overlay page. If you launch this first, each
  page's own auto-reconnect logic (in `common.js`) picks up the connection once the plugin
  comes online - no need to relaunch this app in that case.

## Known limits of "Lock to EQ2 window"

- Finds the game by process name (`EverQuest2.exe`) via `Process.GetProcessesByName` -
  if EQ2 isn't running yet when you check the box, it silently does nothing until EQ2
  launches and the offset gets (re)computed on the next successful lookup.
- Minimized EQ2 windows are detected (`IsIconic`) and skipped rather than repositioning
  the overlay off-screen - but it also means the overlay won't move at all while EQ2 is
  minimized, which is expected since the focus-hide logic already hides it in that case
  (unless hide-when-unfocused has been turned off for that window).
- No handling yet for EQ2 changing display/DPI monitors mid-session - untested.

## Known rough edges, not yet solved

- **White flash on first load**: WebView2 has a documented quirk where the background
  briefly renders white before `DefaultBackgroundColor` fully takes effect. Cosmetic only,
  self-resolves after the first frame.
- **No true native detection of "is something actually covering the overlay"** - the
  focus-hide logic checks whether EQ2 (or the overlay itself) is the *foreground* window,
  not whether another window is visually overlapping the overlay's screen area. The latter
  is doable (sampling points across the overlay's rectangle via `WindowFromPoint` and
  checking what's actually topmost there) but meaningfully more complex and hasn't been
  built - the simple foreground-window check covers the common case well enough for now.
