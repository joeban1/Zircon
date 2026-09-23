using System;
using System.IO;
using Xunit;

namespace MirBot.Tests
{
    public sealed class InfoHistoryTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 22, 8, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void SignedPaceCountsLossAndExcludesReconnectGap()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ndjson");
            try
            {
                var log = new XpLog(path);
                log.Append(new XpPoint { Bot = "b", Character = "Jill", Session = "one",
                    Utc = Now.AddMinutes(-40), Total = 0 });
                log.Append(new XpPoint { Bot = "b", Character = "Jill", Session = "one",
                    Utc = Now.AddMinutes(-30), Total = 500 });
                log.Append(new XpPoint { Bot = "b", Character = "Jill", Session = "one",
                    Utc = Now.AddMinutes(-20), Total = 1000 });
                log.Append(new XpPoint { Bot = "b", Character = "Jill", Session = "two",
                    Utc = Now.AddMinutes(-10), Total = 0 });
                log.Append(new XpPoint { Bot = "b", Character = "Jill", Session = "two",
                    Utc = Now, Total = -100 });
                XpPace pace = log.Measure("b", "Jill", Now);
                Assert.Equal(1800, pace.CoverageSeconds);
                Assert.Equal(1800m, pace.PerHour);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void PaceWarmsUpUntilTwentyMinutes()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ndjson");
            try
            {
                var log = new XpLog(path);
                log.Append(new XpPoint { Bot = "b", Character = "Jill", Session = "one",
                    Utc = Now.AddMinutes(-15), Total = 0 });
                log.Append(new XpPoint { Bot = "b", Character = "Jill", Session = "one",
                    Utc = Now.AddMinutes(-5), Total = 200 });
                log.Append(new XpPoint { Bot = "b", Character = "Jill", Session = "one",
                    Utc = Now, Total = 300 });
                Assert.Null(log.Measure("b", "Jill", Now).PerHour);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void LevelMemoryDeduplicatesAndKeepsCharacterIntervalsSeparate()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            try
            {
                var bank = new LevelMemory(path);
                var first = new LevelEntry { Bot = "b", Character = "Jill", Class = "Taoist",
                    FromLevel = 25, ToLevel = 26, Utc = Now.AddHours(-2) };
                Assert.True(bank.Record(first));
                Assert.False(bank.Record(first));
                Assert.True(bank.Record(new LevelEntry { Bot = "b", Character = "Jill", Class = "Taoist",
                    FromLevel = 26, ToLevel = 27, Utc = Now }));
                Assert.True(bank.Record(new LevelEntry { Bot = "b", Character = "Other", Class = "Wizard",
                    FromLevel = 1, ToLevel = 2, Utc = Now.AddHours(-1) }));
                var rows = bank.Snapshot();
                Assert.Equal(3, rows.Count);
                Assert.Equal("02:00:00", rows[0].Interval);
                Assert.Equal("", rows[1].Interval);
                bank.Flush();
                Assert.Equal(3, new LevelMemory(path).Snapshot().Count);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void BackfillNeedsDateAnchorAndIsIdempotent()
        {
            string folder = Path.Combine(Path.GetTempPath(), "mirbot-level-" + Guid.NewGuid());
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllLines(Path.Combine(folder, "mirbot.log"), new[]
                {
                    "[23:59:00.000] [host] === MirBot log opened 2026-09-20 23:59:00 ===",
                    "[23:59:05.000] [b] Attack | Jill L25 Taoist @ 1,1 map 1 | HP 10/10",
                    "[00:01:00.000] [b] Attack | Jill L26 Taoist @ 1,1 map 1 | HP 10/10"
                });
                var bank = new LevelMemory(Path.Combine(folder, "levels.json"));
                Assert.Equal(1, LevelBackfill.Run(folder, bank).imported);
                Assert.Equal(0, LevelBackfill.Run(folder, bank).imported);
                var row = Assert.Single(bank.Snapshot());
                Assert.Equal(26, row.ToLevel);
                Assert.Equal("log-inferred", row.Source);
                Assert.Equal(new DateTime(2026, 9, 21, 0, 1, 0, DateTimeKind.Local)
                    .ToUniversalTime().ToString("o"), row.Utc);
            }
            finally
            {
                foreach (string path in Directory.GetFiles(folder)) File.Delete(path);
                Directory.Delete(folder);
            }
        }

        [Fact]
        public void NewLogLinesIncludeDateAndBackfillAcceptsMixedFormats()
        {
            string folder = Path.Combine(Path.GetTempPath(), "mirbot-dated-log-" + Guid.NewGuid());
            Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, "mirbot.log");
                using (var log = new BotLog(path)) log.Write("dated test");
                Assert.Contains(File.ReadAllLines(path), line =>
                    System.Text.RegularExpressions.Regex.IsMatch(line,
                        @"^\[\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}\] \[host\] dated test$"));

                File.WriteAllLines(path, new[]
                {
                    "[23:59:00.000] [host] === MirBot log opened 2026-09-20 23:59:00 ===",
                    "[23:59:05.000] [b] Attack | Jill L25 Taoist @ 1,1 map 1 | HP 10/10",
                    "[2026-09-21 00:01:00.000] [b] Attack | Jill L26 Taoist @ 1,1 map 1 | HP 10/10"
                });
                var bank = new LevelMemory(Path.Combine(folder, "levels.json"));
                Assert.Equal(1, LevelBackfill.Run(folder, bank).imported);
                Assert.Equal(0, LevelBackfill.Run(folder, bank).imported);
                Assert.Equal("log-inferred", Assert.Single(bank.Snapshot()).Source);
            }
            finally
            {
                foreach (string path in Directory.GetFiles(folder)) File.Delete(path);
                Directory.Delete(folder);
            }
        }

        [Fact]
        public void DatedBackfillWorksWithoutLegacyOpenAnchor()
        {
            string folder = Path.Combine(Path.GetTempPath(), "mirbot-dated-backfill-" + Guid.NewGuid());
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllLines(Path.Combine(folder, "mirbot.log"), new[]
                {
                    "[2026-09-20 23:59:05.000] [b] Attack | Jill L25 Taoist @ 1,1 map 1 | HP 10/10",
                    "[2026-09-21 00:01:00.000] [b] Attack | Jill L26 Taoist @ 1,1 map 1 | HP 10/10"
                });
                var bank = new LevelMemory(Path.Combine(folder, "levels.json"));
                Assert.Equal(1, LevelBackfill.Run(folder, bank).imported);
            }
            finally
            {
                foreach (string path in Directory.GetFiles(folder)) File.Delete(path);
                Directory.Delete(folder);
            }
        }
    }
}
