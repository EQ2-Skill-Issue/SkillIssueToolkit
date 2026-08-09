using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;

namespace SkillIssueToolkit.Overlay
{
    public partial class App : Application
    {
        // One entry per overlay window. "" as a settings key keeps the DPS overlay's file
        // at the unkeyed path the ACT plugin's settings tab expects. All point at the same
        // hardcoded port (5000) as the plugin's default.
        //
        // AllowDragging/ClickThrough fallbacks only apply on a fresh install: the DPS meter
        // defaults draggable and not click-through, while triggers/timers are passive
        // displays that default click-through instead.
        private static readonly (string Key, string HtmlFile, double FallbackLeft, double FallbackTop, bool FallbackAllowDragging, bool FallbackClickThrough)[] Overlays =
        {
            ("", "dps-meter.html", 40, 40, true, false),
            ("triggers", "triggers.html", 40, 380, false, true),
            ("timers", "timers.html", 500, 40, false, true),
        };

        // Tracks which overlay windows currently exist, keyed by overlay key. Windows remove
        // themselves from this on Closed regardless of why they closed.
        private readonly Dictionary<string, MainWindow> _openWindows = new Dictionary<string, MainWindow>();
        private DispatcherTimer _reconcileTimer;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            ReconcileOverlays();

            // Disabling an overlay is already live via each MainWindow's own settings poll.
            // Re-enabling needs this separate timer to notice and create a window. 1s is
            // plenty responsive for a setting that changes this rarely.
            _reconcileTimer = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(1) };
            _reconcileTimer.Tick += (_, _) => ReconcileOverlays();
            _reconcileTimer.Start();
        }

        private void ReconcileOverlays()
        {
            foreach (var overlay in Overlays)
            {
                if (_openWindows.ContainsKey(overlay.Key)) continue; // already open - nothing to do
                if (!OverlaySettings.Load(overlay.Key).Enabled) continue; // still disabled - nothing to do

                var url = $"http://localhost:5000/{overlay.HtmlFile}";
                var window = new MainWindow(url, overlay.Key, overlay.FallbackLeft, overlay.FallbackTop,
                    overlay.FallbackAllowDragging, overlay.FallbackClickThrough);

                var key = overlay.Key;
                window.Closed += (_, _) => _openWindows.Remove(key);

                _openWindows[overlay.Key] = window;
                window.Show();
            }
        }
    }
}