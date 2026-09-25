using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using Library;
using Library.SystemModels;
using Xunit;

namespace MirBot.Tests
{
    /// <summary>
    /// 2026-09-25: emblems can be worn; elemental attack and resistance count in gear scoring;
    /// Dragon Tornado and Lightning Wave are cast and trained; a warrior's charge toggle hides
    /// inside the swing cooldown; swings pace at the server's attack-speed gate.
    /// </summary>
    public sealed class CombatPacingAndGearTests
    {
        // ---- emblems ---------------------------------------------------------------------------

        private static readonly int EmblemSlot = Globals.EquipmentOffSet + (int)EquipmentSlot.Emblem;

        [Fact]
        public void AMastersEmblemFillsTheEmptyEmblemSlot()
        {
            Backpack bag = new Backpack();
            bag.Reset(new[] { new ClientUserItem { Info = Masters(), Count = 1, Slot = 3 } });

            EquipRequest request = Assert.Single(bag.PendingEquips(MirClass.Warrior, MirGender.Male));
            Assert.Equal(EquipmentSlot.Emblem, request.Slot);
        }

        [Fact]
        public void AStatlessEmblemIsNotWornButAStatlessTorchStillIs()
        {
            Backpack bag = new Backpack();
            bag.Reset(new[]
            {
                new ClientUserItem { Info = Item("PVP Apprentice", ItemType.Emblem, 1), Count = 1, Slot = 3 },
                new ClientUserItem { Info = Item("Candle", ItemType.Torch, 0), Count = 1, Slot = 4 }
            });

            EquipRequest request = Assert.Single(bag.PendingEquips(MirClass.Warrior, MirGender.Male));
            Assert.Equal(EquipmentSlot.Torch, request.Slot);
        }

        [Fact]
        public void AFutureEmblemIsBankedUnlessAStrongerOneIsWorn()
        {
            ItemInfo novice = Item("Novice Emblem", ItemType.Emblem, 60,
                (Stat.MinAC, 2), (Stat.MaxAC, 8), (Stat.MinMR, 2), (Stat.MaxMR, 8));
            ClientUserItem future = new ClientUserItem { Info = novice, Count = 1, Slot = 3 };

            Backpack empty = new Backpack();
            empty.Reset(new[] { future });
            Assert.True(Backpack.WorthStoring(future, MirClass.Warrior, MirGender.Male, 45, new Stats(), null, null));
            Assert.True(empty.ShouldBank(future, MirClass.Warrior, MirGender.Male, 45, new Stats(), null, null));
            Assert.Equal(0, empty.UpgradeGain(novice, MirClass.Warrior, MirGender.Male, 45, new Stats()));

            Backpack wearing = new Backpack();
            wearing.Reset(new[] { future, new ClientUserItem { Info = Masters(), Count = 1, Slot = EmblemSlot } });
            Assert.False(wearing.ShouldBank(future, MirClass.Warrior, MirGender.Male, 45, new Stats(), null, null));
        }

        // ---- elemental scoring -----------------------------------------------------------------

        [Fact]
        public void AnElementalRollBreaksTheTieOnAnOtherwiseIdenticalNecklace()
        {
            ItemInfo butcher = Item("Butcher's Necklace", ItemType.Necklace, 30, (Stat.MaxDC, 6));
            Backpack bag = new Backpack();
            bag.Reset(new[]
            {
                new ClientUserItem { Info = butcher, Count = 1, Slot = Globals.EquipmentOffSet + (int)EquipmentSlot.Necklace },
                new ClientUserItem { Info = butcher, Count = 1, Slot = 3,
                    AddedStats = new Stats { [Stat.FireAttack] = 1, [Stat.HolyAttack] = 1 } }
            });

            EquipRequest request = Assert.Single(bag.PendingEquips(MirClass.Warrior, MirGender.Male));
            Assert.Equal(EquipmentSlot.Necklace, request.Slot);
            Assert.Contains("(48 -> 53)", request.Reason);
        }

        [Fact]
        public void TheLargestElementCountsFourAndTheRestOneAndResistancesOne()
        {
            ItemInfo plain = Item("Ring", ItemType.Ring, 1);
            ItemInfo twoElements = Item("Ring", ItemType.Ring, 1, (Stat.FireAttack, 1), (Stat.HolyAttack, 1));
            ItemInfo resist = Item("Ring", ItemType.Ring, 1, (Stat.FireResistance, 1), (Stat.PhysicalResistance, 1));

            Assert.Equal(0, Backpack.ScoreInfo(plain, MirClass.Warrior));
            Assert.Equal(5, Backpack.ScoreInfo(twoElements, MirClass.Warrior));
            Assert.Equal(3, Backpack.ScoreInfo(resist, MirClass.Warrior));
        }

        // ---- area spell choice ------------------------------------------------------------------

