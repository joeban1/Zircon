using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using Library;
using Library.SystemModels;
using Xunit;

namespace MirBot.Tests
{
    /// <summary>
    /// The game store: the per-class shopping order, saving up rather than skipping, temporaries
    /// only after every permanent, Hunt Gold tracking, and the purchase settlement. Also the mana
    /// potion ceiling and the quest watchdog changes that shipped alongside.
    /// </summary>
    public sealed class GameStoreTests
    {
        private static readonly List<StoreInfo> Rows = BuildRows();

        // ---- the list ------------------------------------------------------------------------

        [Fact]
        public void AWarriorBuysInTheOperatorsOrder()
        {
            GameStore store = Built();

            Assert.Equal(new[]
            {
                "Mir Package [P]", "Tonic Of Destruction [P]", "Mark Of Destruction [P]",
                "Tonic Of Velocity [P]", "Tonic Of Life [P]", "Tonic Of Mana [P]",
                "Tonic Of Dexterity [P]", "Tonic Of Experience [P]", "Tonic Of Knowledge [P]",
                "Tonic Of Spelunking [P]", "Tonic Of Treasure [P]", "Tonic Of Wealth [P]",
                "Mir Package [T]", "Tonic Of Destruction [T]", "Tonic Of Velocity [T]",
                "Tonic Of Life [T]", "Tonic Of Mana [T]", "Tonic Of Experience [T]"
            }, store.WantsFor(MirClass.Warrior).Select(w => w.Name));
        }

        [Fact]
        public void CastersTakeTheirOwnTonicAndMarkAndSkipTheMeleeOnes()
        {
            GameStore store = Built();

            List<string> wizard = store.WantsFor(MirClass.Wizard).Select(w => w.Name).ToList();
            Assert.Equal("Tonic Of Nature [P]", wizard[1]);
            Assert.Equal("Mark Of Nature [P]", wizard[2]);
            Assert.DoesNotContain(wizard, n => n.Contains("Velocity") || n.Contains("Dexterity"));

            List<string> taoist = store.WantsFor(MirClass.Taoist).Select(w => w.Name).ToList();
            Assert.Equal("Mark Of Spirit [P]", taoist[2]);

            List<string> assassin = store.WantsFor(MirClass.Assassin).Select(w => w.Name).ToList();
            Assert.Equal("Mark Of Destruction [P]", assassin[2]);
            Assert.Contains("Tonic Of Velocity [P]", assassin);
        }

        [Fact]
        public void AnItemTheStoreDoesNotListForTheClassIsSkipped()
        {
            List<StoreInfo> rows = BuildRows();
            Set(rows.First(r => r.Item.ItemName == "Mark Of Nature [P]"), "_Filter", "Torch, Permanent, Taoist");
            Set(rows.First(r => r.Item.ItemName == "Tonic Of Life [P]"), "_Available", false);

            GameStore store = new GameStore();
            store.Build(rows);

            Assert.DoesNotContain(store.WantsFor(MirClass.Wizard), w => w.Name == "Mark Of Nature [P]");
            Assert.DoesNotContain(store.WantsFor(MirClass.Warrior), w => w.Name == "Tonic Of Life [P]");
            Assert.Contains(store.Report, l => l.Contains("Mark Of Nature [P]") && l.Contains("not listed"));
        }

        [Fact]
        public void TheNextBuyIsTheFirstUnownedEvenWhenItCannotBeAfforded()
        {
            GameStore store = Built();
            WorldModel world = World();
            Backpack bag = new Backpack();

            StoreWant first = store.Next(MirClass.Warrior, w => GameStore.Satisfied(w, world, bag, Hour));
            Assert.Equal("Mir Package [P]", first.Name);

            // Owning the package moves on to the tonic - not to something cheaper out of order.
            world.ResetBuffs(new[] { Buff(1, first.Item.Index, TimeSpan.MaxValue) });
            StoreWant second = store.Next(MirClass.Warrior, w => GameStore.Satisfied(w, world, bag, Hour));
            Assert.Equal("Tonic Of Destruction [P]", second.Name);
        }

