using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace MirBot
{
    /// <summary>
    /// Owns every bot instance and the things they share.
    ///
    /// The shared indexes (game database, magic books, vendor directory, monster index) are built
    /// once here and are read-only afterwards. That is load-bearing: GameDatabase assigns STATIC
    /// Globals.* collections, so a second load would swap those references out from under live
    /// connections. Every bot in a host therefore shares one System.db - which the server requires
    /// anyway, since it checks the version.
    /// </summary>
    public sealed class BotHost
    {
        private readonly List<BotInstance> _instances = new List<BotInstance>();
        private readonly object _connectLock = new object();
        private DateTime _lastConnectAt = DateTime.MinValue;

        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();

        public BotLog Log { get; }
        public MagicBooks Books { get; } = new MagicBooks();
        public VendorDirectory Vendors { get; } = new VendorDirectory();
        public MonsterIndex Monsters { get; } = new MonsterIndex();
        public byte[] ClientHash { get; private set; }

        public int ConnectSpacingSeconds { get; set; } = 10;
        public int StartStaggerSeconds { get; set; } = 15;
        public int MaxBackoffSeconds { get; set; } = 420;   // > the server's 5-minute IP ban
        public int StatusPort { get; set; } = 8642;

        /// <summary>How long to let bots log out cleanly on shutdown before forcing it.
        /// A bot under sustained attack can never leave combat, so this has a ceiling.</summary>
        public int LogoutGraceSeconds { get; set; } = 40;

        private StatusServer _status;
        private DateTime _started = DateTime.UtcNow;

        public IReadOnlyList<BotInstance> Instances => _instances;

        public BotHost(BotLog log)
        {
            Log = log;
        }

        /// <summary>
        /// Load every bot-*.ini beside the host. Separate files rather than one sectioned file:
        /// the existing parser already ignores section headers, so a directory glob needs no new
        /// parsing code at all.
        /// </summary>
        public void Load(string directory)
        {
            string[] files = Directory.GetFiles(directory, "bot-*.ini")
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (files.Length == 0)
            {
                // Fall back to the historical single file so an existing setup keeps working.
                string legacy = Path.Combine(directory, "bot.ini");
                if (File.Exists(legacy)) files = new[] { legacy };
            }

            foreach (string file in files)
            {
                string id = Path.GetFileNameWithoutExtension(file);
                if (id.StartsWith("bot-", StringComparison.OrdinalIgnoreCase)) id = id.Substring(4);
                if (string.IsNullOrWhiteSpace(id)) id = "bot";

                try
                {
                    BotConfig config = BotConfig.Load(file);

                    if (_instances.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)))
                    {
                        Log.Write($"Duplicate bot id '{id}' from {Path.GetFileName(file)} - skipped.");
                        continue;
                    }

                    _instances.Add(new BotInstance(id, config, this, Log.ForBot(id)));
                    Log.Write($"Loaded bot '{id}' from {Path.GetFileName(file)} " +
                              $"({config.EMailAddress}, autostart {config.AutoStart}).");
                }
                catch (Exception ex)
                {
                    Log.Write($"Could not load {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }

        public bool Prepare()
        {
            BotConfig first = _instances.FirstOrDefault()?.Config;

            if (first == null)
            {
                Log.Write("No bots configured - nothing to do.");
                return false;
            }

            try
            {
                GameDatabase.EnsureLoaded(first.DataPath);
            }
            catch (Exception ex)
            {
                Log.Write("Could not load the game database: " + ex.Message);
                return false;
            }

            Books.Build();
            Vendors.Build(Books);
            Monsters.Build();
            BotConnection.Monsters = Monsters;

            ClientHash = first.ResolveClientHash();

            Log.Write($"Database {GameDatabase.Version}: {Books.Count} skills, " +
                      $"{Monsters.Count} monsters, {Vendors.Count} trading pages.");

            if (ClientHash == null)
                Log.Write("WARNING: no client hash configured; login will fail if the server has " +
                          "CheckVersion=True.");

            return true;
        }

        /// <summary>
        /// Host-wide connect spacing. The server bans the whole IP for five minutes if any one
        /// connection queues too many packets, and that ban drops EVERY connection from the IP - so
        /// a fleet that reconnects in lockstep never recovers.
        /// </summary>
        public bool TryClaimConnectSlot()
        {
            lock (_connectLock)
            {
                if (DateTime.UtcNow < _lastConnectAt.AddSeconds(ConnectSpacingSeconds)) return false;

                _lastConnectAt = DateTime.UtcNow;
                return true;
            }
        }

        public BotInstance Find(string id) =>
            _instances.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>Built on the web thread from published snapshots - never from live state.</summary>
        public HostStatus Snapshot()
        {
            List<BotStatus> bots = new List<BotStatus>();
            foreach (BotInstance instance in _instances) bots.Add(instance.Status);

            BotConfig first = _instances.Count > 0 ? _instances[0].Config : null;

            return new HostStatus(
                DateTime.UtcNow.ToString("o"),
                (int)(DateTime.UtcNow - _started).TotalSeconds,
                GameDatabase.Version ?? "",
                first == null ? "" : $"{first.ServerAddress}:{first.ServerPort}",
                Vendors.Count, Books.Count, Monsters.Count,
                bots);
        }

        public bool Command(string id, BotCommandKind kind)
        {
            BotInstance instance = Find(id);
            return instance != null && instance.TryEnqueue(kind);
        }

        public string[] BotLogTail(string id, int lines) => Find(id)?.LogTail(lines);

        public void Run(int seconds = 0)
        {
            DateTime deadline = seconds > 0 ? DateTime.UtcNow.AddSeconds(seconds) : DateTime.MaxValue;
            foreach (BotInstance instance in _instances) instance.StartThread();

            // Stagger the autostarts so they do not all log in at once.
            int index = 0;
            foreach (BotInstance instance in _instances.Where(x => x.Config.AutoStart))
            {
                int delay = index++ * StartStaggerSeconds;

                if (delay == 0) instance.TryEnqueue(BotCommandKind.Start);
                else
                {
                    BotInstance captured = instance;
                    new Timer(_ => captured.TryEnqueue(BotCommandKind.Start), null,
                        TimeSpan.FromSeconds(delay), Timeout.InfiniteTimeSpan);
                }
            }

            _status = new StatusServer(StatusPort, Log, Snapshot, Command, BotLogTail);
            _status.Start();

            Log.Write($"Host running with {_instances.Count} bot(s). Ctrl+C to stop.");

            while (!_shutdown.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                Log.FlushIfDue();
                Thread.Sleep(500);
            }

            // Ask every bot to log out properly FIRST. Going straight to RequestShutdown tears the
            // socket down without a C.Logout, so characters linger in the world until the server
            // times them out - and the next launch then hits AlreadyLoggedIn for every bot at once.
            Log.Write("Shutting down - asking bots to log out.");

            foreach (BotInstance instance in _instances)
                instance.TryEnqueue(BotCommandKind.Stop);

            DateTime logoutBy = DateTime.UtcNow.AddSeconds(LogoutGraceSeconds);

            while (DateTime.UtcNow < logoutBy &&
                   _instances.Any(x => x.State != BotRunState.Offline &&
                                       x.State != BotRunState.Faulted))
            {
                Log.FlushIfDue();
                Thread.Sleep(250);
            }

            int stillUp = _instances.Count(x => x.State != BotRunState.Offline &&
                                                x.State != BotRunState.Faulted);

            if (stillUp > 0)
                Log.Write($"{stillUp} bot(s) did not log out in time - closing anyway.");

            foreach (BotInstance instance in _instances) instance.RequestShutdown();
            foreach (BotInstance instance in _instances) instance.Join(TimeSpan.FromSeconds(15));

            _status?.Dispose();
            Log.Write("Host stopped.");
        }

        public void RequestShutdown() => _shutdown.Cancel();
    }
}
