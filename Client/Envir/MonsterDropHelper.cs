using Library;
using Library.SystemModels;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Client.Envir
{
    public sealed class MonsterDropEntry
    {
        public ItemInfo Item { get; }
        public int Chance { get; }
        public int Amount { get; }
        public bool PartOnly { get; }

        // Server rolls int.MaxValue / Chance, so Chance is a 1-in-N denominator.
        public decimal ChancePercent => Chance <= 0 ? 0m : 100m / Chance;

        public MonsterDropEntry(ItemInfo item, int chance, int amount, bool partOnly)
        {
            Item = item;
            Chance = chance;
            Amount = amount;
            PartOnly = partOnly;
        }
    }

    public sealed class MonsterDropSource
    {
        public MonsterInfo Monster { get; }
        public MapInfo Map { get; }
        public int Chance { get; }

        public decimal ChancePercent => Chance <= 0 ? 0m : 100m / Chance;

        public MonsterDropSource(MonsterInfo monster, MapInfo map, int chance)
        {
            Monster = monster;
            Map = map;
            Chance = chance;
        }
    }

    public sealed class BrowsableItem
    {
        public ItemInfo Item { get; }
        public string SearchName { get; }

        public BrowsableItem(ItemInfo item)
        {
            Item = item;
            SearchName = (item.ItemName ?? string.Empty).ToLowerInvariant();
        }
    }

    /// <summary>
    /// Mirrors the server's drop rules (ServerLibrary MonsterObject.cs and Map.cs) for display only.
    /// Keep every mirrored rule in this one file so there is a single place to re-check when the
    /// server changes; nothing here fails to compile if the server logic moves.
    /// </summary>
    public static class MonsterDropHelper
    {
        private static List<BrowsableItem> _DroppableItems;

        // Matches the server's first guard: no item, impossible chance, or event-only.
        private static bool IsUsableDrop(DropInfo drop)
        {
            return drop?.Item != null && drop.Chance > 0 && !drop.EasterEvent;
        }

        // A respawn that can actually produce this drop in normal play.
        private static bool CanSpawnNormally(RespawnInfo respawn)
        {
            return respawn != null
                   && !respawn.EventSpawn            // Map.cs: skipped by the normal spawn tick
                   && respawn.Count > 0              // Map.cs: spawn loop never runs
                   && respawn.RespawnIndex == 0      // Map.cs: open-world maps are built with index 0
                   && respawn.Region?.Map != null;   // Region is nullable
        }

        public static List<MonsterDropEntry> GetDrops(MonsterInfo monster, IEnumerable<RespawnInfo> respawns)
        {
            List<MonsterDropEntry> results = new List<MonsterDropEntry>();

            if (monster?.Drops == null) return results;

            // MonsterObject.DropSet is copied from RespawnInfo.DropSet (ServerLibrary Map.cs).
            List<int> dropSets = respawns?
                .Where(x => x != null && x.Monster == monster && !x.EventSpawn)
                .Select(x => x.DropSet)
                .Distinct()
                .ToList() ?? new List<int>();

            if (dropSets.Count == 0)
                dropSets.Add(0);

            foreach (IGrouping<ItemInfo, DropInfo> group in monster.Drops
                         .Where(IsUsableDrop)
                         .Where(x => dropSets.Any(set => (set & x.DropSet) == x.DropSet))
                         .GroupBy(x => x.Item))
            {
                // The same item can appear under several drop sets; show the best odds once.
                DropInfo best = group.OrderBy(x => x.Chance).First();

                results.Add(new MonsterDropEntry(group.Key, best.Chance, best.Amount, group.All(x => x.PartOnly)));
            }

            return results
                .OrderBy(x => x.Chance)
                .ThenBy(x => x.Item.ItemName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>Every item at least one monster can drop. Built once; System.db is immutable at runtime.</summary>
        public static IReadOnlyList<BrowsableItem> GetDroppableItems()
        {
            if (_DroppableItems != null) return _DroppableItems;

            List<BrowsableItem> items = new List<BrowsableItem>();

            if (Globals.ItemInfoList?.Binding != null)
            {
                foreach (ItemInfo item in Globals.ItemInfoList.Binding)
                {
                    if (item?.Drops == null) continue;

                    if (item.Drops.Any(IsUsableDrop))
                        items.Add(new BrowsableItem(item));
                }
            }

            _DroppableItems = items
                .OrderBy(x => x.Item.ItemName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            return _DroppableItems;
        }

        /// <summary>
        /// RequiredClass defaults to All, so only a narrowing restriction is authoritative;
        /// otherwise the item is classed by whichever of DC/MC/SC is its highest stat.
        /// </summary>
        public static bool MatchesClass(ItemInfo item, MirClass mirClass)
        {
            if (item == null) return false;

            if (item.RequiredClass != RequiredClass.All)
                return item.RequiredClass.HasFlag(ToRequiredClass(mirClass));

            return IsPrimaryStat(item, mirClass);
        }

        private static RequiredClass ToRequiredClass(MirClass mirClass)
        {
            switch (mirClass)
            {
                case MirClass.Warrior: return RequiredClass.Warrior;
                case MirClass.Wizard: return RequiredClass.Wizard;
                case MirClass.Taoist: return RequiredClass.Taoist;
                case MirClass.Assassin: return RequiredClass.Assassin;
                default: return RequiredClass.None;
            }
        }

        // Ties count for every tied class, which also covers "Spell Power" items - the game groups
        // MC and SC for display when both Min and Max match (Stat.cs), and those tie here anyway.
        private static bool IsPrimaryStat(ItemInfo item, MirClass mirClass)
        {
            int dc = item.Stats[Stat.MaxDC];
            int mc = item.Stats[Stat.MaxMC];
            int sc = item.Stats[Stat.MaxSC];

            int best = Math.Max(dc, Math.Max(mc, sc));

            if (best <= 0) return false;

            switch (mirClass)
            {
                case MirClass.Warrior:
                case MirClass.Assassin:     // Assassins use DC, same as warriors.
                    return dc == best;
                case MirClass.Wizard:
                    return mc == best;
                case MirClass.Taoist:
                    return sc == best;
                default:
                    return false;
            }
        }

        /// <summary>Every monster that drops the item, one row per map it normally spawns on.</summary>
        public static List<MonsterDropSource> GetDropSources(ItemInfo item)
        {
            List<MonsterDropSource> results = new List<MonsterDropSource>();

            if (item?.Drops == null) return results;

            foreach (DropInfo drop in item.Drops)
            {
                if (!IsUsableDrop(drop)) continue;

                MonsterInfo monster = drop.Monster;

                if (monster == null) continue;

                bool spawned = false;

                if (monster.Respawns != null)
                {
                    foreach (RespawnInfo respawn in monster.Respawns)
                    {
                        if (!CanSpawnNormally(respawn)) continue;

                        // Gate per respawn, not against a union - otherwise maps where this drop
                        // cannot occur would be listed as farmable.
                        if ((respawn.DropSet & drop.DropSet) != drop.DropSet) continue;

                        spawned = true;
                        results.Add(new MonsterDropSource(monster, respawn.Region.Map, drop.Chance));
                    }
                }

                // Still worth listing a monster nobody can find, but only when nothing gates the drop.
                if (!spawned && drop.DropSet == 0)
                    results.Add(new MonsterDropSource(monster, null, drop.Chance));
            }

            // A monster can have several respawns on one map, and several drop rows for one item.
            return results
                .GroupBy(x => new { x.Monster, x.Map })
                .Select(x => x.OrderBy(y => y.Chance).First())
                .OrderBy(x => x.Chance)
                .ThenBy(x => x.Monster.MonsterName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(x => x.Map?.PlayerDescription ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public static string FormatChance(decimal percent)
        {
            if (percent <= 0m) return "-";
            if (percent >= 10m) return percent.ToString("0.#") + "%";
            if (percent >= 1m) return percent.ToString("0.##") + "%";
            if (percent >= 0.01m) return percent.ToString("0.###") + "%";
            if (percent < 0.0001m) return "<0.0001%";

            return percent.ToString("0.0000") + "%";
        }
    }
}
