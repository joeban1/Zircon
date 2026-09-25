using System.Collections.Generic;
using System.Reflection;
using Library;
using Library.SystemModels;
using Xunit;

namespace MirBot.Tests
{
    /// <summary>
    /// Weapon oils: Benediction and Conservation up to Luck / Strength 2 and sold beyond, War God
    /// when the weapon is worn down (two kept), and never any of them on a weapon below level 33.
    /// </summary>
    public sealed class WeaponOilTests
    {
        private static readonly ItemInfo Benediction = Oil(296, "Oil Of Benediction", Backpack.OilOfBenedictionShape);
        private static readonly ItemInfo Conservation = Oil(297, "Oil Of Conservation", Backpack.OilOfConservationShape);
        private static readonly ItemInfo WarGod = Oil(298, "Oil Of The War God", Backpack.OilOfTheWarGodShape);

        [Theory]
        [InlineData(0, true)]
        [InlineData(1, true)]
        [InlineData(2, false)]
        [InlineData(-1, true)]      // cursed: from 0 or below a use is almost always +1
        public void BenedictionIsUsedBelowLuckTwo(int luck, bool wanted)
        {
            ClientUserItem weapon = Weapon(33, luck: luck);
            Assert.Equal(wanted, Backpack.OilWanted(weapon, Backpack.OilOfBenedictionShape));
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(1, true)]
        [InlineData(2, false)]
        public void ConservationIsUsedBelowStrengthTwo(int strength, bool wanted)
        {
            ClientUserItem weapon = Weapon(40, strength: strength);
            Assert.Equal(wanted, Backpack.OilWanted(weapon, Backpack.OilOfConservationShape));
        }

        [Fact]
        public void NoOilOnAWeaponBelowLevelThirtyThree()
        {
            ClientUserItem weapon = Weapon(32, current: 1000, max: 20000);

            Assert.False(Backpack.OilWanted(weapon, Backpack.OilOfBenedictionShape));
            Assert.False(Backpack.OilWanted(weapon, Backpack.OilOfConservationShape));
            Assert.False(Backpack.OilWanted(weapon, Backpack.OilOfTheWarGodShape));

            Backpack bag = Bag(weapon, (Benediction, 3), (WarGod, 3));
            Assert.Equal(-1, bag.WeaponOilToUse());
        }

        [Theory]
        [InlineData(5000, 20000, true)]     // 25%
        [InlineData(6000, 20000, false)]    // exactly 30%
        [InlineData(0, 20000, false)]       // broken: the server refuses an item at 0
        public void WarGodRepairsAWornWeapon(int current, int max, bool wanted)
        {
            ClientUserItem weapon = Weapon(45, current: current, max: max);
            Assert.Equal(wanted, Backpack.OilWanted(weapon, Backpack.OilOfTheWarGodShape));
        }

        [Fact]
        public void TheBagPicksAnOilTheWeaponCanUse()
        {
            Backpack bag = Bag(Weapon(45, luck: 2, strength: 1), (Benediction, 4), (Conservation, 2));

            int slot = bag.WeaponOilToUse();
            Assert.Equal(Conservation, bag.InSlot(slot).Info);
        }

        [Fact]
        public void OilsPastTheTargetAreSoldAndTwoWarGodsKept()
        {
            Backpack bag = Bag(Weapon(45, luck: 2, strength: 2, current: 20000, max: 20000),
                (Benediction, 4), (Conservation, 3), (WarGod, 5));

            Dictionary<int, long> keep = Keeps(bag);

            Assert.False(keep.ContainsKey(0));      // Benediction: Luck is 2 - all sold
            Assert.False(keep.ContainsKey(1));      // Conservation: Strength is 2 - all sold
            Assert.Equal(Backpack.WarGodOilsKept, keep[2]);
        }

        [Fact]
        public void OilsBelowTheTargetAreAllKept()
        {
            Backpack bag = Bag(Weapon(45, luck: 1, strength: 0), (Benediction, 4), (Conservation, 3));

            Dictionary<int, long> keep = Keeps(bag);

            Assert.Equal(4, keep[0]);
            Assert.Equal(3, keep[1]);
        }

        [Fact]
        public void ALowWeaponKeepsAFewForLater()
        {
            Backpack bag = Bag(Weapon(20), (Benediction, 8));
            Assert.Equal(Backpack.OilsKeptForLaterWeapon, Keeps(bag)[0]);
        }

        [Fact]
        public void AnOilsStatChangeReachesTheWeapon()
        {
            Backpack bag = Bag(Weapon(45, luck: 1));

            bag.NoteStatsChanged(GridType.Equipment, (int)EquipmentSlot.Weapon,
                new Stats { [Stat.Luck] = 1 }, replace: false);

            Assert.Equal(2, Backpack.WeaponStat(bag.Weapon, Stat.Luck));
        }

        // ---- helpers ---------------------------------------------------------------------------

        private static Dictionary<int, long> Keeps(Backpack bag) =>
            (Dictionary<int, long>)typeof(Backpack)
                .GetMethod("PlanConsumableKeeps", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(bag, new object[] { 0, 0, 0 });

        private static Backpack Bag(ClientUserItem weapon, params (ItemInfo Info, long Count)[] oils)
        {
            List<ClientUserItem> items = new List<ClientUserItem> { weapon };

            for (int i = 0; i < oils.Length; i++)
                items.Add(new ClientUserItem { Info = oils[i].Info, Count = oils[i].Count, Slot = i });

            Backpack bag = new Backpack();
            bag.Reset(items);
            return bag;
        }

        private static ClientUserItem Weapon(int level, int luck = 0, int strength = 0,
            int current = 20000, int max = 20000)
        {
            ItemInfo info = new ItemInfo();
            typeof(ItemInfo).GetProperty("Index").SetValue(info, 9000 + level);
            Set(info, "_ItemName", $"Level {level} Sword");
            Set(info, "_ItemType", ItemType.Weapon);
            Set(info, "_RequiredType", RequiredType.Level);
            Set(info, "_RequiredAmount", level);
            Set(info, "_CanRepair", true);
            info.Stats ??= new Stats();

            return new ClientUserItem
            {
                Info = info,
                Count = 1,
                Slot = Globals.EquipmentOffSet + (int)EquipmentSlot.Weapon,
                CurrentDurability = current,
                MaxDurability = max,
                AddedStats = new Stats { [Stat.Luck] = luck, [Stat.Strength] = strength }
            };
        }

        private static ItemInfo Oil(int index, string name, int shape)
        {
            ItemInfo info = new ItemInfo();
            typeof(ItemInfo).GetProperty("Index").SetValue(info, index);
            Set(info, "_ItemName", name);
            Set(info, "_ItemType", ItemType.Consumable);
            Set(info, "_Shape", shape);
            Set(info, "_StackSize", 1000);
            info.Stats ??= new Stats();
            return info;
        }

        private static void Set<T>(object target, string field, T value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(target, value);
    }
}