        [Fact]
        public void AnUntrainedAreaSpellIsTrainedOnEqualCoverage()
        {
            WorldModel world = Wizard();
            ClientUserMagic fireStorm = Spell(1, MagicType.FireStorm, 32, 3, 14, 18, 14, 18);
            ClientUserMagic tornado = Spell(2, MagicType.DragonTornado, 35, 0, 14, 18, 13, 17);

            Assert.True(SpellBook.BetterAreaSpell(world, tornado, fireStorm));
            Assert.False(SpellBook.BetterAreaSpell(world, fireStorm, tornado));
        }

        [Fact]
        public void TrainingDoesNotStarveTheOtherSpell()
        {
            WorldModel world = Wizard();
            ClientUserMagic tornado = Spell(2, MagicType.DragonTornado, 35, 3, 14, 18, 13, 17);
            ClientUserMagic wave = Spell(3, MagicType.LightningWave, 33, 1, 14, 18, 14, 18);

            Assert.True(SpellBook.BetterAreaSpell(world, wave, tornado));
        }

        [Fact]
        public void TrainedSpellsCompeteOnPowerThenCost()
        {
            WorldModel world = Wizard();
            ClientUserMagic fireStorm = Spell(1, MagicType.FireStorm, 32, 3, 14, 18, 14, 18);
            ClientUserMagic iceStorm = Spell(4, MagicType.IceStorm, 34, 3, 14, 18, 12, 16);
            Assert.True(SpellBook.BetterAreaSpell(world, fireStorm, iceStorm));

            ClientUserMagic cheap = Spell(5, MagicType.LightningWave, 33, 3, 14, 18, 14, 18, cost: 10);
            Assert.True(SpellBook.BetterAreaSpell(world, cheap, fireStorm));
        }

        [Fact]
        public void ChooseAreaCastsTheUntrainedTornadoAndHigherCoverageStillWins()
        {
            WorldModel world = Wizard();
            world.ApplyMagic(Spell(1, MagicType.FireStorm, 32, 3, 14, 18, 14, 18));
            world.ApplyMagic(Spell(2, MagicType.DragonTornado, 35, 0, 14, 18, 13, 17));
            world.AddMonster(10, "a", 0, new Point(14, 14), MirDirection.Up, false);
            world.AddMonster(11, "b", 0, new Point(15, 15), MirDirection.Up, false);

            SpellBook book = new SpellBook(new BotConfig());
            MapGrid grid = MapGrid.ForTests(30, 30, (_, _) => true);

            var aim = book.ChooseArea(world, world.Find(10), grid);
            Assert.NotNull(aim);
            Assert.Equal(MagicType.DragonTornado, aim.Value.Magic.Info.Magic);
            Assert.Equal(2, aim.Value.Covered);

            // A Fire Wall that covers three beats the tornado's two, however untrained it is.
            world.ApplyMagic(Spell(6, MagicType.FireWall, 24, 3, 1, 6, 2, 9));
            world.AddMonster(12, "c", 0, new Point(14, 15), MirDirection.Up, false);
            world.AddMonster(13, "d", 0, new Point(16, 15), MirDirection.Up, false);
            var wider = book.ChooseArea(world, world.Find(10), grid);
            Assert.NotNull(wider);
            Assert.True(wider.Value.Covered >= 3);
        }

        // ---- swing pacing ----------------------------------------------------------------------

        [Theory]
        [InlineData(0, 1500)]
        [InlineData(3, 1359)]
        [InlineData(20, 800)]
        public void SwingDelayFollowsAttackSpeed(int speed, int expected)
        {
            WorldModel world = new WorldModel();
            world.ApplyStats(new Stats { [Stat.AttackSpeed] = speed, [Stat.BagWeight] = 500 });
            Assert.Equal(expected, (int)world.SwingDelay().TotalMilliseconds);
        }

        [Fact]
        public void SwingDelayIsFlatUntilStatsArriveAndDoublesOverweightOrNeutralized()
        {
            WorldModel world = new WorldModel();
            Assert.Equal(1500, (int)world.SwingDelay().TotalMilliseconds);

            world.ApplyStats(new Stats { [Stat.AttackSpeed] = 3, [Stat.BagWeight] = 500 });
            world.BagWeight = 500;
            Assert.Equal(1359, (int)world.SwingDelay().TotalMilliseconds);     // exactly at the limit
            world.BagWeight = 501;
            Assert.Equal(2718, (int)world.SwingDelay().TotalMilliseconds);     // one over
            world.BagWeight = 100;

            world.SelfID = 7;
            world.ApplyPoison(7, PoisonType.Neutralize);
            Assert.Equal(2718, (int)world.SwingDelay().TotalMilliseconds);
            world.ApplyPoison(7, PoisonType.None);
            Assert.Equal(1359, (int)world.SwingDelay().TotalMilliseconds);
        }

        [Fact]
        public void TogglesShareOneClockAndASwingWaitsForTheChargeToArm()
        {
            SkillSet skills = new SkillSet();
            Assert.True(skills.ToggleReady);

            skills.ChargeSent(MagicType.FlamingSword);
            skills.ToggleIssued();
            Assert.False(skills.ToggleReady);
            Assert.True(skills.AwaitingCharge);

            skills.Toggled(MagicType.FlamingSword, true);
            Assert.False(skills.AwaitingCharge);
            Assert.True(skills.IsArmed(MagicType.FlamingSword));
        }

