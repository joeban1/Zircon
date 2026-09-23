using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text;

namespace MirBot
{
    /// <summary>
    /// A bot's gold, sampled over time, appended one line at a time.
    ///
    /// Deliberately NOT a MemoryBank. The bank pattern holds every entry in memory and rewrites
    /// the whole file, pretty-printed, whenever it is dirty - which is right for a few dozen
    /// hunting records that change rarely, and wrong for a time series that only ever grows. Four
    /// bots sampling every five minutes is over a thousand points a day; rewriting all of them
    /// every thirty seconds to add one would be absurd.
    ///
    /// So: newline-delimited JSON, opened for append, one line per sample. Reading is a scan of a
    /// small file, which the page does once when you open the chart and not on the 1Hz poll.
    ///
    /// The point of keeping it at all is the question "where did the money go". A wizard was
    /// watched falling from 75,000 gold to 31 over an afternoon, and reconstructing that meant
    /// grepping hours of log for purchases and fares. A line every five minutes answers it at a
    /// glance and costs nothing.
    /// </summary>
    public sealed class GoldLog
    {
        /// <summary>Ordinary cadence. Frequent enough to shape a curve, rare enough to ignore.</summary>
        public static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(5);

        /// <summary>
        /// A move this large gets its own point regardless of the clock.
        ///
        /// Without it the interesting moments - a 10,000 gold teleport fare, a 26,937 gold sale -
        /// land between samples and the curve shows a smooth slope where there was a cliff.
        /// </summary>
        public const long NotableChange = 2000;

        private const int MaxLines = 20000;

        private readonly string _path;
        private readonly object _sync = new object();

        /// <summary>
        /// No byte-order mark. Encoding.UTF8 emits one on the first append, which lands in the
        /// middle of a file format whose whole point is that every line stands alone - and any
        /// stricter reader than the one below would choke on the first record.
        /// </summary>
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public GoldLog(string path)
        {
            _path = path;
            TrimOnStartup();
        }

        public string FilePath => _path;

        /// <summary>
        /// Record a sample. Safe from any bot thread.
        ///
        /// The caller decides WHEN - see BotInstance, which samples before publishing its snapshot
        /// so a motionless bot still produces points.
        /// </summary>
        public void Append(string bot, long gold, DateTime utc, long capitalSpent = 0)
        {
            if (string.IsNullOrWhiteSpace(_path)) return;

            string line = "{\"bot\":" + Quote(bot) +
                          ",\"utc\":\"" + utc.ToString("o", CultureInfo.InvariantCulture) +
                          "\",\"capitalSpent\":" + capitalSpent.ToString(CultureInfo.InvariantCulture) +
                          ",\"gold\":" + gold.ToString(CultureInfo.InvariantCulture) + "}";

            lock (_sync)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    File.AppendAllText(_path, line + Environment.NewLine, Utf8NoBom);
                }
                catch (Exception)
                {
                    // Observability, never a dependency. A bot must not die because a chart
                    // could not be written.
                }
            }
        }

        /// <summary>Points for one bot, or all of them, oldest first.</summary>
        public List<GoldPoint> Read(string bot = null)
        {
            List<GoldPoint> points = new List<GoldPoint>();

            lock (_sync)
            {
                if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path)) return points;

                try
                {
                    foreach (string line in File.ReadLines(_path))
                    {
                        GoldPoint point = Parse(line);

                        if (point == null) continue;
                        if (bot != null && !string.Equals(point.Bot, bot, StringComparison.OrdinalIgnoreCase))
                            continue;

                        points.Add(point);
                    }
                }
                catch (Exception) { /* a half-written tail is not worth an error page */ }
            }

            return points;
        }

        public string Describe()
        {
            lock (_sync)
            {
                if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
                    return $"no gold history yet at {_path}";

                try { return $"{File.ReadLines(_path).Count():N0} gold sample(s) at {_path}"; }
                catch (Exception) { return $"gold history at {_path}"; }
            }
        }

        /// <summary>
        /// Keep the file bounded, once, at startup.
        ///
        /// Done here rather than on every append because trimming means rewriting, and rewriting
        /// on the hot path is the very thing this class exists to avoid.
        /// </summary>
        private void TrimOnStartup()
        {
            if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path)) return;

            try
            {
                string[] lines = File.ReadAllLines(_path);
                if (lines.Length <= MaxLines) return;

                File.WriteAllLines(_path, lines[^MaxLines..]);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// A deliberately small parser: three known fields, written only by Append above.
        ///
        /// Using JsonSerializer per line would allocate a document for every one of twenty
        /// thousand lines to read three values that this class wrote itself.
        /// </summary>
        private static GoldPoint Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            string bot = Between(line, "\"bot\":\"", "\"");
            string utc = Between(line, "\"utc\":\"", "\"");
            string gold = Between(line, "\"gold\":", "}");
            string capital = Between(line, "\"capitalSpent\":", ",");

            if (bot == null || utc == null || gold == null) return null;

            if (!long.TryParse(gold.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out long parsedGold) || parsedGold < 0 ||
                !DateTime.TryParse(utc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out _)) return null;
            long spent = 0;
            if (capital != null && (!long.TryParse(capital.Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out spent) || spent < 0)) return null;

            return new GoldPoint { Bot = bot, Utc = utc, Gold = parsedGold.ToString(CultureInfo.InvariantCulture),
                CapitalSpent = spent, HasCapitalSpent = capital != null };
        }

        private static string Between(string text, string open, string close)
        {
            int start = text.IndexOf(open, StringComparison.Ordinal);
            if (start < 0) return null;

            start += open.Length;

            int end = text.IndexOf(close, start, StringComparison.Ordinal);
            return end < 0 ? null : text.Substring(start, end - start);
        }

        private static string Quote(string value) =>
            "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
