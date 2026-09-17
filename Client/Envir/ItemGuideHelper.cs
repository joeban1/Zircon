using Library;
using Library.SystemModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Client.Envir
{
    /// <summary>A heading, or one item, inside a rendered guide.</summary>
    public sealed class GuideRow
    {
        public string Text { get; private set; }
        public bool IsHeading { get; private set; }
        public ItemInfo Item { get; private set; }
        public int PrimaryMin { get; private set; }
        public int PrimaryMax { get; private set; }

        public bool IsItem => Item != null;

        public static GuideRow Heading(string text)
        {
            return new GuideRow { Text = text, IsHeading = true };
        }

        public static GuideRow Placeholder(string text)
        {
            return new GuideRow { Text = text };
        }

        public static GuideRow Entry(ItemInfo item, int primaryMin, int primaryMax)
        {
            return new GuideRow { Item = item, PrimaryMin = primaryMin, PrimaryMax = primaryMax };
        }
    }

    public sealed class GuideEntry
    {
        public string Title { get; }
        public string StatLabel { get; }
        public Func<IReadOnlyList<GuideRow>> Build { get; }

        public GuideEntry(string title, string statLabel, Func<IReadOnlyList<GuideRow>> build)
        {
            Title = title;
            StatLabel = statLabel;
            Build = build;
        }
    }

    /// <summary>
    /// Builds the per-class item progression guides from the client's own definition data.
    ///
    /// This is deliberately separate from MonsterDropHelper.MatchesClass. That answers "can my
    /// class equip this", which is what a filter dropdown means. This answers "is this item
    /// meant for my class", which is an opinionated recommendation - so the two can and do
    /// disagree (a WarWizTao staff is equippable by a warrior but is not warrior gear).
    /// </summary>
    public static class ItemGuideHelper
    {
        private static readonly Regex StaffName =
            new Regex(@"\bstaff\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly ItemType[] WeaponTypes = { ItemType.Weapon };
        private static readonly ItemType[] ArmourTypes = { ItemType.Armour, ItemType.Helmet, ItemType.Shield };
        private static readonly ItemType[] AccessoryTypes = { ItemType.Necklace, ItemType.Bracelet, ItemType.Ring };

        private static readonly MirClass[] GuideClasses =
        {
            MirClass.Warrior, MirClass.Wizard, MirClass.Taoist, MirClass.Assassin,
        };

        private static IReadOnlyList<GuideEntry> _Guides;
        private static Dictionary<MirClass, List<ItemInfo>> _ClassItems;

        public static IReadOnlyList<GuideEntry> Guides
        {
            get
            {
                if (_Guides != null) return _Guides;

                List<GuideEntry> guides = new List<GuideEntry>();

                foreach (MirClass mirClass in GuideClasses)
                {
                    MirClass captured = mirClass;

                    guides.Add(new GuideEntry(
                        string.Format(CEnvir.Language.GuidesItemProgressionTitle, captured),
                        StatLabel(captured),
                        () => BuildProgression(captured)));
                }

                _Guides = guides;

                return _Guides;
            }
        }

        #region Classification

        private static RequiredClass ToRequiredClass(MirClass mirClass)
        {
            switch (mirClass)
            {
                case MirClass.Warrior: return RequiredClass.Warrior;
                case MirClass.Wizard: return RequiredClass.Wizard;
                case MirClass.Taoist: return RequiredClass.Taoist;
                case MirClass.Assassin: return RequiredClass.Assassin;
                default: return RequiredClass.None;
            }
        }

        private static string StatLabel(MirClass mirClass)
        {
            switch (mirClass)
            {
                case MirClass.Wizard: return "MC";
                case MirClass.Taoist: return "SC";
                default: return "DC";
            }
        }

        private static Stat PrimaryMax(MirClass mirClass)
        {
            switch (mirClass)
            {
                case MirClass.Wizard: return Stat.MaxMC;
                case MirClass.Taoist: return Stat.MaxSC;
                default: return Stat.MaxDC;     // Warrior and Assassin both use DC
            }
        }

        private static Stat PrimaryMin(MirClass mirClass)
        {
            switch (mirClass)
            {
                case MirClass.Wizard: return Stat.MinMC;
                case MirClass.Taoist: return Stat.MinSC;
                default: return Stat.MinDC;
            }
        }

        private static bool CanEquip(ItemInfo item, MirClass mirClass)
        {
            return item.RequiredClass.HasFlag(ToRequiredClass(mirClass));
        }

        /// <summary>Base assignment: equipability is a hard gate, then a primary-stat tiebreak.</summary>
        private static bool BelongsTo(ItemInfo item, MirClass mirClass)
        {
            if (item == null || !CanEquip(item, mirClass)) return false;

            // WarWizTao permits every core class, so like All it says nothing about which class
            // the item is *for* - fall through to the stat rule. Anything else is a real narrowing.
            if (item.RequiredClass != RequiredClass.All && item.RequiredClass != RequiredClass.WarWizTao)
                return true;

            int dc = item.Stats[Stat.MaxDC];
            int mc = item.Stats[Stat.MaxMC];
            int sc = item.Stats[Stat.MaxSC];

            int best = Math.Max(dc, Math.Max(mc, sc));

            if (best <= 0) return false;        // no class-defining stat; excluded from guides

            return item.Stats[PrimaryMax(mirClass)] == best;   // ties count for each tied class
        }

        /// <summary>At least a third of the item's DC. Guarding dc &gt; 0 matters: with dc == 0 the
        /// comparison is trivially true and every zero-DC item would read as caster gear.</summary>
        private static bool Significant(int caster, int dc)
        {
            return caster > 0 && dc > 0 && caster * 3 >= dc;
        }

        private static bool IsStaff(ItemInfo item)
        {
            return item.ItemType == ItemType.Weapon
                   && !string.IsNullOrEmpty(item.ItemName)
                   && StaffName.IsMatch(item.ItemName);
        }

        private static Dictionary<MirClass, List<ItemInfo>> ClassItems
        {
            get
            {
                if (_ClassItems != null) return _ClassItems;

                Dictionary<MirClass, List<ItemInfo>> sets = new Dictionary<MirClass, List<ItemInfo>>();

                foreach (MirClass mirClass in GuideClasses)
                    sets[mirClass] = new List<ItemInfo>();

                if (Globals.ItemInfoList?.Binding != null)
                {
                    foreach (ItemInfo item in Globals.ItemInfoList.Binding)
                    {
                        if (item == null) continue;
                        if (!WeaponTypes.Contains(item.ItemType) &&
                            !ArmourTypes.Contains(item.ItemType) &&
                            !AccessoryTypes.Contains(item.ItemType)) continue;

                        foreach (MirClass mirClass in GuideClasses)
                        {
                            if (BelongsTo(item, mirClass))
                                sets[mirClass].Add(item);
                        }
                    }

                    Reclassify(sets);
                }

                _ClassItems = sets;

                return _ClassItems;
            }
        }

        /// <summary>
        /// Moves caster gear out of the melee lists. Staves are never warrior/assassin gear
        /// whatever their DC, and meaningful MC/SC belongs with Wizard/Taoist.
        /// </summary>
        private static void Reclassify(Dictionary<MirClass, List<ItemInfo>> sets)
        {
            foreach (MirClass source in new[] { MirClass.Warrior, MirClass.Assassin })
            {
                List<ItemInfo> keep = new List<ItemInfo>();

                foreach (ItemInfo item in sets[source])
                {
                    int dc = item.Stats[Stat.MaxDC];
                    int mc = item.Stats[Stat.MaxMC];
                    int sc = item.Stats[Stat.MaxSC];

                    bool staff = IsStaff(item);

                    // A staff moves on any caster stat at all, not just a significant one, so
                    // something like Numa Mage Staff (DC12/MC3) still lands somewhere.
                    bool toWizard = staff ? mc > 0 : Significant(mc, dc);
                    bool toTaoist = staff ? sc > 0 : Significant(sc, dc);

                    // Never move an item into a class that cannot equip it. This also covers the
                    // class-locked case: an Assassin-only mask carrying MC/SC stays with Assassin.
                    bool moved = false;

                    if (toWizard && CanEquip(item, MirClass.Wizard))
                        moved |= Add(sets[MirClass.Wizard], item);

                    if (toTaoist && CanEquip(item, MirClass.Taoist))
                        moved |= Add(sets[MirClass.Taoist], item);

                    if (!moved)
                        keep.Add(item);
                }

                sets[source] = keep;
            }
        }

        private static bool Add(List<ItemInfo> list, ItemInfo item)
        {
            if (list.Any(x => x.Index == item.Index)) return true;

            list.Add(item);

            return true;
        }

        #endregion

        private static IReadOnlyList<GuideRow> BuildProgression(MirClass mirClass)
        {
            List<GuideRow> rows = new List<GuideRow>();

            List<ItemInfo> items = ClassItems[mirClass];

            Stat maxStat = PrimaryMax(mirClass);
            Stat minStat = PrimaryMin(mirClass);

            AddSection(rows, items, WeaponTypes, CEnvir.Language.GuidesSectionWeapons, maxStat, minStat);
            AddSection(rows, items, ArmourTypes, CEnvir.Language.GuidesSectionArmour, maxStat, minStat);
            AddSection(rows, items, AccessoryTypes, CEnvir.Language.GuidesSectionAccessories, maxStat, minStat);

            // Crit is a genuine upgrade axis independent of raw DC/MC/SC, so it gets its own list.
            List<ItemInfo> crit = items
                .Where(x => x.Stats[Stat.CriticalChance] > 0 || x.Stats[Stat.CriticalDamage] > 0)
                .OrderByDescending(x => x.Stats[Stat.CriticalChance])
                .ThenByDescending(x => x.Stats[Stat.CriticalDamage])
                .ThenBy(x => x.ItemName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            rows.Add(GuideRow.Heading(CEnvir.Language.GuidesSectionCritical));

            if (crit.Count == 0)
                rows.Add(GuideRow.Placeholder(CEnvir.Language.GuidesNoItems));
            else
            {
                foreach (ItemInfo item in crit)
                    rows.Add(GuideRow.Entry(item, item.Stats[minStat], item.Stats[maxStat]));
            }

            return rows;
        }

        private static void AddSection(List<GuideRow> rows, List<ItemInfo> items, ItemType[] types,
                                       string heading, Stat maxStat, Stat minStat)
        {
            List<ItemInfo> section = items
                .Where(x => types.Contains(x.ItemType))
                .OrderBy(x => x.Stats[maxStat])
                .ThenBy(x => x.ItemName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            rows.Add(GuideRow.Heading(heading));

            if (section.Count == 0)
            {
                rows.Add(GuideRow.Placeholder(CEnvir.Language.GuidesNoItems));
                return;
            }

            foreach (ItemInfo item in section)
                rows.Add(GuideRow.Entry(item, item.Stats[minStat], item.Stats[maxStat]));
        }
    }
}
