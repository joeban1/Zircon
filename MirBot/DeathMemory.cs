using System;
using System.Collections.Generic;

namespace MirBot
{
    /// <summary>One death, stored rather than merely counted.</summary>
    public sealed class DeathEntry
    {
        public string Bot { get; set; } = "";
        public string Character { get; set; } = "";
        public string Class { get; set; } = "";
        public int Level { get; set; }
        public int MapIndex { get; set; }
        public string MapName { get; set; } = "";
        public string Killer { get; set; } = "";

        /// <summary>Gold held at the moment of death, as a string - see BotStatus on precision.</summary>
        public string Gold { get; set; } = "0";

        public int X { get; set; }
        public int Y { get; set; }
        public DateTime Utc { get; set; }
    }

    /// <summary>
    /// The last few hundred deaths, with enough detail to explain them.
    ///
    /// HuntingMemory already counts deaths per map, and that count is what discounts a map's
    /// ranking - but a count cannot answer the questions that actually come up. Which monster.
    /// At what level. Carrying how much. Eight times in the same corner of Phantom Forest, or
    /// once each across eight maps? Those were the questions asked of a wizard that went from
    /// 75,000 gold to nothing in an afternoon, and answering them meant grepping a log file,
    /// because the events existed nowhere else.
    ///
    /// Capped rather than unbounded: this is a diagnostic, not an archive, and the newest few
    /// hundred are the ones anybody reads.
    /// </summary>
    public sealed class DeathMemory : MemoryBank<DeathEntry>
    {
        /// <summary>Kept deaths. Beyond this the oldest are dropped on write.</summary>
        private const int Capacity = 500;

        public DeathMemory(string path) : base(path)
        {
            Load();
        }

        public void Record(DeathEntry entry)
        {
            if (entry == null) return;

            lock (Sync)
            {
                Entries.Add(entry);

                if (Entries.Count > Capacity) Entries.RemoveRange(0, Entries.Count - Capacity);

                MarkDirty();
            }
        }

        /// <summary>
        /// Newest first, copied under the lock.
        ///
        /// Copied for the same reason every other bank copies: the web thread must never hold a
        /// reference into a list four bot threads are still appending to.
        /// </summary>
        public List<DeathRow> Snapshot(int take = 100)
        {
            List<DeathRow> rows = new List<DeathRow>();

            lock (Sync)
            {
                for (int i = Entries.Count - 1; i >= 0 && rows.Count < take; i--)
                {
                    DeathEntry entry = Entries[i];

                    rows.Add(new DeathRow
                    {
                        Bot = entry.Bot,
                        Character = entry.Character,
                        Class = entry.Class,
                        Level = entry.Level,
                        MapIndex = entry.MapIndex,
                        MapName = entry.MapName,
                        Killer = entry.Killer,
                        Gold = entry.Gold,
                        X = entry.X,
                        Y = entry.Y,
                        Utc = entry.Utc.ToString("o")
                    });
                }
            }

            return rows;
        }

        public string Describe()
        {
            lock (Sync) return $"{Entries.Count} death(s) at {FilePath}";
        }
    }
}