        [Fact]
        public void AMarkIsOwnedOnceWornAndAPermanentOnceHeld()
        {
            GameStore store = Built();
            WorldModel world = World();
            Backpack bag = new Backpack();
            StoreWant mark = store.WantsFor(MirClass.Warrior).First(w => w.IsMark);
            StoreWant tonic = store.WantsFor(MirClass.Warrior)[1];

            Assert.False(GameStore.Satisfied(mark, world, bag, Hour));
            bag.Set(new ClientUserItem { Info = mark.Item, Count = 1, Slot = Globals.EquipmentOffSet + (int)EquipmentSlot.Torch });
            Assert.True(GameStore.Satisfied(mark, world, bag, Hour));

            Assert.False(GameStore.Satisfied(tonic, world, bag, Hour));
            bag.Set(new ClientUserItem { Info = tonic.Item, Count = 1, Slot = 5 });
            Assert.True(GameStore.Satisfied(tonic, world, bag, Hour));
        }

        [Fact]
        public void TemporariesWaitForEveryPermanentThenRebuyNearExpiry()
        {
            GameStore store = Built();
            WorldModel world = World();
            Backpack bag = new Backpack();
            List<StoreWant> wants = store.WantsFor(MirClass.Warrior).ToList();

            // Every permanent but the last owned: still the permanent, never a temporary.
            List<ClientBuffInfo> buffs = wants.Where(w => w.Permanent && !w.IsMark).Take(wants.Count(w => w.Permanent && !w.IsMark) - 1)
                .Select((w, i) => Buff(i + 1, w.Item.Index, TimeSpan.MaxValue)).ToList();
            bag.Set(new ClientUserItem { Info = wants.First(w => w.IsMark).Item, Count = 1, Slot = Globals.EquipmentOffSet + (int)EquipmentSlot.Torch });
            world.ResetBuffs(buffs);
            Assert.Equal("Tonic Of Wealth [P]", store.Next(MirClass.Warrior, w => GameStore.Satisfied(w, world, bag, Hour)).Name);

            // All permanents owned: the first temporary.
            buffs.Add(Buff(100, wants.First(w => w.Name == "Tonic Of Wealth [P]").Item.Index, TimeSpan.MaxValue));
            world.ResetBuffs(buffs);
            StoreWant temp = store.Next(MirClass.Warrior, w => GameStore.Satisfied(w, world, bag, Hour));
            Assert.Equal("Mir Package [T]", temp.Name);

            // Running with plenty left: satisfied. Nearly spent: wanted again.
            buffs.Add(Buff(101, temp.Item.Index, TimeSpan.FromDays(3)));
            world.ResetBuffs(buffs);
            Assert.True(GameStore.Satisfied(temp, world, bag, Hour));

            buffs[buffs.Count - 1] = Buff(101, temp.Item.Index, TimeSpan.FromMinutes(20));
            world.ResetBuffs(buffs);
            Assert.False(GameStore.Satisfied(temp, world, bag, Hour));
        }

        // ---- Hunt Gold and settlement ---------------------------------------------------------

        [Fact]
        public void HuntGoldIsTrackedSeparatelyFromGold()
        {
            WorldModel world = World();
            CurrencyInfo gold = Currency(1, CurrencyType.Gold), hunt = Currency(2, CurrencyType.HuntGold);

            world.ApplyCurrencies(new[]
            {
                new ClientUserCurrency { Info = gold, Amount = 5000 },
                new ClientUserCurrency { Info = hunt, Amount = 820 }
            });
            Assert.Equal(5000, world.Gold);
            Assert.Equal(820, world.HuntGold);

            world.ApplyCurrency(2, 420);
            Assert.Equal(5000, world.Gold);
            Assert.Equal(420, world.HuntGold);

            world.ApplyCurrency(1, 4000);
            Assert.Equal(4000, world.Gold);
            Assert.Equal(420, world.HuntGold);
        }

