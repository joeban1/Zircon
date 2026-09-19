using System;
using System.Collections.Generic;

namespace MirBot
{
    public sealed class MonsterDangerEntry
    {
        public string Name { get; set; } = "";

        /// <summary>Hardest single hit ever taken from one, after our own AC.</summary>
        public int WorstHit { get; set; }

        public long TotalDamage { get; set; }
        public int Hits { get; set; }

        /// <summary>Times one of these landed the blow that killed us.</summary>
        public int Kills { get; set; }

        public List<int> SeenOnMaps { get; set; } = new List<int>();
        public DateTime UpdatedUtc { get; set; }

        public int AverageHit => Hits == 0 ? 0 : (int)(TotalDamage / Hits);
    }

    /// <summary>
    /// What actually hurts.
    ///
    /// System.db carries every monster's nominal damage, but not what lands after our AC, our
    /// level, the server's rates and whatever else the formula folds in - and certainly not which
    /// monsters keep killing this character. Damage is therefore measured from the receiving end.
    ///
    /// Attribution is a two-packet dance: S.ObjectStruck names the attacker, and the HealthChanged
    /// that follows carries the number. Neither alone is enough, so a strike is remembered briefly
    /// and claimed by the next health loss.
    /// </summary>
    public sealed class MonsterMemory : MemoryBank<MonsterDangerEntry>
    {
        private readonly Dictionary<string, MonsterDangerEntry> _lookup =
            new Dictionary<string, MonsterDangerEntry>(StringComparer.OrdinalIgnoreCase);

        public MonsterMemory(string path) : base(path)
        {
            Load();
        }

        protected override void Reindex()
        {
            _lookup.Clear();
            foreach (MonsterDangerEntry entry in Entries)
                if (!string.IsNullOrEmpty(entry.Name)) _lookup[entry.Name] = entry;
        }

        public void RecordHit(string monsterName, int damage, int mapIndex)
        {
            if (string.IsNullOrEmpty(monsterName) || damage <= 0) return;

            lock (Sync)
            {
                MonsterDangerEntry entry = Find(monsterName);

                entry.Hits++;
                entry.TotalDamage += damage;
                if (damage > entry.WorstHit) entry.WorstHit = damage;
                if (!entry.SeenOnMaps.Contains(mapIndex)) entry.SeenOnMaps.Add(mapIndex);
                entry.UpdatedUtc = DateTime.UtcNow;

                MarkDirty();
            }
        }

        public void RecordKill(string monsterName)
        {
            if (string.IsNullOrEmpty(monsterName)) return;

            lock (Sync)
            {
                Find(monsterName).Kills++;
                MarkDirty();
            }
        }

        public void RecordSeen(string monsterName, int mapIndex)
        {
            if (string.IsNullOrEmpty(monsterName)) return;

            lock (Sync)
            {
                MonsterDangerEntry entry = Find(monsterName);

                if (entry.SeenOnMaps.Contains(mapIndex)) return;

                entry.SeenOnMaps.Add(mapIndex);
                MarkDirty();
            }
        }

        /// <summary>Call with the lock held.</summary>
        private MonsterDangerEntry Find(string name)
        {
            if (_lookup.TryGetValue(name, out MonsterDangerEntry entry)) return entry;

            entry = new MonsterDangerEntry { Name = name };
            Entries.Add(entry);
            _lookup[name] = entry;
            return entry;
        }

        /// <summary>
        /// Is a map too dangerous to hunt on at this health?
        ///
        /// The rule is the Mir 2 agents': a map is refused only when EVERY monster we have actually
        /// been hit by there can take more than half our health in one blow. Refusing on the worst
        /// single monster would rule out almost every map, since one nasty thing usually wanders
        /// through somewhere.
        /// </summary>
        public bool TooDangerous(int mapIndex, int maxHealth)
        {
            if (maxHealth <= 0) return false;

            int threshold = maxHealth / 2;
            bool anyKnown = false;

            lock (Sync)
            {
                foreach (MonsterDangerEntry entry in Entries)
                {
                    if (entry.Hits == 0 || !entry.SeenOnMaps.Contains(mapIndex)) continue;

                    anyKnown = true;
                    if (entry.WorstHit <= threshold) return false;
                }
            }

            return anyKnown;
        }

        public string Describe()
        {
            lock (Sync)
                return $"{Entries.Count} monsters at {System.IO.Path.GetFileName(FilePath)}";
        }
    }
}
