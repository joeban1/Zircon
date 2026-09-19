using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MirBot
{
    /// <summary>
    /// A loopback status and control page.
    ///
    /// HttpListener rather than a hand-rolled TcpListener: only wildcard prefixes need a urlacl
    /// registration on Windows, so 127.0.0.1 works unelevated, and hand-rolling would mean owning
    /// request-line parsing, Content-Length, chunked bodies, keep-alive and slow-loris for no gain.
    ///
    /// It is constructed with two delegates and never sees a BotInstance, WorldModel or Backpack
    /// type. That is what makes it structurally impossible for this thread to touch live bot state,
    /// rather than relying on discipline.
    ///
    /// Binding to loopback is NOT by itself authorisation: any local process, and any page open in
    /// a browser on this machine, can POST to 127.0.0.1. Control routes therefore require a custom
    /// header (which forces a CORS preflight that is never answered) and a matching Host header.
    /// </summary>
    public sealed class StatusServer : IDisposable
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        private readonly HttpListener _listener = new HttpListener();
        private readonly Func<HostStatus> _read;
        private readonly Func<string, BotCommandKind, bool> _command;
        private readonly Func<string, int, string[]> _log;
        private readonly BotLog _hostLog;
        private readonly int _port;

        private Thread _thread;
        private volatile bool _stopping;

        public StatusServer(int port, BotLog hostLog,
            Func<HostStatus> read,
            Func<string, BotCommandKind, bool> command,
            Func<string, int, string[]> log)
        {
            _port = port;
            _hostLog = hostLog;
            _read = read;
            _command = command;
            _log = log;

            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");

            try { _listener.Prefixes.Add($"http://[::1]:{port}/"); }
            catch { /* no IPv6 loopback; the v4 prefix is enough */ }
        }

        /// <summary>Returns false on failure - the page is observability, never a dependency.</summary>
        public bool Start()
        {
            try
            {
                _listener.Start();
            }
            catch (Exception ex)
            {
                _hostLog.Write($"Status server could not start on port {_port}: {ex.Message} " +
                               "- continuing without it.");
                return false;
            }

            _thread = new Thread(Loop) { Name = "status-server", IsBackground = true };
            _thread.Start();

            _hostLog.Write($"Status page on http://127.0.0.1:{_port}/ " +
                           $"(tunnel with: ssh -L {_port}:localhost:{_port} ...)");
            return true;
        }

        private void Loop()
        {
            while (!_stopping)
            {
                HttpListenerContext context;

                try
                {
                    context = _listener.GetContext();
                }
                catch (Exception)
                {
                    if (_stopping) return;
                    continue;          // a client abort must never kill the accept loop
                }

                try
                {
                    Handle(context);
                }
                catch (Exception ex)
                {
                    TryFail(context, 500, "internal error");
                    _hostLog.Write("Status request failed: " + ex.Message);
                }
            }
        }

        private void Handle(HttpListenerContext context)
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            bool post = string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase);

            if (path == "/" || path == "/index.html")
            {
                Send(context, 200, "text/html; charset=utf-8", Page());
                return;
            }

            if (path == "/api/status" && !post)
            {
                Send(context, 200, "application/json; charset=utf-8",
                    JsonSerializer.Serialize(_read(), Json));
                return;
            }

            // /api/bots/{id}/{action}
            if (path.StartsWith("/api/bots/", StringComparison.Ordinal))
            {
                string[] parts = path.Substring("/api/bots/".Length).Split('/');
                if (parts.Length != 2) { TryFail(context, 404, "not found"); return; }

                string id = Uri.UnescapeDataString(parts[0]);
                string action = parts[1].ToLowerInvariant();

                if (action == "log" && !post)
                {
                    int tail = 200;
                    string raw = context.Request.QueryString["tail"];
                    if (raw != null && int.TryParse(raw, out int parsed)) tail = Math.Clamp(parsed, 1, 500);

                    string[] lines = _log(id, tail);
                    if (lines == null) { TryFail(context, 404, "no such bot"); return; }

                    Send(context, 200, "application/json; charset=utf-8",
                        JsonSerializer.Serialize(lines, Json));
                    return;
                }

                if (!post) { TryFail(context, 405, "use POST"); return; }
                if (!Authorised(context)) { TryFail(context, 403, "forbidden"); return; }

                BotCommandKind kind;

                switch (action)
                {
                    case "start": kind = BotCommandKind.Start; break;
                    case "stop": kind = BotCommandKind.Stop; break;
                    case "towntrip": kind = BotCommandKind.ForceTownTrip; break;
                    case "revive": kind = BotCommandKind.Revive; break;
                    default: TryFail(context, 404, "unknown action"); return;
                }

                bool ok = _command(id, kind);

                Send(context, ok ? 202 : 404, "application/json; charset=utf-8",
                    JsonSerializer.Serialize(new { ok, id, action }, Json));
                return;
            }

            TryFail(context, 404, "not found");
        }

        /// <summary>
        /// A custom header cannot be sent cross-origin without a preflight, which is never
        /// answered - so a hostile local page cannot drive the controls. The Host check blocks DNS
        /// rebinding. No Access-Control-Allow-Origin is ever emitted.
        /// </summary>
        private bool Authorised(HttpListenerContext context)
        {
            if (context.Request.Headers["X-MirBot"] != "1") return false;

            string host = context.Request.Headers["Host"] ?? "";

            return host == $"127.0.0.1:{_port}" || host == $"localhost:{_port}" ||
                   host == $"[::1]:{_port}";
        }

        private string Page()
        {
            // Prefer a file beside the executable so the layout can be edited without a rebuild -
            // builds on the VM take minutes. Fall back to the built-in copy so a missing file is
            // never a failure. FileShare.ReadWrite so an editor holding the file does not break it.
            string path = Path.Combine(AppContext.BaseDirectory, "status.html");

            try
            {
                if (File.Exists(path))
                {
                    using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite);
                    using StreamReader reader = new StreamReader(stream);
                    return reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                _hostLog.Write($"Could not read {path}: {ex.Message} - using the built-in page.");
            }

            return StatusPage.Html;
        }

        private static void Send(HttpListenerContext context, int status, string type, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);

            context.Response.StatusCode = status;
            context.Response.ContentType = type;
            context.Response.ContentLength64 = bytes.Length;   // bytes, not characters
            context.Response.KeepAlive = false;

            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }

        private static void TryFail(HttpListenerContext context, int status, string message)
        {
            try
            {
                Send(context, status, "text/plain; charset=utf-8", message);
            }
            catch
            {
                // The client is gone. Nothing to do and nothing worth logging.
            }
        }

        public void Dispose()
        {
            _stopping = true;

            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }
}