        [Fact]
        public void AChargeIsArmedDuringTheSwingCooldownWithoutMovingIt()
        {
            ScriptedBrain brain = new ScriptedBrain(new BotConfig());
            WorldModel world = WarriorWithCharge();
            WorldObject target = world.Find(42);

            Decision swing = new Decision
            {
                Action = BotAction.Attack, TargetID = target.ObjectID, Subject = target.Name
            };
            typeof(ScriptedBrain).GetField("_committedTarget", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(brain, target.ObjectID);
            brain.Issued(swing, world);
            Assert.False(brain.Ready);
            DateTime gate = (DateTime)typeof(ScriptedBrain)
                .GetField("_nextAction", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(brain);

            Decision toggle = brain.Decide(world, new Backpack(), false);
            Assert.NotNull(toggle);
            Assert.Equal(BotAction.MagicToggle, toggle.Action);
            brain.Issued(toggle, world);

            DateTime after = (DateTime)typeof(ScriptedBrain)
                .GetField("_nextAction", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(brain);
            Assert.Equal(gate, after);

            // The toggle clock stops a second one inside the same cooldown.
            Assert.Null(brain.Decide(world, new Backpack(), false));
        }

        [Fact]
        public void NoMidSwingChargeOnADeadTarget()
        {
            ScriptedBrain brain = new ScriptedBrain(new BotConfig());
            WorldModel world = WarriorWithCharge();
            WorldObject target = world.Find(42);
            typeof(ScriptedBrain).GetField("_committedTarget", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(brain, target.ObjectID);
            brain.Issued(new Decision { Action = BotAction.Attack, TargetID = 42 }, world);

            target.Dead = true;
            Assert.Null(brain.Decide(world, new Backpack(), false));
        }

        // ---- helpers ---------------------------------------------------------------------------

        private static WorldModel Wizard() => new WorldModel
        {
            Class = MirClass.Wizard, Level = 45, Location = new Point(10, 10), MapIndex = 1,
            Mana = 1000, MaxMana = 1000
        };

        private static WorldModel WarriorWithCharge()
        {
            WorldModel world = new WorldModel
            {
                Class = MirClass.Warrior, Level = 46, Location = new Point(10, 10), MapIndex = 1,
                Mana = 200, MaxMana = 200, Health = 2000, MaxHealth = 2000, SelfID = 1
            };
            MagicInfo info = new MagicInfo();
            typeof(MagicInfo).GetProperty("Index").SetValue(info, 77);
            Set(info, "_Name", "Flaming Sword");
            Set(info, "_Magic", MagicType.FlamingSword);
            Set(info, "_NeedLevel1", 1);
            Set(info, "_BaseCost", 10);
            Set(info, "_School", MagicSchool.Active);
            Set(info, "_RequiredClass", RequiredClass.Warrior);
            world.ApplyMagic(new ClientUserMagic { InfoIndex = 77, Info = info, Level = 3 });
            world.AddMonster(42, "Minotaur", 0, new Point(11, 10), MirDirection.Left, false);
            return world;
        }

        private static ClientUserMagic Spell(int index, MagicType type, int need1, int level,
            int minBase, int maxBase, int minLevel, int maxLevel, int cost = 20)
        {
            MagicInfo info = new MagicInfo();
            Set(info, "_Name", type.ToString());
            Set(info, "_Magic", type);
            Set(info, "_NeedLevel1", need1);
            Set(info, "_NeedLevel2", need1 + 2);
            Set(info, "_NeedLevel3", need1 + 4);
            Set(info, "_BaseCost", cost);
            Set(info, "_MinBasePower", minBase);
            Set(info, "_MaxBasePower", maxBase);
            Set(info, "_MinLevelPower", minLevel);
            Set(info, "_MaxLevelPower", maxLevel);
            return new ClientUserMagic { InfoIndex = index, Info = info, Level = level };
        }

        private static ItemInfo Masters() => Item("Masters Emblem", ItemType.Emblem, 0,
            (Stat.MinAC, 12), (Stat.MaxAC, 12), (Stat.MinMR, 12), (Stat.MaxMR, 12),
            (Stat.CriticalChance, 5));

        private static int _nextIndex = 5000;

        private static ItemInfo Item(string name, ItemType type, int level, params (Stat Stat, int Amount)[] stats)
        {
            ItemInfo info = new ItemInfo();
            typeof(ItemInfo).GetProperty("Index").SetValue(info, _nextIndex++);
            Set(info, "_ItemName", name);
            Set(info, "_ItemType", type);
            Set(info, "_RequiredClass", RequiredClass.All);
            Set(info, "_RequiredGender", RequiredGender.None);
            Set(info, "_RequiredType", RequiredType.Level);
            Set(info, "_RequiredAmount", level);
            Set(info, "_StackSize", 1);
            info.Stats = new Stats();
            foreach ((Stat stat, int amount) in stats) info.Stats[stat] = amount;
            return info;
        }

        private static void Set<T>(object target, string field, T value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(target, value);
    }
}
