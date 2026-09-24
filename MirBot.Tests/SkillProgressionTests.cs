using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using Library;
using Library.SystemModels;
using Xunit;

namespace MirBot.Tests
{
    /// <summary>
    /// The boss-book skills (charged warrior skills), the swing priority between armed skills,
    /// and the level 4 training verdict on a book for a skill already known.
    /// </summary>
    public sealed class SkillProgressionTests
    {
        [Fact]
        public void AKnownChargeSkillIsChargedAndThenNamedAheadOfHalfMoon()
        {
            WorldModel world = Warrior();
            world.ApplyMagic(Known(1, MagicType.HalfMoon, 24));
            world.ApplyMagic(Known(2, MagicType.DragonRise, 35));
            SkillSet skills = new SkillSet();

            Assert.Equal(MagicType.DragonRise, skills.PendingCharge(world));

            skills.ChargeSent();
            skills.Toggled(MagicType.HalfMoon, true);
            skills.Toggled(MagicType.DragonRise, true);

            Assert.Equal(MagicType.None, skills.PendingCharge(world));
            Assert.Equal(MagicType.DragonRise, skills.ChooseAttackMagic(world, Target(), 1));
        }

        [Fact]
        public void ShoulderDashIsNeverCharged()
        {
            WorldModel world = Warrior();
            world.ApplyMagic(Known(1, MagicType.ShoulderDash, 27));

            Assert.Equal(MagicType.None, new SkillSet().PendingCharge(world));
        }

        [Fact]
        public void BladeStormIsPreferredOverFlamingSwordAndAChargeIsNotBoughtWithoutMana()
        {
            WorldModel world = Warrior();
            world.ApplyMagic(Known(1, MagicType.FlamingSword, 32));
            world.ApplyMagic(Known(2, MagicType.BladeStorm, 38));

            Assert.Equal(MagicType.BladeStorm, new SkillSet().PendingCharge(world));

            world.Mana = 5;
            Assert.Equal(MagicType.None, new SkillSet().PendingCharge(world));
        }

        [Fact]
        public void DestructiveSurgeIsNamedOverHalfMoonWhenBothAreOn()
        {
            WorldModel world = Warrior();
            world.ApplyMagic(Known(1, MagicType.HalfMoon, 24));
            world.ApplyMagic(Known(2, MagicType.DestructiveSurge, 40));
            SkillSet skills = new SkillSet();
            skills.Toggled(MagicType.HalfMoon, true);
            skills.Toggled(MagicType.DestructiveSurge, true);

            Assert.Equal(MagicType.DestructiveSurge, skills.ChooseAttackMagic(world, Target(), 1));
        }

        [Theory]
        [InlineData(3, false, BookVerdict.Wanted)]        // dropped copy, level 3: train it
        [InlineData(3, true, BookVerdict.AlreadyKnown)]   // bought copy: the server refuses it
        [InlineData(2, false, BookVerdict.AlreadyKnown)]  // refused below level 3
        [InlineData(4, false, BookVerdict.AlreadyKnown)]  // already at the maximum
        public void ABookForAKnownSkillIsWantedOnlyAsALevelFourTrainingRead(int skillLevel,
            bool bought, BookVerdict expected)
        {
            MagicInfo slaying = Magic(3, MagicType.Slaying, 14);
            MagicBooks books = new MagicBooks();
            ((Dictionary<int, MagicInfo>)typeof(MagicBooks)
                .GetField("_byShape", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(books))[3] = slaying;

            WorldModel world = Warrior();
            world.ApplyMagic(new ClientUserMagic { InfoIndex = 3, Info = slaying, Level = skillLevel });

            ItemInfo info = new ItemInfo();
            Set(info, "_ItemType", ItemType.Book);
            Set(info, "_Shape", 3);
            Set(info, "_RequiredClass", RequiredClass.Warrior);
            ClientUserItem book = new ClientUserItem
            {
                Info = info, Count = 1,
                Flags = bought ? UserItemFlags.NonRefinable : UserItemFlags.None
            };

            Assert.Equal(expected, books.Judge(book, MirClass.Warrior, world.Level,
                world.PlayerStats, world));
        }

        private static WorldModel Warrior() => new WorldModel
        {
            Class = MirClass.Warrior,
            Level = 42,
            Mana = 150,
            MaxMana = 150,
            Health = 900,
            MaxHealth = 900,
            Location = new Point(10, 10)
        };

        private static WorldObject Target() => new WorldObject
        {
            ObjectID = 42,
            Kind = ObjectKind.Monster,
            Level = 30,
            Location = new Point(11, 10)
        };

        private static MagicInfo Magic(int index, MagicType type, int needLevel)
        {
            MagicInfo info = new MagicInfo();
            typeof(MagicInfo).GetProperty("Index").SetValue(info, index);
            Set(info, "_Magic", type);
            Set(info, "_NeedLevel1", needLevel);
            Set(info, "_BaseCost", 10);
            Set(info, "_School", MagicSchool.Active);
            Set(info, "_RequiredClass", RequiredClass.Warrior);
            return info;
        }

        private static ClientUserMagic Known(int index, MagicType type, int needLevel) =>
            new ClientUserMagic { InfoIndex = index, Info = Magic(index, type, needLevel), Level = 3 };

        private static void Set<T>(object target, string field, T value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(target, value);
    }
}
