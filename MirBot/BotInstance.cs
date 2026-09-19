using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using Library;
using System.Threading;

namespace MirBot
{
    public enum BotRunState
    {
        Offline,      // not running, nothing held
        Backoff,      // waiting before another connect attempt
        Connecting,   // socket open, handshake/login in progress
        Playing,      // in the world, brain running
        Stopping,     // shutting this run down
        Faulted,      // terminal: bad credentials, wrong version, unhandled exception
        Banned        // the server IP-banned us; do not retry until it expires
    }

    public enum BotCommandKind { Start, Stop, ForceTownTrip, Revive }

    /// <summary>
    /// One bot: its own connection, brain, town trip and dedicated thread.
    ///
    /// Threading contract - this is the whole safety story:
    ///
    /// - Everything private here is touched ONLY by the bot thread. WorldModel, Backpack,
    ///   ScriptedBrain and TownTrip are all single-thread-affine mutable state.
    /// - Other threads may touch exactly two members: State (a volatile read) and
    ///   TryEnqueue (a bounded concurrent queue). Nothing else.
    /// - In particular the connection is NEVER touched from another thread. Disconnect() nulls
    ///   ReceiveList/SendList/_rawData while a socket callback may still be inside ReceiveData,
    ///   so every Enqueue, Disconnect and teardown has to happen here.
    /// </summary>
    public sealed class BotInstance
    {
        private readonly BotHost _host;
        private readonly BotLog _log;
        private readonly ConcurrentQueue<BotCommandKind> _commands = new ConcurrentQueue<BotCommandKind>();

        private Thread _thread;
        private volatile bool _shutdown;
        private volatile BotRunState _state = BotRunState.Offline;

        // Bot-thread-only below this line.
        private BotConnection _connection;
        private ScriptedBrain _brain;
        private TownTrip _town;
        /// <summary>
        /// Explicit desired state. Inferring "should I be running" from AutoStart and the
        /// attempt count does not work: a Start command resets those very fields, so an
        /// AutoStart=false bot would swallow its own Start and never connect.
        /// </summary>
        private bool _wantRunning;
        private bool _stopRequested;
        private DateTime _stopDeadline = DateTime.MinValue;
        private DateTime _startedAt;
        private DateTime _retryAt = DateTime.MinValue;
        private int _attempts;

        private int _decisions;
        private BotAction _lastLogged = BotAction.Idle;
        private Decision _lastDecision;
        private bool _wasDead;
        private int _lastLevel;
        private readonly Dictionary<BotAction, int> _actionCounts = new Dictionary<BotAction, int>();
        private readonly ActionHistory _history = new ActionHistory(64);

        private BotStatus _status;
        private DateTime _nextSnapshot = DateTime.MinValue;
        private long _snapshotWorldVersion = -1;
        private int _snapshotHistoryVersion = -1;

        /// <summary>
        /// The only state another thread may read. A fully-built immutable record published by a
        /// volatile write; the reader never touches live bot state, so no lock is needed on either
        /// side and the web thread can take as long as it likes to serialise.
        /// </summary>
        public BotStatus Status => System.Threading.Volatile.Read(ref _status);

        public string Id { get; }
        public BotConfig Config { get; }
        public BotRunState State => _state;
        public string LastError { get; private set; }

        public BotInstance(string id, BotConfig config, BotHost host, BotLog log)
        {
            Id = id;
            Config = config;
            _host = host;
            _log = log;
            _status = BotStatus.Idle(id, "Offline");
        }

        /// <summary>Safe from any thread. Bounded so a stuck bot cannot grow the queue.</summary>
        public bool TryEnqueue(BotCommandKind command)
        {
            if (_commands.Count >= 16) return false;

            _commands.Enqueue(command);
            return true;
        }

        public void StartThread()
        {
            if (_thread != null) return;

            _thread = new Thread(RunLoop)
            {
                Name = $"bot-{Id}",
                IsBackground = false   // joined explicitly on shutdown
            };

            _thread.Start();
        }

        public void RequestShutdown() => _shutdown = true;

        /// <summary>This bot's recent log lines, for the status page.</summary>
        public string[] LogTail(int lines) => _log.Tail(lines);

        public void Join(TimeSpan timeout) => _thread?.Join(timeout);

        #region The thread

