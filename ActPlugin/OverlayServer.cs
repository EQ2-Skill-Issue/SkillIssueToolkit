using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace SkillIssueToolkit.ActPlugin
{
    /// <summary>
    /// Serves the overlay HTML/JS as static files and pushes EncounterSnapshot JSON to any
    /// connected WebSocket client. Runs inside the plugin's own process (ACT's process),
    /// with no CEF, no separate plugin, and no FFXIV_ACT_Plugin dependency.
    /// </summary>
    public sealed class OverlayServer
    {
        // Newtonsoft defaults to PascalCase (matching our C# property names); the overlay
        // JS expects camelCase (encDps, damagePercent, isYou) - this bridges the two.
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        private readonly HttpListener _listener = new HttpListener();
        private readonly ConcurrentBag<WebSocket> _sockets = new ConcurrentBag<WebSocket>();
        private readonly string _overlaysRoot;
        private readonly int _port;
        private CancellationTokenSource _cts;

        // Wired by Plugin.cs to TriggerEngine.EvaluateLine - lets test.html inject an
        // arbitrary line through the exact same matching code the live game path uses.
        public Action<string> OnTestLine { get; set; }

        public OverlayServer(string overlaysRoot, int port = 5000)
        {
            _overlaysRoot = overlaysRoot;
            _port = port;
            _listener.Prefixes.Add(string.Format("http://localhost:{0}/", port));
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _listener.Start();
            Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _listener.Stop();
            }
            catch
            {
                // best-effort shutdown - ACT is unloading the plugin, nothing to recover into
            }
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    if (token.IsCancellationRequested) return;
                    continue;
                }

                if (ctx.Request.IsWebSocketRequest)
                {
                    _ = HandleWebSocketAsync(ctx, token);
                }
                else if (ctx.Request.HttpMethod == "POST" && ctx.Request.Url.AbsolutePath.TrimStart('/') == "test-line")
                {
                    _ = HandleTestLineAsync(ctx, token);
                }
                else if (ctx.Request.HttpMethod == "POST" && ctx.Request.Url.AbsolutePath.TrimStart('/') == "clear-timers")
                {
                    _ = HandleClearTimersAsync(ctx, token);
                }
                else
                {
                    _ = ServeStaticAsync(ctx, token);
                }
            }
        }

        // Wipes every active timer bar in timers.html - a direct broadcast, no matching logic.
        private async Task HandleClearTimersAsync(HttpListenerContext ctx, CancellationToken token)
        {
            try
            {
                Broadcast("clearTimers", new { });
                ctx.Response.StatusCode = 200;
            }
            catch
            {
                ctx.Response.StatusCode = 500;
            }
            finally
            {
                ctx.Response.Close();
            }
        }

        // Lets test.html inject an arbitrary line for TriggerEngine to evaluate through the
        // exact same matching code the live game path uses - see EvaluateLine in TriggerEngine.cs.
        private async Task HandleTestLineAsync(HttpListenerContext ctx, CancellationToken token)
        {
            try
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var line = await reader.ReadToEndAsync();
                OnTestLine?.Invoke(line);
                ctx.Response.StatusCode = 200;
            }
            catch
            {
                ctx.Response.StatusCode = 500;
            }
            finally
            {
                ctx.Response.Close();
            }
        }

        private async Task HandleWebSocketAsync(HttpListenerContext ctx, CancellationToken token)
        {
            var wsCtx = await ctx.AcceptWebSocketAsync(null);
            _sockets.Add(wsCtx.WebSocket);

            var buffer = new byte[1024];
            try
            {
                // We don't need anything the client sends - just keep the connection open
                // so we notice when it closes.
                while (wsCtx.WebSocket.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var result = await wsCtx.WebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch
            {
                // client dropped
            }
        }

        // Wraps every broadcast in a {type, data} envelope so multiple overlay pages can share
        // this one connection, each filtering for the type(s) it cares about.
        public void Broadcast(string type, object data)
        {
            var envelope = JsonConvert.SerializeObject(new { type, data }, JsonSettings);
            var bytes = Encoding.UTF8.GetBytes(envelope);
            var segment = new ArraySegment<byte>(bytes);

            foreach (var socket in _sockets)
            {
                if (socket.State == WebSocketState.Open)
                {
                    // fire-and-forget; a dead socket here just gets skipped on the next pass
                    socket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
        }

        private async Task ServeStaticAsync(HttpListenerContext ctx, CancellationToken token)
        {
            var requested = ctx.Request.Url.AbsolutePath.TrimStart('/');
            var relPath = string.IsNullOrEmpty(requested) ? "dps-meter.html" : requested;
            var fullPath = Path.GetFullPath(Path.Combine(_overlaysRoot, relPath));
            var rootFull = Path.GetFullPath(_overlaysRoot);

            if (!fullPath.StartsWith(rootFull, StringComparison.Ordinal) || !File.Exists(fullPath))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            // Extension-based MIME type - a catch-all of "everything not .html is
            // application/javascript" would be wrong for .webp class icons and anything else
            // added later; some browsers refuse to render a file with an incorrect MIME type.
            ctx.Response.ContentType = Path.GetExtension(fullPath).ToLowerInvariant() switch
            {
                ".html" => "text/html",
                ".js" => "application/javascript",
                ".css" => "text/css",
                ".json" => "application/json",
                ".webp" => "image/webp",
                ".png" => "image/png",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream"
            };
            var bytes = File.ReadAllBytes(fullPath);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, token);
            ctx.Response.Close();
        }
    }
}