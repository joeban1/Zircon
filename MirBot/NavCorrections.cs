using System;
using System.Collections.Generic;
using System.Drawing;

namespace MirBot
{
    public sealed class NavMapEntry
    {
        public int MapIndex { get; set; }
        public string MapName { get; set; } = "";

        /// <summary>
        /// Cells the server has refused to let us stand on, packed as x * 10000 + y.
        ///
        /// Packed rather than stored as objects because there can be a lot of them and every one
        /// costs a JSON object otherwise. 10000 is comfortably above the largest map on the server.
        /// </summary>
        public List<int> Blocked { get; set; } = new List<int>();

        public DateTime UpdatedUtc { get; set; }
    }

    /// <summary>
    /// Cells the map file says we can walk on and the server says we cannot.
    ///
    /// This is the Mir 2 agents' NavData idea, and it is worth far less to us than it is to them:
    /// their client cannot see the server's map data at all, so their walkable set is a guess that
    /// needs constant correction. We parse the same .map files the server parses, with the same
    /// rule, so the grid is right almost everywhere and this layer should stay nearly empty.
    ///
    /// Where it earns its place is the handful of things the .map flag does not describe. Doors and
    /// gates are the clear case: the tile is marked walkable because it is a doorway, and whether
    /// you may actually step through it is a decision the server makes at the time. Region edges
    /// and scripted blockers behave the same way. Without this the bot plans a route through such a
    /// cell, is refused, replans the identical route, and grinds there until the pursuit watchdog
    /// gives up on whatever it was chasing.
    ///
    /// Two rules keep it honest:
    ///
    /// - Evidence before belief. A single refusal is far more likely to be a monster standing in
    ///   the doorway than the map being wrong, so a cell has to refuse us several times before it
    ///   is routed around, and refusals are only counted when we can see nothing standing there.
    /// - Success erases. Walking onto a cell is proof it is passable, and that clears whatever was
    ///   recorded against it. A gate that opens therefore un-learns itself, which is the whole
    ///   reason a door is worth learning about rather than simply being hard-coded as a wall.
    /// </summary>
    public sealed class NavCorrections : MemoryBank<NavMapEntry>
    {
        private const int Stride = 10000;

        private readonly Dictionary<int, NavMapEntry> _byMap = new Dictionary<int, NavMapEntry>();

        /// <summary>Refusals seen but not yet believed. Deliberately NOT persisted.</summary>
        private readonly Dictionary<long, int> _suspected = new Dictionary<long, int>();

        /// <summary>Believed cells per map, as the set PathFinder wants. Rebuilt on change.</summary>
        private readonly Dictionary<int, HashSet<Point>> _cache = new Dictionary<int, HashSet<Point>>();

        public NavCorrections(string path) : base(path)
        {
            Load();
        }

        protected override void Reindex()
        {
            _byMap.Clear();
            _cache.Clear();

            foreach (NavMapEntry entry in Entries) _byMap[entry.MapIndex] = entry;
        }

        private static int Pack(Point cell) => cell.X * Stride + cell.Y;
        private static Point Unpack(int packed) => new Point(packed / Stride, packed % Stride);
        private static long Key(int mapIndex, Point cell) => (long)mapIndex << 32 | (uint)Pack(cell);

        /// <summary>
        /// The server would not let us step onto this cell.
        ///
        /// Returns true when that has now happened often enough to be believed, which is the moment
        /// worth logging - before then it is indistinguishable from ordinary traffic.
        /// </summary>
        public bool Refused(int mapIndex, string mapName, Point cell, int evidenceNeeded)
        {
            long key = Key(mapIndex, cell);

            lock (Sync)
            {
                _suspected.TryGetValue(key, out int seen);
                seen++;

                if (seen < Math.Max(1, evidenceNeeded))
                {
                    _suspected[key] = seen;
                    return false;
                }

                _suspected.Remove(key);

                NavMapEntry entry = Find(mapIndex, mapName);
                int packed = Pack(cell);

                if (entry.Blocked.Contains(packed)) return false;

                entry.Blocked.Add(packed);
                entry.UpdatedUtc = DateTime.UtcNow;
                _cache.Remove(mapIndex);

                MarkDirty();
                return true;
            }
        }

        /// <summary>
        /// We are standing on it, so it is passable after all. Forgets anything held against it.
        /// </summary>
        public bool Cleared(int mapIndex, Point cell)
        {
            long key = Key(mapIndex, cell);

            lock (Sync)
            {
                _suspected.Remove(key);

                if (!_byMap.TryGetValue(mapIndex, out NavMapEntry entry)) return false;
                if (!entry.Blocked.Remove(Pack(cell))) return false;

                entry.UpdatedUtc = DateTime.UtcNow;
                _cache.Remove(mapIndex);

                MarkDirty();
                return true;
            }
        }

        /// <summary>Believed-blocked cells on a map, or null when there are none.</summary>
        public HashSet<Point> BlockedOn(int mapIndex)
        {
            lock (Sync)
            {
                if (_cache.TryGetValue(mapIndex, out HashSet<Point> cached)) return cached;

                if (!_byMap.TryGetValue(mapIndex, out NavMapEntry entry) || entry.Blocked.Count == 0)
                {
                    _cache[mapIndex] = null;
                    return null;
                }

                HashSet<Point> set = new HashSet<Point>();
                foreach (int packed in entry.Blocked) set.Add(Unpack(packed));

                _cache[mapIndex] = set;
                return set;
            }
        }

        /// <summary>Call with the lock held.</summary>
        private NavMapEntry Find(int mapIndex, string mapName)
        {
            if (_byMap.TryGetValue(mapIndex, out NavMapEntry entry))
            {
                if (string.IsNullOrEmpty(entry.MapName) && !string.IsNullOrEmpty(mapName))
                    entry.MapName = mapName;

                return entry;
            }

            entry = new NavMapEntry { MapIndex = mapIndex, MapName = mapName ?? "" };
            Entries.Add(entry);
            _byMap[mapIndex] = entry;
            return entry;
        }

        public string Describe()
        {
            lock (Sync)
            {
                int cells = 0;
                foreach (NavMapEntry entry in Entries) cells += entry.Blocked.Count;

                return $"{cells} learned blocked cells on {Entries.Count} maps at " +
                       System.IO.Path.GetFileName(FilePath);
            }
        }
    }
}
