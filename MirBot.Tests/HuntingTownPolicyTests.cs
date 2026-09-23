using Xunit;

namespace MirBot.Tests
{
    public sealed class HuntingTownPolicyTests
    {
        [Fact]
        public void HealthyHighLevelBotDefersStarterTownButNotCave()
        {
            Assert.True(HuntingTownPolicy.Defer(true, 10, 30, 10, false, false, false));
            Assert.False(HuntingTownPolicy.Defer(false, 20, 30, 10, false, false, false));
            Assert.False(HuntingTownPolicy.Defer(true, 10, 19, 10, false, false, false));
        }

        [Fact]
        public void RecoveryAndWantedDropOnlyBookExemptTown()
        {
            Assert.False(HuntingTownPolicy.Defer(true, 10, 30, 10, true, false, false));
            Assert.False(HuntingTownPolicy.Defer(true, 10, 30, 10, false, true, false));
            Assert.False(HuntingTownPolicy.Defer(true, 10, 30, 10, false, false, true));
            Assert.True(HuntingTownPolicy.Defer(true, 10, 30, 10, false, false, false));
            Assert.False(HuntingTownPolicy.Defer(true, 10, 30, 0, false, false, false));
        }
    }
}
