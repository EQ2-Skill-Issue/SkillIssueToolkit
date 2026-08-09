using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SkillIssueToolkit.ActPlugin
{
    // Looks up a character's class via Daybreak's Census API. Results are cached in memory
    // only, clearing on the next plugin/ACT restart so a betrayed or recreated character
    // doesn't show a stale class indefinitely.
    //
    // Enforces a request budget (a sliding 60s window, MaxRequestsPerMinute). Failed or
    // queued names retry with a backoff instead of immediately, since an actively-fighting
    // ally whose class hasn't resolved yet would otherwise get re-queried every combat tick.
    public class CensusClassLookup : IDisposable
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        // 10 requests/minute per client IP, matching Census's own published limit for the
        // shared "example" service ID.
        private const int MaxRequestsPerMinute = 10;
        private const int FailureBackoffSeconds = 30;
        private const int RateLimitBackoffSeconds = 90; // longer backoff for a confirmed 429

        private readonly object _lock = new object();
        private readonly Dictionary<string, string> _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _pendingOrInFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _pending = new List<string>();
        private readonly Dictionary<string, DateTime> _nextRetryAllowed = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<DateTime> _recentRequestTimes = new Queue<DateTime>();
        private readonly System.Threading.Timer _pumpTimer;
        private readonly string _serviceId;
        private readonly string _world;
        private readonly Action<string> _log;
        private bool _disposed;

        public CensusClassLookup(string serviceId, string world, Action<string> log)
        {
            _serviceId = string.IsNullOrWhiteSpace(serviceId) ? "example" : serviceId.Trim();
            _world = world;
            _log = log;
            _pumpTimer = new System.Threading.Timer(_ => PumpQueue(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        public void Dispose()
        {
            _disposed = true;
            _pumpTimer?.Dispose();
        }

        // Server/world comes from the parent folder of ACT's active log file path, e.g.
        // "...\logs\Wuoshi\eq2log_Gabriel.txt" -> "Wuoshi".
        public static string DetectWorldFromLogPath(string logFilePath)
        {
            try
            {
                var folder = Path.GetDirectoryName(logFilePath);
                return string.IsNullOrEmpty(folder) ? null : Path.GetFileName(folder);
            }
            catch
            {
                return null;
            }
        }

        // Never blocks on a network call - returns null if nothing's cached yet. Call
        // LookupAsync separately to queue one.
        public string TryGetCachedClass(string characterName)
        {
            lock (_lock)
            {
                return !string.IsNullOrEmpty(characterName) && _cache.TryGetValue(characterName, out var cls) ? cls : null;
            }
        }

        public bool HasAttempted(string characterName)
        {
            lock (_lock)
            {
                return !string.IsNullOrEmpty(characterName)
                    && (_cache.ContainsKey(characterName) || _pendingOrInFlight.Contains(characterName));
            }
        }

        // Queues a lookup - PumpQueue decides when it goes out. Never queues the same name
        // twice, including while on a retry backoff.
        public void LookupAsync(string characterName)
        {
            if (string.IsNullOrWhiteSpace(characterName) || string.IsNullOrWhiteSpace(_world)) return;

            lock (_lock)
            {
                if (HasAttempted(characterName)) return;
                _pendingOrInFlight.Add(characterName);
                _pending.Add(characterName);
            }
        }

        // Prunes the sliding 60s window, then fires as many ready names as the remaining
        // budget allows. A name on backoff stays queued until eligible again.
        private void PumpQueue()
        {
            if (_disposed) return;

            List<string> toFire = new List<string>();
            lock (_lock)
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-1);
                while (_recentRequestTimes.Count > 0 && _recentRequestTimes.Peek() < cutoff)
                    _recentRequestTimes.Dequeue();

                var now = DateTime.UtcNow;
                for (var i = 0; i < _pending.Count && _recentRequestTimes.Count < MaxRequestsPerMinute; i++)
                {
                    var name = _pending[i];
                    if (_nextRetryAllowed.TryGetValue(name, out var notBefore) && notBefore > now) continue;

                    toFire.Add(name);
                    _recentRequestTimes.Enqueue(now);
                }

                foreach (var name in toFire)
                {
                    _pending.Remove(name);
                    _nextRetryAllowed.Remove(name);
                }
            }

            foreach (var name in toFire)
            {
                _ = LookupInternalAsync(name);
            }
        }

        private async Task LookupInternalAsync(string characterName)
        {
            try
            {
                var url = string.Format(
                    "https://census.daybreakgames.com/s:{0}/json/get/eq2/character/?name.first={1}&locationdata.world={2}&c:show=name.first,type.class&c:limit=1",
                    Uri.EscapeDataString(_serviceId),
                    Uri.EscapeDataString(characterName),
                    Uri.EscapeDataString(_world));

                var response = await Http.GetAsync(url);

                // HttpStatusCode.TooManyRequests isn't in net48's enum, so compare the raw int.
                if ((int)response.StatusCode == 429)
                {
                    RequeueWithBackoff(characterName, TimeSpan.FromSeconds(RateLimitBackoffSeconds));
                    _log?.Invoke("Census: rate-limited (429) for " + characterName + " - backing off " + RateLimitBackoffSeconds + "s");
                    return;
                }

                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync();
                var cls = ParseClassFromResponse(body);

                lock (_lock)
                {
                    _cache[characterName] = cls; // cache the miss too, so a new character isn't re-queried every fight
                    _pendingOrInFlight.Remove(characterName);
                }

                _log?.Invoke(!string.IsNullOrEmpty(cls)
                    ? "Census: resolved " + characterName + " -> " + cls
                    : "Census: no class found for " + characterName + " - raw response: " + Truncate(body, 500));
            }
            catch (Exception ex)
            {
                RequeueWithBackoff(characterName, TimeSpan.FromSeconds(FailureBackoffSeconds));
                _log?.Invoke("Census lookup failed for " + characterName + ", retrying in " + FailureBackoffSeconds + "s: " + ex.Message);
            }
        }

        private void RequeueWithBackoff(string characterName, TimeSpan backoff)
        {
            lock (_lock)
            {
                _nextRetryAllowed[characterName] = DateTime.UtcNow + backoff;
                if (!_pending.Contains(characterName)) _pending.Add(characterName);
            }
        }

        private static string ParseClassFromResponse(string json)
        {
            try
            {
                var root = JObject.Parse(json);
                var list = root["character_list"] as JArray;
                if (list == null || list.Count == 0) return null;

                var first = list[0];
                var nested = first["type"]?["class"]?.ToString();
                if (!string.IsNullOrEmpty(nested)) return nested;

                return first["class"]?.ToString(); // fallback if class isn't nested under "type"
            }
            catch
            {
                return null;
            }
        }

        private static string Truncate(string s, int max) => s == null ? "" : (s.Length <= max ? s : s.Substring(0, max) + "...");
    }
}