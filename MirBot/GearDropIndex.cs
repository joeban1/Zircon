using System;
using System.Collections.Generic;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    /// <summary>A plausible, currently wearable equipment upgrade on a hunting map.</summary>
    public sealed record GearTarget(string ItemName, int ScoreGain, double Opportunity,
        double Priority);

    /// <summary>
    /// Database-derived equipment sources. This is only a map-selection hint: the actual item
    /// still has to drop, be looted, and beat the worn instance when the server supplies its roll.
    /// </summary>
    public sealed class GearDropIndex
    {
        private sealed record Source(ItemInfo Item, int MonsterLevel, int SpawnCount, int Chance);
        private readonly Dictionary<int, List<Source>> _byMap = new();

        public int MapCount => _byMap.Count;

        public static bool IsWorthHunting(int candidateScore, int gain, int baseline,
            double opportunity) =>
            candidateScore >= 20 && gain >= 8 &&
            gain >= Math.Max(20, baseline) * 0.25 && opportunity >= 0.01;

        public void Build()
        {
            _byMap.Clear();

            foreach (MonsterInfo monster in Globals.MonsterInfoList?.Binding
                                            ?? Enumerable.Empty<MonsterInfo>())
            {
                if (monster?.IsBoss == true || monster?.Drops == null || monster.Respawns == null)
                    continue;

                foreach (RespawnInfo respawn in monster.Respawns)
                {
                    int map = respawn?.Region?.Map?.Index ?? -1;
                    if (map < 0 || respawn.EventSpawn || respawn.Count <= 0) continue;

                    foreach (DropInfo drop in monster.Drops)
                    {
                        if (drop?.Item == null || drop.Chance <= 0 || drop.Amount <= 0 ||
                            drop.PartOnly || drop.EasterEvent) continue;

                        // Only durable, combat-relevant equipment. The final class/gender/slot
                        // check is per bot in Best().
                        ItemType type = drop.Item.ItemType;
                        if (type != ItemType.Weapon && type != ItemType.Armour &&
                            type != ItemType.Helmet && type != ItemType.Necklace &&
                            type != ItemType.Bracelet && type != ItemType.Ring &&
                            type != ItemType.Shoes && type != ItemType.Shield &&
                            type != ItemType.Emblem) continue;

                        if (!_byMap.TryGetValue(map, out List<Source> list))
                            _byMap[map] = list = new List<Source>();
                        list.Add(new Source(drop.Item, monster.Level, respawn.Count, drop.Chance));
                    }
                }
            }
        }

        public GearTarget Best(int mapIndex, Backpack items, WorldModel world,
            int levelsAbove)
        {
            if (items == null || world == null || !_byMap.TryGetValue(mapIndex, out var sources))
                return null;

            GearTarget best = null;
            foreach (var group in sources.GroupBy(source => source.Item.Index))
            {
                ItemInfo info = group.First().Item;
                if (!Backpack.CanEquipInfo(info, world.Class, world.Gender)) continue;
                if (items.HasUnequippedCopy(info)) continue;
                if (items.Stored.Any(pair => pair.Value?.Info?.Index == info.Index)) continue;

                int gain = items.UpgradeGain(info, world.Class, world.Gender,
                    world.Level, world.PlayerStats);
                int baseline = items.WeakestWornScore(info.ItemType, world.Class);

                // Spawn count / drop denominator is a rough opportunity proxy, not a promised
                // per-kill probability. Ignore boss or over-level sources on otherwise safe maps.
                double opportunity = group
                    .Where(source => source.MonsterLevel <= world.Level + levelsAbove)
                    .Sum(source => (double)source.SpawnCount / source.Chance);
                // An empty slot is useful, but a trivial accessory or impossibly rare drop
                // should not make every starter map an upgrade destination.
                if (!IsWorthHunting(Backpack.ScoreInfo(info, world.Class), gain,
                        baseline, opportunity)) continue;

                double priority = Math.Min(1, gain / Math.Max(20.0, baseline)) *
                                  Math.Min(1, opportunity / 0.05);
                if (best == null || priority > best.Priority)
                    best = new GearTarget(info.ItemName ?? "equipment", gain,
                        opportunity, priority);
            }

            return best;
        }
    }
}
