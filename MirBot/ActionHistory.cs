using System;
using System.Collections.Generic;

namespace MirBot
{
    /// <summary>One line of history: an action, or a noted event like a death or a level-up.</summary>
    public sealed record HistoryEntry(
        string Action,
        string Subject,
        string Detail,
        int Count,
        DateTime FirstAt,
        DateTime LastAt);

    /// <summary>
    /// A rolling record of what the bot did, with repeats collapsed.
    ///
    /// Collapsing is keyed on (Action, Subject) and only against the NEWEST entry. Two details
    /// matter:
    ///
    /// - Subject, not Reason. Reason carries the volatile part - "Cow at 5" becomes "Cow at 4" on
    ///   the next tick - so keying on it would never group anything. Subject is the stable noun.
    /// - Adjacent only. Collapsing against any matching entry anywhere in the ring would destroy
    ///   the chronology.
    ///
    /// Bot-thread-only. Build() copies out for the snapshot; the ring itself is never shared.
    /// </summary>
    public sealed class ActionHistory
    {
        private readonly int _capacity;
        private readonly LinkedList<HistoryEntry> _entries = new LinkedList<HistoryEntry>();

        /// <summary>Bumped on every change, so the snapshot can skip rebuilding when idle.</summary>
        public int Version { get; private set; }

        public ActionHistory(int capacity = 64)
        {
            _capacity = Math.Max(1, capacity);
        }

        public void Record(Decision decision)
        {
            if (decision == null) return;

            Push(decision.Action.ToString(),
                 decision.Subject ?? decision.Reason ?? "",
                 decision.Reason ?? "");
        }

        /// <summary>
        /// A non-decision event - death, level-up, disconnect, ban - so the history reads as one
        /// timeline rather than only listing actions.
        /// </summary>
        public void Note(string category, string text) => Push(category, text, text);

        private void Push(string action, string subject, string detail)
        {
            DateTime now = DateTime.UtcNow;
            LinkedListNode<HistoryEntry> newest = _entries.Last;

            if (newest != null &&
                string.Equals(newest.Value.Action, action, StringComparison.Ordinal) &&
                string.Equals(newest.Value.Subject, subject, StringComparison.Ordinal))
            {
                // Same thing again: bump the count, keep the first timestamp, take the newest
                // detail. A new record rather than a mutation - the old one may already be inside a
                // published snapshot.
                newest.Value = newest.Value with
                {
                    Count = newest.Value.Count + 1,
                    Detail = detail,
                    LastAt = now
                };

                Version++;
                return;
            }

            _entries.AddLast(new HistoryEntry(action, subject, detail, 1, now, now));
            while (_entries.Count > _capacity) _entries.RemoveFirst();

            Version++;
        }

        /// <summary>Newest last. A fresh list every time - never hand out the ring.</summary>
        public List<HistoryEntry> Build()
        {
            List<HistoryEntry> result = new List<HistoryEntry>(_entries.Count);
            foreach (HistoryEntry entry in _entries) result.Add(entry);
            return result;
        }
    }
}
