using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace MirBot.Tests
{
    public sealed class ProfitPolicyTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void CapitalPurchaseDoesNotLookLikeOperatingLoss()
        {
            var samples = new List<GoldSample>();
            for (int i = 0; i <= 48; i++)
            {
                bool bought = i >= 24;
                samples.Add(new GoldSample(Now.AddMinutes(-240 + i * 5),
                    bought ? 70000 : 100000, bought ? 30000 : 0));
            }
            GoldTrendResult result = GoldTrend.Measure(samples, Now, 4);
            Assert.True(result.Valid);
            Assert.Equal(30000, result.CapitalAdjustment);
            Assert.Equal(0, result.Change);
        }

        [Fact]
        public void GappyHistoryCannotTriggerLossWatch()
        {
            var samples = new List<GoldSample>
            {
                new GoldSample(Now.AddHours(-4), 100000, 0),
                new GoldSample(Now.AddHours(-2), 40000, 0),
                new GoldSample(Now, 10000, 0)
            };
            Assert.False(GoldTrend.Measure(samples, Now, 4).Valid);
        }

        [Fact]
        public void CutoffUsesPrecedingSampleForTimeWeightedEndpoint()
        {
            var samples = new List<GoldSample>();
            for (int minutes = -245; minutes <= 0; minutes += 5)
            {
                if (minutes == -240) continue;
                samples.Add(new GoldSample(Now.AddMinutes(minutes),
                    minutes < -225 ? 100000 : 80000, 0));
            }
            GoldTrendResult result = GoldTrend.Measure(samples, Now, 4);
            Assert.True(result.Valid);
            Assert.InRange(result.CoverageHours, 3.99, 4.01);
            Assert.True(result.Change < -10000);
        }

        [Fact]
        public void CorruptLatchStartsUnlatchedWithDiagnostic()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            try
            {
                File.WriteAllText(path, "{not json");
                var policy = new ProfitPolicy(path);
                Assert.False(policy.Active);
                Assert.Contains("unreadable", policy.LoadDiagnostic);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void LatchHoldsUntilTripAndSurvivesRestartAndCaps()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            try
            {
                var policy = new ProfitPolicy(path);
                var loss = new GoldTrendResult(true, -25000, 4, 0);
                Assert.NotEmpty(policy.Update(loss, Now, false, 4, 20000, 20000, 12));
                Assert.True(policy.Active);
                policy = new ProfitPolicy(path);
                Assert.True(policy.Active);
                var gain = new GoldTrendResult(true, 25000, 4, 0);
                Assert.Equal("", policy.Update(gain, Now.AddHours(1), false, 4, 20000, 20000, 12));
                Assert.True(policy.Active);
                Assert.NotEmpty(policy.Update(gain, Now.AddHours(2), true, 4, 20000, 20000, 12));
                Assert.False(policy.Active);
                policy.Update(loss, Now.AddHours(3), false, 4, 20000, 20000, 12);
                Assert.True(policy.Active);
                Assert.NotEmpty(policy.Update(loss, Now.AddHours(16), false, 4, 20000, 20000, 12));
                Assert.False(policy.Active);
                Assert.Equal("", policy.Update(loss, Now.AddHours(17), false, 4, 20000, 20000, 12));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
    }
}