        private void RunLoop()
        {
            // The entire loop is wrapped. BaseConnection.Process RETHROWS anything that is not
            // NotImplementedException, and an unhandled exception on a background thread kills the
            // whole process - taking every other bot with it. Isolation has to be written, not
            // assumed.
            try
            {
                while (!_shutdown)
                {
                    try
                    {
                        DrainCommands();
                        Advance();
                    }
                    catch (Exception ex)
                    {
                        Fault(ex);
                    }

                    PublishIfDue();
                    _log.FlushIfDue();
                    Thread.Sleep(_state == BotRunState.Playing ? 20 : 100);
                }
            }
            catch (Exception ex)
            {
                Fault(ex);
            }
            finally
            {
                try { Teardown("thread ending"); } catch { }
            }
        }

        private void DrainCommands()
        {
            while (_commands.TryDequeue(out BotCommandKind command))
            {
                switch (command)
                {
                    case BotCommandKind.Start:
                        if (_state == BotRunState.Offline || _state == BotRunState.Backoff ||
                            _state == BotRunState.Faulted)
                        {
                            _log.Write("Start requested.");
                            LastError = null;
                            _attempts = 0;
                            _retryAt = DateTime.MinValue;
                            _state = BotRunState.Offline;
                            _stopRequested = false;
                            _wantRunning = true;
                        }
                        break;

                    case BotCommandKind.Stop:
                        if (_state != BotRunState.Offline && _state != BotRunState.Faulted)
                        {
                            _log.Write("Stop requested.");
                            _stopRequested = true;
                            _wantRunning = false;
                        }
                        break;

                    case BotCommandKind.ForceTownTrip:
                        _log.Write("Town trip forced.");
                        _town?.Force();
                        break;

                    case BotCommandKind.Revive:
                        if (_connection != null && _connection.Stage == BotStage.InGame)
                        {
                            _log.Write("Revive requested.");
                            _history.Note("Revive", "requested");
                            _connection.Revive();
                        }
                        break;
                }
            }
        }

        private void Advance()
        {
            if (_stopRequested && _connection != null)
            {
                // Dead characters cannot flee or log out - close immediately.
                if (_connection.World.Dead)
                {
                    Teardown("stopped (dead)");
                    _stopRequested = false;
                    _state = BotRunState.Offline;
                    return;
                }

                if (_stopDeadline == DateTime.MinValue)
                {
                    _stopDeadline = DateTime.UtcNow.AddSeconds(70);
                    _state = BotRunState.Stopping;
                    if (_brain != null) _brain.StopRequested = true;
                    _history.Note("Stop", "requested");
                }

                // The clean logout matters even though the socket is closing: it makes the
                // character leave the world tidily instead of lingering until the server's 20s
                // timeout, where it can be killed by whatever it was fighting.
                if (_connection.LoggedOut || DateTime.UtcNow > _stopDeadline)
                {
                    Teardown(_connection.LoggedOut ? "stopped" : "stopped (logout timed out)");
                    _stopRequested = false;
                    _stopDeadline = DateTime.MinValue;
                    _state = BotRunState.Offline;
                    return;
                }
            }

            switch (_state)
            {
                case BotRunState.Offline:
                    if (_stopRequested) { _stopRequested = false; return; }
                    if (!_wantRunning) return;
                    TryConnect();
                    return;

                case BotRunState.Backoff:
                    if (!_wantRunning) { _state = BotRunState.Offline; return; }
                    if (DateTime.UtcNow < _retryAt) return;
                    TryConnect();
                    return;

                case BotRunState.Faulted:
                case BotRunState.Banned:
                    return;
            }

            if (_connection == null) return;

            _connection.Process();

            if (_connection.Stage == BotStage.InGame && _state != BotRunState.Playing)
            {
                _state = BotRunState.Playing;
                _attempts = 0;
            }

            if (_state == BotRunState.Playing || _state == BotRunState.Stopping) RunBrain();

            if (!_connection.Connected || _connection.Disconnecting)
            {
                bool retry = _connection.Retryable;
                string reason = _connection.ExitReason ?? "disconnected";

                Teardown(reason);

                // AlreadyLoggedIn after a Stop is expected, not fatal - the server holds the old
                // connection briefly. Back off and try again rather than ending the instance.
                if (retry && _wantRunning) Backoff(reason);
            }
        }

