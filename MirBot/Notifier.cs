using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MirBot
{
    /// <summary>
    /// Phone notifications through a Home Assistant webhook.
    ///
    /// Fire-and-forget by design: bots call Send from their own tick, which must never wait on the
    /// network, so messages go on a queue drained by one background sender. A dead or slow Home
    /// Assistant costs a log line every ten minutes, never a stalled bot.
    ///
    /// Each (bot, kind) pair has a cooldown. Repeats inside it are counted rather than sent, and
    /// the count rides along on the next message, so a burst of deaths reads as one alert with
    /// "(+3 more since ...)" rather than four buzzes.
    /// </summary>
    public sealed class Notifier : IDisposable
    {
        private readonly string _url;
        private readonly BotLog _log;
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        private readonly BlockingCollection<(string Json, NotifyRecord Record)> _queue =
            new BlockingCollection<(string, NotifyRecord)>(200);
        private readonly LinkedList<NotifyRecord> _history = new LinkedList<NotifyRecord>();
        private const int HistorySize = 60;
        private readonly Dictionary<string, (DateTime SentUtc, int Suppressed)> _recent =
            new Dictionary<string, (DateTime, int)>();
        private readonly object _gate = new object();
        private readonly Task _sender;
        private DateTime _nextFailureLog = DateTime.MinValue;

        public bool Enabled => !string.IsNullOrWhiteSpace(_url);

        public Notifier(string url, BotLog log)
        {
            _url = url?.Trim() ?? "";
            _log = log;
            _sender = Enabled ? Task.Run(Drain) : Task.CompletedTask;
        }

        /// <summary>Queue a notification. Never blocks; drops it if the queue is full.</summary>
        public void Send(string bot, string character, string kind, string title, string message,
            TimeSpan cooldown)
        {
            if (!Enabled) return;

            string key = bot + "|" + kind;
            DateTime now = DateTime.UtcNow;
            int suppressed;

            lock (_gate)
            {
                if (_recent.TryGetValue(key, out var last) && now - last.SentUtc < cooldown)
                {
                    _recent[key] = (last.SentUtc, last.Suppressed + 1);
                    Remember(new NotifyRecord(now, bot, character, kind, title, message,
                        "held back (cooldown)"));
                    return;
                }

                suppressed = last.Suppressed;
                _recent[key] = (now, 0);

                if (suppressed > 0)
                    message += $" (+{suppressed} more since {last.SentUtc.ToLocalTime():HH:mm})";
            }

            string json = JsonSerializer.Serialize(new
            {
                bot, character, kind, title, message,
                utc = now.ToString("o")
            });

            NotifyRecord record = new NotifyRecord(now, bot, character, kind, title, message, "queued");
            Remember(record);

            if (!_queue.TryAdd((json, record))) record.Outcome = "dropped (queue full)";
        }

        /// <summary>Most recent first, for the status page.</summary>
        public List<NotifyRecord> Recent()
        {
            lock (_gate) return new List<NotifyRecord>(_history);
        }

        private void Remember(NotifyRecord record)
        {
            lock (_gate)
            {
                _history.AddFirst(record);
                while (_history.Count > HistorySize) _history.RemoveLast();
            }
        }

        private async Task Drain()
        {
            foreach ((string json, NotifyRecord record) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    using StringContent body = new StringContent(json, Encoding.UTF8, "application/json");
                    using HttpResponseMessage response = await _http.PostAsync(_url, body);

                    if (response.IsSuccessStatusCode)
                        record.Outcome = "sent";
                    else
                    {
                        record.Outcome = $"failed (HTTP {(int)response.StatusCode})";
                        Failed($"HTTP {(int)response.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    record.Outcome = "failed (" + ex.GetType().Name + ")";
                    Failed(ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private void Failed(string why)
        {
            if (DateTime.UtcNow < _nextFailureLog) return;
            _nextFailureLog = DateTime.UtcNow.AddMinutes(10);
            _log?.Write($"Notify: could not reach the webhook ({why}); further failures quiet for 10 minutes.");
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            try { _sender.Wait(TimeSpan.FromSeconds(5)); } catch { }
            _http.Dispose();
        }
    }

    /// <summary>One notification as the status page shows it. Outcome is updated by the sender.</summary>
    public sealed class NotifyRecord
    {
        public NotifyRecord(DateTime utc, string bot, string character, string kind, string title,
            string message, string outcome)
        {
            Utc = utc; Bot = bot; Character = character; Kind = kind; Title = title;
            Message = message; Outcome = outcome;
        }

        public DateTime Utc { get; }
        public string Bot { get; }
        public string Character { get; }
        public string Kind { get; }
        public string Title { get; }
        public string Message { get; }
        public string Outcome { get; set; }
    }
}
