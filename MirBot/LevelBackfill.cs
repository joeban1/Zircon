using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MirBot
{
    /// <summary>Explicit, idempotent migration from date-anchored action logs only.</summary>
    public static class LevelBackfill
    {
        private static readonly Regex Anchor = new Regex(
            @"MirBot log opened (?<date>\d{4}-\d\d-\d\d) \d\d:\d\d:\d\d",
            RegexOptions.Compiled);
        private static readonly Regex Observation = new Regex(
            @"^\[(?:(?<date>\d{4}-\d\d-\d\d) )?(?<time>\d\d:\d\d:\d\d\.\d{3})\] \[(?<bot>[^\]]+)\].*? \| (?<character>\S+) L(?<level>\d+) (?<class>Warrior|Wizard|Taoist|Assassin|Archer) @",
            RegexOptions.Compiled);

        public static (int imported, int skipped, int files) Run(string logDirectory, LevelMemory bank)
        {
            int imported = 0, skipped = 0, files = 0;
            if (bank == null || !Directory.Exists(logDirectory)) return (0, 0, 0);
            var last = new Dictionary<string, (int level, DateTime utc)>();
            string[] paths = Directory.GetFiles(logDirectory, "mirbot*")
                .Where(p => p.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                    Regex.IsMatch(p, @"\.log\.\d+$", RegexOptions.IgnoreCase))
                .OrderBy(p => File.GetLastWriteTimeUtc(p)).ThenBy(p => p, StringComparer.Ordinal).ToArray();

            DateTime date = DateTime.MinValue;
            TimeSpan previousTime = TimeSpan.Zero;
            bool observedTime = false;
            bool previousWasRotation = false;

            foreach (string path in paths)
            {
                bool rotation = Path.GetFileName(path).StartsWith("mirbot.log", StringComparison.OrdinalIgnoreCase);
                if (!rotation || !previousWasRotation)
                { date = DateTime.MinValue; observedTime = false; }
                previousWasRotation = rotation;
                try
                {
                    files++;
                    foreach (string line in File.ReadLines(path))
                    {
                        Match anchor = Anchor.Match(line);
                        if (anchor.Success && DateTime.TryParseExact(anchor.Groups["date"].Value,
                            "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
                            out DateTime parsedDate))
                        {
                            date = DateTime.SpecifyKind(parsedDate, DateTimeKind.Local);
                            observedTime = false;
                        }

                        Match match = Observation.Match(line);
                        if (!match.Success) continue;
                        if (match.Groups["date"].Success && DateTime.TryParseExact(
                            match.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out DateTime lineDate))
                        {
                            date = DateTime.SpecifyKind(lineDate, DateTimeKind.Local);
                            observedTime = false;
                        }
                        if (date == DateTime.MinValue) { skipped++; continue; }
                        if (!TimeSpan.TryParseExact(match.Groups["time"].Value, @"hh\:mm\:ss\.fff",
                            CultureInfo.InvariantCulture, out TimeSpan clock)) { skipped++; continue; }
                        if (observedTime && clock < previousTime && previousTime - clock > TimeSpan.FromHours(12))
                            date = date.AddDays(1);
                        previousTime = clock;
                        observedTime = true;

                        DateTime utc = date.Date.Add(clock).ToUniversalTime();
                        string bot = match.Groups["bot"].Value;
                        string character = match.Groups["character"].Value;
                        string key = bot + "\0" + character;
                        int level = int.Parse(match.Groups["level"].Value, CultureInfo.InvariantCulture);

                        if (last.TryGetValue(key, out var prior))
                        {
                            if (utc <= prior.utc) { skipped++; continue; }
                            if (level == prior.level + 1)
                            {
                                if (bank.Record(new LevelEntry
                                {
                                    Bot = bot, Character = character,
                                    Class = match.Groups["class"].Value,
                                    FromLevel = prior.level, ToLevel = level, Utc = utc,
                                    Source = "log-inferred", UncertaintyStartUtc = prior.utc.ToString("o")
                                })) imported++;
                            }
                            else if (level < prior.level || level > prior.level + 1) skipped++;
                        }
                        last[key] = (level, utc);
                    }
                }
                catch (IOException) { skipped++; }
                catch (UnauthorizedAccessException) { skipped++; }
            }
            bank.Flush();
            return (imported, skipped, files);
        }
    }
}
