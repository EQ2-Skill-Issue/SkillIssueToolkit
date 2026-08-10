using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Advanced_Combat_Tracker;
using Newtonsoft.Json;

namespace SkillIssueToolkit.ActPlugin
{
    // Matches regex patterns against every raw log line (chat included) and fires text
    // alerts. Countdown timers are handled separately by AlarmTimerBridge, which renders
    // ACT's own native Spell Timers instead of anything authored through this engine.
    //
    // logInfo.logLine is the raw text property name on LogLineEventArgs - check this first
    // if it doesn't compile.
    public sealed class NotificationEngine
    {
        private readonly OverlayServer _server;
        private readonly Action<string> _log;

        // Rules with no Zone set are always candidates. Zone-scoped rules only get tested
        // while CurrentZone matches, so a line isn't checked against rules that don't apply
        // to wherever you currently are.
        private readonly List<(NotificationRule Rule, Regex Regex)> _alwaysActive;
        private readonly Dictionary<string, List<(NotificationRule Rule, Regex Regex)>> _byZone;

        // _lastFired gets written from both the log-line thread and any pending
        // DelaySeconds tasks, so it needs a lock.
        private readonly object _lock = new object();
        private readonly Dictionary<string, DateTime> _lastFired = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // OnLogLineRead fires synchronously on ACT's own log-parsing thread - anything slow
        // in here (even a regex with a MatchTimeout) stalls ACT itself. So the handler only
        // enqueues the raw line and returns immediately; a single background consumer task
        // drains the queue and does the actual (potentially slow) regex evaluation off of
        // ACT's thread entirely.
        private readonly BlockingCollection<string> _pendingLines = new BlockingCollection<string>(new ConcurrentQueue<string>());
        private readonly CancellationTokenSource _consumerCts = new CancellationTokenSource();
        private readonly Task _consumerTask;

