using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Advanced_Combat_Tracker;

namespace SkillIssueToolkit.ActPlugin
{
    public class Plugin : IActPluginV1
    {
        private Label _statusLabel;
        private OverlayServer _server;
        private string _logFilePath;
        private bool _hasDumpedTags;
        private EncounterData _lastEncounter;
        private string _currentEnemyName;

        // Zonewide totals persist across encounter resets, clearing only on a zone change.
        // _encounterContributed tracks what's already been folded into _zonewideTotals per
        // encounter, so each tick adds only the delta.
        private string _lastSeenZone;
        private DateTime _zoneStartTime = DateTime.Now;
        private readonly Dictionary<string, RunningTotals> _zonewideTotals = new Dictionary<string, RunningTotals>();
        private readonly Dictionary<string, RunningTotals> _encounterContributed = new Dictionary<string, RunningTotals>();

        private class RunningTotals
        {
            public long Damage;
            public long Healing;
            public long DamageTaken;
            public long PowerFed;
            public long PowerDrain;
            public int Cures;
            public int Deaths;
        }
        private PluginSettings _settings;
        private CensusClassLookup _censusLookup;
        private bool _hasLoggedGroupDiagnostic;
        private TriggerEngine _triggerEngine;
        private TriggerSettings _triggerSettings;
        private string _pluginDir;
        private string _overlaysRoot;
        private Label _urlLabel;
        private NumericUpDown _portInput;
        private LinkLabel _updateLabel;

        public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
        {
            _statusLabel = pluginStatusText;

            // ACT loads plugins via Assembly.Load(byte[]), so Assembly.Location is empty here.
            // PluginGetSelfData is the reliable way to get the plugin's own folder.
            var selfData = ActGlobals.oFormActMain.PluginGetSelfData(this);
            var pluginDir = selfData.pluginFile.DirectoryName;

            // Same reason dependency probing fails for Newtonsoft.Json.dll next to the plugin.
            // Register before anything that might trigger a dependency load.
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => ResolveDependency(args, pluginDir);

            _overlaysRoot = Path.Combine(pluginDir, "Overlays");
            _pluginDir = pluginDir;

            _logFilePath = Path.Combine(pluginDir, "eq2overlay-debug.log");
            Log("InitPlugin starting");

            _settings = PluginSettings.Load(pluginDir);
            BuildSettingsUi(pluginScreenSpace, pluginDir);

            StartServer(_settings.Port);
            LaunchOverlayHostIfNotRunning(pluginDir);
            _triggerSettings = TriggerSettings.Load(pluginDir);
            _ = StartTriggerEngineAsync(pluginDir);
            _ = CheckForUpdateAsync();

            ActGlobals.oFormActMain.AfterCombatAction += OnAfterCombatAction;
            ActGlobals.oFormActMain.OnCombatEnd += OnCombatEnd;

            var world = CensusClassLookup.DetectWorldFromLogPath(ActGlobals.oFormActMain.LogFilePath);
            _censusLookup = new CensusClassLookup(_settings.CensusServiceId, world, Log);
            Log("Census lookup ready - detected world: " + (world ?? "(none - LogFilePath not available yet)"));

            Log("InitPlugin complete - listening on port " + _settings.Port);
        }

        // Checks GitHub Releases for a newer version and, if one exists, updates the settings
        // tab with a clickable link. Silent (log-only) when there's nothing newer or no
        // releases have been published yet - this must never bother the user with a popup.
        private async System.Threading.Tasks.Task CheckForUpdateAsync()
        {
            var update = await UpdateChecker.CheckForUpdateAsync(Log);
            if (update == null) return;

            if (_updateLabel != null)
            {
                _updateLabel.Text = "Update available: v" + update.LatestVersion + " - view release page";
                _updateLabel.Links.Clear();
                _updateLabel.Links.Add(0, _updateLabel.Text.Length, update.HtmlUrl);
                _updateLabel.Visible = true;
            }

            ShowUpdateTrayNotification(update);
        }

