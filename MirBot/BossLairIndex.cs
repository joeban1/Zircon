using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    /// <summary>One boss spawn region: where to stand to find it, and how often it comes back.</summary>
    public sealed class BossLair
    {
        public int MapIndex;
        public int MonsterIndex;
        public string MonsterName = "";
        public Point Centre;
        public int Spawns;
        public int RespawnMinutes;

        /// <summary>A boss actually seen (in view or on the tracker), not a spawn-region guess.</summary>
        public bool Sighted;
    }

    /// <summary>
    /// Where each boss lives, from System.db's respawn regions.
    ///
    /// The Lv 3 cave bosses are the only source of the Taoist summons and the warriors' Blade
    /// Storm, Dragon Rise and Flaming Sword, and there are one to six of them on a map packed with
    /// ordinary monsters. Coverage roaming finds them eventually, but a bot usually fills its bag
    /// long before it wanders into the right corner. The spawn regions are fixed and known, so the
    /// bot can walk to them instead of searching.
    ///
    /// The centre is the walkable cell of the region nearest its centroid - a region is a bitmap
    /// that can include walls, and its average point can be one.
    /// </summary>
    public sealed class BossLairIndex
    {
        private readonly Dictionary<int, List<BossLair>> _byMap = new Dictionary<int, List<BossLair>>();

        public int LairCount => _byMap.Values.Sum(x => x.Count);

        public IReadOnlyList<BossLair> On(int mapIndex) =>
            _byMap.TryGetValue(mapIndex, out List<BossLair> lairs) ? lairs : Array.Empty<BossLair>();

        /// <summary>Every lair of one boss, on any map.</summary>
        public IReadOnlyList<BossLair> ForMonster(int monsterIndex) =>
            _byMap.Values.SelectMany(x => x).Where(l => l.MonsterIndex == monsterIndex).ToList();

        /// <summary>The maps one boss spawns on.</summary>
        public IReadOnlyList<int> MapsForMonster(int monsterIndex) =>
            ForMonster(monsterIndex).Select(l => l.MapIndex).Distinct().OrderBy(x => x).ToList();

        private MapLibrary _maps;
        private readonly Dictionary<(int Monster, int Map), List<Point>> _spawnCells =
            new Dictionary<(int, int), List<Point>>();

        /// <summary>
        /// Walkable cells of every region where ANY monster spawns on a map - built on first use
        /// and cached. Quest hunting uses it: Forest Yetis live only in Bichon Town's outer
        /// "Spawn Ring 2", which random local roaming near Joeban almost never reaches.
        /// </summary>
        public IReadOnlyList<Point> SpawnCells(int monsterIndex, int mapIndex)
        {
            if (_spawnCells.TryGetValue((monsterIndex, mapIndex), out List<Point> cached)) return cached;

            List<Point> cells = new List<Point>();
            try
            {
                MonsterInfo monster = Globals.MonsterInfoList?.Binding?.FirstOrDefault(m => m.Index == monsterIndex);
                MapGrid grid = _maps?.For(mapIndex);
                if (monster?.Respawns != null && grid != null)
                    foreach (RespawnInfo respawn in monster.Respawns)
                    {
                        MapRegion region = respawn?.Region;
                        if (region?.Map?.Index != mapIndex || respawn.EventSpawn || respawn.Count <= 0) continue;
                        if (region.PointList == null || region.PointList.Count == 0)
                            region.CreatePoints(grid.Width);
                        if (region.PointList != null) cells.AddRange(region.PointList.Where(grid.Walkable));
                    }
            }
            catch
            {
                cells.Clear();
            }

            _spawnCells[(monsterIndex, mapIndex)] = cells;
            return cells;
        }

        public void Build(MapLibrary maps)
        {
            _maps = maps;
            _spawnCells.Clear();
            _byMap.Clear();

            try
            {
                foreach (MonsterInfo monster in Globals.MonsterInfoList?.Binding ??
                                                Enumerable.Empty<MonsterInfo>())
                {
                    if (monster == null || !monster.IsBoss || monster.Respawns == null) continue;

                    foreach (RespawnInfo respawn in monster.Respawns)
                    {
                        MapRegion region = respawn?.Region;
                        if (region?.Map == null || respawn.EventSpawn || respawn.Count <= 0) continue;

                        MapGrid grid = maps?.For(region.Map.Index);
                        if (grid == null) continue;

                        if (region.PointList == null || region.PointList.Count == 0)
                            region.CreatePoints(grid.Width);

                        List<Point> cells = region.PointList?.Where(grid.Walkable).ToList();
                        if (cells == null || cells.Count == 0) continue;

                        double cx = cells.Average(p => p.X), cy = cells.Average(p => p.Y);
                        Point centre = cells.OrderBy(p => (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy))
                            .First();

                        if (!_byMap.TryGetValue(region.Map.Index, out List<BossLair> list))
                            _byMap[region.Map.Index] = list = new List<BossLair>();

                        list.Add(new BossLair
                        {
                            MapIndex = region.Map.Index,
                            MonsterIndex = monster.Index,
                            MonsterName = monster.MonsterName ?? "boss",
                            Centre = centre,
                            Spawns = respawn.Count,
                            RespawnMinutes = Math.Max(1, respawn.Delay)
                        });
                    }
                }
            }
            catch
            {
                // Pre-login or no database: no lairs, and roaming behaves as it always has.
                _byMap.Clear();
            }
        }
    }
}
