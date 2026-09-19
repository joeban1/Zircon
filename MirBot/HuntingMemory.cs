using System;
using System.Collections.Generic;
using System.IO;

namespace MirBot
{
    public sealed class HuntingEntry
    {
        public int MapIndex { get; set; }
        public string MapName { get; set; } = "";
        public string Class { get; set; } = "";

        /// <summary>
        /// The band of levels this rate was measured in, not the exact level.
        ///
        /// Keying by exact level - as the Mir 2 agents do - throws every measurement away on each
        /// level-up. That is affordable with hundreds of agents sharing one memory, because someone
        /// else has already ground through the level you just reached. With one or two bots it means
        /// the memory is almost always empty at the level being played, and the bot explores at
        /// random forever.
        /// </summary>
        public int LevelBand { get; set; }

        /// <summary>The level actually being played when this was recorded, for reading the file.</summary>
        public int Level { get; set; }

        /// <summary>The best rate seen for this combination, not the latest or the average.</summary>
        public double BestExperiencePerHour { get; set; }
        public double LastExperiencePerHour { get; set; }
        public int Samples { get; set; }
        public double HoursSampled { get; set; }
        public int Deaths { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    /// <summary>
    /// How good each map actually is, measured rather than assumed.
    ///
    /// System.db lists every monster on a map and its experience value, but not what a character of
    /// a given class and level actually earns there once travel, downtime, town trips and dying are
    /// counted. That number can only be observed, and it is the input hunting-ground selection needs.
    ///
    /// Keyed by (map, class, level) because the answer differs sharply on all three. The best rate
    /// is kept rather than the mean: a poor sample usually means the bot spent the window walking to
    /// town, not that the map is poor.
    /// </summary>
    public sealed class HuntingMemory : MemoryBank<HuntingEntry>
    {
        private readonly Dictionary<(int, string, int), HuntingEntry> _lookup =
            new Dictionary<(int, string, int), HuntingEntry>();

        private readonly int _bandSize;

        public HuntingMemory(string path, int bandSize) : base(path)
        {
            _bandSize = Math.Max(1, bandSize);
            Load();
        }

        /// <summary>Levels 1-5 are band 0 at the default size of 5, 6-10 band 1, and so on.</summary>
        public int BandOf(int level) => Math.Max(0, level - 1) / _bandSize;

        public string DescribeBand(int level)
        {
            int band = BandOf(level);
            return $"{band * _bandSize + 1}-{(band + 1) * _bandSize}";
        }

        protected override void Reindex()
        {
            _lookup.Clear();
            foreach (HuntingEntry entry in Entries)
                _lookup[(entry.MapIndex, entry.Class, entry.LevelBand)] = entry;
        }

        public void Record(int mapIndex, string mapName, string mirClass, int level,
            double experiencePerHour, double hours)
        {
            if (experiencePerHour <= 0 || hours <= 0) return;

            lock (Sync)
            {
                HuntingEntry entry = Find(mapIndex, mapName, mirClass, level);

                entry.Samples++;
                entry.HoursSampled += hours;
                entry.LastExperiencePerHour = experiencePerHour;

                if (experiencePerHour > entry.BestExperiencePerHour)
                    entry.BestExperiencePerHour = experiencePerHour;

                entry.UpdatedUtc = DateTime.UtcNow;
                MarkDirty();
            }
        }

        public void RecordDeath(int mapIndex, string mapName, string mirClass, int level)
        {
            lock (Sync)
            {
                Find(mapIndex, mapName, mirClass, level).Deaths++;
                MarkDirty();
            }
        }

        /// <summary>Call with the lock held.</summary>
        private HuntingEntry Find(int mapIndex, string mapName, string mirClass, int level)
        {
            int band = BandOf(level);
            var key = (mapIndex, mirClass, band);

            if (_lookup.TryGetValue(key, out HuntingEntry entry))
            {
                entry.Level = level;       // the most recent level in this band
                return entry;
            }

            entry = new HuntingEntry
            {
                MapIndex = mapIndex,
                MapName = mapName ?? "",
                Class = mirClass,
                LevelBand = band,
                Level = level
            };

            Entries.Add(entry);
            _lookup[key] = entry;
            return entry;
        }

        /// <summary>Best known maps for this class and level, best first. Empty until something is measured.</summary>
        public List<HuntingEntry> Best(string mirClass, int level, int take)
        {
            List<HuntingEntry> found = new List<HuntingEntry>();

            lock (Sync)
            {
                int band = BandOf(level);

                foreach (HuntingEntry entry in Entries)
                {
                    if (entry.Class != mirClass || entry.LevelBand != band) continue;
                    if (entry.BestExperiencePerHour <= 0) continue;

                    found.Add(entry);
                }
            }

            found.Sort((a, b) => b.BestExperiencePerHour.CompareTo(a.BestExperiencePerHour));

            if (found.Count > take) found.RemoveRange(take, found.Count - take);

            return found;
        }

        public string Describe()
        {
            lock (Sync)
                return $"{Entries.Count} map/class/band records (bands of {_bandSize}) " +
                       $"at {System.IO.Path.GetFileName(FilePath)}";
        }
    }
}
