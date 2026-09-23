using System;
using System.IO;
using Xunit;

namespace MirBot.Tests
{
    public sealed class ProgressHistoryTests
    {
        [Fact]
        public void KeepsOnlyConfirmedPositiveUpgradesAndSurvivesReload()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            try
            {
                var bank = new ProgressHistory(path);
                var entry = new ProgressEntry
                {
                    Kind = "upgrade", Bot = "Mirbot", Character = "Mirbot", Class = "Warrior",
                    Utc = DateTime.UtcNow, PreviousGear = "Power Axe", NewGear = "Iron Sword",
                    ScoreIncrease = 8
                };
                Assert.True(bank.Record(entry));
                Assert.False(bank.Record(entry));
                Assert.False(bank.Record(new ProgressEntry
                {
                    Kind = "upgrade", Bot = "Mirbot", Character = "Mirbot", Class = "Warrior",
                    Utc = entry.Utc.AddSeconds(1), PreviousGear = "Power Axe",
                    NewGear = "Iron Sword", ScoreIncrease = 0
                }));
                bank.Flush();
                Assert.Equal(8, Assert.Single(new ProgressHistory(path).Upgrades(10)).ScoreIncrease);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void BackfillOnlyPairsDatedAttemptsAndOutcomes()
        {
            string dir = Path.Combine(Path.GetTempPath(), "mirbot-history-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            string log = Path.Combine(dir, "mirbot.log");
            try
            {
                File.WriteAllLines(log, new[]
                {
                    "[2026-09-23 09:31:31.133] [Mirbot8] LearnBook (learning from slot 20) | Dreadlord L32 Assassin @ 77,147 map 136 | 16 magics",
                    "[2026-09-23 09:31:34.204] [Mirbot8] LEARN REFUSED: Waning Moon taught us nothing - still 16 skills. The book is gone and the skill is not.",
                    "[2026-09-23 11:49:57.210] [Mirbot8] LearnBook (learning from slot 24) | Dreadlord L33 Assassin @ 223,86 map 136 | 16 magics",
                    "[2026-09-23 11:50:00.259] [Mirbot8] Learned Ghost Walk - now 17 skills.",
                    "[09:00:00.000] [Mirbot3] Learned Summon Skeleton - now 12 skills.",
                    "[2026-09-23 12:00:00.000] [Mirbot8] Equip (Sword -> Weapon (upgrade over Axe (4 -> 8))) | Dreadlord L33 Assassin @ 1,1 map 1"
                });
                var bank = new ProgressHistory(Path.Combine(dir, "progress.json"));
                Assert.Equal(2, ProgressBackfill.Run(log, bank));
                Assert.Equal(0, ProgressBackfill.Run(log, bank));
                Assert.Empty(bank.Upgrades(10));
                var rows = bank.Skills(10);
                Assert.Equal(2, rows.Count);
                Assert.True(rows[0].Success);
                Assert.Equal("Ghost Walk", rows[0].Skill);
                Assert.False(rows[1].Success);
                Assert.Equal("Assassin", rows[1].Class);
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
