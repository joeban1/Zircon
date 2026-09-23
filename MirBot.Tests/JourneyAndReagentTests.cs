using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using Library;
using Library.SystemModels;
using Xunit;

namespace MirBot.Tests
{
    public sealed class JourneyAndReagentTests
    {
        [Fact]
        public void PoisonIsNeededEvenWhileAmuletsAreAlsoShort()
        {
            TownTrip trip = new TownTrip(new BotConfig { ReagentReserve = 300 }, null, null, null);
            WorldModel world = new WorldModel { SelfID = 1, Name = "Jill", Level = 33 };

            world.ApplyMagic(new ClientUserMagic
            {
                InfoIndex = 1,
                Info = Magic(MagicType.SummonSkeleton, "Summon Skeleton", 17)
            });
            world.ApplyMagic(new ClientUserMagic
            {
                InfoIndex = 2,
                Info = Magic(MagicType.PoisonDust, "Poison Dust", 14)
            });

            Backpack bag = new Backpack();
            bag.Reset(new[]
            {
                new ClientUserItem
                {
                    Info = Item("Talisman", ItemType.Amulet),
                    Slot = Globals.EquipmentOffSet + (int)EquipmentSlot.Amulet,
                    Count = 250
                }
            });

            Assert.Equal(new[] { ItemType.Amulet, ItemType.Poison }, trip.ReagentsNeeded(world, bag));
        }

        [Fact]
        public void PoisonOnlyWhenAmuletsAreStocked()
        {
            TownTrip trip = new TownTrip(new BotConfig { ReagentReserve = 300 }, null, null, null);
            WorldModel world = new WorldModel { SelfID = 1, Name = "Jill", Level = 33 };

            world.ApplyMagic(new ClientUserMagic
            {
                InfoIndex = 1,
                Info = Magic(MagicType.SummonSkeleton, "Summon Skeleton", 17)
            });
            world.ApplyMagic(new ClientUserMagic
            {
                InfoIndex = 2,
                Info = Magic(MagicType.PoisonDust, "Poison Dust", 14)
            });

            Backpack bag = new Backpack();
            bag.Reset(new[]
            {
                new ClientUserItem
                {
                    Info = Item("Talisman", ItemType.Amulet),
                    Slot = Globals.EquipmentOffSet + (int)EquipmentSlot.Amulet,
                    Count = 300
                }
            });

            Assert.Equal(new[] { ItemType.Poison }, trip.ReagentsNeeded(world, bag));
        }

        [Fact]
        public void FightThroughHoldsWhileKillsKeepComing()
        {
            DateTime now = DateTime.UtcNow;
            WorldModel world = new WorldModel { LastExperienceGainUtc = now.AddSeconds(-10) };

            // 120 seconds into the fight - the old total-time limit of 90 would have released it.
            Assert.True(ScriptedBrain.FightThroughProgressing(world, now.AddSeconds(-120), now, 90, 600));
        }

        [Fact]
        public void FightThroughReleasesWithoutAKill()
        {
            DateTime now = DateTime.UtcNow;
            WorldModel world = new WorldModel { LastExperienceGainUtc = now.AddSeconds(-100) };

            Assert.False(ScriptedBrain.FightThroughProgressing(world, now.AddSeconds(-120), now, 90, 600));

            // A fight with no kill yet is measured from its own start.
            world.LastExperienceGainUtc = DateTime.MinValue;
            Assert.True(ScriptedBrain.FightThroughProgressing(world, now.AddSeconds(-30), now, 90, 600));
        }

        [Fact]
        public void FightThroughHasAHardCeiling()
        {
            DateTime now = DateTime.UtcNow;
            WorldModel world = new WorldModel { LastExperienceGainUtc = now.AddSeconds(-5) };

            Assert.False(ScriptedBrain.FightThroughProgressing(world, now.AddSeconds(-700), now, 90, 600));
            Assert.True(ScriptedBrain.FightThroughProgressing(world, now.AddSeconds(-700), now, 90, 0));
        }

