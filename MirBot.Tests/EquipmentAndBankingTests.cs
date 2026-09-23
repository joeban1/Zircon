using System.Reflection;
using Library;
using Library.SystemModels;
using Xunit;

namespace MirBot.Tests
{
    public sealed class EquipmentAndBankingTests
    {
        [Fact]
        public void BurntOutTorchCountRemovesPhantomAndMakesSpareEquippable()
        {
            ItemInfo candle = Info("Candle", ItemType.Torch);
            Backpack bag = new Backpack();
            bag.Reset(new[]
            {
                new ClientUserItem { Info = candle, Slot = Globals.EquipmentOffSet +
                    (int)EquipmentSlot.Torch, Count = 1, CurrentDurability = 0 },
                new ClientUserItem { Info = candle, Slot = 2, Count = 1,
                    CurrentDurability = 8000 }
            });

            Assert.Empty(bag.PendingEquips(MirClass.Warrior, MirGender.Male));
            bag.NoteSlotCount(GridType.Equipment, (int)EquipmentSlot.Torch, 0);
            Assert.DoesNotContain(bag.Worn, x => x.Key == (int)EquipmentSlot.Torch);
            Assert.Equal(EquipmentSlot.Torch,
                Assert.Single(bag.PendingEquips(MirClass.Warrior, MirGender.Male)).Slot);
        }

        [Fact]
        public void EquipmentReagentCountUsesAcceptedRemainingAmount()
        {
            ItemInfo poison = Info("Poison Dust", ItemType.Poison);
            Backpack bag = new Backpack();
            bag.Reset(new[] { new ClientUserItem { Info = poison,
                Slot = Globals.EquipmentOffSet + (int)EquipmentSlot.Poison, Count = 100 } });

            bag.NoteSlotCount(GridType.Equipment, (int)EquipmentSlot.Poison, 99);
            Assert.Equal(99, bag.EquippedReagentCount(ItemType.Poison));
            bag.NoteSlotCount(GridType.Equipment, (int)EquipmentSlot.Poison, 0);
            Assert.Equal(0, bag.EquippedReagentCount(ItemType.Poison));
        }

        [Theory]
        [InlineData(MirClass.Warrior, RequiredType.MC, Stat.MaxDC)]
        [InlineData(MirClass.Wizard, RequiredType.SC, Stat.MaxMC)]
        [InlineData(MirClass.Taoist, RequiredType.MC, Stat.MaxSC)]
        [InlineData(MirClass.Assassin, RequiredType.MC, Stat.MaxDC)]
        public void WrongCombatStatRequirementIsNeverBankedAndLegacyCopyIsReclaimed(
            MirClass mirClass, RequiredType wrongRequirement, Stat ownStat)
        {
            ItemInfo worn = Info("class ring", ItemType.Ring);
            worn.Stats[ownStat] = 3;
            ItemInfo wrong = Info("wrong-stat ring", ItemType.Ring);
            Set(wrong, "_RequiredType", wrongRequirement);
            Set(wrong, "_RequiredAmount", 9);
            wrong.Stats[Stat.MaxAC] = 2; // positive score must not override the wrong gate
            wrong.Stats[wrongRequirement == RequiredType.MC ? Stat.MaxMC : Stat.MaxSC] = 1;
            var item = new ClientUserItem { Info = wrong, Slot = 1, Count = 1 };

            Backpack bag = new Backpack();
            bag.Reset(new[]
            {
                new ClientUserItem { Info = worn, Slot = Globals.EquipmentOffSet +
                    (int)EquipmentSlot.RingL, Count = 1 },
                new ClientUserItem { Info = worn, Slot = Globals.EquipmentOffSet +
                    (int)EquipmentSlot.RingR, Count = 1 },
                item
            });

            Assert.False(bag.ShouldBank(item, mirClass, MirGender.Male, 25,
                new Stats(), null, null));
            bag.ResetStorage(new[] { new ClientUserItem { Info = wrong, Slot = 4, Count = 1 } });
            Assert.Equal("not a future upgrade - selling",
                Assert.Single(bag.StorageReclaims(mirClass, MirGender.Male, 25,
                    new Stats(), null, null)).Why);
        }

        [Fact]
        public void FutureClassUpgradeBanksButObsoleteRingDoesNot()
        {
            ItemInfo worn = Info("discipline", ItemType.Ring);
            worn.Stats[Stat.MaxSC] = 3;
            ItemInfo future = Info("better discipline", ItemType.Ring);
            Set(future, "_RequiredType", RequiredType.Level);
            Set(future, "_RequiredAmount", 30);
            future.Stats[Stat.MaxSC] = 5;
            ItemInfo obsolete = Info("lesser discipline", ItemType.Ring);
            Set(obsolete, "_RequiredType", RequiredType.Level);
            Set(obsolete, "_RequiredAmount", 30);
            obsolete.Stats[Stat.MaxSC] = 2;
            Backpack bag = new Backpack();
            bag.Reset(new[]
            {
                new ClientUserItem { Info = worn, Slot = Globals.EquipmentOffSet +
                    (int)EquipmentSlot.RingL, Count = 1 },
                new ClientUserItem { Info = worn, Slot = Globals.EquipmentOffSet +
                    (int)EquipmentSlot.RingR, Count = 1 }
            });

            Assert.True(bag.ShouldBank(new ClientUserItem { Info = future, Count = 1 },
                MirClass.Taoist, MirGender.Male, 25, new Stats(), null, null));
            Assert.False(bag.ShouldBank(new ClientUserItem { Info = obsolete, Count = 1 },
                MirClass.Taoist, MirGender.Male, 25, new Stats(), null, null));

            bag.ResetStorage(new[] { new ClientUserItem { Info = obsolete, Slot = 5, Count = 1 } });
            Assert.Equal("not a future upgrade - selling",
                Assert.Single(bag.StorageReclaims(MirClass.Taoist, MirGender.Male, 25,
                    new Stats(), null, null)).Why);
        }

        [Fact]
        public void PartsRemainInStorageWhileLegacyGearIsCleanedUp()
        {
            ItemInfo part = Info("part", ItemType.ItemPart);
            Set(part, "_ItemEffect", ItemEffect.ItemPart);
            Backpack bag = new Backpack();
            bag.ResetStorage(new[] { new ClientUserItem { Info = part, Slot = 1, Count = 2 } });

            Assert.Empty(bag.StorageReclaims(MirClass.Taoist, MirGender.Male, 25,
                new Stats(), null, null));
        }

        private static ItemInfo Info(string name, ItemType type)
        {
            var info = new ItemInfo();
            Set(info, "_ItemName", name);
            Set(info, "_ItemType", type);
            Set(info, "_RequiredClass", RequiredClass.All);
            Set(info, "_RequiredGender", RequiredGender.None);
            return info;
        }

        private static void Set<T>(object target, string field, T value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(target, value);
    }
}
