using System;
using System.Collections.Generic;
using System.Linq;

namespace MirBot
{
    public sealed class QuestLogEntry
    {
        public string Bot { get; set; } = "";
        public string Character { get; set; } = "";
        public string Class { get; set; } = "";
        public int Level { get; set; }
        public int QuestIndex { get; set; }
        public string Quest { get; set; } = "";

        /// <summary>"accepted", "completed", or "journey" (set off to kill a quest boss).</summary>
        public string Event { get; set; } = "";
        public int MapIndex { get; set; }
        public string Map { get; set; } = "";
        public DateTime Utc { get; set; }
        public string Rewards { get; set; } = "";
    }

    /// <summary>
    /// Every quest a bot accepted or completed, from the server's own S.QuestChanged transitions
    /// (never from what the bot asked for). Also holds the quest-boss journey stamps, so the
    /// "two attempts in six hours" limit survives a host restart.
    /// </summary>
    public sealed class QuestLogMemory : MemoryBank<QuestLogEntry>
    {
        private const int Capacity = 5000;

        public QuestLogMemory(string path) : base(path) { Load(); }

        public void Record(QuestLogEntry entry)
        {
            lock (Sync)
            {
                Entries.Add(entry);
                if (Entries.Count > Capacity) Entries.RemoveRange(0, Entries.Count - Capacity);
                MarkDirty();
            }
        }

        /// <summary>How many quest-boss journeys this bot started for a quest since a time.</summary>
        public int JourneysSince(string bot, int questIndex, DateTime sinceUtc)
        {
            lock (Sync)
                return Entries.Count(e => e.Bot == bot && e.QuestIndex == questIndex &&
                                          e.Event == "journey" && e.Utc >= sinceUtc);
        }

        public List<QuestLogEntry> Snapshot(int take = 1000)
        {
            List<QuestLogEntry> rows = new List<QuestLogEntry>();
            lock (Sync)
            {
                for (int i = Entries.Count - 1; i >= 0 && rows.Count < take; i--)
                {
                    QuestLogEntry e = Entries[i];
                    rows.Add(new QuestLogEntry
                    {
                        Bot = e.Bot, Character = e.Character, Class = e.Class, Level = e.Level,
                        QuestIndex = e.QuestIndex, Quest = e.Quest, Event = e.Event,
                        MapIndex = e.MapIndex, Map = e.Map, Utc = e.Utc, Rewards = e.Rewards
                    });
                }
            }
            return rows;
        }
    }
}
