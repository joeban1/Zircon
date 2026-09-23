using System;
using Xunit;

namespace MirBot.Tests
{
    public sealed class WeightedChoiceTests
    {
        [Fact]
        public void EmptySingletonAndInvalidWeightsAreSafe()
        {
            var random = new Random(1);
            Assert.Equal(-1, WeightedChoice.Pick(Array.Empty<double>(), 2, random, out _));
            Assert.Equal(0, WeightedChoice.Pick(new[] { double.NaN }, 2, random, out double only));
            Assert.Equal(1, only);
            Assert.Equal(1, WeightedChoice.Pick(new[] { -1.0, 5.0, double.PositiveInfinity },
                2, random, out double chance));
            Assert.Equal(1, chance);
        }

        [Fact]
        public void PowerZeroIsUniformAndPowerTwoFavorsHigherScores()
        {
            var scores = new[] { 1768073.0, 1277333.0, 1211045.0 };
            int[] counts = new int[3];
            var random = new Random(42);
            for (int i = 0; i < 100000; i++)
                counts[WeightedChoice.Pick(scores, 2, random, out _)]++;
            Assert.InRange(counts[0], 48500, 52000);
            Assert.InRange(counts[1], 24500, 28000);
            Assert.InRange(counts[2], 22000, 25500);

            counts = new int[3];
            for (int i = 0; i < 30000; i++)
                counts[WeightedChoice.Pick(scores, 0, random, out _)]++;
            foreach (int count in counts) Assert.InRange(count, 9500, 10500);
        }
    }
}
