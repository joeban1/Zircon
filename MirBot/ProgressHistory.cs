using System;
using System.Collections.Generic;
using System.Linq;

namespace MirBot
{
    public sealed class ProgressEntry
    {
        public string Kind { get; set; } = "";
        public string Bot { get; set; } = "";
        public string Character { get; set; } = "";
        public string Class { get; set; } = "";
        public DateTime Utc { get; set; }
        public string PreviousGear { get; set; } = "";
        public string NewGear { get; set; } = "";
        public int ScoreIncrease { get; set; }
        public string Skill { get; set; } = "";
        public bool Success { get; set; }
        public string Source { get; set; } = "observed";
    }

    /// <summary>Confirmed equipment upgrades and resolved skill-book attempts.</summary>
    public sealed class ProgressHistory : MemoryBank<ProgressEntry>
    {
        private const int Capacity = 4000;

        public ProgressHistory(string path) : base(path) { Load(); }

        public bool Record(ProgressEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Bot) ||
                string.IsNullOrWhiteSpace(entry.Character) || entry.Utc == default ||
                (entry.Kind != "upgrade" && entry.Kind != "skill")) return false;
            if (entry.Kind == "upgrade" &&
                (entry.ScoreIncrease <= 0 || string.IsNullOrWhiteSpace(entry.PreviousGear) ||
                 string.IsNullOrWhiteSpace(entry.NewGear))) return false;
            if (entry.Kind == "skill" && string.IsNullOrWhiteSpace(entry.Skill)) return false;

            lock (Sync)
            {
                if (Entries.Any(x => x.Kind == entry.Kind && x.Utc == entry.Utc &&
                    string.Equals(x.Bot, entry.Bot, StringComparison.OrdinalIgnoreCase) &&
                    (entry.Kind == "skill"
                        ? string.Equals(x.Skill, entry.Skill, StringComparison.OrdinalIgnoreCase)
                        : x.PreviousGear == entry.PreviousGear && x.NewGear == entry.NewGear)))
                    return false;

                Entries.Add(entry);
                Entries.Sort((a, b) => a.Utc.CompareTo(b.Utc));
                if (Entries.Count > Capacity) Entries.RemoveRange(0, Entries.Count - Capacity);
                MarkDirty();
                return true;
            }
        }

        public List<ProgressEntry> Upgrades(int take) => Snapshot("upgrade", take);
        public List<ProgressEntry> Skills(int take) => Snapshot("skill", take);

        private List<ProgressEntry> Snapshot(string kind, int take)
        {
            var rows = new List<ProgressEntry>();
            lock (Sync)
                for (int i = Entries.Count - 1; i >= 0 && rows.Count < take; i--)
                {
                    ProgressEntry e = Entries[i];
                    if (e.Kind != kind) continue;
                    rows.Add(new ProgressEntry
                    {
                        Kind = e.Kind, Bot = e.Bot, Character = e.Character, Class = e.Class,
                        Utc = e.Utc, PreviousGear = e.PreviousGear, NewGear = e.NewGear,
                        ScoreIncrease = e.ScoreIncrease, Skill = e.Skill,
                        Success = e.Success, Source = e.Source
                    });
                }
            return rows;
        }
    }
}
