using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Library;
using Library.SystemModels;

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
        public TeleportDirectory Teleports { get; } = new TeleportDirectory();
        public MapProfile Profiles { get; } = new MapProfile();
        public MonsterIndex Monsters { get; } = new MonsterIndex();
        public MapLibrary Maps { get; private set; }
        public HuntingMemory Hunting { get; private set; }
        public MonsterMemory Danger { get; private set; }
        public NavCorrections Nav { get; private set; }
        public WorldGraph World { get; } = new WorldGraph();

        /// <summary>
        /// Every map the travel graph can name, for the status page's destination list. Not
        /// filtered by reachability: that depends on which bot is asking and where it is standing,
        /// and a route that does not exist is reported clearly when the journey is planned.
        /// </summary>
        public List<MapChoice> TravelChoices()
        {
            List<MapChoice> choices = new List<MapChoice>();

            if (Globals.MapInfoList?.Binding == null) return choices;

            foreach (MapInfo info in Globals.MapInfoList.Binding)
            {
                if (string.IsNullOrWhiteSpace(info.Description)) continue;
                if (World.ExitsFrom(info.Index).Count == 0 && !World.IsDestination(info.Index)) continue;

                choices.Add(new MapChoice(info.Index, info.Description));
            }

            choices.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return choices;
        }
        public byte[] ClientHash { get; private set; }

        public int ConnectSpacingSeconds { get; set; } = 10;
        public int StartStaggerSeconds { get; set; } = 15;

        /// <summary>Holds the staggered-autostart timers alive until they fire - see Run.</summary>
        private readonly List<Timer> _startTimers = new List<Timer>();
        public int MaxBackoffSeconds { get; set; } = 420;   // > the server's 5-minute IP ban
        public int StatusPort { get; set; } = 8642;

        /// <summary>
        /// Load every map and report on it, then exit. Run with --check-maps after changing
        /// MapPath: a path that is wrong or points at the wrong build produces a bot that silently
        /// falls back to blind steering, which looks like the pathfinding simply not working.
        /// </summary>
        public bool CheckMaps()
        {
            if (Maps == null || !Maps.Available)
            {
                Log.Write("No maps: " + Maps?.Describe());
                return false;
            }

            int loaded = 0, missing = 0;

            foreach (MapInfo info in Globals.MapInfoList.Binding.OrderBy(x => x.Index))
            {
                MapGrid grid = Maps.For(info.Index);

                if (grid == null)
                {
                    missing++;
                    Log.Write($"  [{info.Index,4}] {info.FileName,-24} MISSING");
                    continue;
                }

                loaded++;

                int walkable = 0;
                for (int x = 0; x < grid.Width; x++)
                    for (int y = 0; y < grid.Height; y++)
                        if (grid.Walkable(x, y)) walkable++;

                int total = grid.Width * grid.Height;
                double percent = total == 0 ? 0 : 100.0 * walkable / total;

                Log.Write($"  [{info.Index,4}] {info.FileName,-24} {grid.Width,5}x{grid.Height,-5} " +
                          $"{percent,5:0.0}% walkable  {info.Description}");
            }

            Log.Write($"Maps: {loaded} loaded, {missing} missing.");
            return missing == 0;
        }

        /// <summary>
        /// Plan a journey without making one. Takes a map name or index and prints the route the
        /// bot would walk, so a change to the graph can be checked in seconds rather than by
        /// sending a live character somewhere it cannot come back from.
        /// </summary>
        public bool CheckTravel(string destination)
        {
            MapInfo from = Globals.MapInfoList.Binding.FirstOrDefault(x => x.Index == 1);

            if (from == null)
            {
                Log.Write("No map index 1 to start from.");
                return false;
            }

            MapInfo to = Globals.MapInfoList.Binding.FirstOrDefault(x =>
                string.Equals(x.Description, destination, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.FileName, destination, StringComparison.OrdinalIgnoreCase) ||
                x.Index.ToString() == destination);

            if (to == null)
            {
                Log.Write($"No map matches '{destination}'.");
                return false;
            }

            Log.Write(World.Describe() + ".");

            foreach (int level in new[] { 1, 14, 30, 60 })
            {
                List<MapExit> route = World.Route(from.Index, to.Index, MirClass.Warrior, level);

                if (route == null)
                {
                    Log.Write($"  Warrior L{level,-3} no route to {to.Description}.");
                    continue;
                }

                string path = from.Description;
                foreach (MapExit exit in route) path += " -> " + exit.ToMapName;

                Log.Write($"  Warrior L{level,-3} {route.Count} map change(s): {path}");
            }

            int reachable = World.Reachable(from.Index, MirClass.Warrior, 14).Count;
            Log.Write($"Reachable from {from.Description} as a level 14 Warrior: {reachable} maps.");

            return true;
        }

        /// <summary>
        /// Print the vendor directory, optionally for one map. The bot's whole idea of who sells
        /// what comes from here, so when it walks somewhere surprising this is the first place to
        /// look rather than the decision code.
        /// </summary>
        public void DumpVendors(string mapFilter)
        {
            int shown = 0;

            foreach (VendorEntry entry in Vendors.Entries
                .OrderBy(x => x.MapName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.NPC?.NPCName, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(mapFilter) &&
                    entry.MapName.IndexOf(mapFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                Log.Write("  " + entry);
                shown++;
            }

            Log.Write($"{shown} trading page(s)" +
                      (string.IsNullOrWhiteSpace(mapFilter) ? "." : $" matching '{mapFilter}'."));
        }

        /// <summary>
        /// Print the teleport network, optionally for one map.
        ///
        /// Same reasoning as DumpVendors, and the same lesson: three rounds of reasoning about the
        /// vendor SELECTION code found no bug, and dumping the vendor DATA found two in two
        /// attempts. Whether paying an NPC is even an option is a question about the data.
        /// </summary>
        public void DumpTeleports(string mapFilter)
        {
            int shown = 0;

            foreach (TeleportRoute route in Teleports.Routes
                .OrderBy(x => x.FromMapName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Cost))
            {
                if (!string.IsNullOrWhiteSpace(mapFilter) &&
                    route.FromMapName.IndexOf(mapFilter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    route.ToMapName.IndexOf(mapFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                Log.Write("  " + route);
                shown++;
            }

            Log.Write($"{shown} teleport route(s)" +
                      (string.IsNullOrWhiteSpace(mapFilter) ? "." : $" matching '{mapFilter}'.") +
                      " " + Teleports.Describe());
        }

        /// <summary>
        /// Print what the database says lives on each map.
        ///
        /// The reason this is a diagnostic before it is a feature: this server leaves a lot of
        /// metadata at its defaults, and the item work already found RequiredLevel unreliable
        /// enough that items had to be sorted by primary stat instead. Monster levels may be the
        /// same. Printing level and experience side by side makes that checkable - if the two do
        /// not move together, the levels are decorative and the filter has to use experience.
        /// </summary>
        public void DumpMapProfiles(string filter)
        {
            int shown = 0;

            foreach (MapProfileEntry entry in Profiles.Entries
                .OrderBy(x => x.MedianLevel)
                .ThenBy(x => x.MapName, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(filter) &&
                    entry.MapName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                Log.Write("  " + entry);
                shown++;
            }

            Log.Write($"{shown} map(s)" +
                      (string.IsNullOrWhiteSpace(filter) ? "." : $" matching '{filter}'.") +
                      " " + Profiles.Describe());
        }

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
            Teleports.Build();
            Profiles.Build();
            Vendors.SetTownMaps(first.TownMaps.Split(','));
            Monsters.Build();
            BotConnection.Monsters = Monsters;

            // Shared: maps never change, and one copy of the grids serves every bot.
            Maps = new MapLibrary(first.MapPath);

            // Shared learned state. One copy per host, flushed on a timer and on shutdown.
            string memory = Path.IsPathRooted(first.MemoryPath)
                ? first.MemoryPath
                : Path.Combine(AppContext.BaseDirectory, first.MemoryPath);

            Hunting = new HuntingMemory(Path.Combine(memory, "hunting.json"), first.LevelBandSize);
            Danger = new MonsterMemory(Path.Combine(memory, "monsters.json"));
            Nav = new NavCorrections(Path.Combine(memory, "navdata.json"));

            // Needs the grids: a map region is a bitmap whose width is the map's own.
            World.Build(Maps);


            // Teleport NPCs become extra edges in the same graph, so one search weighs a paid hop
            // against the walk rather than the two being planned separately.
            if (first.UseTeleportNPCs) World.AddTeleports(Teleports);
            Log.Write("Teleports: " + Teleports.Describe() +
                      (first.UseTeleportNPCs ? "." : " - disabled by config."));
            Log.Write("Maps: " + Profiles.Describe() + ".");
            ClientHash = first.ResolveClientHash();

            Log.Write($"Database {GameDatabase.Version}: {Books.Count} skills, " +
                      $"{Monsters.Count} monsters, {Vendors.Count} trading pages.");

            Log.Write($"Memory: {Hunting.Describe()}, {Danger.Describe()}, {Nav.Describe()}.");
            Log.Write(World.Describe() + ".");
            Log.Write("Vendors: " + Vendors.DescribeTownMaps() + ".");

            if (!Maps.Available)
                Log.Write("WARNING: MapPath is not set or does not exist, so the bot cannot " +
                          "pathfind and will steer blind. Point it at the client's Map folder.");

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

        public bool Command(string id, BotCommandKind kind, string argument = null)
        {
            BotInstance instance = Find(id);
            return instance != null && instance.TryEnqueue(kind, argument);
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

                    // The timer is KEPT. A System.Threading.Timer that nothing references is
                    // eligible for collection before it ever fires, so this used to be a race
                    // against the garbage collector: with two bots the delays were short enough
                    // that it always won, and at four bots Mirbot3's thirty-second timer was
                    // collected and it simply never started - no error, no connect attempt, just a
                    // bot sitting Offline for ever while the others came up around it.
                    _startTimers.Add(new Timer(_ => captured.TryEnqueue(BotCommandKind.Start), null,
                        TimeSpan.FromSeconds(delay), Timeout.InfiniteTimeSpan));
                }
            }

            _status = new StatusServer(StatusPort, Log, Snapshot, Command, TravelChoices, BotLogTail);
            _status.Start();

            Log.Write($"Host running with {_instances.Count} bot(s). Ctrl+C to stop.");

            while (!_shutdown.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                Log.FlushIfDue();
                Hunting.FlushIfDue();
                Danger.FlushIfDue();
                Nav.FlushIfDue();
                Thread.Sleep(500);
            }

            // Anything learned since the last timed write would otherwise be lost.
            Hunting.Flush();
            Danger.Flush();
            Nav.Flush();

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

            foreach (Timer timer in _startTimers) timer.Dispose();
            _startTimers.Clear();

            _status?.Dispose();
            Log.Write("Host stopped.");
        }

        public void RequestShutdown() => _shutdown.Cancel();
    }
}