        private void TryConnect()
        {
            if (!_host.TryClaimConnectSlot()) return;

            _state = BotRunState.Connecting;
            _attempts++;

            TcpClient client = new TcpClient();

            try
            {
                client.Connect(Config.ServerAddress, Config.ServerPort);
            }
            catch (Exception ex)
            {
                try { client.Dispose(); } catch { }
                Backoff($"could not reach the server: {ex.Message}");
                return;
            }

            // Always a FRESH connection. Disconnect() tears down BaseConnection state and
            // ExitReason is set-once, so a reused instance carries the previous run's failure.
            _connection = new BotConnection(client, Config, _host.ClientHash, _log);
            _brain = new ScriptedBrain(Config) { Books = _host.Books };
            _town = new TownTrip(Config, _host.Vendors, _host.Books);
            _brain.Town = _town;

            _connection.OnNPCPage = page =>
            {
                string kind = page == null ? "(none)" : page.DialogType.ToString();
                _log.Write($"NPC page: {kind} | trip {_town.Phase} {_town.Status}" +
                           (string.IsNullOrEmpty(_town.SellDiagnostic) ? "" : $" | {_town.SellDiagnostic}") +
                           (string.IsNullOrEmpty(_town.BookDiagnostic) ? "" : $" | books: {_town.BookDiagnostic}"));
            };

            _connection.OnRepairResult = success =>
                _log.Write(success ? "Repair succeeded."
                                   : "Repair refused (usually not enough gold) - not retried this trip.");

            _connection.OnResync = _brain.Resynced;

            _stopDeadline = DateTime.MinValue;
            _startedAt = DateTime.UtcNow;
            _decisions = 0;
            _actionCounts.Clear();
            _lastLogged = BotAction.Idle;

            _log.Write($"Connecting to {Config.ServerAddress}:{Config.ServerPort} (attempt {_attempts}).");
        }

        private void RunBrain()
        {
            Decision decision = _brain.Decide(_connection.World, _connection.Items);
            if (decision == null || decision.Action == BotAction.Idle) return;

            _connection.Act(decision);
            _brain.Issued(decision, _connection.World);

            _decisions++;
            _actionCounts.TryGetValue(decision.Action, out int count);
            _actionCounts[decision.Action] = count + 1;

            if (_connection.World.Dead && !_wasDead) { _history.Note("Died", "killed"); _wasDead = true; }
            else if (!_connection.World.Dead) _wasDead = false;

            if (_connection.World.Level != _lastLevel && _lastLevel > 0)
                _history.Note("Level up", $"now {_connection.World.Level}");
            _lastLevel = _connection.World.Level;

            _lastDecision = decision;
            _history.Record(decision);

            if (decision.Action != _lastLogged)
            {
                _log.Write($"{decision} | {_connection.World.Describe()}");
                _lastLogged = decision.Action;
            }
        }

        private void Backoff(string why)
        {
            LastError = why;

            // Full jitter, and a ceiling deliberately longer than the server's 5-minute IP ban so a
            // banned fleet waits it out rather than hammering.
            double seconds = Math.Min(_host.MaxBackoffSeconds, 10 * Math.Pow(2, _attempts - 1));
            seconds *= 0.5 + Random.Shared.NextDouble() * 0.5;

            _retryAt = DateTime.UtcNow.AddSeconds(seconds);
            _state = BotRunState.Backoff;

            _log.Write($"{why} - retrying in {(int)seconds}s.");
        }

        private void Fault(Exception ex)
        {
            LastError = ex.Message;
            _log.Write("FAULT: " + ex);

            try { Teardown("faulted"); } catch { }

            _state = BotRunState.Faulted;
        }

        private void Teardown(string why)
        {
            if (_connection == null) return;

            WriteSummary(why);

            try
            {
                if (_connection.Connected) _connection.TryDisconnect();
            }
            catch (Exception ex)
            {
                _log.Write("Teardown error (ignored): " + ex.Message);
            }

            _connection = null;
            _brain = null;
            _town = null;

            if (_state != BotRunState.Faulted) _state = BotRunState.Offline;
        }

        #region Snapshot

        private void PublishIfDue()
        {
            DateTime now = DateTime.UtcNow;
            if (now < _nextSnapshot) return;

            // Skip the rebuild when nothing has changed. WorldModel.Version increments on every
            // mutation and was, until now, read by nothing.
            long worldVersion = _connection?.World.Version ?? -1;

            bool changed = worldVersion != _snapshotWorldVersion ||
                           _history.Version != _snapshotHistoryVersion;

            _nextSnapshot = now.AddSeconds(1);   // the page polls at 1Hz
            if (!changed && _status != null) return;

            _snapshotWorldVersion = worldVersion;
            _snapshotHistoryVersion = _history.Version;

            System.Threading.Volatile.Write(ref _status, Build());
        }

