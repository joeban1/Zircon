using System;
using System.Collections.Generic;

namespace MirBot
{
    public sealed class MapTripEntry
    {
        public string Bot { get; set; } = "";
        public string Character { get; set; } = "";
        public string Class { get; set; } = "";
        public int MapIndex { get; set; }
        public string Map { get; set; } = "";
        public string SelectionReason { get; set; } = "";
        public DateTime SelectedUtc { get; set; }
        public DateTime? ArrivedUtc { get; set; }
        public DateTime? LeftUtc { get; set; }
        public int CreditedKills { get; set; }
        public string LeavingReason { get; set; } = "";
    }

    /// <summary>
    /// One row per farming choice. The count is positive XP awards received while on the chosen
    /// map: normally monster kills, but quest/item XP can contribute too. Never present it as an
    /// exact server kill ledger.
    /// </summary>
    public sealed class MapTripMemory : MemoryBank<MapTripEntry>
    {
        private const int Capacity = 2000;

        public MapTripMemory(string path) : base(path)
        {
            Load();
            lock (Sync)
            {
                Entries.Sort((a, b) => a.SelectedUtc.CompareTo(b.SelectedUtc));
                foreach (MapTripEntry entry in Entries)
                {
                    if (entry.LeftUtc.HasValue) continue;
                    entry.LeftUtc = DateTime.UtcNow;
                    entry.LeavingReason = "host restarted before departure was observed";
                    MarkDirty();
                }
            }
        }

        public MapTripEntry Start(string bot, string character, string mirClass, int mapIndex,
            string map, string reason)
        {
            var entry = new MapTripEntry
            {
                Bot = bot, Character = character, Class = mirClass, MapIndex = mapIndex,
                Map = map, SelectionReason = reason, SelectedUtc = DateTime.UtcNow
            };
            lock (Sync)
            {
                Entries.Add(entry);
                if (Entries.Count > Capacity) Entries.RemoveRange(0, Entries.Count - Capacity);
                MarkDirty();
            }
            return entry;
        }

        public void Observe(MapTripEntry entry, int mapIndex, int creditedKills)
        {
            if (entry == null) return;
            lock (Sync)
            {
                if (entry.LeftUtc.HasValue || entry.MapIndex != mapIndex) return;
                bool changed = false;
                if (!entry.ArrivedUtc.HasValue)
                {
                    entry.ArrivedUtc = DateTime.UtcNow;
                    changed = true;
                }
                if (creditedKills > entry.CreditedKills)
                {
                    entry.CreditedKills = creditedKills;
                    changed = true;
                }
                if (changed) MarkDirty();
            }
        }

        public void Close(MapTripEntry entry, string reason, int creditedKills)
        {
            if (entry == null) return;
            lock (Sync)
            {
                if (entry.LeftUtc.HasValue) return;
                entry.CreditedKills = Math.Max(entry.CreditedKills, creditedKills);
                entry.LeftUtc = DateTime.UtcNow;
                entry.LeavingReason = reason ?? "";
                MarkDirty();
            }
        }

        public List<MapTripEntry> Snapshot(int take = 200)
        {
            var rows = new List<MapTripEntry>();
            lock (Sync)
            {
                for (int i = Entries.Count - 1; i >= 0 && rows.Count < take; i--)
                {
                    MapTripEntry e = Entries[i];
                    rows.Add(new MapTripEntry
                    {
                        Bot = e.Bot, Character = e.Character, Class = e.Class,
                        MapIndex = e.MapIndex, Map = e.Map, SelectionReason = e.SelectionReason,
                        SelectedUtc = e.SelectedUtc, ArrivedUtc = e.ArrivedUtc, LeftUtc = e.LeftUtc,
                        CreditedKills = e.CreditedKills, LeavingReason = e.LeavingReason
                    });
                }
            }
            return rows;
        }
    }
}
