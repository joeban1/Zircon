using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    /// <summary>One way out of a map: stand on any of these cells and the server moves you.</summary>
    public sealed class MapExit
    {
        public int FromMapIndex;
        public int ToMapIndex;
        public string ToMapName = "";

        /// <summary>Cells on the source map that trigger the move.</summary>
        public IReadOnlyList<Point> Cells = Array.Empty<Point>();

        /// <summary>Requirements the server checks on arrival, so we can rule the exit out first.</summary>
        public int MinimumLevel;
        public int MaximumLevel;
        public RequiredClass RequiredClass;
        public bool NeedsItem;
        public bool NeedsInstance;

        /// <summary>
        /// Non-null when this is not a cell to walk onto but an NPC to talk to. The route is
        /// otherwise identical: a way of getting from one map to another, with requirements.
        /// </summary>
        public TeleportRoute Teleport;

        public bool IsTeleport => Teleport != null;
        public long Cost => Teleport?.Cost ?? 0;

        /// <summary>
        /// Can this character use this exit, with this much gold in hand?
        ///
        /// Gold is part of the question because a paid exit we cannot pay for is not an exit, and
        /// finding that out by walking to the NPC and being refused wastes the whole leg. The floor
        /// is what has to SURVIVE the fare: a bot that lands somewhere far from home having spent
        /// its last coin cannot buy potions and cannot buy a way back, which is worse than the walk
        /// it was trying to avoid.
        /// </summary>
        public bool Allows(MirClass mirClass, int level, long gold, long goldFloor, int pkPoints,
            int maxGoldPercent = 0)
        {
            if (Teleport != null)
            {
                if (!Teleport.Allows(mirClass, level, pkPoints)) return false;
                // Two separate tests, because a dialogue asks two separate questions. A Gold
                // CHECK wants a balance and takes nothing; TakeGold is what actually charges. A
                // route that wants you to hold 50,000 and charges 5,000 must be judged on both,
                // not on a single invented 55,000 figure.
                if (gold < Teleport.RequiredStartingGold) return false;
                if (gold - Teleport.Cost < goldFloor) return false;

                // Proportion as well as remainder. See TeleportMaxGoldPercent: the floor is a fixed
                // line and a rich-ish bot can clear it over and over while spending half of
                // everything it has on getting about.
                if (maxGoldPercent > 0 && gold < long.MaxValue &&
                    Teleport.Cost * 100L > gold * maxGoldPercent) return false;
            }

            return Allows(mirClass, level);
        }

        public bool Allows(MirClass mirClass, int level)
        {
            if (NeedsItem || NeedsInstance) return false;
            if (MinimumLevel > level) return false;
            if (MaximumLevel > 0 && MaximumLevel < level) return false;

            if (RequiredClass == RequiredClass.None || RequiredClass == RequiredClass.All) return true;

            RequiredClass flag = mirClass switch
            {
                MirClass.Warrior => RequiredClass.Warrior,
                MirClass.Wizard => RequiredClass.Wizard,
                MirClass.Taoist => RequiredClass.Taoist,
                MirClass.Assassin => RequiredClass.Assassin,
                _ => RequiredClass.None
            };

            return flag != RequiredClass.None && RequiredClass.HasFlag(flag);
        }
    }

    /// <summary>
    /// The map-link graph, built once from MovementInfo in System.db.
    ///
    /// This is the part the Mir 2 agents had to learn by walking through every exit on the server
    /// and writing down where they came out. We do not: MovementInfo already pairs a source region
    /// with a destination region, and MapRegion.CreatePoints turns a region into concrete cells once
    /// the map's width is known. Their single largest piece of engineering is a lookup for us.
    ///
    /// Walking onto a cell that carries a movement is what triggers it - see Map.GetMovement. The
    /// destination is one of the destination region's points chosen at random by the server, so a
    /// route can only ever plan as far as "the next map", never to an exact arrival tile.
    /// </summary>
    public sealed class WorldGraph
    {
        private readonly Dictionary<int, List<MapExit>> _exits = new Dictionary<int, List<MapExit>>();

        /// <summary>Built on demand by ExitCellsOn and thrown away whenever the graph is rebuilt.</summary>
        private readonly Dictionary<int, HashSet<Point>> _exitCells = new Dictionary<int, HashSet<Point>>();

        public int MapCount => _exits.Count;
        public int ExitCount { get; private set; }
        public int SkippedCount { get; private set; }

        /// <summary>
        /// Needs the map library: a region is stored as a bitmap whose width is the map's, so the
        /// grid has to be loaded before a region can be turned into points.
        /// </summary>
        public void Build(MapLibrary maps)
        {
            _exits.Clear();
            _exitCells.Clear();
            ExitCount = 0;
            SkippedCount = 0;

            if (Globals.MovementInfoList?.Binding == null) return;

            foreach (MovementInfo movement in Globals.MovementInfoList.Binding)
            {
                MapRegion source = movement.SourceRegion;
                MapRegion destination = movement.DestinationRegion;

                if (source?.Map == null || destination?.Map == null)
                {
                    SkippedCount++;
                    continue;
                }

                MapGrid grid = maps?.For(source.Map.Index);

                if (grid == null)
                {
                    SkippedCount++;
                    continue;
                }

                List<Point> cells = Points(source, grid);

                if (cells.Count == 0)
                {
                    SkippedCount++;
                    continue;
                }

                MapExit exit = new MapExit
                {
                    FromMapIndex = source.Map.Index,
                    ToMapIndex = destination.Map.Index,
                    ToMapName = destination.Map.Description ?? "",
                    Cells = cells,
                    MinimumLevel = destination.Map.MinimumLevel,
                    MaximumLevel = destination.Map.MaximumLevel,
                    RequiredClass = destination.Map.RequiredClass,
                    NeedsItem = movement.NeedItem != null,
                    NeedsInstance = movement.NeedInstance != null
                };

                if (!_exits.TryGetValue(exit.FromMapIndex, out List<MapExit> list))
                    _exits[exit.FromMapIndex] = list = new List<MapExit>();

                list.Add(exit);
                ExitCount++;
            }
        }

        /// <summary>
        /// Add the teleport NPCs as extra edges, so one search covers both ways of crossing a map
        /// boundary and the cheaper one wins on its merits.
        ///
        /// Worth saying plainly what this bought on THIS server, because the answer was not what
        /// was expected: dumping the data found three teleport NPCs in total, all free, and none of
        /// them a travel network. Two are Hexa Holy Stones deep inside Banya Temple that return the
        /// character to the temple hall, and one sits in Bichon Town and leads to Assassin's
        /// Hideout. So this does not turn cross-country travel into a taxi ride - there is no taxi
        /// on this server. What it does do is give the bot the dungeon exits a player would use,
        /// and the gold accounting is already right for whenever content adds a paid one.
        /// </summary>
        public void AddTeleports(TeleportDirectory teleports)
        {
            if (teleports == null) return;

            foreach (TeleportRoute route in teleports.Routes)
            {
                if (route.NPC?.Region?.Map == null) continue;

                Point spot = route.NPCPoint;
                if (spot == Point.Empty) continue;

                // The destination's own gates apply exactly as they do to a walked exit. Leaving
                // them off looked harmless and was not: a level 18 WARRIOR was cheerfully routed
                // into Assassin's Hideout, walked ninety tiles across Bichon to the teleporter,
                // called it, and stood there while nothing happened - because the map is gated to
                // Assassins and the server simply declined. A gate the planner does not know about
                // is a journey the bot cannot be talked out of.
                MapInfo destination = route.Destination;

                MapExit exit = new MapExit
                {
                    FromMapIndex = route.FromMapIndex,
                    ToMapIndex = route.ToMapIndex,
                    ToMapName = route.ToMapName,
                    Cells = new[] { spot },
                    Teleport = route,
                    MinimumLevel = destination?.MinimumLevel ?? 0,
                    MaximumLevel = destination?.MaximumLevel ?? 0,
                    RequiredClass = destination?.RequiredClass ?? RequiredClass.None
                };

                if (!_exits.TryGetValue(exit.FromMapIndex, out List<MapExit> list))
                    _exits[exit.FromMapIndex] = list = new List<MapExit>();

                list.Add(exit);
                _exitCells.Remove(exit.FromMapIndex);
                ExitCount++;
                TeleportCount++;
            }
        }

        public int TeleportCount { get; private set; }

        private static List<Point> Points(MapRegion region, MapGrid grid)
        {
            // CreatePoints caches into PointList; it needs the map width to decode a bit region.
            if (region.PointList == null || region.PointList.Count == 0)
                region.CreatePoints(grid.Width);

            List<Point> cells = new List<Point>();

            if (region.PointList == null) return cells;

            foreach (Point point in region.PointList)
                if (grid.Walkable(point)) cells.Add(point);

            return cells;
        }

        /// <summary>Can anything reach this map at all? Used to keep dead maps out of menus.</summary>
        public bool IsDestination(int mapIndex)
        {
            foreach (List<MapExit> list in _exits.Values)
                foreach (MapExit exit in list)
                    if (exit.ToMapIndex == mapIndex) return true;

            return false;
        }

        public IReadOnlyList<MapExit> ExitsFrom(int mapIndex) =>
            _exits.TryGetValue(mapIndex, out List<MapExit> list)
                ? list
                : (IReadOnlyList<MapExit>)Array.Empty<MapExit>();

        /// <summary>
        /// Every cell on this map that will move the character to another one.
        ///
        /// Walking onto one of these is how a map change happens, which makes them doors when the
        /// bot means to travel and traps the rest of the time. The server drops an arriving
        /// character at a random point of the destination region, and that region is the paired one
        /// for the exit coming back - so a bot begins its stay on a map standing on or beside the
        /// way out, and a wander that does not know about these walks straight back through one.
        /// That is not hypothetical: bot 1 oscillated between Sabuk Keep and Banya Village until
        /// monsters happened to block the cell.
        ///
        /// Teleport exits are excluded deliberately. Their Cells hold the NPC's own tile rather
        /// than a trigger, so nothing happens by standing there - and treating it as an obstacle
        /// would make the teleporter unapproachable on the one journey that needs it.
        /// </summary>
        public HashSet<Point> ExitCellsOn(int mapIndex)
        {
            if (_exitCells.TryGetValue(mapIndex, out HashSet<Point> cached)) return cached;

            HashSet<Point> cells = new HashSet<Point>();

            foreach (MapExit exit in ExitsFrom(mapIndex))
            {
                if (exit.IsTeleport) continue;

                foreach (Point cell in exit.Cells) cells.Add(cell);
            }

            _exitCells[mapIndex] = cells;
            return cells;
        }

        /// <summary>
        /// Fewest map changes from one map to another, or null when there is no usable route.
        ///
        /// Breadth-first over maps rather than a weighted search over cells: the cost of a journey
        /// is dominated by how many maps it crosses, and the within-map legs are planned separately
        /// by A* as each one is walked. Exits the character cannot use - level gates, class gates,
        /// anything needing an item or an instance - are excluded here rather than discovered by
        /// walking into them.
        /// </summary>
        public List<MapExit> Route(int fromMapIndex, int toMapIndex, MirClass mirClass, int level,
            long gold = long.MaxValue, long goldFloor = 0, int pkPoints = 0,
            int maxGoldPercent = 0)
        {
            if (fromMapIndex == toMapIndex) return new List<MapExit>();

            Queue<int> queue = new Queue<int>();
            Dictionary<int, MapExit> cameBy = new Dictionary<int, MapExit>();
            HashSet<int> seen = new HashSet<int> { fromMapIndex };

            queue.Enqueue(fromMapIndex);

            while (queue.Count > 0)
            {
                int map = queue.Dequeue();

                foreach (MapExit exit in ExitsFrom(map))
                {
                    if (seen.Contains(exit.ToMapIndex)) continue;
                    if (!exit.Allows(mirClass, level, gold, goldFloor, pkPoints, maxGoldPercent)) continue;

                    seen.Add(exit.ToMapIndex);
                    cameBy[exit.ToMapIndex] = exit;

                    if (exit.ToMapIndex == toMapIndex) return Reconstruct(cameBy, fromMapIndex, toMapIndex);

                    queue.Enqueue(exit.ToMapIndex);
                }
            }

            return null;
        }

        private static List<MapExit> Reconstruct(Dictionary<int, MapExit> cameBy, int from, int to)
        {
            List<MapExit> route = new List<MapExit>();
            int current = to;

            while (current != from)
            {
                if (!cameBy.TryGetValue(current, out MapExit exit)) return null;

                route.Add(exit);
                current = exit.FromMapIndex;
            }

            route.Reverse();
            return route;
        }

        /// <summary>
        /// How many map transitions each reachable map is away, in one breadth-first pass.
        ///
        /// Reachable answers "can I get there" and that was enough while travel was free. It is not
        /// enough to answer "can I AFFORD to get there": a journey's real cost scales with the
        /// number of maps crossed, because every hop is another map to walk back over if it goes
        /// wrong, and arriving somewhere distant with an empty purse is how a bot dies far from a
        /// vendor. Routing each candidate separately would be a BFS per map; this is one.
        /// </summary>
        public Dictionary<int, int> HopCounts(int fromMapIndex, MirClass mirClass, int level,
            long gold = long.MaxValue, long goldFloor = 0, int pkPoints = 0,
            int maxGoldPercent = 0)
        {
            Dictionary<int, int> hops = new Dictionary<int, int> { [fromMapIndex] = 0 };
            Queue<int> queue = new Queue<int>();

            queue.Enqueue(fromMapIndex);

            while (queue.Count > 0)
            {
                int map = queue.Dequeue();
                int next = hops[map] + 1;

                foreach (MapExit exit in ExitsFrom(map))
                {
                    if (hops.ContainsKey(exit.ToMapIndex)) continue;
                    if (!exit.Allows(mirClass, level, gold, goldFloor, pkPoints, maxGoldPercent)) continue;

                    hops[exit.ToMapIndex] = next;
                    queue.Enqueue(exit.ToMapIndex);
                }
            }

            hops.Remove(fromMapIndex);
            return hops;
        }

        /// <summary>Every map reachable from here, for reporting and for choosing where to hunt.</summary>
        public List<int> Reachable(int fromMapIndex, MirClass mirClass, int level,
            long gold = long.MaxValue, long goldFloor = 0, int pkPoints = 0,
            int maxGoldPercent = 0)
        {
            List<int> found = new List<int>();
            Queue<int> queue = new Queue<int>();
            HashSet<int> seen = new HashSet<int> { fromMapIndex };

            queue.Enqueue(fromMapIndex);

            while (queue.Count > 0)
            {
                foreach (MapExit exit in ExitsFrom(queue.Dequeue()))
                {
                    if (seen.Contains(exit.ToMapIndex)) continue;
                    if (!exit.Allows(mirClass, level, gold, goldFloor, pkPoints, maxGoldPercent)) continue;

                    seen.Add(exit.ToMapIndex);
                    found.Add(exit.ToMapIndex);
                    queue.Enqueue(exit.ToMapIndex);
                }
            }

            return found;
        }

        public string Describe() =>
            $"world graph: {ExitCount} exits across {MapCount} maps " +
            $"({TeleportCount} via NPC, {SkippedCount} unusable)";
    }
}
