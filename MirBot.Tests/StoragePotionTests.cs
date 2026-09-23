using System.Linq;
using System.Reflection;
using Library;
using Library.SystemModels;
using Xunit;

namespace MirBot.Tests
{
    public sealed class StoragePotionTests
    {
        [Fact]
        public void PotionsAreNeverFutureEquipmentButPartsStillBank()
        {
            ClientUserItem potion = HealingPotion(1, 29);
            Set(potion.Info, "_RequiredAmount", 99);
            ClientUserItem part = new ClientUserItem
            {
                Info = Info("[Part]", ItemType.ItemPart), Slot = 2, Count = 1
            };
            Set(part.Info, "_ItemEffect", ItemEffect.ItemPart);

            Assert.False(Backpack.WorthStoring(potion, MirClass.Assassin,
                MirGender.Male, 32, new Stats(), null, null));
            Assert.True(Backpack.WorthStoring(part, MirClass.Assassin,
                MirGender.Male, 32, new Stats(), null, null));
        }

        [Fact]
        public void PreviouslyBankedPotionIsReclaimedWithoutChangingOtherDeposits()
        {
            Backpack bag = new Backpack();
            bag.ResetStorage(new[] { HealingPotion(1, 29) });

            Backpack.Reclaim reclaim = Assert.Single(bag.StorageReclaims(
                MirClass.Assassin, MirGender.Male, 32, new Stats(), null, null));

            Assert.Equal(1, reclaim.Slot);
            Assert.Equal(29, reclaim.Item.Count);
            Assert.Equal("potion belongs in the bag", reclaim.Why);
            Assert.Single(bag.Stored); // A decision only; server verdict moves it.
        }

        private static ClientUserItem HealingPotion(int slot, int count)
        {
            ItemInfo info = Info("Healing Potion", ItemType.Consumable);
            info.Stats[Stat.Health] = 30;
            return new ClientUserItem { Info = info, Slot = slot, Count = count };
        }

        private static ItemInfo Info(string name, ItemType type)
        {
            ItemInfo info = new ItemInfo();
            Set(info, "_ItemName", name);
            Set(info, "_ItemType", type);
            return info;
        }

        private static void Set<T>(object target, string field, T value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(target, value);
    }
}
