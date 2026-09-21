using System;
using System.Collections.Generic;
using System.Drawing;

namespace MirBot
{
    /// <summary>A destination selected from one coarse exploration sector.</summary>
    public sealed record ExplorationChoice(
        Point Target,
        int SectorX,
        int SectorY,
        bool MapWide,
        DateTime? LastVisitedUtc);

    /// <summary>Read-only counters for the status snapshot.</summary>
    public sealed record ExplorationSnapshot(int VisitedSectors, int TotalSectors);

    /// <summary>
    /// Per-bot, per-session memory of where a character has actually been.
    ///
    /// Crystal's agents permanently remove individual visited cells. That is useful for covering a
    /// static map once, but monsters respawn: permanent depletion eventually turns itself off. This
    /// version timestamps coarse sectors instead. Unseen wins first; after the map has been covered,
    /// the least-recently visited sector naturally becomes interesting again.
    ///
    /// This is deliberately separate from NavCorrections. Coverage says "we have been here";
    /// NavCorrections says "the server repeatedly refused this cell". Mixing the two would make a
    /// temporarily stale hunting area look physically impassable.
    /// </summary>
    public sealed class ExplorationCoverage
    {
        public const int SectorSize = 16;

        private readonly Dictionary<int, MapState> _maps = new Dictionary<int, MapState>();

        private sealed class MapState
        {
            public int Width;
            public int Height;
            public readonly List<Sector> Eligible = new List<Sector>();
            public readonly Dictionary<long, DateTime> Visited = new Dictionary<long, DateTime>();
            public readonly Dictionary<long, DateTime> BarredUntil = new Dictionary<long, DateTime>();
        }

