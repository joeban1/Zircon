using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>
        /// Built in Start(), not here, because a failed HttpListener.Start() DISPOSES the listener -
        /// so a retry with fewer prefixes needs a brand new one. Reusing it throws
        /// ObjectDisposedException, which took the whole host down once.
        /// </summary>
        private HttpListener _listener;

        /// <summary>Loopback prefixes, which are all we truly need.</summary>
        private readonly List<string> _basePrefixes = new List<string>();
        private readonly Func<HostStatus> _read;
        private readonly Func<string, BotCommandKind, string, bool> _command;
        private readonly Func<List<MapChoice>> _maps;
        private readonly Func<string, int, string[]> _log;

        // Read-only feeds for the memory views. Delegates rather than a BotHost reference, for the
        // same reason as everything else here: this thread must not be able to reach live state.
        private readonly Func<List<HuntingRow>> _hunting;
        private readonly Func<int, List<DeathRow>> _deaths;
        private readonly Func<int, List<LevelRow>> _levels;
        private readonly Func<int, List<MapTripEntry>> _mapTrips;
        private readonly Func<int, List<ProgressEntry>> _upgrades;
        private readonly Func<int, List<ProgressEntry>> _skills;
        private readonly Func<string, List<GoldPoint>> _gold;
        private readonly Func<int, MapMask> _map;
        private readonly Func<List<HostConfigField>> _config;
        private readonly Func<BotCommandKind, string, int> _commandAll;
        private readonly Func<string, int, List<LootRow>> _loot;
        private readonly BotLog _hostLog;
        private readonly int _port;

        /// <summary>Phone notifications: status/history for the Notifications tab, and a test send.
        /// Set by the host after construction; the tab reports "unavailable" while null.</summary>
        public Func<object> NotifyStatus;
        public Func<bool> NotifyTest;

        /// <summary>Boss kill log for the Info tab. Set by the host after construction.</summary>
        public Func<int, List<BossKillEntry>> BossKills;

        /// <summary>Quest accept/complete log for the Info tab. Set by the host after construction.</summary>
        public Func<int, List<QuestLogEntry>> QuestLog;

        private Thread _thread;
        private volatile bool _stopping;
        private bool _warnedAboutOverride;

        public StatusServer(int port, BotLog hostLog,
            Func<HostStatus> read,
            Func<string, BotCommandKind, string, bool> command,
            Func<List<MapChoice>> maps,
            Func<string, int, string[]> log,
            Func<List<HuntingRow>> hunting,
            Func<int, List<DeathRow>> deaths,
            Func<int, List<LevelRow>> levels,
            Func<int, List<MapTripEntry>> mapTrips,
            Func<string, List<GoldPoint>> gold,
            Func<int, MapMask> map,
            Func<List<HostConfigField>> config,
            Func<BotCommandKind, string, int> commandAll,
            Func<string, int, List<LootRow>> loot,
            Func<int, List<ProgressEntry>> upgrades,
            Func<int, List<ProgressEntry>> skills,
            string extraHosts = "")
        {
            _port = port;
            _hostLog = hostLog;
            _read = read;
            _command = command;
            _maps = maps;
            _loot = loot;
            _log = log;
            _hunting = hunting;
            _deaths = deaths;
            _levels = levels;
            _mapTrips = mapTrips;
            _upgrades = upgrades;
            _skills = skills;
            _gold = gold;
            _map = map;
            _config = config;
            _commandAll = commandAll;

            _basePrefixes.Add($"http://127.0.0.1:{port}/");
            _basePrefixes.Add($"http://[::1]:{port}/");

            _allowedHosts.Add($"127.0.0.1:{port}");
            _allowedHosts.Add($"localhost:{port}");
            _allowedHosts.Add($"[::1]:{port}");

            // Anything else the operator has asked for - typically this machine's LAN address, so
            // the page can be read from a phone. Each one is both listened on AND added to the
            // allowed Host values, because the rebinding guard below compares against this set.
            foreach (string extra in (extraHosts ?? "").Split(','))
            {
                string host = extra.Trim();
                if (host.Length == 0) continue;

                _extraPrefixes.Add($"http://{host}:{port}/");
                _allowedHosts.Add($"{host}:{port}");
            }
        }

        /// <summary>
        /// Host header values we will answer control requests for.
        ///
        /// This is a DNS-rebinding guard, not authentication. It stops a hostile page the user
        /// happens to open from resolving its own name to this machine and driving the bots; it
        /// does nothing against anything that can address the server directly, which is precisely
        /// why StatusExtraHosts carries the warning it does.
        /// </summary>
        private readonly HashSet<string> _allowedHosts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Prefixes beyond loopback, so they can be dropped if binding them fails.</summary>
        private readonly List<string> _extraPrefixes = new List<string>();

        /// <summary>Returns false on failure - the page is observability, never a dependency.</summary>
        public bool Start()
        {
            // Every prefix we would like, then - if that is refused - just the loopback ones.
            //
            // The retry is not optional politeness. On Windows, the moment ANY url reservation
            // exists for a port, HTTP.sys demands one for every prefix on that port, including the
            // loopback prefixes that needed none before. So reserving a LAN address without also
            // reserving loopback does not merely fail to add LAN access, it takes the local page
            // away too - and the page is observability, never a dependency.
            if (TryListen(_basePrefixes.Concat(_extraPrefixes).ToList())) { }
            else if (_extraPrefixes.Count > 0)
            {
                _hostLog.Write($"Status page: cannot bind {string.Join(", ", _extraPrefixes)} - " +
                               "falling back to loopback. On Windows every prefix on a port needs " +
                               "its own urlacl once any one of them has one.");

                foreach (string extra in _extraPrefixes)
                    _allowedHosts.Remove(extra.Replace("http://", "").TrimEnd('/'));

                _extraPrefixes.Clear();

                if (!TryListen(_basePrefixes)) return false;
            }
            else return false;

            _thread = new Thread(Loop) { Name = "status-server", IsBackground = true };
            _thread.Start();

            _hostLog.Write($"Status page on http://127.0.0.1:{_port}/" +
                           (_extraPrefixes.Count > 0
                               ? " and " + string.Join(", ", _extraPrefixes)
                               : $" (tunnel with: ssh -L {_port}:localhost:{_port} ...)"));
            return true;
        }

        /// <summary>
        /// One attempt at listening on exactly these prefixes.
        ///
        /// A fresh HttpListener every time: a failed Start() disposes the instance, so the one that
        /// just refused can never be reused - not even to read its own Prefixes.
        /// </summary>
        private bool TryListen(List<string> prefixes)
        {
            HttpListener candidate = new HttpListener();

            foreach (string prefix in prefixes)
            {
                try { candidate.Prefixes.Add(prefix); }
                catch { /* malformed or unsupported - the others may still work */ }
            }

            try
            {
                candidate.Start();
                _listener = candidate;
                return true;
            }
            catch (Exception ex)
            {
                _lastListenError = ex.Message;
                try { candidate.Close(); } catch { }
                return false;
            }
        }

        private string _lastListenError = "";

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
                // Never cached. The page is redeployed constantly during development and a
                // browser holding yesterday's copy looks exactly like a broken feature - which it
                // did once, convincingly, for several minutes.
                context.Response.Headers["Cache-Control"] = "no-store";

                Send(context, 200, "text/html; charset=utf-8", Page());
                return;
            }

            // Item artwork, extracted offline by the IconExport tool. Absent by default, and
            // the page is built to work without it - see the cell rendering in StatusPage.
            if (path.StartsWith("/icons/", StringComparison.Ordinal) && !post)
            {
                ServeIcon(context, path.Substring("/icons/".Length));
                return;
            }

            if (path == "/api/maps" && !post)
            {
                Send(context, 200, "application/json; charset=utf-8",
                    JsonSerializer.Serialize(_maps(), Json));
                return;
            }

            // The memory views. All read-only, all serving copies.
            if (path == "/api/hunting" && !post)
            {
                Send(context, 200, "application/json; charset=utf-8",
                    JsonSerializer.Serialize(_hunting(), Json));
                return;
            }

            if (path == "/api/deaths" && !post)
            {
                int take = 100;
                string rawTake = context.Request.QueryString["take"];
                if (rawTake != null && int.TryParse(rawTake, out int parsedTake))
                    take = Math.Clamp(parsedTake, 1, 500);

                Send(context, 200, "application/json; charset=utf-8",
                    JsonSerializer.Serialize(_deaths(take), Json));
                return;
            }

            if (path == "/api/levels" && !post)
            {
                int take = 500;
                string rawTake = context.Request.QueryString["take"];
                if (rawTake != null && int.TryParse(rawTake, out int parsedTake))
                    take = Math.Clamp(parsedTake, 1, 2000);
                Send(context, 200, "application/json; charset=utf-8",
                    JsonSerializer.Serialize(_levels(take), Json));
                return;
            }

            if (path == "/api/boss-kills" && !post)
            {
                int take = 500;
                string rawTake = context.Request.QueryString["take"];
                if (rawTake != null && int.TryParse(rawTake, out int parsedTake))
                    take = Math.Clamp(parsedTake, 1, 2000);
                Send(context, 200, "application/json; charset=utf-8",
                    JsonSerializer.Serialize(BossKills?.Invoke(take) ?? new List<BossKillEntry>(), Json));
                return;
            }

            if (path == "/api/quest-log" && !post)
            {
                int take = 1000;
                string rawTake = context.Request.QueryString["take"];
                if (rawTake != null && int.TryParse(rawTake, out int parsedTake))
                    take = Math.Clamp(parsedTake, 1, 2000);
                Send(context, 200, "application/json; charset=utf-8",
                    JsonSerializer.Serialize(QuestLog?.Invoke(take) ?? new List<QuestLogEntry>(), Json));
                return;
            }

            if (path == "/api/map-trips" && !post)
            {
                int take = 200;
                string rawTake = context.Request.QueryString["take"];
                if (rawTake != null && int.TryParse(rawTake, out int parsedTake))
                    take = Math.Clamp(parsedTake, 1, 2000);
                Send(context, 200, "application/json; charset=utf-8",
                    JsonSerializer.Serialize(_mapTrips(take), Json));
                return;
            }

            if ((path == "/api/upgrades" || path == "/api/skills-history") && !post)
            {
                int take = 200;
                string rawTake = context.Request.QueryString["take"];
                if (rawTake != null && int.TryParse(rawTake, out int parsedTake))
                    take = Math.Clamp(parsedTake, 1, 2000);
                Send(context, 200, "application/json; charset=utf-8",
                    JsonSerializer.Serialize(path == "/api/upgrades"
                        ? _upgrades(take) : _skills(take), Json));
                return;
            }

            // WHERE DID THIS EVER DROP? Answered from the log, which is the only place a loot
            // event is recorded. Bounded by take and by the search string being non-empty, so an
            // accidental request cannot ask the host to serialise every loot it has ever seen.
            if (path == "/api/loot" && !post)
            {
                string item = context.Request.QueryString["item"] ?? "";

                if (item.Trim().Length < 2)
                {
                    TryFail(context, 400, "item needs at least two characters");
                    return;
                }

                int take = 200;
                string rawTake = context.Request.QueryString["take"];

                if (rawTake != null && int.TryParse(rawTake, out int parsedTake))
                    take = Math.Clamp(parsedTake, 1, 2000);

                Send(context, 200, "application/json; charset=utf-8",
                    JsonSerializer.Serialize(_loot(item.Trim(), take), Json));
                return;
            }

            if (path == "/api/gold" && !post)
            {
                Send(context, 200, "application/json; charset=utf-8",
                    JsonSerializer.Serialize(_gold(context.Request.QueryString["bot"]), Json));
                return;
            }

            // A map's walkability. Its own route rather than a sub-resource of a bot: the
            // exact-two-segment rule below applies only inside /api/bots/.
            if (path == "/api/map" && !post)
            {
                if (!int.TryParse(context.Request.QueryString["index"], out int mapIndex))
                {
                    TryFail(context, 400, "index required");
                    return;
                }

                MapMask mask = _map(mapIndex);

                if (mask == null) { TryFail(context, 404, "no grid for that map"); return; }

                // Immutable for a given version, and the version is in the payload rather than the
                // URL, so revalidation is cheap and a changed file is still picked up.
                context.Response.Headers["Cache-Control"] = "public, max-age=3600";

                Send(context, 200, "application/json; charset=utf-8",
                    JsonSerializer.Serialize(mask, Json));
                return;
            }

            // Settings, for the whole host. GET reads, POST applies to every bot.
            if (path == "/api/config")
            {
                if (!post)
                {
                    Send(context, 200, "application/json; charset=utf-8",
                        JsonSerializer.Serialize(_config(), Json));
                    return;
                }

                if (!Authorised(context)) { TryFail(context, 403, "forbidden"); return; }

                string key = context.Request.QueryString["key"];
                string value = context.Request.QueryString["value"];

                // Validated once, here, before anything is queued - see ConfigSchema.Validate on
                // why the queue cannot report a refusal.
                if (!ConfigSchema.Validate(key, value, out string why))
                {
                    Send(context, 400, "application/json; charset=utf-8",
                        JsonSerializer.Serialize(new { ok = false, error = why }, Json));
                    return;
                }

                int taken = _commandAll(BotCommandKind.SetConfig, key + "=" + value);

                Send(context, taken > 0 ? 202 : 503, "application/json; charset=utf-8",
                    JsonSerializer.Serialize(new { ok = taken > 0, key, value, bots = taken }, Json));
                return;
            }

            if (path == "/api/notify" && !post)
            {
                Send(context, 200, "application/json; charset=utf-8",
                    JsonSerializer.Serialize(NotifyStatus?.Invoke() ?? new { enabled = false, recent = Array.Empty<object>() }, Json));
                return;
            }

            if (path == "/api/notify/test" && post)
            {
                if (!Authorised(context)) { TryFail(context, 403, "forbidden"); return; }

                bool queued = NotifyTest?.Invoke() ?? false;
                Send(context, queued ? 202 : 503, "application/json; charset=utf-8",
                    JsonSerializer.Serialize(new
                    {
                        ok = queued,
                        error = queued ? null : "notifications are not configured (no NotifyWebhookUrl)"
                    }, Json));
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
                    case "travel": kind = BotCommandKind.Travel; break;
                    case "forcerepair": kind = BotCommandKind.ForceRepair; break;
                    case "nexttarget": kind = BotCommandKind.NextTarget; break;
                    case "quests": kind = BotCommandKind.DoQuests; break;
                    case "setconfig": kind = BotCommandKind.SetConfig; break;
                    default: TryFail(context, 404, "unknown action"); return;
                }

                // Travel takes a destination: ?map=Ant%20Cave%20North, or a map index.
                // SetConfig takes ?key=HealAtPercent&value=55.
                string argument = null;

                if (kind == BotCommandKind.Travel) argument = context.Request.QueryString["map"];
                else if (kind == BotCommandKind.SetConfig)
                {
                    string key = context.Request.QueryString["key"];
                    string value = context.Request.QueryString["value"];

                    // Validated HERE, before the command is queued, because the queue cannot
                    // answer back: the 202 below is sent the moment TryEnqueue succeeds, so a bad
                    // value discovered later on the bot thread would already have been reported to
                    // the operator as a success.
                    if (!ConfigSchema.Validate(key, value, out string why))
                    {
                        Send(context, 400, "application/json; charset=utf-8",
                            JsonSerializer.Serialize(new { ok = false, error = why }, Json));
                        return;
                    }

                    argument = key + "=" + value;
                }

                bool ok = _command(id, kind, argument);

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

            return _allowedHosts.Contains(context.Request.Headers["Host"] ?? "");
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
                    // SAY SO, ONCE. A stale override is invisible and silently shadows every page
                    // change made in code: this file was written on 21 September and went on being
                    // served for a day afterwards, so the pet tracking added to StatusPage.cs after
                    // that date was never on screen and was believed delivered. An override is a
                    // reasonable thing to want and a terrible thing to forget about.
                    if (!_warnedAboutOverride)
                    {
                        _warnedAboutOverride = true;

                        _hostLog.Write($"Status page: serving {path} " +
                                       $"(written {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm}) " +
                                       "INSTEAD of the built-in page. Delete it to use the page " +
                                       "compiled into this build.");
                    }

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

        /// <summary>
        /// Send bytes rather than text.
        ///
        /// Everything this server returned until now was UTF-8, so Send took a string and encoded
        /// it. Icons are PNGs; encoding those as text would corrupt them, and there is no sensible
        /// way to express one as a string.
        /// </summary>
        private static void SendBytes(HttpListenerContext context, int status, string type,
            byte[] bytes)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = type;
            context.Response.ContentLength64 = bytes.Length;
            context.Response.KeepAlive = false;

            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }

        /// <summary>
        /// Serve one item icon by its image index.
        ///
        /// The name is parsed as an integer and recombined, never used as a path. Anything else -
        /// a traversal, a different extension, a name with a slash in it - cannot survive
        /// int.TryParse, so the usual "../../" problem simply has nowhere to enter.
        ///
        /// Cached hard: an icon for a given index is immutable until somebody re-runs the
        /// extractor against a new client build, which is a manual act.
        /// </summary>
        private void ServeIcon(HttpListenerContext context, string name)
        {
            if (string.IsNullOrEmpty(IconPath) ||
                !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(name.Substring(0, name.Length - 4), out int index) ||
                index < 0)
            {
                TryFail(context, 404, "not found");
                return;
            }

            string file = Path.Combine(IconPath, index + ".png");

            if (!File.Exists(file)) { TryFail(context, 404, "no icon"); return; }

            try
            {
                context.Response.Headers["Cache-Control"] = "public, max-age=604800";
                SendBytes(context, 200, "image/png", File.ReadAllBytes(file));
            }
            catch (Exception)
            {
                TryFail(context, 500, "could not read the icon");
            }
        }

        /// <summary>Where the extracted item icons live, or empty when there are none.</summary>
        public string IconPath { get; set; } = "";

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

            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
        }
    }
}