        [Fact]
        public void APurchaseIsConfirmedByTheHuntGoldDropOrBackedOff()
        {
            GameStore store = Built();
            StoreWant package = store.WantsFor(MirClass.Warrior)[0];
            List<string> log = new List<string>();
            StoreShopper shopper = new StoreShopper { Log = log.Add };
            DateTime t0 = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

            shopper.NoteSent(package, 2000, t0);
            shopper.Update(2000, t0.AddSeconds(1));
            Assert.True(shopper.Pending);

            shopper.Update(600, t0.AddSeconds(2));
            Assert.False(shopper.Pending);
            Assert.True(shopper.JustBought(package, t0.AddSeconds(3)));
            Assert.Contains(log, l => l.Contains("bought Mir Package [P]"));

            // No drop within the window: refused, and left alone for a while.
            shopper.NoteSent(package, 600, t0.AddMinutes(1));
            shopper.NoteServerLine("You cannot carry this many items, please make room in your inventory.",
                t0.AddMinutes(1).AddSeconds(1));
            shopper.Update(600, t0.AddMinutes(1).AddSeconds(6));
            Assert.False(shopper.Pending);
            Assert.True(shopper.InBackoff(package, t0.AddMinutes(5)));
            Assert.False(shopper.InBackoff(package, t0.AddMinutes(12)));
            Assert.Contains(log, l => l.Contains("refused") && l.Contains("cannot carry"));
        }

        // ---- the mark beats a torch --------------------------------------------------------------

        [Theory]
        [InlineData(MirClass.Warrior, "Mark Of Destruction [P]")]
        [InlineData(MirClass.Wizard, "Mark Of Nature [P]")]
        [InlineData(MirClass.Taoist, "Mark Of Spirit [P]")]
        [InlineData(MirClass.Assassin, "Mark Of Destruction [P]")]
        public void TheClassMarkOutscoresAPlainTorch(MirClass c, string mark)
        {
            ItemInfo torch = Item(900, "Torch", ItemType.Torch, 0);
            ItemInfo markInfo = Rows.First(r => r.Item.ItemName == mark).Item;

            Assert.True(Backpack.ScoreInfo(markInfo, c) > Backpack.ScoreInfo(torch, c));
        }

        // ---- store buffs are not potions ---------------------------------------------------

        [Fact]
        public void ATonicOfLifeOrManaIsABuffNotAPotion()
        {
            ItemInfo life = Item(950, "Tonic Of Life [T]", ItemType.Consumable, 1);
            life.Stats[Stat.Health] = 70;
            life.Stats[Stat.Duration] = 86400;
            ItemInfo mana = Item(951, "Tonic Of Mana [P]", ItemType.Consumable, 1);
            mana.Stats[Stat.Mana] = 30;
            mana.Stats[Stat.Duration] = -1;
            ItemInfo potion = Item(952, "Mana Potion (II)", ItemType.Consumable, 0);
            potion.Stats[Stat.Mana] = 110;

            ClientUserItem Held(ItemInfo i) => new ClientUserItem { Info = i, Count = 1,
                Flags = UserItemFlags.Locked | UserItemFlags.Worthless | UserItemFlags.Bound };

            Assert.False(Backpack.IsHealthPotion(Held(life)));
            Assert.False(Backpack.IsWeakRestorative(Held(life)));
            Assert.False(Backpack.IsManaPotion(Held(mana)));
            Assert.True(Backpack.IsManaPotion(new ClientUserItem { Info = potion, Count = 1 }));
            Assert.False(Backpack.Sellable(Held(life), null));
        }

        // ---- part-crafted gear --------------------------------------------------------------

        [Fact]
        public void GearThatPartsCombineIntoIsNeverSoldAndIsBanked()
        {
            ItemInfo crafted = Item(970, "Dragon Necklace Of Revival", ItemType.Necklace, 0);
            Set(crafted, "_PartCount", 10);
            ItemInfo plain = Item(971, "Iron Plate Necklace", ItemType.Necklace, 0);
            ClientUserItem craftedItem = new ClientUserItem { Info = crafted, Count = 1 };
            ClientUserItem plainItem = new ClientUserItem { Info = plain, Count = 1 };
            Set(crafted, "_CanSell", true);
            Set(plain, "_CanSell", true);

            Assert.True(Backpack.IsPartCrafted(craftedItem));
            Assert.False(Backpack.IsPartCrafted(plainItem));
            Assert.False(Backpack.Sellable(craftedItem, null));
            Assert.True(Backpack.Sellable(plainItem, null));
            Assert.True(Backpack.WorthStoring(craftedItem, MirClass.Taoist, MirGender.Male, 44, new Stats(), null, null));
        }

        // ---- mana potion ceiling ---------------------------------------------------------------

