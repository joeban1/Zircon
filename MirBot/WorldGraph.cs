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
        /// Fewest map changes from one map to another, or null when there is no usable route.
        ///
        /// Breadth-first over maps rather than a weighted search over cells: the cost of a journey
        /// is dominated by how many maps it crosses, and the within-map legs are planned separately
        /// by A* as each one is walked. Exits the character cannot use - level gates, class gates,
        /// anything needing an item or an instance - are excluded here rather than discovered by
        /// walking into them.
        /// </summary>
        public List<MapExit> Route(int fromMapIndex, int toMapIndex, MirClass mirClass, int level)
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
                    if (!exit.Allows(mirClass, level)) continue;

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

        /// <summary>Every map reachable from here, for reporting and for choosing where to hunt.</summary>
        public List<int> Reachable(int fromMapIndex, MirClass mirClass, int level)
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
                    if (!exit.Allows(mirClass, level)) continue;

                    seen.Add(exit.ToMapIndex);
                    found.Add(exit.ToMapIndex);
                    queue.Enqueue(exit.ToMapIndex);
                }
            }

            return found;
        }

        public string Describe() =>
            $"world graph: {ExitCount} exits across {MapCount} maps ({SkippedCount} unusable)";
    }
}
