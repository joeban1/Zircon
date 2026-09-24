using System;
using System.Collections.Generic;
using System.Drawing;

namespace MirBot
{
    /// <summary>One thing that hit the floor when the boss died, and whether we got it.</summary>
    public sealed class BossDrop
    {
        public string Name { get; set; } = "";
        public long Count { get; set; }
        public bool Gold { get; set; }

        /// <summary>"taken", "left" (still there when we moved on), or "gone" (vanished without
        /// our picking it up - another player, or it expired).</summary>
        public string Outcome { get; set; } = "left";
    }

    public sealed class BossKillEntry
    {
        public string Bot { get; set; } = "";
        public string Character { get; set; } = "";
        public string Class { get; set; } = "";
        public int Level { get; set; }
        public string Monster { get; set; } = "";

        /// <summary>"mini-boss" (several spawns, respawning within the hour) or "boss".</summary>
        public string Kind { get; set; } = "";
        public int MapIndex { get; set; }
        public string Map { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public DateTime Utc { get; set; }

        /// <summary>Items that landed in the bag in the two minutes after the kill.</summary>
        public List<string> Loot { get; set; } = new List<string>();

        /// <summary>
        /// Everything new on the ground around the boss just after it died, with what became of
        /// it. Nearby kills in the same seconds can add their drops too - the server does not say
        /// whose drop is whose.
        /// </summary>
        public List<BossDrop> Dropped { get; set; } = new List<BossDrop>();
    }

    /// <summary>
    /// Every boss a bot helped kill. "Helped" because the server announces a death, not a killer:
    /// a boss counts when it dies within 30 seconds of this bot attacking it, which is also how a
    /// player would describe having killed it in a group. Loot is attributed by time and map only.
    /// </summary>
    public sealed class BossKillMemory : MemoryBank<BossKillEntry>
    {
        private const int Capacity = 2000;
        private static readonly TimeSpan LootWindow = TimeSpan.FromMinutes(2);

        public BossKillMemory(string path) : base(path) { Load(); }

        public BossKillEntry Record(BossKillEntry entry)
        {
            lock (Sync)
            {
                Entries.Add(entry);
                if (Entries.Count > Capacity) Entries.RemoveRange(0, Entries.Count - Capacity);
                MarkDirty();
            }
            return entry;
        }

        /// <summary>Attach a gained item to this bot's latest kill, if it is still fresh.</summary>
        public void NoteLoot(BossKillEntry entry, int mapIndex, string item)
        {
            if (entry == null || string.IsNullOrEmpty(item)) return;
            lock (Sync)
            {
                if (entry.MapIndex != mapIndex || DateTime.UtcNow - entry.Utc > LootWindow) return;
                if (entry.Loot.Count >= 40) return;
                entry.Loot.Add(item);
                MarkDirty();
            }
        }

        /// <summary>Replace the drop list (the watcher builds it on the bot thread).</summary>
        public void SetDrops(BossKillEntry entry, List<BossDrop> drops)
        {
            if (entry == null) return;
            lock (Sync)
            {
                entry.Dropped = drops.ConvertAll(d => new BossDrop
                    { Name = d.Name, Count = d.Count, Gold = d.Gold, Outcome = d.Outcome });
                MarkDirty();
            }
        }

        public static bool LootWindowOpen(BossKillEntry entry) =>
            entry != null && DateTime.UtcNow - entry.Utc <= LootWindow;

        public List<BossKillEntry> Snapshot(int take = 500)
        {
            var rows = new List<BossKillEntry>();
            lock (Sync)
            {
                for (int i = Entries.Count - 1; i >= 0 && rows.Count < take; i--)
                {
                    BossKillEntry e = Entries[i];
                    rows.Add(new BossKillEntry
                    {
                        Bot = e.Bot, Character = e.Character, Class = e.Class, Level = e.Level,
                        Monster = e.Monster, Kind = e.Kind, MapIndex = e.MapIndex, Map = e.Map,
                        X = e.X, Y = e.Y, Utc = e.Utc, Loot = new List<string>(e.Loot),
                        Dropped = e.Dropped.ConvertAll(d => new BossDrop
                            { Name = d.Name, Count = d.Count, Gold = d.Gold, Outcome = d.Outcome })
                    });
                }
            }
            return rows;
        }
    }
}
