using System;
using System.Drawing;
using System.Reflection;
using Library;
using Library.SystemModels;
using Xunit;

namespace MirBot.Tests
{
    public sealed class AssassinSkillTests
    {
        [Fact]
        public void LotusAttackIsSelectedWithoutServerToggleAndHonoursCooldown()
        {
            WorldModel world = Assassin();
            world.ApplyMagic(Known(1, MagicType.FullBloom));
            world.ApplyMagic(Known(2, MagicType.WhiteLotus));
            SkillSet skills = new SkillSet();
            WorldObject target = Target();

            Assert.Equal(MagicType.FullBloom, skills.ChooseAttackMagic(world, target, 1));
            skills.Cooldown(1, 60000);
            Assert.Equal(MagicType.WhiteLotus, skills.ChooseAttackMagic(world, target, 1));
            skills.Cooldown(2, 60000);
            Assert.Equal(MagicType.None, skills.ChooseAttackMagic(world, target, 1));
        }

        [Fact]
        public void PuppetAndWraithHaveCombatAndRepeatGates()
        {
            WorldModel world = Assassin();
            world.ApplyMagic(Known(3, MagicType.SummonPuppet));
            world.ApplyMagic(Known(4, MagicType.WraithGrip));
            SpellBook spells = new SpellBook(new BotConfig { CastSpells = true, CastRange = 10 });
            WorldObject target = Target();

            Assert.Null(spells.ChoosePuppet(world, target, 3));
            Assert.NotNull(spells.ChoosePuppet(world, target, 2));
            spells.PuppetIssued();
            Assert.Null(spells.ChoosePuppet(world, target, 2));

            Assert.NotNull(spells.ChooseWraithGrip(world, target, 2));
            spells.WraithIssued(target.ObjectID);
            Assert.Null(spells.ChooseWraithGrip(world, target, 2));
            target.Poison = PoisonType.WraithGrip;
            Assert.Null(spells.ChooseWraithGrip(world, target, 2));
        }

        [Fact]
        public void AugmentSkillsAreReportedAsPassive()
        {
            Assert.False(SpellBook.IsSupported(MagicType.PledgeOfBlood, out string why));
            Assert.Contains("Summon Puppet", why);
            Assert.True(SpellBook.IsPassive(MagicType.PledgeOfBlood));
            Assert.True(SpellBook.IsPassive(MagicType.WillowDance));
            Assert.True(SpellBook.IsSupported(MagicType.SummonPuppet, out _));
            Assert.True(SpellBook.IsSupported(MagicType.WraithGrip, out _));
        }

        [Fact]
        public void SummonedPuppetWithoutOwnerFieldIsNotAnEnemy()
        {
            WorldModel world = Assassin();
            world.AddMonster(50, "SummonPuppet", 0, new Point(11, 10),
                MirDirection.Up, false);
            world.AddMonster(51, "Devouring Ghost", 0, new Point(12, 10),
                MirDirection.Up, false);

            Assert.Equal((uint)51, world.NearestLiveMonster(3).ObjectID);
            Assert.Equal(1, world.LiveMonstersWithin(3));
        }

        private static WorldModel Assassin() => new WorldModel
        {
            Class = MirClass.Assassin,
            Level = 33,
            Mana = 100,
            MaxMana = 100,
            Health = 500,
            MaxHealth = 500,
            Location = new Point(10, 10)
        };

        private static WorldObject Target() => new WorldObject
        {
            ObjectID = 42,
            Kind = ObjectKind.Monster,
            Level = 32,
            Location = new Point(11, 10)
        };

        private static ClientUserMagic Known(int index, MagicType type)
        {
            MagicInfo info = new MagicInfo();
            Set(info, "_Magic", type);
            Set(info, "_NeedLevel1", 1);
            Set(info, "_BaseCost", 10);
            return new ClientUserMagic { InfoIndex = index, Info = info };
        }

        private static void Set<T>(object target, string field, T value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(target, value);
    }
}
