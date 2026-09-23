using System;
using System.Collections.Generic;

namespace MirBot
{
    public sealed class LevelEntry
    {
        public string Bot { get; set; } = "";
        public string Character { get; set; } = "";
        public string Class { get; set; } = "";
        public int FromLevel { get; set; }
        public int ToLevel { get; set; }
        public DateTime Utc { get; set; }
        public string Source { get; set; } = "observed";
        public string UncertaintyStartUtc { get; set; } = "";
    }

    public sealed record LevelRow
    {
        public string Bot { get; init; } = "";
        public string Character { get; init; } = "";
        public string Class { get; init; } = "";
        public int FromLevel { get; init; }
        public int ToLevel { get; init; }
        public string Utc { get; init; } = "";
        public string Source { get; init; } = "";
        public string UncertaintyStartUtc { get; init; } = "";
        public string Interval { get; init; } = "";
        public bool IntervalApproximate { get; init; }
    }

    /// <summary>Bounded, persistent level events; never inferred from a login snapshot.</summary>
    public sealed class LevelMemory : MemoryBank<LevelEntry>
    {
        private const int Capacity = 2000;

        public LevelMemory(string path) : base(path) { Load(); }

        public bool Record(LevelEntry entry)
        {
            if (entry == null || entry.ToLevel <= entry.FromLevel ||
                string.IsNullOrWhiteSpace(entry.Bot) || string.IsNullOrWhiteSpace(entry.Character))
                return false;

            lock (Sync)
            {
                foreach (LevelEntry existing in Entries)
                {
                    if (string.Equals(existing.Bot, entry.Bot, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(existing.Character, entry.Character, StringComparison.OrdinalIgnoreCase) &&
                        existing.ToLevel == entry.ToLevel)
                        return false;
                }

                Entries.Add(entry);
                Entries.Sort((a, b) => a.Utc.CompareTo(b.Utc));
                if (Entries.Count > Capacity) Entries.RemoveRange(0, Entries.Count - Capacity);
                MarkDirty();
                return true;
            }
        }

        public List<LevelRow> Snapshot(int take = 200)
        {
            var result = new List<LevelRow>();
            lock (Sync)
            {
                for (int i = Entries.Count - 1; i >= 0 && result.Count < take; i--)
                {
                    LevelEntry current = Entries[i];
                    LevelEntry previous = null;
                    for (int j = i - 1; j >= 0; j--)
                    {
                        LevelEntry candidate = Entries[j];
                        if (string.Equals(candidate.Bot, current.Bot, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(candidate.Character, current.Character, StringComparison.OrdinalIgnoreCase))
                        { previous = candidate; break; }
                    }

                    bool consecutive = previous != null && previous.ToLevel == current.FromLevel &&
                        previous.Utc < current.Utc;
                    result.Add(new LevelRow
                    {
                        Bot = current.Bot, Character = current.Character, Class = current.Class,
                        FromLevel = current.FromLevel, ToLevel = current.ToLevel,
                        Utc = current.Utc.ToString("o"), Source = current.Source,
                        UncertaintyStartUtc = current.UncertaintyStartUtc,
                        Interval = consecutive ? (current.Utc - previous.Utc).ToString() : "",
                        IntervalApproximate = consecutive &&
                            (current.Source != "observed" || previous.Source != "observed")
                    });
                }
            }
            return result;
        }
    }
}