        private BotStatus Build()
        {
            // Publish even when not in game - a bot stuck Connecting is exactly when you want to
            // see something rather than an empty card.
            if (_connection == null || _connection.World.SelfID == 0)
                return BotStatus.Idle(Id, _state.ToString()) with
                {
                    ExitReason = _connection?.ExitReason,
                    LastError = LastError,
                    History = BuildHistory()
                };

            WorldModel world = _connection.World;
            Backpack items = _connection.Items;

            double? percent = null;
            if (world.MaxExperienceKnown && world.MaxExperience > 0)
                percent = Math.Round((double)(world.Experience / world.MaxExperience) * 100, 2);

            List<EquipmentStatus> equipment = new List<EquipmentStatus>();

            foreach (KeyValuePair<int, ClientUserItem> pair in items.Worn)
            {
                ClientUserItem item = pair.Value;

                equipment.Add(new EquipmentStatus(
                    ((Library.EquipmentSlot)pair.Key).ToString(),
                    item.Info.ItemName,
                    item.CurrentDurability / Backpack.DurabilityScale,
                    item.MaxDurability / Backpack.DurabilityScale,
                    Backpack.IsBroken(item),
                    Backpack.IsWorn(item, Config.RepairAtDurability)));
            }

            List<ItemStatus> inventory = new List<ItemStatus>();

            foreach (KeyValuePair<int, ClientUserItem> pair in items.Carried)
                inventory.Add(new ItemStatus(pair.Key, pair.Value.Info.ItemName,
                    pair.Value.Count, pair.Value.Info.ItemType.ToString()));

            inventory.Sort((a, b) => a.Slot.CompareTo(b.Slot));

            return new BotStatus(
                Id, _state.ToString(),
                _lastDecision?.Action.ToString() ?? "", _lastDecision?.Subject ?? "",
                _lastDecision?.Reason ?? "",
                _connection.ExitReason, LastError,
                world.Name, world.Class.ToString(), world.Level, world.Dead,
                world.Health, world.MaxHealth, world.HealthPercent,
                world.Mana, world.MaxMana, world.ManaPercent,
                world.Experience.ToString("0"), world.MaxExperience.ToString("0"),
                percent, world.AtMaxLevel, world.Gold.ToString(),
                world.MapIndex, world.Location.X, world.Location.Y, world.InSafeZone,
                world.BagWeight, world.MaxBagWeight, world.WeightPercent,
                _town?.Phase.ToString() ?? "", _town?.Status ?? "",
                (int)(DateTime.UtcNow - _startedAt).TotalSeconds,
                _decisions, _connection.ResyncCount, _brain?.DetoursTaken ?? 0,
                _connection.DroppedPackets, world.KnownMagicCount,
                equipment, inventory, BuildHistory());
        }

        private List<HistoryStatus> BuildHistory()
        {
            List<HistoryStatus> result = new List<HistoryStatus>();

            foreach (HistoryEntry entry in _history.Build())
                result.Add(new HistoryStatus(entry.Action, entry.Subject, entry.Detail,
                    entry.Count, entry.LastAt.ToString("o")));

            return result;
        }

        #endregion

        private void WriteSummary(string why)
        {
            _log.Write("");
            _log.Write($"=== Summary ({why}) ===");
            _log.Write($"Uptime:            {(int)(DateTime.UtcNow - _startedAt).TotalSeconds}s");
            _log.Write($"Pings answered:    {_connection.PingCount}");
            _log.Write($"Packets processed: {_connection.TotalPacketsProcessed}");
            _log.Write($"Decisions issued:  {_decisions}");
            _log.Write($"Server resyncs:    {_connection.ResyncCount}");
            _log.Write($"Packets dropped:   {_connection.DroppedPackets}" +
                       (_connection.DroppedPackets > 0
                           ? "   <-- the packet budget fired; something was looping"
                           : ""));
            _log.Write($"Final world:       {_connection.World.Describe()}");
            _log.Write($"Equipped:          {_connection.Items.DescribeEquipment()}");
            _log.Write($"Durability:        {_connection.Items.DescribeDurability(Config.RepairAtDurability)}");
            _log.Write($"Inventory:         {_connection.Items.DescribeInventory()}");

            if (_actionCounts.Count > 0)
            {
                _log.Write("Actions taken:");
                foreach (KeyValuePair<BotAction, int> pair in _actionCounts)
                    _log.Write($"  {pair.Value,6}  {pair.Key}");
            }

            if (!string.IsNullOrEmpty(_connection.ExitReason))
                _log.Write("Exit reason: " + _connection.ExitReason);
        }

        #endregion
    }
}