        private readonly struct Sector
        {
            public readonly int X;
            public readonly int Y;

            public Sector(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        /// <summary>
        /// Record an authoritative location. This is cheap and may be called on every decision tick;
        /// the map-wide walkability scan is deferred until a target or snapshot actually needs it.
        /// </summary>
        public void Observe(int mapIndex, Point location, DateTime utc)
        {
            MapState state = StateFor(mapIndex);
            state.Visited[Key(location.X / SectorSize, location.Y / SectorSize)] = utc;
        }

        /// <summary>
        /// Pick the oldest eligible sector, using reservoir sampling to randomise equal-age ties.
        /// No path search happens here. The caller validates this one destination through its
        /// existing A*, which prevents an exploration decision from running several 20,000-node
        /// searches on a bot thread.
        /// </summary>
        public ExplorationChoice Choose(int mapIndex, MapGrid grid, Point origin, int radius,
            bool mapWide, HashSet<Point> excluded, Random random, DateTime utc)
        {
            if (grid == null || random == null) return null;

            MapState state = EnsureIndexed(mapIndex, grid);

            // A sector whose only usable point has become excluded should not stop selection. Try
            // a few next-oldest sectors, but still return only one destination for A* validation.
            for (int pass = 0; pass < 8; pass++)
            {
                Sector? chosen = null;
                DateTime oldest = DateTime.MaxValue;
                int ties = 0;

                foreach (Sector sector in state.Eligible)
                {
                    long key = Key(sector.X, sector.Y);

                    if (IsBarred(state, key, utc)) continue;
                    if (!mapWide && DistanceToSector(origin, sector) > radius) continue;

                    DateTime visited = state.Visited.TryGetValue(key, out DateTime seen)
                        ? seen
                        : DateTime.MinValue;

                    if (visited < oldest)
                    {
                        chosen = sector;
                        oldest = visited;
                        ties = 1;
                    }
                    else if (visited == oldest && random.Next(++ties) == 0)
                    {
                        chosen = sector;
                    }
                }

                if (!chosen.HasValue) return null;

                Sector sectorChoice = chosen.Value;
                Point target = PointInSector(grid, sectorChoice, origin,
                    mapWide ? 0 : radius, excluded, random);

                if (target != Point.Empty)
                    return new ExplorationChoice(
                        target,
                        sectorChoice.X,
                        sectorChoice.Y,
                        mapWide,
                        oldest == DateTime.MinValue ? (DateTime?)null : oldest);

                // Every walkable cell in this sector is currently unsuitable (normally an exit or
                // a learned refusal). A short sentence prevents choosing it eight times in a row.
                state.BarredUntil[Key(sectorChoice.X, sectorChoice.Y)] = utc.AddSeconds(30);
            }

            return null;
        }

        public void Bar(int mapIndex, Point target, DateTime until)
        {
            StateFor(mapIndex).BarredUntil[Key(target.X / SectorSize, target.Y / SectorSize)] = until;
        }

        public ExplorationSnapshot Snapshot(int mapIndex, MapGrid grid)
        {
            if (grid == null) return new ExplorationSnapshot(0, 0);

            MapState state = EnsureIndexed(mapIndex, grid);
            int visited = 0;

            foreach (Sector sector in state.Eligible)
                if (state.Visited.ContainsKey(Key(sector.X, sector.Y))) visited++;

            return new ExplorationSnapshot(visited, state.Eligible.Count);
        }

        private MapState StateFor(int mapIndex)
        {
            if (_maps.TryGetValue(mapIndex, out MapState state)) return state;

            state = new MapState();
            _maps.Add(mapIndex, state);
            return state;
        }

        private MapState EnsureIndexed(int mapIndex, MapGrid grid)
        {
            MapState state = StateFor(mapIndex);

            if (state.Width == grid.Width && state.Height == grid.Height && state.Eligible.Count > 0)
                return state;

            state.Width = grid.Width;
            state.Height = grid.Height;
            state.Eligible.Clear();

            int sectorsX = (grid.Width + SectorSize - 1) / SectorSize;
            int sectorsY = (grid.Height + SectorSize - 1) / SectorSize;

            for (int sx = 0; sx < sectorsX; sx++)
                for (int sy = 0; sy < sectorsY; sy++)
                    if (HasWalkableCell(grid, sx, sy)) state.Eligible.Add(new Sector(sx, sy));

            return state;
        }

        private static bool HasWalkableCell(MapGrid grid, int sx, int sy)
        {
            int left = sx * SectorSize;
            int top = sy * SectorSize;
            int right = Math.Min(grid.Width, left + SectorSize);
            int bottom = Math.Min(grid.Height, top + SectorSize);

            for (int x = left; x < right; x++)
                for (int y = top; y < bottom; y++)
                    if (grid.Walkable(x, y)) return true;

            return false;
        }

        private static Point PointInSector(MapGrid grid, Sector sector, Point origin, int radius,
            HashSet<Point> excluded, Random random)
        {
            int left = sector.X * SectorSize;
            int top = sector.Y * SectorSize;
            int right = Math.Min(grid.Width, left + SectorSize);
            int bottom = Math.Min(grid.Height, top + SectorSize);
            int width = right - left;
            int height = bottom - top;
            int cells = width * height;

            if (cells <= 0) return Point.Empty;

            int start = random.Next(cells);

            for (int i = 0; i < cells; i++)
            {
                int offset = (start + i) % cells;
                Point candidate = new Point(left + offset / height, top + offset % height);
                int distance = Chebyshev(origin, candidate);

                if (!grid.Walkable(candidate)) continue;
                if (distance < 4) continue;
                if (radius > 0 && distance > radius) continue;
                if (excluded != null && excluded.Contains(candidate)) continue;

                return candidate;
            }

            return Point.Empty;
        }

        private static int DistanceToSector(Point origin, Sector sector)
        {
            int left = sector.X * SectorSize;
            int top = sector.Y * SectorSize;
            int right = left + SectorSize - 1;
            int bottom = top + SectorSize - 1;

            int dx = origin.X < left ? left - origin.X : origin.X > right ? origin.X - right : 0;
            int dy = origin.Y < top ? top - origin.Y : origin.Y > bottom ? origin.Y - bottom : 0;

            return Math.Max(dx, dy);
        }

        private static bool IsBarred(MapState state, long key, DateTime utc)
        {
            if (!state.BarredUntil.TryGetValue(key, out DateTime until)) return false;
            if (utc < until) return true;

            state.BarredUntil.Remove(key);
            return false;
        }

        private static int Chebyshev(Point a, Point b) =>
            Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        private static long Key(int sectorX, int sectorY) =>
            ((long)sectorX << 32) | (uint)sectorY;
    }
}
