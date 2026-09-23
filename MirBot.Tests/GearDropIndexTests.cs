using Xunit;

namespace MirBot.Tests
{
    public sealed class GearDropIndexTests
    {
        [Fact]
        public void MeaningfulUpgradeRequiresEnoughGainAndDropOpportunity()
        {
            Assert.True(GearDropIndex.IsWorthHunting(70, 20, 50, 0.02));
            Assert.False(GearDropIndex.IsWorthHunting(70, 7, 50, 0.02));
            Assert.False(GearDropIndex.IsWorthHunting(70, 10, 50, 0.02));
            Assert.False(GearDropIndex.IsWorthHunting(70, 20, 50, 0.001));
            Assert.False(GearDropIndex.IsWorthHunting(15, 15, 0, 0.02));
        }

        [Fact]
        public void EmptySlotStillRequiresUsefulEquipment()
        {
            Assert.True(GearDropIndex.IsWorthHunting(25, 25, 0, 0.02));
            Assert.False(GearDropIndex.IsWorthHunting(19, 19, 0, 0.02));
        }
    }
}
