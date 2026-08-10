using System;
using System.Collections.Generic;
using Advanced_Combat_Tracker;

namespace SkillIssueToolkit.ActPlugin
{
    // Optional, opt-in tap into every raw log line - separate from NotificationEngine so turning
    // capture on/off never touches the live notification-matching path. Used by
    // notification-builder.html: flip capture on, get hit by whatever you don't have a
    // notification for yet, flip it off, then pick the line out of the list instead of digging
    // through ACT or the raw log file by hand.
    //
    // Broadcasting is gated on IsCapturing so idle time (the common case) costs nothing
    // beyond the OnLogLineRead subscription itself - no buffering, no websocket traffic.
    public sealed class LogLineCapture
    {
        private const int MaxBufferedLines = 300;

        private readonly OverlayServer _server;
        private readonly Action<string> _log;
        private readonly object _lock = new object();
        private readonly LinkedList<CapturedLine> _buffer = new LinkedList<CapturedLine>();

        public bool IsCapturing { get; private set; }

        public LogLineCapture(OverlayServer server, Action<string> log)
        {
            _server = server;
            _log = log;
            ActGlobals.oFormActMain.OnLogLineRead += OnLogLineRead;
        }

        public void Start()
        {
            lock (_lock)
            {
                _buffer.Clear();
            }

            IsCapturing = true;
            _log?.Invoke("LogLineCapture: capture started");
            _server.Broadcast("captureStateChanged", new { isCapturing = true });
        }

        public void Stop()
        {
            IsCapturing = false;
            _log?.Invoke("LogLineCapture: capture stopped");
            _server.Broadcast("captureStateChanged", new { isCapturing = false });
        }

        public List<CapturedLine> GetSnapshot()
        {
            lock (_lock)
            {
                return new List<CapturedLine>(_buffer);
            }
        }

        public void Unsubscribe()
        {
            ActGlobals.oFormActMain.OnLogLineRead -= OnLogLineRead;
        }

        // Runs on ACT's own log-parsing thread, same as NotificationEngine.OnLogLineRead - the
        // IsCapturing check up front means the common (not capturing) case is a single
        // boolean read and nothing else.
        private void OnLogLineRead(bool isImport, LogLineEventArgs logInfo)
        {
            if (!IsCapturing) return;

            try
            {
                var captured = new CapturedLine
                {
                    Text = logInfo.logLine,
                    CapturedAt = DateTime.Now
                };

                lock (_lock)
                {
                    _buffer.AddLast(captured);
                    if (_buffer.Count > MaxBufferedLines) _buffer.RemoveFirst();
                }

                _server.Broadcast("logLineCaptured", captured);
            }
            catch (Exception ex)
            {
                _log?.Invoke("LogLineCapture error: " + ex);
            }
        }
    }

    public class CapturedLine
    {
        public string Text { get; set; }
        public DateTime CapturedAt { get; set; }
    }
}
