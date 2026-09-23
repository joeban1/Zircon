using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MirBot
{
    public sealed class XpPoint
    {
        public string Bot { get; set; } = "";
        public string Character { get; set; } = "";
        public string Session { get; set; } = "";
        public DateTime Utc { get; set; }
        public decimal Total { get; set; }
    }

    public readonly record struct XpPace(decimal? PerHour, int CoverageSeconds);

    /// <summary>Bounded signed-XP checkpoints; only same-session intervals count as play time.</summary>
    public sealed class XpLog
    {
        public static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(5);
        private const int MaxLines = 20000;
        private readonly string _path;
        private readonly object _sync = new object();
        private readonly List<XpPoint> _points = new List<XpPoint>();
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public XpLog(string path)
        {
            _path = path;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            try
            {
                string[] lines = File.ReadAllLines(path);
                foreach (string line in lines.Skip(Math.Max(0, lines.Length - MaxLines)))
                {
                    try
                    {
                        XpPoint point = JsonSerializer.Deserialize<XpPoint>(line);
                        if (point != null && point.Utc.Kind == DateTimeKind.Utc &&
                            !string.IsNullOrWhiteSpace(point.Session)) _points.Add(point);
                    }
                    catch (JsonException) { }
                }
                if (lines.Length > MaxLines)
                    File.WriteAllLines(path, lines[^MaxLines..], Utf8);
            }
            catch (Exception) { /* Observability cannot stop the host. */ }
        }

        public void Append(XpPoint point)
        {
            if (point == null || string.IsNullOrWhiteSpace(_path)) return;
            lock (_sync)
            {
                _points.Add(point);
                if (_points.Count > MaxLines) _points.RemoveRange(0, _points.Count - MaxLines);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    File.AppendAllText(_path, JsonSerializer.Serialize(point) + Environment.NewLine, Utf8);
                }
                catch (Exception) { }
            }
        }

        public XpPace Measure(string bot, string character, DateTime now)
        {
            DateTime cutoff = now - TimeSpan.FromHours(2);
            decimal net = 0m;
            double seconds = 0;
            lock (_sync)
            {
                XpPoint prior = null;
                foreach (XpPoint point in _points)
                {
                    if (!string.Equals(point.Bot, bot, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(point.Character, character, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (prior != null && point.Session == prior.Session &&
                        point.Utc > prior.Utc && point.Utc <= now &&
                        point.Utc - prior.Utc <= TimeSpan.FromMinutes(10) && point.Utc > cutoff)
                    {
                        DateTime start = prior.Utc > cutoff ? prior.Utc : cutoff;
                        double fraction = (point.Utc - start).TotalSeconds /
                            (point.Utc - prior.Utc).TotalSeconds;
                        net += (point.Total - prior.Total) * (decimal)fraction;
                        seconds += (point.Utc - start).TotalSeconds;
                    }
                    prior = point;
                }
            }
            if (seconds < 1) return new XpPace(null, 0);
            int coverage = (int)Math.Min(7200, Math.Round(seconds));
            if (coverage < 1200) return new XpPace(null, coverage);
            return new XpPace(net * 3600m / (decimal)seconds, coverage);
        }
    }
}
