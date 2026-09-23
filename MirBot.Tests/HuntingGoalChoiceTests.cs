using System;
using Xunit;

namespace MirBot.Tests
{
    public sealed class HuntingGoalChoiceTests
    {
        [Fact]
        public void BookGoalHasPriorityButOrdinaryGroundsRemainPossible()
        {
            var random = new Random(42);
            int book = 0;
            for (int i = 0; i < 10000; i++)
                if (HuntingGoalChoice.PursueBook(true, true, false, 0, 70, 20, 2,
                    random, out int chance))
                {
                    Assert.Equal(70, chance);
                    book++;
                }
            Assert.InRange(book, 6700, 7300);
        }

        [Fact]
        public void LossWatchReducesBookGoalAndStreakForcesOrdinary()
        {
            var random = new Random(42);
            int book = 0;
            for (int i = 0; i < 10000; i++)
                if (HuntingGoalChoice.PursueBook(true, true, true, 0, 70, 20, 2,
                    random, out int chance))
                {
                    Assert.Equal(20, chance);
                    book++;
                }
            Assert.InRange(book, 1700, 2300);
            Assert.False(HuntingGoalChoice.PursueBook(true, true, false, 2, 70, 20, 2,
                random, out int forcedChance));
            Assert.Equal(0, forcedChance);
        }

        [Fact]
        public void SingleAvailableGoalRemainsReachable()
        {
            var random = new Random(1);
            Assert.True(HuntingGoalChoice.PursueBook(true, false, true, 99, 70, 20, 2,
                random, out int bookChance));
            Assert.Equal(100, bookChance);
            Assert.False(HuntingGoalChoice.PursueBook(false, true, false, 0, 70, 20, 2,
                random, out int ordinaryChance));
            Assert.Equal(0, ordinaryChance);
        }
    }
}