        [Theory]
        [InlineData(182, 110, true)]    // Mirbot: tier II now fits (was 109)
        [InlineData(171, 110, true)]    // Banner
        [InlineData(328, 180, true)]    // Sindo: tier III
        [InlineData(328, 250, false)]   //        but not tier IV
        [InlineData(1073, 250, true)]   // a wizard: tier IV
        public void AManaPotionMayOvershootByATenthOfThePool(int pool, int restores, bool fits)
        {
            Assert.Equal(fits, restores <= TownTrip.PotionCeiling(pool, 40, healing: false));
        }

        [Fact]
        public void TheHealthCeilingIsUnchanged()
        {
            Assert.Equal(1812 * 40 / 100, TownTrip.PotionCeiling(1812, 60, healing: true));
            Assert.Equal(int.MaxValue, TownTrip.PotionCeiling(0, 60, healing: true));
        }

        // ---- level 4 training books --------------------------------------------------------

        [Fact]
        public void ALevelThreeSkillOnlyTrainsFromBooksOrdinaryMonstersDrop()
        {
            BookDropIndex index = new BookDropIndex();
            var anyBook = (Dictionary<int, ItemInfo>)typeof(BookDropIndex)
                .GetField("_anyBook", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(index);
            var ordinary = (HashSet<int>)typeof(BookDropIndex)
                .GetField("_fromOrdinary", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(index);

            anyBook[1] = Item(961, "Flaming Sword", ItemType.Book, 0);     // mini-boss only
            anyBook[2] = Item(962, "Thrusting", ItemType.Book, 0);         // ordinary monsters too
            ordinary.Add(2);

            WorldModel world = World();
            world.ApplyMagic(new ClientUserMagic { InfoIndex = 1, Level = 3 });
            world.ApplyMagic(new ClientUserMagic { InfoIndex = 2, Level = 3 });

            HashSet<int> wanted = index.Wanted(MirClass.Warrior, 45, new Stats(), world);

            Assert.True(index.BossOnly(1));
            Assert.DoesNotContain(1, wanted);
            Assert.Contains(2, wanted);
        }

        [Fact]
        public void ANoHuntSkillIsNeverAHuntingTarget()
        {
            BookDropIndex index = new BookDropIndex();
            var anyBook = (Dictionary<int, ItemInfo>)typeof(BookDropIndex)
                .GetField("_anyBook", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(index);
            var ordinary = (HashSet<int>)typeof(BookDropIndex)
                .GetField("_fromOrdinary", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(index);
            var noHunt = (HashSet<int>)typeof(BookDropIndex)
                .GetField("_noHunt", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(index);

            anyBook[3] = Item(963, "Potion Mastery", ItemType.Book, 0);    // ordinary monsters drop it
            ordinary.Add(3);

            WorldModel world = World();
            world.ApplyMagic(new ClientUserMagic { InfoIndex = 3, Level = 3 });

            Assert.Contains(3, index.Wanted(MirClass.Warrior, 45, new Stats(), world));

            noHunt.Add(3);                                                   // NoHuntSkills=Potion Mastery
            Assert.True(index.NoHunt(3));
            Assert.DoesNotContain(3, index.Wanted(MirClass.Warrior, 45, new Stats(), world));
        }

        // ---- helpers -------------------------------------------------------------------------

        private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

        private static GameStore Built()
        {
            GameStore store = new GameStore();
            store.Build(Rows);
            return store;
        }

        private static WorldModel World() => new WorldModel
        {
            Class = MirClass.Warrior, Level = 44, Location = new Point(10, 10)
        };

        private static ClientBuffInfo Buff(int index, int itemIndex, TimeSpan left) =>
            new ClientBuffInfo { Index = index, Type = BuffType.ItemBuff, ItemIndex = itemIndex, RemainingTime = left };

        /// <summary>The live store rows that matter, with their real names, prices and filters.</summary>
        private static List<StoreInfo> BuildRows()
        {
            const string All = "Warrior, Wizard, Taoist, Assassin", Melee = "Warrior, Assassin";
            var spec = new List<(string Name, int Price, string Filter, ItemType Type, Stat Stat)>
            {
                ("Mark Of Destruction [P]", 1000, "Torch, Permanent, " + Melee, ItemType.Torch, Stat.MaxDC),
                ("Mark Of Nature [P]", 1000, "Torch, Permanent, Wizard", ItemType.Torch, Stat.MaxMC),
                ("Mark Of Spirit [P]", 1000, "Torch, Permanent, Taoist", ItemType.Torch, Stat.MaxSC),
                ("Mir Package [T]", 700, "Consumable, Temporary, " + All, ItemType.Consumable, Stat.None),
                ("Tonic Of Experience [T]", 100, "Consumable, Temporary, " + All, ItemType.Consumable, Stat.None),
                ("Tonic Of Destruction [T]", 100, "Consumable, Temporary, " + Melee, ItemType.Consumable, Stat.None),
                ("Tonic Of Nature [T]", 100, "Consumable, Temporary, Wizard", ItemType.Consumable, Stat.None),
                ("Tonic Of Spirit [T]", 100, "Consumable, Temporary, Taoist", ItemType.Consumable, Stat.None),
                ("Tonic Of Life [T]", 100, "Consumable, Temporary, " + All, ItemType.Consumable, Stat.None),
                ("Tonic Of Mana [T]", 100, "Consumable, Temporary, " + All, ItemType.Consumable, Stat.None),
                ("Tonic Of Velocity [T]", 100, "Consumable, Temporary, " + Melee, ItemType.Consumable, Stat.None),
                ("Mir Package [P]", 1400, "Consumable, Permanent, " + All, ItemType.Consumable, Stat.None),
                ("Tonic Of Experience [P]", 300, "Consumable, Permanent, " + All, ItemType.Consumable, Stat.None),
                ("Tonic Of Treasure [P]", 300, "Consumable, Permanent, " + All, ItemType.Consumable, Stat.None),
                ("Tonic Of Wealth [P]", 300, "Consumable, Permanent, " + All, ItemType.Consumable, Stat.None),
                ("Tonic Of Spelunking [P]", 300, "Consumable, Permanent, " + All, ItemType.Consumable, Stat.None),
                ("Tonic Of Destruction [P]", 300, "Consumable, Permanent, " + Melee, ItemType.Consumable, Stat.None),
                ("Tonic Of Nature [P]", 300, "Consumable, Permanent, Wizard", ItemType.Consumable, Stat.None),
                ("Tonic Of Spirit [P]", 300, "Consumable, Permanent, Taoist", ItemType.Consumable, Stat.None),
                ("Tonic Of Life [P]", 300, "Consumable, Permanent, " + All, ItemType.Consumable, Stat.None),
                ("Tonic Of Mana [P]", 300, "Consumable, Permanent, " + All, ItemType.Consumable, Stat.None),
                ("Tonic Of Velocity [P]", 300, "Consumable, Permanent, " + Melee, ItemType.Consumable, Stat.None),
                ("Tonic Of Dexterity [P]", 300, "Consumable, Permanent, " + Melee, ItemType.Consumable, Stat.None),
                ("Tonic Of Knowledge [P]", 300, "Consumable, Permanent, " + All, ItemType.Consumable, Stat.None),
            };

            List<StoreInfo> rows = new List<StoreInfo>();
            int index = 40;

            foreach (var s in spec)
            {
                ItemInfo item = Item(1000 + index, s.Name, s.Type, 1);
                if (s.Stat != Stat.None) item.Stats[s.Stat] = 3;

                StoreInfo row = new StoreInfo();
                typeof(StoreInfo).GetProperty("Index").SetValue(row, index++);
                Set(row, "_Item", item);
                Set(row, "_Price", s.Price);
                Set(row, "_Filter", s.Filter);
                Set(row, "_Available", true);
                rows.Add(row);
            }

            return rows;
        }

        private static ItemInfo Item(int index, string name, ItemType type, int shape)
        {
            ItemInfo info = new ItemInfo();
            typeof(ItemInfo).GetProperty("Index").SetValue(info, index);
            Set(info, "_ItemName", name);
            Set(info, "_ItemType", type);
            Set(info, "_Shape", shape);
            Set(info, "_StackSize", 1);
            info.Stats ??= new Stats();
            return info;
        }

        private static CurrencyInfo Currency(int index, CurrencyType type)
        {
            CurrencyInfo info = new CurrencyInfo();
            typeof(CurrencyInfo).GetProperty("Index").SetValue(info, index);
            Set(info, "_Type", type);
            return info;
        }

        private static void Set<T>(object target, string field, T value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(target, value);
    }
}
