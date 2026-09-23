using System.Drawing;
using System.Reflection;
using Library;
using Library.SystemModels;
using Xunit;

namespace MirBot.Tests
{
    public sealed class TaoistSupportTests
    {
        [Fact]
        public void LockedPoisonIsUnlockedThenEquipped()
        {
            ItemInfo poison = PoisonInfo();

            Backpack bag = new Backpack();
            bag.Reset(new[]
            {
                new ClientUserItem
                {
                    Info = poison,
                    Slot = 1,
                    Count = 300,
                    Flags = UserItemFlags.Locked
                }
            });

            EquipRequest unlock = bag.PendingEquipUnlock(MirClass.Taoist, MirGender.Female);

            Assert.NotNull(unlock);
            Assert.Equal(1, unlock.FromSlot);
            Assert.Equal(EquipmentSlot.Poison, unlock.Slot);
            Assert.Empty(bag.PendingEquips(MirClass.Taoist, MirGender.Female));

            bag.NoteUnlocked(1);

            Assert.Null(bag.PendingEquipUnlock(MirClass.Taoist, MirGender.Female));
            Assert.Equal(EquipmentSlot.Poison,
                Assert.Single(bag.PendingEquips(MirClass.Taoist, MirGender.Female)).Slot);
        }

        [Fact]
        public void OnlyEquippedPoisonCountsAsUsable()
        {
            ItemInfo poison = PoisonInfo();

            Backpack bag = new Backpack();
            bag.Reset(new[]
            {
                new ClientUserItem { Info = poison, Slot = 1, Count = 300 }
            });

            Assert.Equal(300, bag.CountReagent(ItemType.Poison));
            Assert.Equal(0, bag.EquippedReagentCount(ItemType.Poison));

            bag.Reset(new[]
            {
                new ClientUserItem
                {
                    Info = poison,
                    Slot = Globals.EquipmentOffSet + (int)EquipmentSlot.Poison,
                    Count = 300
                }
            });

            Assert.Equal(300, bag.EquippedReagentCount(ItemType.Poison));
        }

        [Fact]
        public void HealTargetsLowOwnedPetAndDoesNotImmediatelyRepeat()
        {
            BotConfig config = new BotConfig
            {
                CastSpells = true,
                HealPetAtPercent = 60,
                CastRange = 9,
                SpellManaFloorPercent = 0
            };

            SpellBook spells = new SpellBook(config);
            WorldModel world = new WorldModel
            {
                SelfID = 1,
                Name = "Jill",
                Location = new Point(10, 10),
                Level = 24,
                Mana = 100,
                MaxMana = 100
            };

            world.ApplyMagic(new ClientUserMagic
            {
                Info = MagicInfo(MagicType.Heal, "Heal", 7, 4)
            });

            world.AddMonster(2, "Skeleton", 0, new Point(11, 10), MirDirection.Up,
                false, "Jill");
            world.ApplyHealthMana(2, 40, 0, false);
            world.ApplyMaxHealthMana(2, 100, 0);

            ClientUserMagic heal = spells.ChoosePetHeal(world, out WorldObject target);

            Assert.NotNull(heal);
            Assert.Equal((uint)2, target.ObjectID);

            spells.PetHealIssued(target.ObjectID);

            Assert.Null(spells.ChoosePetHeal(world, out _));
        }

        private static ItemInfo PoisonInfo()
        {
            ItemInfo info = new ItemInfo();
            Set(info, "_ItemName", "Green Poison");
            Set(info, "_ItemType", ItemType.Poison);
            Set(info, "_RequiredClass", RequiredClass.All);
            Set(info, "_RequiredGender", RequiredGender.None);
            return info;
        }

        private static MagicInfo MagicInfo(MagicType type, string name, int needLevel, int cost)
        {
            MagicInfo info = new MagicInfo();
            Set(info, "_Name", name);
            Set(info, "_Magic", type);
            Set(info, "_NeedLevel1", needLevel);
            Set(info, "_BaseCost", cost);
            return info;
        }

        // DBObject setters require a live MirDB Session. These are inert definitions used only by
        // the unit, so populate their backing fields without manufacturing a database.
        private static void Set<T>(object target, string field, T value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(target, value);
    }
}