        [Fact]
        public void UnroutableExitCellFallsBackToTheOthers()
        {
            Journey journey = new Journey(null);
            MapExit stairs = new MapExit
            {
                ToMapName = "Deserted Mine Lv 2",
                Cells = new[] { new Point(311, 33), new Point(312, 33), new Point(312, 34) }
            };

            Set(journey, "_route", new List<MapExit> { stairs });
            Set(journey, "_leg", 0);
            Set(journey, "_aim", new Point(311, 33));

            WorldModel world = new WorldModel { Location = new Point(285, 74) };

            Assert.True(journey.TryAnotherExitCell(world));
            Assert.Equal(new Point(312, 34), journey.Aim);

            Assert.True(journey.TryAnotherExitCell(world));
            Assert.Equal(new Point(312, 33), journey.Aim);

            Assert.False(journey.TryAnotherExitCell(world));
        }

        [Fact]
        public void RouteSnapshotTrimsLongRoutesButKeepsTheGoal()
        {
            List<Point> path = new List<Point>();
            for (int i = 1; i <= 650; i++) path.Add(new Point(i, 10));

            Point[] snap = ScriptedBrain.RouteSnapshot(path, new Point(650, 10));

            Assert.True(snap.Length <= 201);
            Assert.Equal(new Point(1, 10), snap[0]);
            Assert.Equal(new Point(650, 10), snap[snap.Length - 1]);

            // No grid: the bot steers straight at the goal, so that is the whole route.
            Assert.Equal(new[] { new Point(5, 5) }, ScriptedBrain.RouteSnapshot(null, new Point(5, 5)));
            Assert.Empty(ScriptedBrain.RouteSnapshot(new List<Point>(), new Point(5, 5)));
        }

        [Fact]
        public void JourneyLootKeepsOnlyValuableOrUsefulDrops()
        {
            Backpack bag = new Backpack();
            bag.Reset(new ClientUserItem[0]);

            ItemInfo junk = Single(Priced(Item("Copper Ore", ItemType.Ore), 500, 4));
            ItemInfo valuable = Single(Priced(Item("Silver Ore", ItemType.Ore), 2500, 4));
            ItemInfo book = Priced(Item("Fire Wall", ItemType.Book), 100, 1);
            ItemInfo part = Priced(Item("[Part]", ItemType.ItemPart), 10, 1);
            ItemInfo potion = Priced(Item("Healing Potion", ItemType.Consumable), 50, 1);
            potion.Stats[Stat.Health] = 50;
            ItemInfo necklace = Single(Priced(Item("Plain Necklace", ItemType.Necklace), 100, 1));

            Assert.False(bag.WorthLootingOnJourney(junk, null, MirClass.Wizard, 1500));
            Assert.True(bag.WorthLootingOnJourney(valuable, null, MirClass.Wizard, 1500));
            Assert.True(bag.WorthLootingOnJourney(part, null, MirClass.Wizard, 1500));
            Assert.True(bag.WorthLootingOnJourney(potion, null, MirClass.Wizard, 1500));

            // Empty necklace slot: any necklace is an upgrade, however cheap.
            Assert.True(bag.WorthLootingOnJourney(necklace, null, MirClass.Wizard, 1500));
        }

