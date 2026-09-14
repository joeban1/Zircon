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

    public static class MonsterDropHelper
    {
        public static List<MonsterDropEntry> GetDrops(MonsterInfo monster, IEnumerable<RespawnInfo> respawns)
        {
            List<MonsterDropEntry> results = new List<MonsterDropEntry>();

            if (monster?.Drops == null) return results;

            // MonsterObject.DropSet is copied from RespawnInfo.DropSet (ServerLibrary Map.cs).
            // EventSpawn respawns are skipped by the normal spawn tick, so they cannot drop in normal play.
            List<int> dropSets = respawns?
                .Where(x => x != null && x.Monster == monster && !x.EventSpawn)
                .Select(x => x.DropSet)
                .Distinct()
                .ToList() ?? new List<int>();

            if (dropSets.Count == 0)
                dropSets.Add(0);

            foreach (IGrouping<ItemInfo, DropInfo> group in monster.Drops
                         .Where(x => x?.Item != null && x.Chance > 0 && !x.EasterEvent)
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
