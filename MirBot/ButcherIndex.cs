using System;
using System.Collections.Generic;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    /// <summary>
    /// Which monsters yield anything when their corpse is butchered.
    ///
    /// The signal is MonsterInfo.AI, from System.db: 1 Chicken, 2 Cow/Deer/Pig/Sheep, 5
    /// Carnivorous Plant. Passive things killed for their carcass rather than their drops.
    ///
    /// NOT the Mir 2 agents' AutoHarvestAIs = { 1, 2, 4, 5, 7, 9 }. On this server AI 7 is Ant
    /// Needler, Bone Archer and Apparition Archer, and AI 9 is the sorcerers - ordinary combat
    /// monsters that would have had the bot sawing at corpses that give nothing.
    ///
    /// A DROP-TABLE HEURISTIC WAS TRIED HERE AND REMOVED, which is worth recording so it is not
    /// tried again. The harvest tables look distinctive - Beef/1, Beef/10, Beef/20, Beef/30,
    /// Beef/40, Beef/50, one roll per cut - and "the same item four or more times" seemed to
    /// capture it. Run against all 309 monsters it matched 46, among them Arch Lich Taedu,
    /// Chaos Knight and Black Palace Warlord: ordinary tables repeat a common consumable just as
    /// freely (Ginseng Of Eternity ten times, Rejuvenation Potion ten times, Scroll Of Town Portal
    /// five times). The shape is how the data expresses multiple rolls of anything, not a mark of
    /// harvesting. Five hand-picked examples agreed with it and the full table did not.
    ///
    /// So the list is the AI whitelist alone, and it is CONFIGURABLE (BotConfig.ButcherAIs)
    /// because that is the honest way to carry a value derived from inspection rather than proof.
    /// Add 4 to include Chestnut Tree - its yield is the chestnut tiers - but note that AI 4 also
    /// holds the level 250 castle objectives.
    /// </summary>
    public sealed class ButcherIndex
    {
        private readonly HashSet<int> _butcherable = new HashSet<int>();
        private readonly List<string> _names = new List<string>();

        public int Count => _butcherable.Count;

        /// <summary>The monsters found, for the startup log - this is the sort of inference that
        /// should be visible rather than assumed correct.</summary>
        public string Describe() =>
            _names.Count == 0 ? "none"
                              : $"{_names.Count}: {string.Join(", ", _names.Take(12))}" +
                                (_names.Count > 12 ? ", ..." : "");

        public void Build(IEnumerable<int> aiWhitelist)
        {
            _butcherable.Clear();
            _names.Clear();

            HashSet<int> ais = new HashSet<int>(aiWhitelist ?? Enumerable.Empty<int>());

            try
            {
                foreach (MonsterInfo monster in Globals.MonsterInfoList?.Binding
                                                ?? Enumerable.Empty<MonsterInfo>())
                {
                    if (monster == null) continue;
                    if (!ais.Contains(monster.AI)) continue;

                    _butcherable.Add(monster.Index);
                    _names.Add(monster.MonsterName);
                }
            }
            catch
            {
                // Pre-login, or a database that did not load. Nothing is butcherable, which is the
                // safe answer: the bot simply behaves as it did before.
            }

            _names.Sort(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Worth butchering?</summary>
        public bool IsButcherable(int monsterIndex) =>
            monsterIndex >= 0 && _butcherable.Contains(monsterIndex);
    }
}
