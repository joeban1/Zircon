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

        /// <summary>Where it last killed us, and at what level. Written by the post-mortem.</summary>
        public string LastKillMap { get; set; } = "";
        public int LastKillX { get; set; }
        public int LastKillY { get; set; }
        public int LastKillLevel { get; set; }
        public DateTime LastKillUtc { get; set; }

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

        /// <summary>
        /// The post-mortem. Damage taken tells us what a monster CAN do; a death tells us what it
        /// actually did, which is the stronger signal and the one worth keeping the details of.
        ///
        /// Recording where as well as what matters because the two answer different questions:
        /// the monster feeds per-monster avoidance, the map and level feed hunting-ground choice.
        /// </summary>
        public void RecordKill(string monsterName, string mapName = null,
            System.Drawing.Point where = default, int level = 0)
        {
            if (string.IsNullOrEmpty(monsterName)) return;

            lock (Sync)
            {
                MonsterDangerEntry entry = Find(monsterName);

                entry.Kills++;

                if (!string.IsNullOrEmpty(mapName))
                {
                    entry.LastKillMap = mapName;
                    entry.LastKillX = where.X;
                    entry.LastKillY = where.Y;
                    entry.LastKillLevel = level;
                    entry.LastKillUtc = DateTime.UtcNow;
                }

                MarkDirty();
            }
        }

        /// <summary>
        /// Is this particular monster too dangerous to pick a fight with right now?
        ///
        /// TooDangerous below answers the same question about a whole MAP, which is the
        /// granularity the Mir 2 agents use and the only granularity we used until now - so the
        /// per-monster numbers were being collected and then thrown away. This is the finer
        /// reading: one nasty thing wandering through a good map should be walked around, not
        /// cause the map to be abandoned.
        ///
        /// Measured against CURRENT health, not maximum. A monster that takes three hits to kill
        /// us at full health takes one at a third, so the same memory yields caution that grows as
        /// the fight goes badly - which is the behaviour we actually want.
        ///
        /// Having KILLED us counts for more than any damage figure: it is the one observation that
        /// already accounts for everything the damage model leaves out.
        /// </summary>
        public bool TooDangerousToFight(string monsterName, int currentHealth, int hitsToDeath,
            int minimumHits)
        {
            if (hitsToDeath <= 0 || currentHealth <= 0 || string.IsNullOrEmpty(monsterName))
                return false;

            lock (Sync)
            {
                if (!_lookup.TryGetValue(monsterName, out MonsterDangerEntry entry)) return false;
                if (entry.Hits < Math.Max(1, minimumHits)) return false;

                // One that has actually finished us off is avoided on a tighter margin.
                int margin = entry.Kills > 0 ? hitsToDeath + 1 : hitsToDeath;

                return entry.WorstHit > 0 && entry.WorstHit * margin >= currentHealth;
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
