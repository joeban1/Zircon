using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace MirBot
{
    /// <summary>
    /// Import only date-stamped, resolved book attempts. Historic Equip lines are requests, not
    /// server verdicts, so they cannot establish a confirmed upgrade and are deliberately skipped.
    /// </summary>
    public static class ProgressBackfill
    {
        private static readonly Regex Line = new Regex(
            @"^\[(?<stamp>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3})\] \[(?<bot>[^\]]+)\] (?<body>.*)$",
            RegexOptions.Compiled);
        private static readonly Regex Attempt = new Regex(
            @"^LearnBook \(learning from slot \d+\) \| (?<character>\S+) L\d+ (?<class>Warrior|Wizard|Taoist|Assassin|Archer) @",
            RegexOptions.Compiled);
        private static readonly Regex Learned = new Regex(@"^Learned (?<skill>.+) - now \d+ skills\.$",
            RegexOptions.Compiled);
        private static readonly Regex Failed = new Regex(@"^LEARN REFUSED: (?<skill>.+) taught us nothing - still \d+ skills\.",
            RegexOptions.Compiled);

        public static int Run(string logPath, ProgressHistory history)
        {
            if (history == null || string.IsNullOrWhiteSpace(logPath)) return 0;
            var pending = new Dictionary<string, (string character, string mirClass, DateTime utc)>();
            int imported = 0;
            // These are the rotations the live loot lookup reads. Older, undated lines cannot be
            // placed safely on a timeline and are excluded even if they describe a result.
            foreach (string file in new[] { logPath + ".3", logPath + ".2", logPath })
            {
                if (!File.Exists(file)) continue;
                pending.Clear();
                try
                {
                    using FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite);
                    using StreamReader reader = new StreamReader(stream);
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        Match match = Line.Match(line);
                        if (!match.Success || !DateTime.TryParseExact(match.Groups["stamp"].Value,
                            "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeLocal, out DateTime local)) continue;

                        DateTime utc = local.ToUniversalTime();
                        string bot = match.Groups["bot"].Value;
                        string body = match.Groups["body"].Value;
                        Match attempt = Attempt.Match(body);
                        if (attempt.Success)
                        {
                            pending[bot] = (attempt.Groups["character"].Value,
                                attempt.Groups["class"].Value, utc);
                            continue;
                        }

                        Match outcome = Learned.Match(body);
                        bool success = outcome.Success;
                        if (!success) outcome = Failed.Match(body);
                        if (!outcome.Success || !pending.TryGetValue(bot, out var earlier)) continue;
                        pending.Remove(bot);
                        if (utc < earlier.utc || utc - earlier.utc > TimeSpan.FromSeconds(30)) continue;

                        if (history.Record(new ProgressEntry
                        {
                            Kind = "skill", Bot = bot, Character = earlier.character,
                            Class = earlier.mirClass, Utc = earlier.utc,
                            Skill = outcome.Groups["skill"].Value, Success = success,
                            Source = "dated-log"
                        })) imported++;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            history.Flush();
            return imported;
        }
    }
}
