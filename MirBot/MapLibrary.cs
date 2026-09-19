using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    /// <summary>
    /// Every map's walkability, loaded on demand and shared by all bots in the host.
    ///
    /// Maps are static and read-only, so one copy serves every bot; a 1000x1000 map costs about a
    /// megabyte as a bitmap against nearly four hundred on disk. Loading is done under a lock
    /// because bot threads share this, and it happens once per map for the life of the process.
    ///
    /// A missing or unreadable map is cached as a null result rather than retried: the bot falls
    /// back to blind steering for that map, which is what it did everywhere before, so a wrong
    /// MapPath degrades the bot instead of stopping it.
    /// </summary>
    public sealed class MapLibrary
    {
        private readonly string _mapPath;
        private readonly object _sync = new object();
        private readonly Dictionary<int, MapGrid> _grids = new Dictionary<int, MapGrid>();

        public int LoadedCount { get; private set; }
        public int MissingCount { get; private set; }

        public MapLibrary(string mapPath)
        {
            _mapPath = mapPath;
        }

        public bool Available => !string.IsNullOrWhiteSpace(_mapPath) && Directory.Exists(_mapPath);

        /// <summary>The grid for a map index, or null when it cannot be had.</summary>
        public MapGrid For(int mapIndex)
        {
            if (!Available) return null;

            lock (_sync)
            {
                if (_grids.TryGetValue(mapIndex, out MapGrid cached)) return cached;

                MapGrid grid = LoadLocked(mapIndex);
                _grids[mapIndex] = grid;

                if (grid == null) MissingCount++;
                else LoadedCount++;

                return grid;
            }
        }

        private MapGrid LoadLocked(int mapIndex)
        {
            MapInfo info = Globals.MapInfoList?.Binding?.FirstOrDefault(x => x.Index == mapIndex);

            if (info == null || string.IsNullOrWhiteSpace(info.FileName)) return null;

            return MapGrid.Load(Path.Combine(_mapPath, info.FileName + ".map"));
        }

        public string Describe()
        {
            if (!Available)
                return $"maps: none at '{_mapPath}' - falling back to blind steering";

            return $"maps: {_mapPath} ({LoadedCount} loaded, {MissingCount} missing)";
        }
    }
}
