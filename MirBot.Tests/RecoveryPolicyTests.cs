using Xunit;

namespace MirBot.Tests
{
    public sealed class RecoveryPolicyTests
    {
        [Fact]
        public void RecoveryStaysLatchedUntilAHealthyPostRestockBalance()
        {
            RecoveryPolicy policy = new RecoveryPolicy();

            Assert.True(policy.Update(4_999, 5_000, 25_000, false, false));
            Assert.True(policy.Update(5_001, 5_000, 25_000, false, false));
            Assert.True(policy.Update(30_000, 5_000, 25_000, false, false));
            Assert.True(policy.Update(30_000, 5_000, 25_000, true, true));
            Assert.False(policy.Update(30_000, 5_000, 25_000, true, false));
        }

        [Fact]
        public void DisabledRecoveryCannotRemainLatched()
        {
            RecoveryPolicy policy = new RecoveryPolicy();

            Assert.True(policy.Update(100, 5_000, 25_000, false, false));
            Assert.False(policy.Update(100, 0, 25_000, false, false));
        }

        [Fact]
        public void MoneyCaveStageDoesNotChurnAsPotionsAreUsed()
        {
            RecoveryPolicy policy = new RecoveryPolicy();
            policy.Update(100, 5_000, 25_000, false, false);

            Assert.True(policy.UpdateMoneyCave(true, false));
            Assert.True(policy.UpdateMoneyCave(false, false));
            Assert.False(policy.UpdateMoneyCave(false, true));
        }

        [Theory]
        [InlineData(19, 60, 60, true, 60, 60, 3, false)]
        [InlineData(20, 35, 60, true, 60, 60, 3, false)]
        [InlineData(20, 60, 60, true, 35, 60, 3, false)]
        [InlineData(20, 60, 60, true, 60, 60, 2, false)]
        [InlineData(20, 60, 60, true, 60, 60, 3, true)]
        [InlineData(20, 60, 60, false, 0, 60, 3, true)]
        public void MoneyCaveRequiresLevelSuppliesAndScrollReserve(int level,
            int healthLoad, int healthTarget, bool spendsMana, int manaLoad, int manaTarget,
            int scrolls, bool expected)
        {
            bool actual = RecoveryPolicy.SuppliesReady(level, 20, 60,
                healthLoad, healthTarget, spendsMana, manaLoad, manaTarget, scrolls, 3);

            Assert.Equal(expected, actual);
        }
    }
}
