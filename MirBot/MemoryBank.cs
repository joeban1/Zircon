using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MirBot
{
    /// <summary>
    /// A list of learned facts, kept as JSON beside the executable and shared by every bot.
    ///
    /// Everything the bot knows today comes from System.db, which is exact and free but only
    /// describes what the designers wrote down. It cannot say how much experience an hour on a map
    /// is actually worth to a level 14 warrior, or which monster hits hard enough to matter. Those
    /// are measured, not looked up, and they are worth keeping: a bot that forgets them on every
    /// restart re-learns the same lessons forever.
    ///
    /// Modelled on the memory banks in the Mir 2 Crystal agents, with two deliberate departures.
    /// They save the whole file on every single discovery and guard it with a named system mutex,
    /// because their agents run as separate processes. Ours share one process, so a plain lock does,
    /// and writes are debounced - a busy map would otherwise rewrite the file hundreds of times a
    /// minute.
    /// </summary>
    public abstract class MemoryBank<TEntry>
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

        protected readonly List<TEntry> Entries = new List<TEntry>();
        protected readonly object Sync = new object();

        private readonly string _path;
        private bool _dirty;
        private DateTime _nextSave = DateTime.MinValue;

        protected MemoryBank(string path)
        {
            _path = path;
        }

        public int Count
        {
            get { lock (Sync) return Entries.Count; }
        }

        public string FilePath => _path;

        /// <summary>Rebuild any lookup the subclass keeps. Called with the lock held.</summary>
        protected virtual void Reindex()
        {
        }

        /// <summary>
        /// Read the file. Called by the SUBCLASS constructor, never by this one.
        ///
        /// Load calls the virtual Reindex, and in C# a derived class's field initialisers run after
        /// the base constructor has finished - so loading from the base constructor would hand
        /// Reindex a lookup dictionary that is still null, and every bot would die on startup with
        /// a NullReferenceException the moment a memory file existed to read.
        /// </summary>
        protected void Load()
        {
            if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path)) return;

            try
            {
                string json = File.ReadAllText(_path);
                List<TEntry> loaded = JsonSerializer.Deserialize<List<TEntry>>(json, Json);

                lock (Sync)
                {
                    Entries.Clear();
                    if (loaded != null) Entries.AddRange(loaded);
                    Reindex();
                }
            }
            catch (Exception)
            {
                // A corrupt or half-written file is not worth killing a bot over: start empty and
                // the next save overwrites it.
            }
        }

        /// <summary>Call with the lock held, after changing anything.</summary>
        protected void MarkDirty()
        {
            _dirty = true;
            if (_nextSave == DateTime.MinValue) _nextSave = DateTime.UtcNow + SaveInterval;
        }

        /// <summary>Write if enough has changed and enough time has passed. Cheap to call often.</summary>
        public void FlushIfDue()
        {
            if (!_dirty || DateTime.UtcNow < _nextSave) return;

            Flush();
        }

        public void Flush()
        {
            string json;

            lock (Sync)
            {
                if (!_dirty) return;

                json = JsonSerializer.Serialize(Entries, Json);
                _dirty = false;
                _nextSave = DateTime.MinValue;
            }

            if (string.IsNullOrWhiteSpace(_path)) return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));

                // Write beside and rename, so a crash mid-write cannot leave a truncated file that
                // loses everything learned so far.
                string temporary = _path + ".tmp";
                File.WriteAllText(temporary, json);

                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
            }
            catch (Exception)
            {
                // Losing a save is survivable; the entries are still in memory and will be retried.
                lock (Sync) _dirty = true;
            }
        }
    }
}
