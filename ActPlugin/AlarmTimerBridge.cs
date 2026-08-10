using System;
using System.Drawing;
using Advanced_Combat_Tracker;

namespace SkillIssueToolkit.ActPlugin
{
    // Renders ACT's own native Spell Timers (Options -> Spell Timers) instead of maintaining
    // a separate timer-authoring format - timers.html becomes a nicer-looking view of
    // whatever the user already has configured natively in ACT, rather than something this
    // plugin owns end-to-end. There is no equivalent native event for ACT's Custom Triggers
    // (text/TTS alerts), so those are still handled by NotificationEngine.
    public sealed class AlarmTimerBridge
    {
        private readonly OverlayServer _server;
        private readonly Action<string> _log;

        public AlarmTimerBridge(OverlayServer server, Action<string> log)
        {
            _server = server;
            _log = log;

            ActGlobals.oFormSpellTimers.OnSpellTimerNotify += OnSpellTimerNotify;
            ActGlobals.oFormSpellTimers.OnSpellTimerWarning += OnSpellTimerWarning;
            ActGlobals.oFormSpellTimers.OnSpellTimerExpire += OnSpellTimerExpire;
            ActGlobals.oFormSpellTimers.OnSpellTimerRemoved += OnSpellTimerRemoved;
        }

        public void Unsubscribe()
        {
            ActGlobals.oFormSpellTimers.OnSpellTimerNotify -= OnSpellTimerNotify;
            ActGlobals.oFormSpellTimers.OnSpellTimerWarning -= OnSpellTimerWarning;
            ActGlobals.oFormSpellTimers.OnSpellTimerExpire -= OnSpellTimerExpire;
            ActGlobals.oFormSpellTimers.OnSpellTimerRemoved -= OnSpellTimerRemoved;
        }

        private void OnSpellTimerNotify(TimerFrame spellTimer) => Broadcast(spellTimer, "Started");
        private void OnSpellTimerWarning(TimerFrame spellTimer) => Broadcast(spellTimer, "Warning");
        private void OnSpellTimerExpire(TimerFrame spellTimer) => Broadcast(spellTimer, "Expired");

        private void OnSpellTimerRemoved(TimerFrame spellTimer)
        {
            try
            {
                _server.Broadcast("alarmTimerRemoved", new AlarmTimerRemovedEvent
                {
                    Name = spellTimer.Name,
                    Category = spellTimer.TimerData?.Category
                });
            }
            catch (Exception ex)
            {
                _log?.Invoke("AlarmTimerBridge error on removed: " + ex);
            }
        }

        private void Broadcast(TimerFrame spellTimer, string state)
        {
            try
            {
                var data = spellTimer.TimerData;
                if (data == null) return;

                // ACT's own Spell Timers window only ever shows ONE counting-down number per
                // timer, sourced from GetLargestVal(IncludeNonMaster: false) - i.e. the master
                // timer's own remaining time, completely ignoring whatever non-master "renewal"
                // entries have piled up in spellTimer.SpellTimers (ACT appends rather than
                // replaces there any time the same timer's regex matches again within its own
                // 12-second window - see FormSpellTimers.NotifyTimer/tmrHalfSec_Tick). Mirroring
                // that same master-only value here, instead of SpellTimers.Count, is what keeps
                // this overlay's single bar in sync with ACT's own display rather than bouncing
                // back up and "stacking" a count that ACT itself never actually shows.
                var remainingSeconds = spellTimer.GetLargestVal(IncludeNonMaster: false);

                _server.Broadcast("alarmTimerUpdate", new AlarmTimerUpdateEvent
                {
                    Name = spellTimer.Name,
                    Category = data.Category,
                    Tooltip = data.Tooltip,
                    Color = ColorTranslator.ToHtml(data.FillColor),
                    DurationSeconds = data.TimerValue,
                    WarningSeconds = data.WarningValue,
                    RemainingSeconds = remainingSeconds,
                    State = state,
                    UpdatedAt = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                _log?.Invoke("AlarmTimerBridge error on " + state + ": " + ex);
            }
        }
    }

    public class AlarmTimerUpdateEvent
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public string Tooltip { get; set; }
        public string Color { get; set; }
        public int DurationSeconds { get; set; }
        public int WarningSeconds { get; set; }
        public int RemainingSeconds { get; set; }
        public string State { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class AlarmTimerRemovedEvent
    {
        public string Name { get; set; }
        public string Category { get; set; }
    }
}
