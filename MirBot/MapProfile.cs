using System;
using System.Collections.Generic;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    /// <summary>What lives on a map, according to the database rather than experience.</summary>
    public sealed class MapProfileEntry
    {
        public int MapIndex;
        public string MapName = "";

        /// <summary>Distinct monster kinds that spawn here, excluding event-only spawns.</summary>
        public int Kinds;

        /// <summary>Total spawn count across all respawns - a rough density.</summary>
        public int Population;

        /// <summary>Levels of the monsters here, weighted by how many of each spawn.</summary>
        public int MedianLevel;
        public int MaxLevel;

        /// <summary>The best experience a single kill here is worth.</summary>
        public decimal BestExperience;

        public bool HasBoss;

        /// <summary>Gates the server itself enforces on entry.</summary>
        public int MinimumLevel;
        public int MaximumLevel;

        public override string ToString() =>
            $"{MapName} ({MapIndex}): {Kinds} kinds, {Population} spawns, " +
            $"levels median {MedianLevel} max {MaxLevel}, best exp {BestExperience:N0}" +
            (HasBoss ? ", BOSS" : "") +
            (MinimumLevel > 0 ? $", entry level {MinimumLevel}+" : "") +
            (MaximumLevel > 0 ? $", entry level {MaximumLevel}-" : "");
    }

    /// <summary>
    /// A monster profile for every map, built once from System.db.
    ///
    /// This exists to answer a question the learned memory structurally cannot: is a map we have
    /// NEVER VISITED worth exploring? MonsterMemory.TooDangerous only knows what has already hurt
    /// us, so it is silent exactly when the question is being asked - before the first visit. The
    /// database knows what lives there before we go.
    ///
    /// A caution about which fields to trust. This server's content leaves a lot of metadata at its
    /// defaults - the item progression work found RequiredLevel so unreliable that items had to be
    /// ordered by their primary stat instead. So prefer fields the SERVER ITSELF COMPUTES WITH, on
    /// the grounds that a field the game depends on has to be right: Experience decides what a kill
    /// is worth, and Stats decide whether it can hurt us. MonsterInfo.Level may be decorative, which
    /// is why this reports levels and experience side by side - so the two can be compared before
    /// anything is built on either.
    /// </summary>
    public sealed class MapProfile
    {
        private readonly Dictionary<int, MapProfileEntry> _byMap = new Dictionary<int, MapProfileEntry>();

        public int Count => _byMap.Count;
        public IEnumerable<MapProfileEntry> Entries => _byMap.Values;

        /// <summary>
        /// Does anything actually spawn here?
        ///
        /// 49 of this server's 244 maps have no respawns at all - castle interiors, halls, and the
        /// corridors that join regions together. Sabuk Keep is one: it sits on the route from
        /// Bichon Town to Banya Temple, so a journey crosses it legitimately, but there is nothing
        /// on it to kill. A bot left standing there roams for ever, finding no targets and earning
        /// nothing, which is exactly what one did for most of a session.
        ///
        /// Travel is deliberately NOT gated on this - WorldGraph knows nothing about monsters and
        /// must not, or the routes that pass through these maps would disappear.
        /// </summary>
        public bool HasMonsters(int mapIndex) => For(mapIndex) != null;

        public MapProfileEntry For(int mapIndex) =>
            _byMap.TryGetValue(mapIndex, out MapProfileEntry entry) ? entry : null;

        public void Build()
        {
            _byMap.Clear();

            IEnumerable<MonsterInfo> monsters;

            try
            {
                monsters = Globals.MonsterInfoList?.Binding?.ToList() ?? new List<MonsterInfo>();
            }
            catch
            {
                return;
            }

            // Levels collected per map, one entry per spawned individual, so a map with forty
            // chickens and one ogre reads as a chicken map rather than an ogre map.
            Dictionary<int, List<int>> levels = new Dictionary<int, List<int>>();

            foreach (MonsterInfo monster in monsters)
            {
                if (monster?.Respawns == null) continue;

                foreach (RespawnInfo respawn in monster.Respawns)
                {
                    if (respawn == null || respawn.EventSpawn) continue;
                    if (respawn.Count <= 0) continue;

                    MapInfo map = respawn.Region?.Map;
                    if (map == null) continue;

                    if (!_byMap.TryGetValue(map.Index, out MapProfileEntry entry))
                    {
                        _byMap[map.Index] = entry = new MapProfileEntry
                        {
                            MapIndex = map.Index,
                            MapName = map.Description ?? "",
                            MinimumLevel = map.MinimumLevel,
                            MaximumLevel = map.MaximumLevel
                        };

                        levels[map.Index] = new List<int>();
                    }

                    entry.Kinds++;
                    entry.Population += respawn.Count;

                    if (monster.Level > entry.MaxLevel) entry.MaxLevel = monster.Level;
                    if (monster.Experience > entry.BestExperience) entry.BestExperience = monster.Experience;
                    if (monster.IsBoss) entry.HasBoss = true;

                    for (int i = 0; i < respawn.Count; i++) levels[map.Index].Add(monster.Level);
                }
            }

            foreach (KeyValuePair<int, List<int>> pair in levels)
            {
                List<int> list = pair.Value;
                if (list.Count == 0) continue;

                list.Sort();
                _byMap[pair.Key].MedianLevel = list[list.Count / 2];
            }
        }

        /// <summary>
        /// Is this map a sensible thing to go and TRY at this level?
        ///
        /// Deliberately permissive. This is a filter against walking into somewhere that will simply
        /// kill us, not an attempt to predict which map is best - that is what the measured rates
        /// are for, and the whole point of exploring is to find out something the database cannot
        /// tell us. So it rules out only the clearly wrong: a map whose ordinary inhabitants are far
        /// above us, or one built around a boss.
        /// </summary>
        public bool WorthExploring(int mapIndex, int level, int levelsAbove, out string why)
        {
            why = "";

            MapProfileEntry entry = For(mapIndex);

            if (entry == null)
            {
                why = "nothing spawns here";
                return false;
            }

            if (entry.MinimumLevel > level)
            {
                why = $"entry needs level {entry.MinimumLevel}";
                return false;
            }

            if (entry.MaximumLevel > 0 && entry.MaximumLevel < level)
            {
                why = $"entry capped at level {entry.MaximumLevel}";
                return false;
            }

            // NEITHER HasBoss NOR MaxLevel is used here, and that is a finding rather than an
            // oversight. Dumping the profiles showed a level 250 monster spawning on 72 of the 195
            // maps - including Bichon Town and Banya Village, the two starter towns the bot must be
            // able to use. It is a world boss on a long respawn, and both flags therefore mark 38%
            // of the server, including the places that are objectively safe. Filtering on either
            // would have excluded almost everywhere and stopped exploration dead.
            //
            // The MEDIAN level is the signal that survived contact with the data: it runs 10 for
            // the towns, 18 for the first caves, 20 for the mines, then 25, 30, 35, 45 and upward
            // in a clean progression that matches the content tiers. One nasty thing wandering
            // through a reasonable map is a monster to walk around, which per-monster avoidance
            // already handles; a map whose TYPICAL inhabitant outclasses us is a different matter,
            // and that is what this rules out. The eight genuine boss maps sit at median 250 and
            // are excluded by this test anyway, without needing a flag.
            if (entry.MedianLevel > level + levelsAbove)
            {
                why = $"typical monster is level {entry.MedianLevel}, we are {level}";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Split a map name into its cave and its depth: "Deserted Mine Lv 2" is level 2 of
        /// "Deserted Mine". Depth 0 means the name carries no level at all.
        ///
        /// This matters because the levels of a cave are physically nested. Reaching Lv 3 means
        /// walking the whole length of Lv 1 and Lv 2 first, fighting or fleeing the entire way, and
        /// arriving at the hardest floor with the least health. Picking a deep floor to "explore"
        /// is therefore not one decision, it is three - and the bot has no measurement for any of
        /// the floors it has to cross to get there.
        /// </summary>
        public static (string Cave, int Depth) SplitDepth(string mapName)
        {
            if (string.IsNullOrEmpty(mapName)) return ("", 0);

            int marker = mapName.LastIndexOf(" Lv ", StringComparison.OrdinalIgnoreCase);

            if (marker < 0) return (mapName, 0);

            string tail = mapName.Substring(marker + 4).Trim();

            // Floors are sometimes suffixed - "Lv 3-W" - so read the leading digits and stop.
            int digits = 0;
            while (digits < tail.Length && char.IsDigit(tail[digits])) digits++;

            if (digits == 0) return (mapName, 0);

            return (mapName.Substring(0, marker),
                    int.Parse(tail.Substring(0, digits)));
        }

        public string Describe()
        {
            if (_byMap.Count == 0) return "no map profiles (no monster spawn data)";

            int withLevels = _byMap.Values.Count(x => x.MaxLevel > 0);

            return $"{_byMap.Count} maps profiled, {withLevels} with monster levels set";
        }
    }
}