        // Surfaces the update via ACT's own main-window tray slider notification (the same
        // mechanism ACT itself uses for things like update/download notices). Its single
        // button just opens the GitHub release page in the browser - never downloads or
        // installs anything automatically.
        private void ShowUpdateTrayNotification(UpdateChecker.UpdateResult update)
        {
            try
            {
                var slider = ActGlobals.oFormActMain.TraySliderAdd(
                    "Version " + update.LatestVersion + " is available.",
                    "Skill Issue Toolkit update available");
                if (slider == null) return;

                slider.ButtonOK.Text = "View Release";
                slider.ButtonOK.Click += (s, e) =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(update.HtmlUrl) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        Log("Failed to open update link from tray notification: " + ex);
                    }
                };
            }
            catch (Exception ex)
            {
                Log("Failed to show update tray notification: " + ex);
            }
        }

        // Refreshes the cached default triggers from the remote URL (if enabled), then loads
        // and merges default + custom rules and (re)starts the engine on the result. Awaited
        // fire-and-forget from InitPlugin/the Reload button - never blocks plugin startup on
        // network access, since LoadAll falls back to whatever's already cached on disk.
        private async System.Threading.Tasks.Task StartTriggerEngineAsync(string pluginDir)
        {
            if (_triggerSettings.AutoUpdateDefaultTriggers)
            {
                await TriggerSourceManager.RefreshDefaultRulesAsync(pluginDir, _triggerSettings, Log);
            }

            var rules = TriggerSourceManager.LoadAll(pluginDir, _triggerSettings, Log);
            _triggerEngine?.Unsubscribe();
            _triggerEngine = new TriggerEngine(_server, rules, Log);
            _server.OnTestLine = _triggerEngine.EvaluateLine;
            Log("TriggerEngine started with " + rules.Count + " rule(s)");
        }

        // Skips launching if an instance is already running, so reloading the plugin doesn't
        // spawn a second overlay window. Use RelaunchOverlayHost() to force a fresh instance.
        private void LaunchOverlayHostIfNotRunning(string pluginDir)
        {
            if (Process.GetProcessesByName("SkillIssueToolkit.Overlay").Length > 0)
            {
                Log("SkillIssueToolkit.Overlay.exe already running - not launching a second instance");
                return;
            }

            LaunchOverlayHost(pluginDir);
        }

        // Kills any existing instance first, then launches fresh - for the "Relaunch Overlay"
        // settings button, when the overlay is running but stuck or misbehaving.
        private void RelaunchOverlayHost(string pluginDir)
        {
            foreach (var proc in Process.GetProcessesByName("SkillIssueToolkit.Overlay"))
            {
                try
                {
                    proc.Kill();
                    proc.WaitForExit(3000);
                }
                catch (Exception ex)
                {
                    Log("Failed to stop existing SkillIssueToolkit.Overlay.exe instance: " + ex);
                }
                finally
                {
                    proc.Dispose();
                }
            }

            LaunchOverlayHost(pluginDir);
        }

        private void LaunchOverlayHost(string pluginDir)
        {
            try
            {
                var exePath = Path.Combine(pluginDir, "Overlay", "SkillIssueToolkit.Overlay.exe");
                if (!File.Exists(exePath))
                {
                    Log("SkillIssueToolkit.Overlay.exe not found at " + exePath + " - overlay window won't start");
                    return;
                }

                Process.Start(new ProcessStartInfo(exePath)
                {
                    WorkingDirectory = Path.GetDirectoryName(exePath),
                    UseShellExecute = true
                });
                Log("Launched SkillIssueToolkit.Overlay.exe");
            }
            catch (Exception ex)
            {
                Log("Failed to launch SkillIssueToolkit.Overlay.exe: " + ex);
            }
        }

        // One entry per overlay that has settings controllable from here.
        private static readonly (string Key, string DisplayName)[] KnownOverlays =
        {
            ("", "DPS Meter"),
            ("triggers", "Triggers"),
            ("timers", "Timers"),
        };

        // Builds the settings panel in the plugin's tab under Plugins -> SkillIssueToolkit.ActPlugin.dll.
        // Uses a running Y cursor so adding/removing a section doesn't require renumbering.
        private void BuildSettingsUi(TabPage tab, string pluginDir)
        {
            tab.AutoScroll = true;
            const int lineHeight = 25;
            var y = 10;

            var title = new Label
            {
                Text = "Skill Issue Toolkit",
                Font = new System.Drawing.Font(tab.Font.FontFamily, 10, System.Drawing.FontStyle.Bold),
                Location = new System.Drawing.Point(10, y),
                AutoSize = true
            };
            tab.Controls.Add(title);
            y += 30;

            _updateLabel = new LinkLabel
            {
                Text = string.Empty,
                Location = new System.Drawing.Point(10, y),
                AutoSize = true,
                Visible = false
            };
            _updateLabel.LinkClicked += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(e.Link.LinkData.ToString()) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Log("Failed to open update link: " + ex);
                }
            };
            tab.Controls.Add(_updateLabel);
            y += lineHeight - 5;

            var portLabel = new Label { Text = "Port:", Location = new System.Drawing.Point(10, y + 3), AutoSize = true };
            _portInput = new NumericUpDown
            {
                Minimum = 1024,
                Maximum = 65535,
                Value = _settings.Port,
                Location = new System.Drawing.Point(55, y),
                Width = 80
            };
            var applyButton = new Button { Text = "Apply && Restart Server", Location = new System.Drawing.Point(150, y - 2), AutoSize = true };
            applyButton.Click += (s, e) =>
            {
                var newPort = (int)_portInput.Value;
                _settings.Port = newPort;
                _settings.Save(pluginDir);
                StartServer(newPort);
                Log("Server restarted on port " + newPort + " via settings UI");
            };
            tab.Controls.Add(portLabel);
            tab.Controls.Add(_portInput);
            tab.Controls.Add(applyButton);
            y += lineHeight + 12;

            var censusLabel = new Label { Text = "Census Service ID (for class auto-lookup):", Location = new System.Drawing.Point(10, y), AutoSize = true };
            tab.Controls.Add(censusLabel);
            y += lineHeight - 2;

            var censusInput = new TextBox
            {
                Text = _settings.CensusServiceId,
                Location = new System.Drawing.Point(10, y),
                Width = 200
            };
            censusInput.TextChanged += (s, e) =>
            {
                _settings.CensusServiceId = string.IsNullOrWhiteSpace(censusInput.Text) ? "example" : censusInput.Text.Trim();
                _settings.Save(pluginDir);
                // Rebuilds the lookup with the new ID and whatever world was already
                // detected - cheap enough to just do outright rather than track a dirty flag.
                // Dispose the old one first - it owns a live timer that would otherwise keep
                // pumping (and hitting Census with the stale ID) after being discarded.
                _censusLookup?.Dispose();
                var world = CensusClassLookup.DetectWorldFromLogPath(ActGlobals.oFormActMain.LogFilePath);
                _censusLookup = new CensusClassLookup(_settings.CensusServiceId, world, Log);
            };
            tab.Controls.Add(censusInput);
            y += lineHeight - 3;

            var censusHintLabel = new Label
            {
                Text = "\"example\" is throttled per-IP (fine for normal use) - only register your own if you personally need more, and don't share it",
                Location = new System.Drawing.Point(10, y),
                AutoSize = true,
                ForeColor = System.Drawing.Color.Gray
            };
            tab.Controls.Add(censusHintLabel);
            y += lineHeight + 10;

            _urlLabel = new Label { Text = "Current: (starting...)", Location = new System.Drawing.Point(10, y), AutoSize = true };
            tab.Controls.Add(_urlLabel);
            y += lineHeight + 5;

            var openFolderButton = new Button { Text = "Open Overlays Folder", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            openFolderButton.Click += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(_overlaysRoot) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Log("Failed to open overlays folder: " + ex);
                }
            };

            var relaunchButton = new Button { Text = "Relaunch Overlay", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            relaunchButton.Click += (s, e) =>
            {
                Log("Relaunch requested via plugin settings UI");
                RelaunchOverlayHost(pluginDir);
            };

            // All overlay windows share one process, so this closes all of them at once -
            // no way to close just one specifically without IPC between the plugin and the
            // overlay app.
            var closeOverlayButton = new Button { Text = "Close Overlay", AutoSize = true };
            closeOverlayButton.Click += (s, e) =>
            {
                Log("Close requested via plugin settings UI");
                foreach (var proc in Process.GetProcessesByName("SkillIssueToolkit.Overlay"))
                {
                    try
                    {
                        proc.Kill();
                    }
                    catch (Exception ex)
                    {
                        Log("Failed to close SkillIssueToolkit.Overlay.exe: " + ex);
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            };

            // FlowLayoutPanel lays out the buttons correctly regardless of each one's
            // AutoSize width - manually computing X from the previous button's .Right isn't
            // reliable, since an AutoSize button doesn't finalize its true width until it's
            // actually parented.
            var mainButtonRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Location = new System.Drawing.Point(10, y),
                Margin = new Padding(0)
            };
            mainButtonRow.Controls.Add(openFolderButton);
            mainButtonRow.Controls.Add(relaunchButton);
            mainButtonRow.Controls.Add(closeOverlayButton);
            tab.Controls.Add(mainButtonRow);
            y += mainButtonRow.Height + 15;

            // Each overlay's controls live in their own GroupBox. These edit
            // SkillIssueToolkit.Overlay's own settings file for that overlay - a separate process that
            // polls its file and applies changes live within ~300ms if already running.
            // Left/Top and the lock offset are left alone here; only the overlay process
            // knows its own window position well enough to compute those.
            foreach (var overlay in KnownOverlays)
            {
                var hostSettings = OverlayHostSettings.Load(overlay.Key);
                var key = overlay.Key; // captured explicitly for clarity in the closures below

                var groupBox = new GroupBox
                {
                    Text = overlay.DisplayName + " overlay",
                    Location = new System.Drawing.Point(10, y),
                    Size = new System.Drawing.Size(460, 100) // height corrected below, once actual content is known
                };

                var innerY = 22; // leaves room for the GroupBox's own caption at the top

                // Live in both directions - the overlay process continuously reconciles
                // which overlays are enabled against which windows are open (see
                // App.xaml.cs's ReconcileOverlays). Unchecking closes the window within
                // ~300ms; checking reopens it within ~1s.
                var enabledCheckbox = new CheckBox
                {
                    Text = "Enable this overlay",
                    Location = new System.Drawing.Point(10, innerY),
                    AutoSize = true,
                    Checked = hostSettings.Enabled
                };
                enabledCheckbox.CheckedChanged += (s, e) =>
                {
                    var current = OverlayHostSettings.Load(key);
                    current.Enabled = enabledCheckbox.Checked;
                    current.Save(key);
                    Log(overlay.DisplayName + ": enabled set to " + enabledCheckbox.Checked + " via plugin settings UI");
                };
                groupBox.Controls.Add(enabledCheckbox);
                innerY += lineHeight;

                var lockCheckbox = new CheckBox
                {
                    Text = "Lock overlay to EQ2 window",
                    Location = new System.Drawing.Point(10, innerY),
                    AutoSize = true,
                    Checked = hostSettings.LockToWindow
                };
                lockCheckbox.CheckedChanged += (s, e) =>
                {
                    var current = OverlayHostSettings.Load(key);
                    current.LockToWindow = lockCheckbox.Checked;
                    current.Save(key);
                    Log(overlay.DisplayName + ": lock-to-window set to " + lockCheckbox.Checked + " via plugin settings UI");
                };
                groupBox.Controls.Add(lockCheckbox);
                innerY += lineHeight;

                var clickThroughCheckbox = new CheckBox
                {
                    Text = "Click-through overlay" + (string.IsNullOrEmpty(key) ? " (Ctrl+Alt+L also toggles this)" : ""),
                    Location = new System.Drawing.Point(10, innerY),
                    AutoSize = true,
                    Checked = hostSettings.ClickThrough
                };
                clickThroughCheckbox.CheckedChanged += (s, e) =>
                {
                    var current = OverlayHostSettings.Load(key);
                    current.ClickThrough = clickThroughCheckbox.Checked;
                    current.Save(key);
                    Log(overlay.DisplayName + ": click-through set to " + clickThroughCheckbox.Checked + " via plugin settings UI");
                };
                groupBox.Controls.Add(clickThroughCheckbox);
                innerY += lineHeight;

                var allowDraggingCheckbox = new CheckBox
                {
                    Text = "Allow dragging (adds a small grip strip)",
                    Location = new System.Drawing.Point(10, innerY),
                    AutoSize = true,
                    Checked = hostSettings.AllowDragging
                };
                allowDraggingCheckbox.CheckedChanged += (s, e) =>
                {
                    var current = OverlayHostSettings.Load(key);
                    current.AllowDragging = allowDraggingCheckbox.Checked;
                    current.Save(key);
                    Log(overlay.DisplayName + ": allow dragging set to " + allowDraggingCheckbox.Checked + " via plugin settings UI");
                };
                groupBox.Controls.Add(allowDraggingCheckbox);
                innerY += lineHeight;

                var gripHeightLabel = new Label { Text = "Grip height (px):", Location = new System.Drawing.Point(10, innerY + 3), AutoSize = true };
                var gripHeightInput = new NumericUpDown
                {
                    Minimum = 6,
                    Maximum = 40,
                    Value = (decimal)(hostSettings.DragHandleHeight > 0 ? hostSettings.DragHandleHeight : 12),
                    Location = new System.Drawing.Point(130, innerY),
                    Width = 60
                };
                gripHeightInput.ValueChanged += (s, e) =>
                {
                    var current = OverlayHostSettings.Load(key);
                    current.DragHandleHeight = (double)gripHeightInput.Value;
                    current.Save(key);
                    Log(overlay.DisplayName + ": grip height set to " + gripHeightInput.Value + "px via plugin settings UI");
                };
                groupBox.Controls.Add(gripHeightLabel);
                groupBox.Controls.Add(gripHeightInput);
                innerY += lineHeight;

                var hideWhenUnfocusedCheckbox = new CheckBox
                {
                    Text = "Hide overlay when EQ2 loses focus",
                    Location = new System.Drawing.Point(10, innerY),
                    AutoSize = true,
                    Checked = hostSettings.HideWhenUnfocused
                };
                hideWhenUnfocusedCheckbox.CheckedChanged += (s, e) =>
                {
                    var current = OverlayHostSettings.Load(key);
                    current.HideWhenUnfocused = hideWhenUnfocusedCheckbox.Checked;
                    current.Save(key);
                    Log(overlay.DisplayName + ": hide-when-unfocused set to " + hideWhenUnfocusedCheckbox.Checked + " via plugin settings UI");
                };
                groupBox.Controls.Add(hideWhenUnfocusedCheckbox);
                innerY += lineHeight;

                var zoomLabel = new Label { Text = "Zoom %:", Location = new System.Drawing.Point(10, innerY + 3), AutoSize = true };
                var zoomInput = new NumericUpDown
                {
                    Minimum = 50,
                    Maximum = 300,
                    Value = (decimal)(hostSettings.ZoomFactor > 0 ? hostSettings.ZoomFactor * 100 : 100),
                    Location = new System.Drawing.Point(90, innerY),
                    Width = 60
                };
                zoomInput.ValueChanged += (s, e) =>
                {
                    var current = OverlayHostSettings.Load(key);
                    current.ZoomFactor = (double)zoomInput.Value / 100.0;
                    current.Save(key);
                    Log(overlay.DisplayName + ": zoom set to " + zoomInput.Value + "% via plugin settings UI");
                };
                groupBox.Controls.Add(zoomLabel);
                groupBox.Controls.Add(zoomInput);
                innerY += lineHeight;

                // Shows placeholder content so the overlay can be positioned even when empty
                // (mainly for Triggers/Timers, which are normally invisible when idle).
                var showPreviewCheckbox = new CheckBox
                {
                    Text = "Show preview content (for positioning)",
                    Location = new System.Drawing.Point(10, innerY),
                    AutoSize = true,
                    Checked = hostSettings.ShowPreview
                };
                showPreviewCheckbox.CheckedChanged += (s, e) =>
                {
                    var current = OverlayHostSettings.Load(key);
                    current.ShowPreview = showPreviewCheckbox.Checked;
                    current.Save(key);
                    Log(overlay.DisplayName + ": show-preview set to " + showPreviewCheckbox.Checked + " via plugin settings UI");
                };
                groupBox.Controls.Add(showPreviewCheckbox);
                innerY += lineHeight;

                groupBox.Size = new System.Drawing.Size(460, innerY + 8);
                tab.Controls.Add(groupBox);
                y += groupBox.Height + 10;
            }

            // No in-UI rule editor for custom triggers yet - edit eq2overlay-triggers.custom.json
            // directly, then hit Reload. Default triggers are fetched from GitHub and cached
            // to eq2overlay-triggers.default.json - not meant to be hand-edited.
            var triggersLabel = new Label
            {
                Text = "Triggers: default triggers auto-update from GitHub; add your own in the custom file:",
                Location = new System.Drawing.Point(10, y),
                AutoSize = true
            };
            tab.Controls.Add(triggersLabel);
            y += lineHeight - 2;

            var openCustomTriggersFileButton = new Button { Text = "Open Custom Triggers File", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            openCustomTriggersFileButton.Click += (s, e) =>
            {
                try
                {
                    var path = TriggerSourceManager.CustomRulesPath(pluginDir);
                    if (!File.Exists(path)) File.WriteAllText(path, "[]");
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Log("Failed to open custom triggers file: " + ex);
                }
            };

            var checkForTriggerUpdatesButton = new Button { Text = "Check for Trigger Updates", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            checkForTriggerUpdatesButton.Click += (s, e) =>
            {
                _ = StartTriggerEngineAsync(pluginDir);
            };

            var reloadTriggersButton = new Button { Text = "Reload From Disk", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            reloadTriggersButton.Click += (s, e) =>
            {
                _triggerEngine?.Unsubscribe();
                var rules = TriggerSourceManager.LoadAll(pluginDir, _triggerSettings, Log);
                _triggerEngine = new TriggerEngine(_server, rules, Log);
                _server.OnTestLine = _triggerEngine.EvaluateLine;
                Log("TriggerEngine reloaded with " + rules.Count + " rule(s)");
                RefreshTriggerListUi(pluginDir);
            };

            var openTestPageButton = new Button { Text = "Open Trigger Tester", AutoSize = true };
            openTestPageButton.Click += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("http://localhost:" + _settings.Port + "/test.html") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Log("Failed to open trigger tester: " + ex);
                }
            };

            var triggersButtonRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Location = new System.Drawing.Point(10, y),
                Margin = new Padding(0)
            };
            triggersButtonRow.Controls.Add(openCustomTriggersFileButton);
            triggersButtonRow.Controls.Add(checkForTriggerUpdatesButton);
            triggersButtonRow.Controls.Add(reloadTriggersButton);
            triggersButtonRow.Controls.Add(openTestPageButton);
            tab.Controls.Add(triggersButtonRow);
            y += lineHeight + 12;

            var autoUpdateCheckbox = new CheckBox
            {
                Text = "Auto-update default triggers on startup/reload",
                Location = new System.Drawing.Point(10, y),
                AutoSize = true,
                Checked = _triggerSettings.AutoUpdateDefaultTriggers
            };
            autoUpdateCheckbox.CheckedChanged += (s, e) =>
            {
                _triggerSettings.AutoUpdateDefaultTriggers = autoUpdateCheckbox.Checked;
                _triggerSettings.Save(pluginDir);
            };
            tab.Controls.Add(autoUpdateCheckbox);
            y += lineHeight + 8;

            var triggerListLabel = new Label
            {
                Text = "Individual triggers (uncheck to disable, regardless of source):",
                Location = new System.Drawing.Point(10, y),
                AutoSize = true
            };
            tab.Controls.Add(triggerListLabel);
            y += lineHeight - 2;

            _triggerListPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoScroll = true,
                Height = 160,
                Width = 440,
                Location = new System.Drawing.Point(10, y),
                BorderStyle = BorderStyle.FixedSingle
            };
            tab.Controls.Add(_triggerListPanel);
            RefreshTriggerListUi(pluginDir);
        }

        private FlowLayoutPanel _triggerListPanel;

        // Rebuilds the per-rule checkbox list from both trigger files (including anything
        // currently disabled) - called after any reload/refresh so it reflects what's
        // actually on disk right now, not a stale snapshot from settings-UI construction time.
        private void RefreshTriggerListUi(string pluginDir)
        {
            if (_triggerListPanel == null) return;

            _triggerListPanel.Controls.Clear();

            var allRules = TriggerSourceManager.LoadAllIncludingDisabled(pluginDir, Log);
            foreach (var rule in allRules.OrderBy(r => r.Source).ThenBy(r => r.Name))
            {
                var checkbox = new CheckBox
                {
                    Text = "[" + rule.Source + "] " + rule.Name,
                    AutoSize = true,
                    Checked = !_triggerSettings.IsDisabled(rule.Source, rule.Name)
                };
                var source = rule.Source;
                var name = rule.Name;
                checkbox.CheckedChanged += (s, e) =>
                {
                    _triggerSettings.SetDisabled(source, name, !checkbox.Checked);
                    _triggerSettings.Save(pluginDir);
                    _triggerEngine?.Unsubscribe();
                    var rules = TriggerSourceManager.LoadAll(pluginDir, _triggerSettings, Log);
                    _triggerEngine = new TriggerEngine(_server, rules, Log);
                    _server.OnTestLine = _triggerEngine.EvaluateLine;
                    Log((checkbox.Checked ? "Enabled" : "Disabled") + " trigger " + source + ":" + name + " via plugin settings UI");
                };
                _triggerListPanel.Controls.Add(checkbox);
            }
        }

        // Starts (or restarts) the overlay server on the given port - stopping any existing
        // instance first, so changing the port via the settings UI doesn't leave the old
        // listener bound.
        private void StartServer(int port)
        {
            _server?.Stop();
            _server = new OverlayServer(_overlaysRoot, port);
            // Re-wire on every (re)start, not just the first - StartServer() is also called
            // from the "Apply && Restart Server" button, which creates a brand new
            // OverlayServer instance that would otherwise have a null OnTestLine.
            if (_triggerEngine != null) _server.OnTestLine = _triggerEngine.EvaluateLine;
            _server.Start();

            var url = "http://localhost:" + port + "/";
            _statusLabel.Text = "EQ2 Overlay: running on " + url;
            if (_urlLabel != null) _urlLabel.Text = "Current: " + url;
        }

        // Resolves a dependency DLL (Newtonsoft.Json today) from the plugin's own folder.
        // Uses LoadFrom(path), not Load(args.Name), which would re-trigger this event.
        private static Assembly ResolveDependency(ResolveEventArgs args, string pluginDir)
        {
            var assemblyName = new AssemblyName(args.Name).Name;
            var candidatePath = System.IO.Path.Combine(pluginDir, assemblyName + ".dll");
            return File.Exists(candidatePath) ? Assembly.LoadFrom(candidatePath) : null;
        }

        public void DeInitPlugin()
        {
            ActGlobals.oFormActMain.AfterCombatAction -= OnAfterCombatAction;
            ActGlobals.oFormActMain.OnCombatEnd -= OnCombatEnd;
            _triggerEngine?.Unsubscribe();
            _censusLookup?.Dispose();
            _server?.Stop();
            _statusLabel.Text = "EQ2 Overlay: stopped";
            Log("DeInitPlugin complete");
        }

        // Gated on !isImport - a historical log import replaying old combat isn't a live
        // fight ending, so clearing timer bars over that wouldn't mean anything.
        private void OnCombatEnd(bool isImport, CombatToggleEventArgs encounterInfo)
        {
            if (isImport) return;

            _server?.Broadcast("clearTimers", new { });
            Log("Combat ended - cleared all timer bars");
        }

        private void OnAfterCombatAction(bool isImport, CombatActionEventArgs actionInfo)
        {
            try
            {
                var encounter = ActGlobals.oFormActMain.ActiveZone?.ActiveEncounter;
                if (encounter == null)
                {
                    Log("AfterCombatAction fired but ActiveZone/ActiveEncounter is null");
                    return;
                }

                if (!_hasLoggedGroupDiagnostic)
                {
                    _hasLoggedGroupDiagnostic = true;
                    DumpGroupRosterProperties(encounter);
                }

                var allies = encounter.GetAllies();
                var totalDamage = encounter.Damage;
                // encounter.Items includes everyone in the fight - allies and enemies. ACT's own
                // DamagePercent getter checks GetAllies().Contains(this) first; do the same or
                // the mob you're fighting shows up as a row.
                var allyData = encounter.Items.Where(c => allies.Contains(c.Value)).Select(c => c.Value).ToList();

                // ACT's encounter.Title is only reliable for a single isolated mob - fighting
                // several mobs back-to-back within one encounter-timeout window rolls them all
                // into one EncounterData wrapper titled generically "Encounter".
                // CombatActionEventArgs gives the actual attacker/victim of this action, so the
                // real current target is tracked directly - reset only when the EncounterData
                // object itself changes (a new engagement), not per sub-mob within one.
                if (!ReferenceEquals(encounter, _lastEncounter))
                {
                    _lastEncounter = encounter;
                    _currentEnemyName = null;
                }

                var allyNames = new HashSet<string>(allyData.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(actionInfo.victim) && !allyNames.Contains(actionInfo.victim))
                {
                    _currentEnemyName = actionInfo.victim;
                }
                else if (!string.IsNullOrEmpty(actionInfo.attacker) && !allyNames.Contains(actionInfo.attacker))
                {
                    _currentEnemyName = actionInfo.attacker;
                }

                var encounterDisplayName = _currentEnemyName ?? encounter.Title;

                // One-time check on whether CombatantData.Tags (a generic bag) holds
                // class/archetype info anywhere - dumps it once per session to inspect real
                // output instead of guessing.
                if (!_hasDumpedTags && allyData.Count > 0)
                {
                    _hasDumpedTags = true;
                    foreach (var c in allyData)
                    {
                        var tagsDump = c.Tags.Count == 0
                            ? "(empty)"
                            : string.Join(", ", c.Tags.Select(t => t.Key + "=" + t.Value));
                        Log(string.Format("Tags for '{0}': {1}", c.Name, tagsDump));
                    }
                }

                // GetThreatDelta/GetThreatStr don't track anything meaningful for EQ2 - using
                // DamageTaken instead, since threat attribution isn't wired up for this game.
                var damageTakenByCombatant = allyData.ToDictionary(c => c, c => c.DamageTaken);
                var maxDamageTaken = damageTakenByCombatant.Count > 0 ? damageTakenByCombatant.Values.Max() : 0L;

                // Zone change (not just encounter change) - reset the persistent zonewide
                // totals entirely, since "zonewide" means "since entering this zone".
                var currentZone = ActGlobals.oFormActMain.CurrentZone;
                if (_lastSeenZone != null && currentZone != _lastSeenZone)
                {
                    _zonewideTotals.Clear();
                    _encounterContributed.Clear();
                    _zoneStartTime = DateTime.Now;
                    Log("Zone changed to '" + currentZone + "' - zonewide totals reset");
                }
                _lastSeenZone = currentZone;

                // New encounter object (not just a new mob within the same grouped window) -
                // the "already contributed" tracker is stale for it, but the zonewide totals
                // themselves are NOT reset here, only on an actual zone change above.
                if (!ReferenceEquals(encounter, _lastEncounter))
                {
                    _encounterContributed.Clear();
                }

                foreach (var c in allyData)
                {
                    if (!_zonewideTotals.TryGetValue(c.Name, out var zoneTotal))
                    {
                        zoneTotal = new RunningTotals();
                        _zonewideTotals[c.Name] = zoneTotal;
                    }
                    if (!_encounterContributed.TryGetValue(c.Name, out var contributed))
                    {
                        contributed = new RunningTotals();
                        _encounterContributed[c.Name] = contributed;
                    }

                    zoneTotal.Damage += Math.Max(0, c.Damage - contributed.Damage);
                    zoneTotal.Healing += Math.Max(0, c.Healed - contributed.Healing);
                    zoneTotal.DamageTaken += Math.Max(0, c.DamageTaken - contributed.DamageTaken);
                    zoneTotal.PowerFed += Math.Max(0, c.PowerReplenish - contributed.PowerFed);
                    zoneTotal.PowerDrain += Math.Max(0, c.PowerDamage - contributed.PowerDrain);
                    zoneTotal.Cures += Math.Max(0, c.CureDispels - contributed.Cures);
                    zoneTotal.Deaths += Math.Max(0, c.Deaths - contributed.Deaths);

                    contributed.Damage = c.Damage;
                    contributed.Healing = c.Healed;
                    contributed.DamageTaken = c.DamageTaken;
                    contributed.PowerFed = c.PowerReplenish;
                    contributed.PowerDrain = c.PowerDamage;
                    contributed.Cures = c.CureDispels;
                    contributed.Deaths = c.Deaths;
                }

                var combatants = allyData
                    .Select(c => new CombatantSnapshot
                    {
                        Name = c.Name,
                        Damage = c.Damage,
                        DamagePercent = totalDamage > 0 ? (double)c.Damage / totalDamage * 100.0 : 0.0,
                        EncDps = SafeDouble(c.EncDPS),
                        IsYou = c.Name == ActGlobals.charName,
                        // CritHits/Hits are plain ints - safe to divide directly, just guard
                        // the zero-hits case.
                        CritPercent = c.Hits > 0 ? (double)c.CritHits / c.Hits * 100.0 : 0.0,
                        // GetMaxHit(false, true) = no attack-type prefix, with K/M/B suffix -
                        // ACT formats this itself.
                        MaxHit = c.GetMaxHit(false, true),
                        Healing = c.Healed,
                        Hps = SafeDouble(c.EncHPS),
                        DamageTaken = c.DamageTaken,
                        DamageTakenPercent = maxDamageTaken > 0 ? (double)c.DamageTaken / maxDamageTaken * 100.0 : 0.0,
                        // CureDispels and Deaths are plain CombatantData properties.
                        Cures = c.CureDispels,
                        Deaths = c.Deaths,
                        // PowerReplenish is power given to others (fed); PowerDamage is
                        // power drained from enemies (drain).
                        PowerFed = c.PowerReplenish,
                        PowerDrain = c.PowerDamage,
                        // Resolved via Census, cached in memory only - see
                        // CensusClassLookup for why persisting this would risk staying
                        // wrong after a betrayal or character recreation.
                        Class = ResolveClass(c.Name)
                    })
                    .OrderByDescending(c => c.Damage)
                    .ToArray();

                var snapshot = new EncounterSnapshot
                {
                    EncounterName = encounterDisplayName,
                    Duration = encounter.DurationS,
                    TotalDamage = totalDamage,
                    TotalDps = SafeDouble(encounter.DPS),
                    Combatants = combatants
                };

                _server.Broadcast("encounterSnapshot", snapshot);
                Log(string.Format("Broadcast OK - {0} allies, totalDamage={1}", combatants.Length, totalDamage));

                // Zonewide combatant list comes from _zonewideTotals' own keys, not allyData -
                // someone who stepped away mid-zone should still show their accumulated
                // totals, not disappear because they're not swinging on this specific mob.
                var zoneTotalDamage = _zonewideTotals.Values.Sum(t => t.Damage);
                var zoneMaxDamageTaken = _zonewideTotals.Values.Count > 0 ? _zonewideTotals.Values.Max(t => t.DamageTaken) : 0L;
                var zoneElapsed = Math.Max(1, (DateTime.Now - _zoneStartTime).TotalSeconds);

                var zoneCombatants = _zonewideTotals
                    .Select(kvp => new CombatantSnapshot
                    {
                        Name = kvp.Key,
                        Damage = kvp.Value.Damage,
                        DamagePercent = zoneTotalDamage > 0 ? (double)kvp.Value.Damage / zoneTotalDamage * 100.0 : 0.0,
                        EncDps = SafeDouble(kvp.Value.Damage / zoneElapsed),
                        IsYou = kvp.Key == ActGlobals.charName,
                        // Zonewide max-hit would need intercepting individual swings as they
                        // happen (we only read ACT's per-encounter aggregate today) - not
                        // built yet, so this stays blank rather than showing a misleading
                        // per-encounter value under a "zonewide" label.
                        MaxHit = null,
                        Healing = kvp.Value.Healing,
                        Hps = SafeDouble(kvp.Value.Healing / zoneElapsed),
                        DamageTaken = kvp.Value.DamageTaken,
                        DamageTakenPercent = zoneMaxDamageTaken > 0 ? (double)kvp.Value.DamageTaken / zoneMaxDamageTaken * 100.0 : 0.0,
                        Cures = kvp.Value.Cures,
                        Deaths = kvp.Value.Deaths,
                        PowerFed = kvp.Value.PowerFed,
                        PowerDrain = kvp.Value.PowerDrain,
                        Class = ResolveClass(kvp.Key)
                    })
                    .OrderByDescending(c => c.Damage)
                    .ToArray();

                var zoneSnapshot = new EncounterSnapshot
                {
                    EncounterName = _lastSeenZone ?? "Zone",
                    Duration = TimeSpan.FromSeconds(zoneElapsed).ToString(@"mm\:ss"),
                    TotalDamage = zoneTotalDamage,
                    TotalDps = SafeDouble(zoneTotalDamage / zoneElapsed),
                    Combatants = zoneCombatants
                };

                _server.Broadcast("zoneSnapshot", zoneSnapshot);
            }
            catch (Exception ex)
            {
                // Never let a broadcast failure take down ACT's combat parsing thread.
                Log("EXCEPTION: " + ex);
                ActGlobals.oFormActMain.WriteDebugLog("SkillIssueToolkit.ActPlugin error: " + ex);
            }
        }

        // Whatever Census has already resolved this session (cached in memory only), or
        // kicks off an async lookup if it hasn't been asked about this name yet - the result
        // (if any) shows up on a later broadcast once that resolves, not this one, since this
        // must never block the combat processing thread waiting on a network call.
        private string ResolveClass(string characterName)
        {
            var fromCensus = _censusLookup?.TryGetCachedClass(characterName);
            if (!string.IsNullOrEmpty(fromCensus)) return fromCensus;

            _censusLookup?.LookupAsync(characterName);
            return null;
        }

        // EncDPS/DPS are plain division (Damage / Duration.TotalSeconds) in ACT's real source -
        // a near-instant kill means Duration is ~0, producing Infinity/NaN. Newtonsoft would
        // serialize those as the bare tokens Infinity/NaN, which is NOT valid JSON and would
        // make the browser's JSON.parse throw. Zero is a safe, honest stand-in: "not enough
        // duration data yet" rather than a crash.
        // Note: double.IsFinite() doesn't exist in classic .NET Framework (added in .NET Core
        // 2.1+) - net48 needs the IsNaN/IsInfinity combination instead.
        private static double SafeDouble(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? 0.0 : value;
        }

        // Diagnostic - checks whether ACT exposes a live group/raid roster anywhere,
        // separate from combat-encounter ally lists (which only include people who've
        // actually swung or healed, not simply "currently in your group"). Runs once, the
        // first time there's a real EncounterData to inspect. Filtered to relevant-sounding
        // property names to keep the output readable.
        private void DumpGroupRosterProperties(EncounterData encounter)
        {
            try
            {
                var keywords = new[] { "group", "raid", "party", "member", "roster" };

                void DumpType(object instance, string label)
                {
                    if (instance == null)
                    {
                        Log(label + ": instance is null, skipping");
                        return;
                    }

                    var props = instance.GetType().GetProperties()
                        .Where(p => keywords.Any(k => p.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0));

                    foreach (var p in props)
                    {
                        try
                        {
                            var value = p.GetValue(instance);
                            Log(label + " property: " + p.Name + " (" + p.PropertyType.Name + ") = " + value);
                        }
                        catch (Exception ex)
                        {
                            Log(label + " property: " + p.Name + " - error reading: " + ex.Message);
                        }
                    }
                }

                DumpType(encounter, "EncounterData");
                DumpType(ActGlobals.oFormActMain.ActiveZone, "ZoneData");
                DumpType(ActGlobals.oFormActMain, "FormActMain");
            }
            catch (Exception ex)
            {
                Log("DumpGroupRosterProperties failed: " + ex);
            }
        }

        private void Log(string message)
        {
            try
            {
                File.AppendAllText(_logFilePath, string.Format("[{0:HH:mm:ss}] {1}{2}", DateTime.Now, message, Environment.NewLine));
            }
            catch
            {
                // logging must never be the thing that breaks combat parsing
            }
        }
    }
}