        [Fact]
        public void JourneyLootTakesOnlyBooksThisClassCanLearn()
        {
            MagicBooks books = new MagicBooks();
            Dictionary<int, MagicInfo> byShape = (Dictionary<int, MagicInfo>)typeof(MagicBooks)
                .GetField("_byShape", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(books);
            byShape[10] = ClassMagic(MagicType.FireWall, "Fire Wall", RequiredClass.Wizard);
            byShape[11] = ClassMagic(MagicType.Swordsmanship, "Swordsmanship", RequiredClass.Warrior);

            ItemInfo fireWall = Single(Priced(Item("Fire Wall", ItemType.Book), 1000, 1));
            Set(fireWall, "_Shape", 10);
            ItemInfo swordsmanship = Single(Priced(Item("Swordsmanship", ItemType.Book), 1000, 1));
            Set(swordsmanship, "_Shape", 11);

            Backpack bag = new Backpack();
            bag.Reset(new ClientUserItem[0]);
            WorldModel wizard = new WorldModel { Level = 35 };

            Assert.True(bag.WorthLootingOnJourney(fireWall, null, MirClass.Wizard, 1500, books, wizard));
            Assert.False(bag.WorthLootingOnJourney(swordsmanship, null, MirClass.Wizard, 1500, books, wizard));
        }

        [Fact]
        public void WeightlessCargoStillHasToPayForItsSlot()
        {
            Backpack bag = new Backpack();
            bag.Reset(new ClientUserItem[0]);

            ItemInfo token = Single(Priced(Item("Worthless Token", ItemType.Nothing), 400, 0));
            ItemInfo gold = Priced(Item("Gold", ItemType.Currency), 0, 0);

            Assert.False(bag.WorthLootingOnJourney(token, null, MirClass.Wizard, 1500));
            Assert.True(bag.WorthLootingOnJourney(gold, null, MirClass.Wizard, 1500));
        }

        [Fact]
        public void StackableMaterialsAreTakenWhileTravelling()
        {
            Backpack bag = new Backpack();
            bag.Reset(new ClientUserItem[0]);

            ItemInfo bone = Priced(Item("Zombie Bone", ItemType.Nothing), 400, 0);

            Assert.True(bag.WorthLootingOnJourney(bone, null, MirClass.Wizard, 1500));
        }

        private static MagicInfo ClassMagic(MagicType type, string name, RequiredClass cls)
        {
            MagicInfo info = Magic(type, name, 1);
            Set(info, "_RequiredClass", cls);
            Set(info, "_School", MagicSchool.Fire);
            return info;
        }

        [Fact]
        public void JourneyLootRefusesCheapGearWeAlreadyBeat()
        {
            ItemInfo worn = Single(Priced(Item("Good Necklace", ItemType.Necklace), 100, 1));
            worn.Stats[Stat.MaxMC] = 5;
            ItemInfo drop = Single(Priced(Item("Plain Necklace", ItemType.Necklace), 100, 1));

            Backpack bag = new Backpack();
            bag.Reset(new[]
            {
                new ClientUserItem
                {
                    Info = worn,
                    Slot = Globals.EquipmentOffSet + (int)EquipmentSlot.Necklace,
                    Count = 1
                }
            });

            Assert.False(bag.WorthLootingOnJourney(drop, null, MirClass.Wizard, 1500));
        }

        private static ItemInfo Single(ItemInfo info)
        {
            Set(info, "_StackSize", 1);
            return info;
        }

        private static ItemInfo Priced(ItemInfo info, int price, int weight)
        {
            Set(info, "_Price", price);
            Set(info, "_SellRate", 1m);
            Set(info, "_Weight", weight);
            return info;
        }

        private static ItemInfo Item(string name, ItemType type)
        {
            ItemInfo info = new ItemInfo();
            Set(info, "_ItemName", name);
            Set(info, "_ItemType", type);
            Set(info, "_RequiredClass", RequiredClass.All);
            Set(info, "_RequiredGender", RequiredGender.None);
            Set(info, "_StackSize", 1000);
            return info;
        }

        private static MagicInfo Magic(MagicType type, string name, int needLevel)
        {
            MagicInfo info = new MagicInfo();
            Set(info, "_Name", name);
            Set(info, "_Magic", type);
            Set(info, "_NeedLevel1", needLevel);
            return info;
        }

        private static void Set<T>(object target, string field, T value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(target, value);
    }
}
