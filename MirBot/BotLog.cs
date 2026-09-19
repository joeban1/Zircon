using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MirBot
{
    /// <summary>
    /// A log sink. One file for the whole host, with a per-bot prefix on every line.
    ///
    /// The file matters: output from a process on the VM does not stream back over the
    /// SSH/ProxyCommand chain, so a long run shows nothing at all until it exits. Reading the file
    /// is the only way to see what happened while it was happening.
    ///
    /// One combined file rather than one per bot is deliberate. When two bots misbehave together
    /// you want their lines interleaved in time order; separate files mean merging by timestamp by
    /// hand. The per-bot in-memory ring below serves "just this bot's lines" instead.
    /// </summary>
    public sealed class BotLog : IDisposable
    {
        private const long RotateAtBytes = 32L * 1024 * 1024;
        private const int RingCapacity = 200;
        private static readonly TimeSpan FlushEvery = TimeSpan.FromMilliseconds(500);

        private readonly object _lock = new object();
        private readonly string _path;
        private readonly bool _console;

        private StreamWriter _writer;
        private long _written;
        private DateTime _nextFlush = DateTime.MinValue;

        private readonly Queue<string> _ring = new Queue<string>(RingCapacity);

        /// <summary>Prefix for every line from this sink, e.g. "Mirbot" or "host".</summary>
        public string Prefix { get; }

        private BotLog(BotLog parent, string prefix)
        {
            _lock = parent._lock;
            _path = parent._path;
            _console = parent._console;
            _writer = null;          // shares the parent's writer via _shared
            _shared = parent._shared ?? parent;
            Prefix = prefix;
        }

        private readonly BotLog _shared;

        public BotLog(string path, string prefix = "host", bool console = false)
        {
            _path = path;
            _console = console;
            Prefix = prefix;
            _shared = null;

            try
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                Open();
                Write($"=== MirBot log opened {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            }
            catch (Exception ex)
            {
                _writer = null;
                Console.WriteLine($"(could not open log file {path}: {ex.Message})");
            }
        }

        /// <summary>A view on the same file with a different prefix and its own ring.</summary>
        public BotLog ForBot(string prefix) => new BotLog(this, prefix);

        private void Open()
        {
            // FileShare.ReadWrite so the file can still be tailed over SSH while it is held open,
            // and AutoFlush off so a busy bot is not doing a syscall per line. The previous
            // implementation used File.AppendAllText per line, which opens and closes the handle
            // every time - the most likely thing to start blocking under antivirus or on a share.
            // A block longer than TimeOutDelay (20s) tears the game connection down.
            FileStream stream = new FileStream(_path, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite);

            _written = stream.Length;
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
        }

        public void Write(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{Prefix}] {message}";

            if (_console) Console.WriteLine(line);

            BotLog sink = _shared ?? this;

            lock (sink._lock)
            {
                // The ring is per-view, so /api/bots/{id}/log can serve one bot's lines.
                _ring.Enqueue(line);
                while (_ring.Count > RingCapacity) _ring.Dequeue();

                if (sink._writer == null) return;

                try
                {
                    sink._writer.WriteLine(line);
                    sink._written += line.Length + 2;

                    if (DateTime.Now >= sink._nextFlush)
                    {
                        sink._writer.Flush();
                        sink._nextFlush = DateTime.Now + FlushEvery;
                    }

                    if (sink._written >= RotateAtBytes) sink.Rotate();
                }
                catch
                {
                    // A logging failure must never take a bot down.
                }
            }
        }

        /// <summary>Called from the bot thread so a quiet bot still gets its lines to disk.</summary>
        public void FlushIfDue()
        {
            BotLog sink = _shared ?? this;

            lock (sink._lock)
            {
                if (sink._writer == null || DateTime.Now < sink._nextFlush) return;

                try
                {
                    sink._writer.Flush();
                    sink._nextFlush = DateTime.Now + FlushEvery;
                }
                catch { }
            }
        }

        /// <summary>Most recent lines from THIS view. Safe to call from another thread.</summary>
        public string[] Tail(int lines)
        {
            BotLog sink = _shared ?? this;

            lock (sink._lock)
            {
                string[] all = _ring.ToArray();
                if (lines >= all.Length) return all;

                string[] result = new string[lines];
                Array.Copy(all, all.Length - lines, result, 0, lines);
                return result;
            }
        }

        private void Rotate()
        {
            try
            {
                _writer.Flush();
                _writer.Dispose();
                _writer = null;

                string third = _path + ".3";
                if (File.Exists(third)) File.Delete(third);

                for (int i = 2; i >= 1; i--)
                {
                    string from = i == 1 ? _path : $"{_path}.{i}";
                    string to = $"{_path}.{i + 1}";

                    if (File.Exists(from)) File.Move(from, to, true);
                }

                Open();
            }
            catch
            {
                // If rotation fails, carry on appending to whatever is open.
                if (_writer == null)
                {
                    try { Open(); } catch { }
                }
            }
        }

        public void Dispose()
        {
            if (_shared != null) return;   // a view does not own the writer

            lock (_lock)
            {
                try
                {
                    _writer?.Flush();
                    _writer?.Dispose();
                }
                catch { }

                _writer = null;
            }
        }
    }
}
