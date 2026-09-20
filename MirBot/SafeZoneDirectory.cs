using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Library.SystemModels;

namespace MirBot
{
    /// <summary>
    /// Where the safe zones are, per map.
    ///
    /// Storage is only reachable from inside one (PlayerObject.cs:7421), and the town trip used to
    /// attempt banking wherever the itinerary happened to finish - on the assumption that vendors
    /// stand in safe zones. Nobody had checked. Across a full log the bots had made zero Deposit
    /// and zero Withdraw decisions: every trip reached the bank step, found itself outside a safe
    /// zone, and skipped it silently. Items flagged WorthStoring are excluded from the sell list
    /// on the promise that they will be banked, so the promise being unkept meant they rode around
    /// in the bag for ever, filling slots that nothing else could use.
    ///
    /// SafeZoneInfo is not one of the collections GameDatabase binds onto Globals, so it is read
    /// from the session directly. The region is a bit-packed bitmap like every other MapRegion and
    /// needs the map width before it can be turned into cells.
    /// </summary>
    public sealed class SafeZoneDirectory
    {
        private readonly Dictionary<int, List<Point>> _cells = new Dictionary<int, List<Point>>();

        public int MapCount => _cells.Count;
        public int CellCount { get; private set; }

        public void Build(MapLibrary maps)
        {
            _cells.Clear();
            CellCount = 0;

            if (GameDatabase.Session == null) return;

            foreach (SafeZoneInfo zone in GameDatabase.Session.GetCollection<SafeZoneInfo>().Binding)
            {
                MapRegion region = zone?.Region;

                if (region?.Map == null) continue;

                MapGrid grid = maps?.For(region.Map.Index);

                if (grid == null) continue;

                if (region.PointList == null || region.PointList.Count == 0)
                    region.CreatePoints(grid.Width);

                if (region.PointList == null) continue;

                if (!_cells.TryGetValue(region.Map.Index, out List<Point> list))
                    _cells[region.Map.Index] = list = new List<Point>();

                // Bind points first where the zone names any: those are cells the server itself
                // materialises players on, so they are known-good standing ground rather than
                // merely inside the region.
                IEnumerable<Point> candidates = zone.ValidBindPoints != null && zone.ValidBindPoints.Count > 0
                    ? zone.ValidBindPoints.Concat(region.PointList)
                    : region.PointList;

                foreach (Point point in candidates)
                {
                    if (!grid.Walkable(point)) continue;
                    if (list.Contains(point)) continue;

                    list.Add(point);
                    CellCount++;
                }
            }
        }

        public bool Has(int mapIndex) => _cells.ContainsKey(mapIndex) && _cells[mapIndex].Count > 0;

        public IReadOnlyList<Point> On(int mapIndex) =>
            _cells.TryGetValue(mapIndex, out List<Point> list)
                ? list
                : (IReadOnlyList<Point>)Array.Empty<Point>();

        /// <summary>Closest safe cell on this map, or Point.Empty when the map has no safe zone.</summary>
        public Point Nearest(int mapIndex, Point from)
        {
            Point best = Point.Empty;
            int bestDistance = int.MaxValue;

            foreach (Point cell in On(mapIndex))
            {
                int distance = Math.Max(Math.Abs(cell.X - from.X), Math.Abs(cell.Y - from.Y));

                if (distance >= bestDistance) continue;

                bestDistance = distance;
                best = cell;
            }

            return best;
        }
    }
}
