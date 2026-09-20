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

        /// <summary>The best rate ever seen here. Kept for reading; NO LONGER used for ranking.</summary>
        public double BestExperiencePerHour { get; set; }
        public double LastExperiencePerHour { get; set; }
        public int Samples { get; set; }
        public double HoursSampled { get; set; }

        /// <summary>Raw experience accumulated across every sample, for a true average.</summary>
        public double TotalExperience { get; set; }

        public int Deaths { get; set; }
        public DateTime UpdatedUtc { get; set; }

        /// <summary>
        /// Experience per hour across everything ever measured here.
        ///
        /// This replaces the best-ever figure as the ranking number, and the reason is a five-hour
        /// run that produced nothing. A warrior recorded these seven samples on Bichon Town:
        ///
        ///     171,656 | 29,005 | 24,321 | 21,281 | 18,196 | 9,824 | 202
        ///
        /// Ranking on the maximum meant one freak fifteen-minute window - about six times the
        /// typical rate - anchored the map permanently. The bot walked to Deserted Mine, spent
        /// thirteen minutes there, and was pulled straight back by a number no ordinary hour could
        /// ever beat. The mean of those samples is around 39,000, which a genuinely better map can
        /// actually compete with.
        /// </summary>
        public double AverageExperiencePerHour =>
            HoursSampled > 0 ? TotalExperience / HoursSampled : 0;

        /// <summary>
        /// The ranking number: what this map pays, discounted by how often it kills us.
        ///
        /// Deaths were already being recorded here and used by nothing at all. They matter enormously
        /// and are invisible in the rate, because time spent dead, reviving and walking back is time
        /// the sample window either missed or was discarded for. One wizard had 33 deaths on its own
        /// starter map against a measured 1,011 exp/hour and nothing in the ranking noticed.
        ///
        /// Counted per death rather than per hour on purpose: HoursSampled only covers completed
        /// measurement windows, and dying is one of the things that stops a window completing, so
        /// deaths-per-hour would divide by a number the deaths themselves shrank.
        /// </summary>
        public double Score(double deathPenalty) =>
            AverageExperiencePerHour / (1 + Math.Max(0, Deaths) * Math.Max(0, deathPenalty));
    }

    /// <summary>
    /// How good each map actually is, measured rather than assumed.
    ///
    /// System.db lists every monster on a map and its experience value, but not what a character of
    /// a given class and level actually earns there once travel, downtime, town trips and dying are
    /// counted. That number can only be observed, and it is the input hunting-ground selection needs.
    ///
    /// Keyed by (map, class, level) because the answer differs sharply on all three.
    ///
    /// Ranking is on the MEAN rate, discounted by deaths - not on the best rate ever seen. Keeping
    /// the maximum was copied from the Mir 2 agents and is defensible with hundreds of agents
    /// feeding one memory, where outliers are drowned by volume. With two bots it means a single
    /// lucky window becomes an unbeatable anchor, and that is exactly what happened: see
    /// AverageExperiencePerHour.
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
            {
                // Records written before averaging existed carry hours and rates but no running
                // total, so their mean cannot be reconstructed - only the best and the last
                // survive, and neither is an average. Bichon Town illustrates why guessing is
                // worse than admitting it: its best was 171,656 and its last was 202, against a
                // true mean around 39,000. Seeding from either would invent a number.
                //
                // So the RATE is discarded and the map is treated as unmeasured, which simply
                // means it gets measured again - cheap, now that part-finished windows are banked
                // instead of binned. The hours and sample count go with it, because leaving them
                // would dilute the first honest sample against time it cannot account for.
                //
                // DEATHS are kept. They are a raw count, they were never part of the rate, and
                // thirty-five deaths on a map is exactly the kind of history worth carrying.
                if (entry.TotalExperience <= 0 && entry.HoursSampled > 0)
                {
                    entry.HoursSampled = 0;
                    entry.Samples = 0;
                    entry.BestExperiencePerHour = 0;
                    entry.LastExperiencePerHour = 0;
                    MarkDirty();
                }

                _lookup[(entry.MapIndex, entry.Class, entry.LevelBand)] = entry;
            }
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
                entry.TotalExperience += experiencePerHour * hours;
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

        /// <summary>
        /// Every record, copied, for the status page.
        ///
        /// COPIED, and copied while holding the lock. Best() hands out the live HuntingEntry
        /// objects and releases the lock before returning them, which is fine for the bot thread
        /// that asked - it owns the decision it is about to make - but serialising them on the web
        /// thread would race every other bot's Record() and RecordDeath(). The rule at the top of
        /// BotStatus.cs applies here too: nothing mutable leaves the bot threads.
        /// </summary>
        public List<HuntingRow> Snapshot(double deathPenalty = DefaultDeathPenalty)
        {
            List<HuntingRow> rows = new List<HuntingRow>();

            lock (Sync)
            {
                foreach (HuntingEntry entry in Entries)
                    rows.Add(new HuntingRow
                    {
                        MapIndex = entry.MapIndex,
                        MapName = entry.MapName,
                        Class = entry.Class,
                        LevelBand = entry.LevelBand,
                        Level = entry.Level,
                        AveragePerHour = entry.AverageExperiencePerHour,
                        BestPerHour = entry.BestExperiencePerHour,
                        LastPerHour = entry.LastExperiencePerHour,
                        Samples = entry.Samples,
                        HoursSampled = entry.HoursSampled,
                        Deaths = entry.Deaths,
                        Score = entry.Score(deathPenalty),
                        UpdatedUtc = entry.UpdatedUtc.ToString("o")
                    });
            }

            rows.Sort((a, b) => b.Score.CompareTo(a.Score));
            return rows;
        }

        /// <summary>
        /// Best known maps for this class and level, best first, ranked on mean rate discounted by
        /// deaths. Empty until something is measured.
        /// </summary>
        public List<HuntingEntry> Best(string mirClass, int level, int take,
            double deathPenalty = DefaultDeathPenalty)
        {
            List<HuntingEntry> found = new List<HuntingEntry>();

            lock (Sync)
            {
                int band = BandOf(level);

                foreach (HuntingEntry entry in Entries)
                {
                    if (entry.Class != mirClass || entry.LevelBand != band) continue;
                    if (entry.AverageExperiencePerHour <= 0) continue;

                    found.Add(entry);
                }
            }

            found.Sort((a, b) => b.Score(deathPenalty).CompareTo(a.Score(deathPenalty)));

            if (found.Count > take) found.RemoveRange(take, found.Count - take);

            return found;
        }

        /// <summary>
        /// Maps that have killed us repeatedly without ever yielding a measurement.
        ///
        /// These fall through every other guard, and the gap is not obvious until it bites. Best()
        /// drops any entry with no measured rate, so a map lethal enough that no sample window ever
        /// completed is absent from the ranking - and TryExplore builds its "already measured" set
        /// from Best(), so the same map looks brand new and gets chosen again. The worse a map is,
        /// the more attractive it becomes.
        ///
        /// Phantom Forest did exactly this: a level 18-19 wizard was sent there eight times, died
        /// eight times, banked no experience at all, and paid a 5,000-10,000 gold teleport fare
        /// each way. Between the fares and re-equipping after each death it went from 75,000 gold
        /// to almost nothing, and the record of all nineteen deaths sat in memory the whole time
        /// being read by nothing.
        ///
        /// Deaths are counted from this band and every band below it. Dying at level 18 is still
        /// worth knowing at level 19; it stops mattering once the bot has genuinely outgrown the
        /// place, which is what forgetAfterBands expresses. Nothing is struck off permanently, and
        /// a map with any measured rate is left to Score() and its death discount instead - this
        /// is only about the maps we know nothing about except that they killed us.
        /// </summary>
        public HashSet<int> Lethal(string mirClass, int level, int minDeaths = 3,
            int forgetAfterBands = 2)
        {
            HashSet<int> lethal = new HashSet<int>();

            lock (Sync)
            {
                int band = BandOf(level);

                foreach (HuntingEntry entry in Entries)
                {
                    if (entry.Class != mirClass) continue;
                    if (entry.AverageExperiencePerHour > 0) continue;
                    if (entry.Deaths < minDeaths) continue;

                    // Only our own band and the ones beneath it, and not from so far below that
                    // the character it happened to is no longer recognisably this one.
                    if (entry.LevelBand > band) continue;
                    if (band - entry.LevelBand >= forgetAfterBands) continue;

                    lethal.Add(entry.MapIndex);
                }
            }

            return lethal;
        }

        /// <summary>
        /// How hard a death counts against a map. Each death divides the rate by (1 + n * this),
        /// so at 0.15 one death costs about 13% and ten cost 60%. Bounded and monotonic: a map
        /// that keeps killing us falls steadily rather than being struck off on a single accident.
        /// </summary>
        public const double DefaultDeathPenalty = 0.15;

        /// <summary>How many distinct maps we have a usable measurement for, at this class and band.</summary>
        public int MeasuredCount(string mirClass, int level)
        {
            int count = 0;

            lock (Sync)
            {
                int band = BandOf(level);

                foreach (HuntingEntry entry in Entries)
                {
                    if (entry.Class != mirClass || entry.LevelBand != band) continue;
                    if (entry.AverageExperiencePerHour <= 0) continue;

                    count++;
                }
            }

            return count;
        }

        public string Describe()
        {
            lock (Sync)
                return $"{Entries.Count} map/class/band records (bands of {_bandSize}) " +
                       $"at {System.IO.Path.GetFileName(FilePath)}";
        }
    }
}
