using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MirBot
{
    public readonly record struct GoldSample(DateTime Utc, long Gold, long CapitalSpent)
    {
        public decimal Adjusted => (decimal)Gold + CapitalSpent;
    }

    public readonly record struct GoldTrendResult(bool Valid, decimal Change,
        double CoverageHours, long CapitalAdjustment);

    public static class GoldTrend
    {
        private static readonly TimeSpan EndpointSpan = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(20);

        /// <summary>Time-weighted endpoint balances, not an event-count-weighted mean.</summary>
        public static GoldTrendResult Measure(IReadOnlyList<GoldSample> history,
            DateTime now, int watchHours)
        {
            if (history == null || history.Count < 2 || watchHours < 1)
                return default;
            DateTime cutoff = now.AddHours(-watchHours);
            GoldSample[] ordered = history.Where(x => x.Utc <= now)
                .OrderBy(x => x.Utc).ToArray();
            GoldSample[] points = ordered.Where(x => x.Utc >= cutoff).ToArray();
            GoldSample? predecessor = ordered.LastOrDefault(x => x.Utc < cutoff);
            if (predecessor.HasValue && predecessor.Value.Utc != default)
                points = (new[] { predecessor.Value }).Concat(points).ToArray();
            if (points.Length < 2) return default;

            DateTime start = predecessor.HasValue && predecessor.Value.Utc != default
                ? cutoff : points[0].Utc;
            double coverage = (points[^1].Utc - start).TotalHours;
            double needed = Math.Max(watchHours * 0.75, watchHours - 1.0);
            if (coverage < needed || start + EndpointSpan >= points[^1].Utc)
                return new GoldTrendResult(false, 0, coverage, 0);

            for (int i = 1; i < points.Length; i++)
                if (points[i].Utc - points[i - 1].Utc > MaxGap)
                    return new GoldTrendResult(false, 0, coverage, 0);

            decimal? early = Average(points, start, start + EndpointSpan);
            decimal? late = Average(points, points[^1].Utc - EndpointSpan, points[^1].Utc);
            if (early == null || late == null)
                return new GoldTrendResult(false, 0, coverage, 0);

            return new GoldTrendResult(true, late.Value - early.Value, coverage,
                points[^1].CapitalSpent - points[0].CapitalSpent);
        }

        private static decimal? Average(GoldSample[] points, DateTime start, DateTime end)
        {
            decimal area = 0;
            double covered = 0;
            for (int i = 1; i < points.Length; i++)
            {
                DateTime a = points[i - 1].Utc > start ? points[i - 1].Utc : start;
                DateTime b = points[i].Utc < end ? points[i].Utc : end;
                if (b <= a) continue;
                double length = (points[i].Utc - points[i - 1].Utc).TotalSeconds;
                decimal left = points[i - 1].Adjusted +
                    (points[i].Adjusted - points[i - 1].Adjusted) *
                    (decimal)((a - points[i - 1].Utc).TotalSeconds / length);
                decimal right = points[i - 1].Adjusted +
                    (points[i].Adjusted - points[i - 1].Adjusted) *
                    (decimal)((b - points[i - 1].Utc).TotalSeconds / length);
                double seconds = (b - a).TotalSeconds;
                area += (left + right) / 2m * (decimal)seconds;
                covered += seconds;
            }
            return Math.Abs(covered - (end - start).TotalSeconds) < 1
                ? area / (decimal)covered : null;
        }
    }

    public sealed class ProfitPolicy
    {
        private sealed class State
        {
            public bool Active { get; set; }
            public DateTime ActiveSinceUtc { get; set; }
            public DateTime CooldownUntilUtc { get; set; }
        }

        private readonly string _path;
        private State _state = new State();
        public bool Active => _state.Active;
        public DateTime CooldownUntilUtc => _state.CooldownUntilUtc;
        public string LoadDiagnostic { get; private set; } = "";

        public ProfitPolicy(string path)
        {
            _path = path;
            try
            {
                if (File.Exists(path))
                    _state = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new State();
                else LoadDiagnostic = "loss-watch state missing; starting unlatched";
            }
            catch (Exception ex)
            {
                _state = new State();
                LoadDiagnostic = "loss-watch state unreadable; starting unlatched (" +
                    ex.GetType().Name + ")";
            }
        }

        /// <returns>A transition to log once, or empty when unchanged.</returns>
        public string Update(GoldTrendResult trend, DateTime now, bool tripCompleted,
            int watchHours, long dropGold, long recoverGold, int maxHours)
        {
            if (dropGold <= 0)
            {
                if (!_state.Active && _state.CooldownUntilUtc == DateTime.MinValue) return "";
                _state = new State(); Save(); return "loss watch disabled";
            }
            if (_state.Active && now >= _state.ActiveSinceUtc.AddHours(Math.Max(1, maxHours)))
            {
                _state.Active = false;
                _state.CooldownUntilUtc = now.AddHours(Math.Max(1, watchHours));
                Save(); return "loss watch capped; cooling down for one watch window";
            }
            if (_state.Active && tripCompleted && trend.Valid && trend.Change >= recoverGold)
            {
                _state.Active = false; Save(); return "operating trend recovered after town trip";
            }
            if (!_state.Active && now >= _state.CooldownUntilUtc && trend.Valid &&
                trend.Change <= -dropGold)
            {
                _state.Active = true;
                _state.ActiveSinceUtc = now;
                Save(); return "sustained operating loss detected";
            }
            return "";
        }

        private void Save()
        {
            if (string.IsNullOrWhiteSpace(_path)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                string temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(_state));
                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
            }
            catch (Exception) { /* A missing latch file fails unlatched on restart. */ }
        }
    }
}