        public NotificationEngine(OverlayServer server, IEnumerable<NotificationRule> rules, Action<string> log)
        {
            _server = server;
            _log = log;

            var compiled = rules
                .Where(r => !string.IsNullOrEmpty(r.Pattern))
                .Select(r => (Rule: r, Regex: new Regex(
                    r.Pattern,
                    RegexOptions.Compiled | RegexOptions.IgnoreCase,
                    TimeSpan.FromMilliseconds(200))))
                .ToList();

            _alwaysActive = compiled.Where(c => string.IsNullOrEmpty(c.Rule.Zone)).ToList();
            _byZone = compiled
                .Where(c => !string.IsNullOrEmpty(c.Rule.Zone))
                .GroupBy(c => c.Rule.Zone, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            _consumerTask = Task.Run(() => ConsumeLinesAsync(_consumerCts.Token));

            ActGlobals.oFormActMain.OnLogLineRead += OnLogLineRead;
        }

        public void Unsubscribe()
        {
            ActGlobals.oFormActMain.OnLogLineRead -= OnLogLineRead;
            _pendingLines.CompleteAdding();
            _consumerCts.Cancel();
        }

        // Never do real work here - just hand the line off to the background consumer and
        // return immediately, so ACT's log-parsing thread is never blocked by notification matching.
        private void OnLogLineRead(bool isImport, LogLineEventArgs logInfo)
        {
            try
            {
                _pendingLines.Add(logInfo.logLine);
            }
            catch (Exception ex)
            {
                _log?.Invoke("NotificationEngine error queuing line: " + ex);
            }
        }

        // Runs on its own background thread for the life of the plugin, pulling queued lines
        // and evaluating them one at a time. A slow/timed-out regex here only delays this
        // queue's own processing - it can never stall ACT itself.
        private async Task ConsumeLinesAsync(CancellationToken token)
        {
            try
            {
                foreach (var line in _pendingLines.GetConsumingEnumerable(token))
                {
                    try
                    {
                        EvaluateLine(line);
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke("NotificationEngine error: " + ex);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown via Unsubscribe()
            }

            await Task.CompletedTask;
        }

        private static readonly Dictionary<string, string> SeverityNormalization =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Alarm", "Alarm" },
                { "Alert", "Alert" },
                { "Info", "Info" },
            };

        // Public and separate from OnLogLineRead so the test page can run a line through the
        // same matching code the live game uses.
        public void EvaluateLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;

            IEnumerable<(NotificationRule Rule, Regex Regex)> candidates = _alwaysActive;
            var currentZone = ActGlobals.oFormActMain.CurrentZone;
            if (!string.IsNullOrEmpty(currentZone) && _byZone.TryGetValue(currentZone, out var zoneRules))
            {
                candidates = candidates.Concat(zoneRules);
            }

            foreach (var (rule, regex) in candidates)
            {
                Match match;
                try
                {
                    match = regex.Match(line);
                }
                catch (RegexMatchTimeoutException)
                {
                    _log?.Invoke("NotificationEngine: rule '" + rule.Name + "' pattern timed out - skipping this line");
                    continue;
                }
                if (!match.Success) continue;

                if (!PassesPlayerMatchCheck(rule, match))
                {
                    continue; // requires it be about the player specifically, and it isn't
                }

                if (IsOnCooldown(rule))
                {
                    continue; // still inside its own SuppressSeconds window
                }

                MarkFired(rule);

                if (rule.DelaySeconds > 0)
                {
                    _log?.Invoke("NotificationEngine: '" + rule.Name + "' matched - delaying " + rule.DelaySeconds + "s");
                    _ = FireDelayedAsync(rule, match, rule.DelaySeconds);
                }
                else
                {
                    FireNow(rule, match);
                }
            }
        }

        private bool PassesPlayerMatchCheck(NotificationRule rule, Match match)
        {
            if (string.IsNullOrEmpty(rule.RequirePlayerMatch)) return true;

            var group = match.Groups[rule.RequirePlayerMatch];
            return group.Success && string.Equals(group.Value, ActGlobals.charName, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsOnCooldown(NotificationRule rule)
        {
            if (rule.SuppressSeconds <= 0) return false;

            lock (_lock)
            {
                return _lastFired.TryGetValue(CooldownKey(rule), out var last)
                    && (DateTime.UtcNow - last).TotalSeconds < rule.SuppressSeconds;
            }
        }

        private void MarkFired(NotificationRule rule)
        {
            if (rule.SuppressSeconds <= 0) return;

            lock (_lock)
            {
                _lastFired[CooldownKey(rule)] = DateTime.UtcNow;
            }
        }

        // Keyed by Source+Name so a default rule and a custom rule sharing a Name track
        // cooldowns independently instead of colliding.
        private static string CooldownKey(NotificationRule rule) => NotificationSettings.MakeKey(rule.Source, rule.Name);

        private async Task FireDelayedAsync(NotificationRule rule, Match match, double delaySeconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            FireNow(rule, match); // fires regardless of whatever's changed in the meantime - no cancellation
        }

        // Fires the rule's text alert, if it has one configured.
        private void FireNow(NotificationRule rule, Match match)
        {
            if (string.IsNullOrEmpty(rule.DisplayText)) return;

            var severity = !string.IsNullOrEmpty(rule.Severity) && SeverityNormalization.TryGetValue(rule.Severity, out var normalized)
                ? normalized
                : "Alert"; // typo'd or missing severity falls back to the middle tier
            var displayText = InterpolateCaptureGroups(rule.DisplayText, match);

            _log?.Invoke("NotificationEngine: '" + rule.Name + "' fired - raw Severity in rules file: "
                + (rule.Severity ?? "(null/missing)") + ", resolved to: " + severity);

            _server.Broadcast("notificationFired", new NotificationFiredEvent
            {
                RuleName = rule.Name,
                DisplayText = displayText,
                Severity = severity,
                FiredAt = DateTime.Now
            });
        }

        private static readonly Regex PlaceholderPattern = new Regex(@"\{(\w+)\}", RegexOptions.Compiled);

        // Lets DisplayText reference a named capture group from the rule's own Pattern, e.g.
        // pattern "^You have been slain by (?<who>.+)\." with text "Killed by {who}!". An
        // unrecognized {placeholder} is left as-is rather than dropped, so a typo shows up
        // instead of silently disappearing.
        private static string InterpolateCaptureGroups(string displayText, Match match)
        {
            if (displayText == null) return match.Value;

            return PlaceholderPattern.Replace(displayText, m =>
            {
                var groupName = m.Groups[1].Value;
                var group = match.Groups[groupName];
                return group.Success ? group.Value : m.Value;
            });
        }

        public static List<NotificationRule> LoadRules(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return JsonConvert.DeserializeObject<List<NotificationRule>>(json) ?? new List<NotificationRule>();
                }
            }
            catch
            {
                // corrupt or unreadable - start with an empty rule set instead of failing plugin init
            }

            return new List<NotificationRule>();
        }
    }
}
