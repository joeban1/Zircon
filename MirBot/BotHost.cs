using System;
using System.Collections.Generic;
using System.Drawing;
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
        public ButcherIndex Butcher { get; } = new ButcherIndex();
        public BookDropIndex BookDrops { get; } = new BookDropIndex();
        public GearDropIndex GearDrops { get; } = new GearDropIndex();
        public MapLibrary Maps { get; private set; }

        /// <summary>A map's display name, or a readable fallback when the database has none.</summary>
        private static string MapNameOf(int mapIndex) =>
            Globals.MapInfoList?.Binding?.FirstOrDefault(x => x.Index == mapIndex)?.Description
            ?? $"map {mapIndex}";
        public HuntingMemory Hunting { get; private set; }
        public MonsterMemory Danger { get; private set; }
        public NavCorrections Nav { get; private set; }
        public DeathMemory Deaths { get; private set; }
        public LevelMemory Levels { get; private set; }
        public MapTripMemory MapTrips { get; private set; }
        public GoldLog Gold { get; private set; }
        public XpLog Xp { get; private set; }

        /// <summary>Where the memory banks and the extracted item icons live.</summary>
        public string MemoryFolder { get; private set; } = "";
        public WorldGraph World { get; } = new WorldGraph();
        public SafeZoneDirectory SafeZones { get; } = new SafeZoneDirectory();

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

                MapInfo info = Globals.MapInfoList.Binding
                    .FirstOrDefault(x => x.Index == entry.MapIndex);

                // AllowTT decides whether a town scroll works here at all. A scroll spent on a map
                // that forbids it is answered with a chat line and nothing else, so the bot has to
                // know before it spends one.
                Log.Write("  " + entry +
                          (info != null && !info.AllowTT ? "  [NO TOWN SCROLL]" : ""));
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
        /// <summary>
        /// What the doorway avoidance is actually working from: how many cells on each map move
        /// the character somewhere else, and how much of the map they cover. A huge exit region
        /// would mean the avoidance walls the bot in, which is the one way this can do harm.
        /// </summary>
        public void DumpExits(string mapFilter)
        {
            int shown = 0, worst = 0;
            string worstMap = "";

            foreach (MapInfo info in Globals.MapInfoList.Binding
                .OrderBy(x => x.Description, StringComparer.OrdinalIgnoreCase))
            {
                IReadOnlyList<MapExit> exits = World.ExitsFrom(info.Index);

                if (exits.Count == 0) continue;

                if (!string.IsNullOrWhiteSpace(mapFilter) &&
                    (info.Description ?? "").IndexOf(mapFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                HashSet<Point> cells = World.ExitCellsOn(info.Index);
                MapGrid grid = Maps?.For(info.Index);
                int walkable = grid?.WalkableCount ?? 0;

                double share = walkable > 0 ? cells.Count * 100.0 / walkable : 0;

                if (cells.Count > worst) { worst = cells.Count; worstMap = info.Description; }

                Console.WriteLine($"{info.Description} (map {info.Index}): {exits.Count} exit(s), " +
                                  $"{cells.Count} cell(s)" +
                                  (walkable > 0 ? $" of {walkable:N0} walkable ({share:N2}%)" : "") +
                                  (info.AllowTT ? "" : "  [NO TOWN SCROLL]") +
                                  $" -> {string.Join(", ", exits.Select(x => x.ToMapName).Distinct())}");

                // Coordinates only when a map was actually named, or this buries the summary.
                if (!string.IsNullOrWhiteSpace(mapFilter))
                    foreach (MapExit exit in exits)
                        Console.WriteLine($"    -> {exit.ToMapName}{(exit.IsTeleport ? " (NPC)" : "")}: " +
                                          string.Join(" ", exit.Cells.Select(c => $"{c.X},{c.Y}")));

                shown++;
            }

            Console.WriteLine();
            Console.WriteLine($"{shown} map(s). Largest exit footprint: {worstMap} at {worst} cell(s).");
        }

        /// <summary>
        /// Where the safe zones are, and which vendors actually stand in one.
        ///
        /// Banking is only possible inside a safe zone (PlayerObject.cs:7421), and the town trip
        /// used to attempt it wherever the itinerary happened to end. Whether that was ever a safe
        /// zone was an assumption nobody had checked - this checks it.
        /// </summary>
        public void DumpSafeZones(string mapFilter)
        {
            foreach (MapInfo map in Globals.MapInfoList.Binding
                .OrderBy(x => x.Description, StringComparer.OrdinalIgnoreCase))
            {
                if (!SafeZones.Has(map.Index)) continue;

                if (!string.IsNullOrWhiteSpace(mapFilter) &&
                    (map.Description ?? "").IndexOf(mapFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                HashSet<Point> cells = new HashSet<Point>(SafeZones.On(map.Index));

                Console.WriteLine($"{map.Description} (map {map.Index}): " +
                                  $"{cells.Count} walkable safe cell(s)");

                foreach (NPCInfo npc in Globals.NPCInfoList.Binding)
                {
                    if (npc.Region?.Map != map) continue;
                    if (npc.Region.PointRegion == null || npc.Region.PointRegion.Length == 0) continue;

                    Point spot = npc.Region.PointRegion[0];

                    Console.WriteLine($"    {(cells.Contains(spot) ? "SAFE  " : "unsafe")} " +
                                      $"{npc.NPCName} at {spot.X},{spot.Y}");
                }
            }
        }

        /// <summary>
        /// Item stats straight out of System.db - weight, what it restores, price.
        ///
        /// Added because a potion's WEIGHT drives how many the bot can budget for, and its HEAL
        /// drives when drinking one is worth it, and both were being estimated from bag deltas in a
        /// log. Tiers do not weigh the same, so a guess that fits one tier is wrong for the next.
        /// </summary>
        public void DumpItems(string filter)
        {
            int shown = 0;

            foreach (ItemInfo info in Globals.ItemInfoList.Binding
                .OrderBy(x => x.ItemType.ToString(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ItemName, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(filter) &&
                    (info.ItemName ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                int health = info.Stats[Stat.Health];
                int mana = info.Stats[Stat.Mana];

                Log.Write($"  {info.ItemName} ({info.ItemType}) shape {info.Shape}: " +
                          $"weight {info.Weight}, price {info.Price:N0}" +
                          (health > 0 ? $", heals {health} HP" : "") +
                          (mana > 0 ? $", restores {mana} MP" : "") +
                          (health > 0 && info.Weight > 0
                              ? $"  [{health / (double)info.Weight:N1} HP per weight]" : "") +
                          $"  |{(info.StartItem ? " START" : "")}" +
                          $"{(info.CanSell ? "" : " NOSELL")}{(info.CanDrop ? "" : " NODROP")}" +
                          $"{(info.CanStore ? "" : " NOSTORE")}{(info.CanTrade ? "" : " NOTRADE")}");
                shown++;
            }

            Log.Write($"{shown} item(s)" +
                      (string.IsNullOrWhiteSpace(filter) ? "." : $" matching '{filter}'."));
        }

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

            // Which teleporters we could NOT read, and why. The routes above only say what was
            // found; this says what was missed, which is the half that used to be silent.
            Log.Write("Coverage:");

            foreach (string line in Teleports.DescribeCoverage(mapFilter))
                Log.Write(line);
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

                MapInfo info = Globals.MapInfoList.Binding
                    .FirstOrDefault(x => x.Index == entry.MapIndex);

                // AllowTT decides whether a town scroll works here at all. A scroll spent on a map
                // that forbids it is answered with a chat line and nothing else, so the bot has to
                // know before it spends one.
                Log.Write("  " + entry +
                          (info != null && !info.AllowTT ? "  [NO TOWN SCROLL]" : ""));
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
            // TownMaps is a HOST-WIDE setting that happens to be read off the first bot's config.
            // Taking a fleet-wide rule from whichever bot sorted first is a trap, so say so here:
            // the other bots' TownMaps values are parsed and deliberately ignored. The resolved
            // names are logged below, and they must be checked - SetTownMaps drops a name it cannot
            // match silently, and an empty whitelist then FAILS OPEN and shops anywhere.
            Vendors.SetTownMaps(first.TownMaps.Split(','));
            Monsters.Build();

            // Host-wide for the same reason as TownMaps: what counts as a potion is a property of
            // the server's item data, not of a character.
            Backpack.MinPotionRestore = first.MinPotionRestore;

            // Built from the same first-bot config as TownMaps above, and host-wide for the same
            // reason: which monsters carry a harvest yield is a property of the SERVER's data, not
            // of any character. Logged because it is an inference, not a lookup - if it names
            // something odd, the detector is wrong and that should be visible.
            List<int> butcherAIs = new List<int>();

            foreach (string part in (first.ButcherAIs ?? "").Split(','))
                if (int.TryParse(part.Trim(), out int ai)) butcherAIs.Add(ai);

            Butcher.Build(butcherAIs);
            Log.Write($"Butcherable monsters - {Butcher.Describe()}");

            BookDrops.Build(Books);
            Log.Write($"Drop-only skill books: {BookDrops.BookCount} across " +
                      $"{BookDrops.MapCount} map(s)");
            GearDrops.Build();
            Log.Write($"Equipment drops indexed across {GearDrops.MapCount} map(s)");
            BotConnection.Monsters = Monsters;

            // Shared: maps never change, and one copy of the grids serves every bot.
            Maps = new MapLibrary(first.MapPath);

            // Shared learned state. One copy per host, flushed on a timer and on shutdown.
            string memory = Path.IsPathRooted(first.MemoryPath)
                ? first.MemoryPath
                : Path.Combine(AppContext.BaseDirectory, first.MemoryPath);

            // Kept, because Run needs it too - the icon folder lives beside the memory banks and
            // resolving the same relative path twice invites the two to disagree.
            MemoryFolder = memory;

            Hunting = new HuntingMemory(Path.Combine(memory, "hunting.json"), first.LevelBandSize)
            {
                HalfLifeHours = first.HuntingHalfLifeHours,
                MinimumSampleHours = first.MinimumSampleHours
            };
            Danger = new MonsterMemory(Path.Combine(memory, "monsters.json"));
            Nav = new NavCorrections(Path.Combine(memory, "navdata.json"));
            Deaths = new DeathMemory(Path.Combine(memory, "deaths.json"));
            Levels = new LevelMemory(Path.Combine(memory, "levels.json"));
            MapTrips = new MapTripMemory(Path.Combine(memory, "map-trips.json"));
            Gold = new GoldLog(Path.Combine(memory, "gold.ndjson"));
            Xp = new XpLog(Path.Combine(memory, "xp.ndjson"));

            // Needs the grids: a map region is a bitmap whose width is the map's own.
            World.Build(Maps);

            // Same reason, and needed before the first town trip: banking cannot happen outside a
            // safe zone, so the trip has to know where to stand.
            SafeZones.Build(Maps);


            // Teleport NPCs become extra edges in the same graph, so one search weighs a paid hop
            // against the walk rather than the two being planned separately.
            if (first.UseTeleportNPCs) World.AddTeleports(Teleports);
            Log.Write("Teleports: " + Teleports.Describe() +
                      (first.UseTeleportNPCs ? "." : " - disabled by config."));
            Log.Write("Maps: " + Profiles.Describe() + ".");
            Log.Write($"Safe zones: {SafeZones.CellCount:N0} cell(s) across " +
                      $"{SafeZones.MapCount} map(s).");
            ClientHash = first.ResolveClientHash();

            Log.Write($"Database {GameDatabase.Version}: {Books.Count} skills, " +
                      $"{Monsters.Count} monsters, {Vendors.Count} trading pages.");

            Log.Write($"Memory: {Hunting.Describe()}, {Danger.Describe()}, {Nav.Describe()}, " +
                      $"{Deaths.Describe()}, {Gold.Describe()}.");
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

        /// <summary>
        /// One map's walkability, or null if we have no grid for it.
        ///
        /// Safe from the web thread: MapLibrary.For is locked, and a MapGrid is immutable once
        /// built, so nothing here can be mutated underneath a response. The encoding is cached on
        /// the grid itself, so only the first request for a map pays for it.
        /// </summary>
        private MapMask MapMaskFor(int mapIndex)
        {
            MapGrid grid = Maps?.For(mapIndex);
            if (grid == null) return null;

            return new MapMask
            {
                Index = mapIndex,
                Name = MapNameOf(mapIndex),
                Width = grid.Width,
                Height = grid.Height,

                // Derived from the file, not from a constant: a re-exported map must not keep
                // serving last week's shape out of the browser cache.
                Version = grid.Version,
                Mask = grid.PackedMask
            };
        }

        /// <summary>
        /// The hunting table, with the lethal maps marked.
        ///
        /// Lethal() is evaluated per class and level from the bots that are actually running, so
        /// the page can show WHY a map with a good-looking rate is nevertheless being skipped.
        /// Built on the web thread, but only from copies - see HuntingMemory.Snapshot.
        /// </summary>
        private List<HuntingRow> HuntingRows()
        {
            List<HuntingRow> rows = Hunting.Snapshot();

            HashSet<(string, int)> lethal = new HashSet<(string, int)>();

            foreach (BotInstance instance in _instances)
            {
                BotStatus status = instance.Status;
                if (string.IsNullOrEmpty(status?.Class)) continue;

                foreach (int mapIndex in Hunting.Lethal(status.Class, status.Level))
                    lethal.Add((status.Class, mapIndex));
            }

            for (int i = 0; i < rows.Count; i++)
                if (lethal.Contains((rows[i].Class, rows[i].MapIndex)))
                    rows[i] = rows[i] with { Lethal = true };

            return rows;
        }

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

        /// <summary>
        /// Send one command to every bot, and report how many took it.
        ///
        /// Used for settings, which are a statement about how the operator wants the bots to play
        /// rather than about one character. Each bot still applies it on its own thread and writes
        /// its own ini - the fan-out is only about where the instruction comes from.
        ///
        /// A bot whose queue is full is counted as a miss rather than retried: the queue is bounded
        /// precisely so a wedged bot cannot accumulate work, and silently waiting on one here would
        /// block the web thread.
        /// </summary>
        public int CommandAll(BotCommandKind kind, string argument = null)
        {
            int taken = 0;

            foreach (BotInstance instance in _instances)
                if (instance.TryEnqueue(kind, argument)) taken++;

            return taken;
        }

        /// <summary>
        /// The editable settings as they stand across every bot.
        ///
        /// Built from the PUBLISHED SNAPSHOTS, never from the live BotConfig objects: those are now
        /// written by bot threads, so reading them here would be the data race the whole snapshot
        /// discipline exists to avoid.
        /// </summary>
        private List<HostConfigField> HostConfig()
        {
            List<HostConfigField> rows = new List<HostConfigField>();

            List<BotStatus> statuses = new List<BotStatus>();

            foreach (BotInstance instance in _instances)
            {
                BotStatus status = instance.Status;
                if (status?.Config != null && status.Config.Count > 0) statuses.Add(status);
            }

            foreach (ConfigField field in ConfigSchema.All)
            {
                List<string> values = new List<string>();

                foreach (BotStatus status in statuses)
                    foreach (ConfigField owned in status.Config)
                        if (owned.Key == field.Key) { values.Add(owned.Value); break; }

                bool mixed = false;

                for (int i = 1; i < values.Count; i++)
                    if (values[i] != values[0]) { mixed = true; break; }

                rows.Add(new HostConfigField
                {
                    Key = field.Key,
                    Kind = field.Kind,
                    Min = field.Min,
                    Max = field.Max,
                    Group = field.Group,
                    Note = field.Note,
                    Reach = field.Reach,
                    Value = values.Count > 0 ? values[0] : "",
                    Mixed = mixed,
                    PerBot = mixed ? values : Array.Empty<string>()
                });
            }

            return rows;
        }

        public string[] BotLogTail(string id, int lines) => Find(id)?.LogTail(lines);

        /// <summary>
        /// Every time an item was looted, newest first, from the log and its rotations.
        ///
        /// THE LOG IS THE ONLY RECORD. A loot event is written once and stored nowhere else - not
        /// in hunting.json, deaths.json or the gold log - so "has Fire Wall ever dropped, and
        /// where" could only be answered by grepping tens of megabytes by hand. That is a question
        /// worth asking often enough to deserve a button.
        ///
        /// Read with FileShare.ReadWrite because the live log is open for writing by this very
        /// process; anything stricter throws IOException on the file that matters most. Streamed a
        /// line at a time rather than ReadAllLines: these files reach 32MB each and the answer is
        /// usually a handful of rows.
        ///
        /// Newest first means reading the CURRENT file first and stopping as soon as take rows are
        /// collected, so the common case never touches the rotations at all.
        /// </summary>
        public List<LootRow> LootSearch(string item, int take)
        {
            List<LootRow> found = new List<LootRow>();

            if (string.IsNullOrWhiteSpace(item)) return found;

            take = Math.Clamp(take, 1, 2000);

            string path = Log?.FilePath;

            if (string.IsNullOrEmpty(path)) return found;

            // Map names by index, resolved once. MapInfo.Description is what every other view
            // calls a map, so the loot table agrees with the travel dropdown and the death list.
            Dictionary<int, string> names = new Dictionary<int, string>();

            if (Globals.MapInfoList?.Binding != null)
                foreach (MapInfo info in Globals.MapInfoList.Binding)
                    if (!string.IsNullOrWhiteSpace(info.Description))
                        names[info.Index] = info.Description;

            foreach (string file in new[] { path, path + ".2", path + ".3" })
            {
                if (found.Count >= take) break;
                if (!File.Exists(file)) continue;

                List<LootRow> here = new List<LootRow>();

                try
                {
                    using FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite);
                    using StreamReader reader = new StreamReader(stream);

                    string line;

                    while ((line = reader.ReadLine()) != null)
                    {
                        LootRow row = ParseLoot(line, item, names);

                        if (row != null) here.Add(row);
                    }
                }
                catch
                {
                    // A rotation being moved out from under us mid-read is expected, not an error.
                    continue;
                }

                // Within one file the lines are oldest first, so reverse before appending: the
                // caller asked for newest first and the files are already walked newest first.
                here.Reverse();

                foreach (LootRow row in here)
                {
                    if (found.Count >= take) break;
                    found.Add(row);
                }
            }

            return found;
        }

        /// <summary>
        /// One log line, if it is a loot of the item asked for.
        ///
        /// The shape is fixed by BotLog and the brain's status suffix:
        ///   [2026-09-22 07:12:04.881] [Mirbot2] Loot (Skeleton Bone) | Wizzler L26 Wizard @ 173,83 map 59 | ...
        ///
        /// Matching is a case-insensitive SUBSTRING of the item name, so "fire" finds Fire Wall and
        /// Fire Ball and the whole name still works. Deliberately not a regex over the line: an item
        /// name can contain parentheses - "Healing Potion (II)" - and the first ") | " is a far more
        /// reliable terminator than trying to balance them.
        /// </summary>
        private static LootRow ParseLoot(string line, string item, Dictionary<int, string> names)
        {
            if (line == null) return null;

            const string Marker = " Loot (";

            int loot = line.IndexOf(Marker, StringComparison.Ordinal);

            if (loot < 0) return null;

            int nameStart = loot + Marker.Length;
            int nameEnd = line.IndexOf(") | ", nameStart, StringComparison.Ordinal);

            if (nameEnd < 0) return null;

            string looted = line.Substring(nameStart, nameEnd - nameStart);

            if (looted.IndexOf(item, StringComparison.OrdinalIgnoreCase) < 0) return null;

            // [yyyy-MM-dd HH:MM:SS.mmm] [BotId] (older logs have time only).
            string time = "", bot = "";

            int close = line.IndexOf(']');

            if (line.Length > 0 && line[0] == '[' && close > 1)
            {
                time = line.Substring(1, close - 1);

                int open2 = line.IndexOf('[', close);
                int close2 = open2 < 0 ? -1 : line.IndexOf(']', open2);

                if (open2 > 0 && close2 > open2) bot = line.Substring(open2 + 1, close2 - open2 - 1);
            }

            // "Wizzler L26 Wizard @ 173,83 map 59" - read from the status suffix rather than
            // guessed, because the character name is the one thing the operator actually reads.
            string character = "", mirClass = "";
            int level = 0, x = 0, y = 0, mapIndex = 0;

            int endOfSuffix = line.IndexOf(" | ", nameEnd + 4, StringComparison.Ordinal);

            string suffix = endOfSuffix > 0
                ? line.Substring(nameEnd + 4, endOfSuffix - nameEnd - 4)
                : line.Substring(nameEnd + 4);

            string[] parts = suffix.Split(' ');

            if (parts.Length > 0) character = parts[0];

            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].Length > 1 && parts[i][0] == 'L' &&
                    int.TryParse(parts[i].Substring(1), out int parsedLevel))
                    level = parsedLevel;

                if (i > 1 && parts[i - 1].Length > 1 && parts[i - 1][0] == 'L' &&
                    int.TryParse(parts[i - 1].Substring(1), out _) &&
                    Enum.TryParse(parts[i], true, out MirClass parsedClass) &&
                    Enum.IsDefined(parsedClass))
                    mirClass = parsedClass.ToString();

                if (parts[i] == "@" && i + 1 < parts.Length)
                {
                    string[] xy = parts[i + 1].Split(',');

                    if (xy.Length == 2)
                    {
                        int.TryParse(xy[0], out x);
                        int.TryParse(xy[1], out y);
                    }
                }

                if (parts[i] == "map" && i + 1 < parts.Length)
                    int.TryParse(parts[i + 1], out mapIndex);
            }

            return new LootRow
            {
                Time = time,
                Bot = bot,
                Character = character,
                Class = mirClass,
                Item = looted,
                Level = level,
                MapIndex = mapIndex,
                MapName = names.TryGetValue(mapIndex, out string mapName)
                    ? mapName
                    : (mapIndex > 0 ? "map " + mapIndex : ""),
                X = x,
                Y = y
            };
        }


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

            _status = new StatusServer(StatusPort, Log, Snapshot, Command, TravelChoices, BotLogTail,
                HuntingRows, Deaths.Snapshot, Levels.Snapshot, MapTrips.Snapshot, Gold.Read, MapMaskFor,
                HostConfig, CommandAll, LootSearch,
                _instances.FirstOrDefault()?.Config?.StatusExtraHosts ?? "");

            // Icons are optional: the folder is normally absent, and the page falls back to
            // coloured tiles. Set here rather than in the constructor so the server needs no
            // opinion about where the bot keeps its memory.
            _status.IconPath = Path.Combine(MemoryFolder, "icons");
            _status.Start();

            Log.Write($"Host running with {_instances.Count} bot(s). Ctrl+C to stop.");

            while (!_shutdown.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                Log.FlushIfDue();
                Hunting.FlushIfDue();
                Danger.FlushIfDue();
                Nav.FlushIfDue();
                Deaths.FlushIfDue();
                Levels.FlushIfDue();
                MapTrips.FlushIfDue();
                Thread.Sleep(500);
            }

            // An early flush, so a hang in the logout loop below cannot cost a whole session's
            // learning. It is NOT the authoritative one - see the second flush after the threads
            // have joined.
            FlushMemory();

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

            // The flush that actually matters, AFTER every bot thread has stopped.
            //
            // The banks used to be written only before the logout request, and the bots keep
            // playing throughout the grace period that follows - up to forty seconds of fighting,
            // dying and learning, all of it discarded. It went unnoticed while the banks held
            // hunting rates and blocked cells, where losing the last half minute is nothing. It
            // became obvious the moment deaths were recorded: an assassin died eighteen seconds
            // into a shutdown, the log recorded it, and deaths.json never heard about it.
            FlushMemory();

            foreach (Timer timer in _startTimers) timer.Dispose();
            _startTimers.Clear();

            _status?.Dispose();
            Log.Write("Host stopped.");
        }

        /// <summary>Write every memory bank. Cheap when nothing has changed - each one no-ops.</summary>
        private void FlushMemory()
        {
            Hunting.Flush();
            Danger.Flush();
            Nav.Flush();
            Deaths.Flush();
            Levels.Flush();
            MapTrips.Flush();
        }

        public void RequestShutdown() => _shutdown.Cancel();
    }
}
