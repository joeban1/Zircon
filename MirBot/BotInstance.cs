using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using Library;
using Library.SystemModels;
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

    public enum BotCommandKind { Start, Stop, ForceTownTrip, Revive, Travel, SetConfig, ForceRepair, NextTarget }

    /// <summary>A queued command and, for Travel, the map it names.</summary>
    public readonly struct BotCommand
    {
        public readonly BotCommandKind Kind;
        public readonly string Argument;

        public BotCommand(BotCommandKind kind, string argument = null)
        {
            Kind = kind;
            Argument = argument;
        }
    }

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
        private readonly ConcurrentQueue<BotCommand> _commands = new ConcurrentQueue<BotCommand>();

        private Thread _thread;
        private volatile bool _shutdown;
        private volatile BotRunState _state = BotRunState.Offline;

        // Bot-thread-only below this line.
        private BotConnection _connection;
        private ScriptedBrain _brain;
        private TownTrip _town;
        private readonly RecoveryPolicy _recovery = new RecoveryPolicy();
        private bool _lastFrugalRecoveryCombat;
        /// <summary>
        /// Explicit desired state. Inferring "should I be running" from AutoStart and the
        /// attempt count does not work: a Start command resets those very fields, so an
        /// AutoStart=false bot would swallow its own Start and never connect.
        /// </summary>
        private bool _wantRunning;
        private bool _stopRequested;
        private DateTime _stopDeadline = DateTime.MinValue;
        private DateTime _startedAt;

        /// <summary>
        /// When the character actually entered the world, as distinct from when the instance began
        /// connecting.
        ///
        /// This is the clock the "last kill" counter falls back to before the first kill. A bot
        /// that has been in game for a quarter of an hour and killed nothing is in exactly the
        /// same trouble as one that stopped killing a quarter of an hour ago, and hiding the
        /// first case behind a dash only means the one bot that never got started is the one the
        /// page says nothing about.
        /// </summary>
        private DateTime _inGameAt = DateTime.MinValue;
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
        public bool TryEnqueue(BotCommandKind command, string argument = null)
        {
            if (_commands.Count >= 16) return false;

            _commands.Enqueue(new BotCommand(command, argument));
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
            while (_commands.TryDequeue(out BotCommand queued))
            {
                switch (queued.Kind)
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

                    case BotCommandKind.Travel:
                        StartTravel(queued.Argument);
                        break;

                    case BotCommandKind.SetConfig:
                        ApplyConfigChange(queued.Argument);
                        break;

                    case BotCommandKind.NextTarget:
                        if (_brain == null || !_brain.ForceNextTarget())
                            _log.Write("Next target requested, but nothing is targeted.");
                        else
                        {
                            _log.Write("Next target forced - current one parked for 20s.");
                            _history.Note("Target", "skipped on request");
                        }
                        break;

                    case BotCommandKind.ForceRepair:
                        if (_town == null) _log.Write("Repair forced, but there is no trip to force.");
                        else
                        {
                            _log.Write("Repair forced.");
                            _history.Note("Repair", "forced");
                            _town.ForceRepair();
                        }
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

            // Silence is the only answer an unaffordable repair gets, so it has to be timed.
            _connection.CheckRepairTimeout();

            if (_connection.Stage == BotStage.InGame && _state != BotRunState.Playing)
            {
                _state = BotRunState.Playing;
                _attempts = 0;
                _inGameAt = DateTime.UtcNow;
            }

            WatchForDeath();

            if (_state == BotRunState.Playing || _state == BotRunState.Stopping) RunBrain();

            if (!_connection.Connected || _connection.Disconnecting)
            {
                bool retry = _connection.Retryable;
                string reason = _connection.ExitReason ?? "disconnected";
                TimeSpan serverWait = _connection.RetryAfter;

                Teardown(reason);

                if (!_wantRunning) return;

                // AlreadyLoggedIn after a Stop is expected, not fatal - the server holds the old
                // connection briefly. Back off and try again rather than ending the instance.
                if (retry)
                {
                    _minimumRetryWait = serverWait;
                    Backoff(reason);
                    return;
                }

                // Anything else is terminal, and saying so matters more than it looks. Teardown
                // leaves the state Offline, and Offline with _wantRunning set reconnects on the
                // very next tick - so a wrong password used to produce an unthrottled reconnect
                // loop at ten attempts a second, which is precisely how the server's packet guard
                // turns one bad config line into an IP ban for every bot on the machine.
                LastError = reason;
                _state = BotRunState.Faulted;
                _log.Write($"{reason} - not retrying. Fix it and press Start.");
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
            _recovery.Reset();
            _lastFrugalRecoveryCombat = false;
            _tripWasActive = false;
            _brain = new ScriptedBrain(Config)
            {
                Books = _host.Books,
                Maps = _host.Maps,
                Danger = _host.Danger,
                Nav = _host.Nav,
                Exits = _host.World,
                Butcher = _host.Butcher
            };
            _brain.BrainLog = message => _log.Write(message);
            _brain.IsDropPending = () => _connection != null && _connection.DropPending;
            _connection.OnDropRefused = slot => _brain?.NoteDropRefused(slot);
            _connection.OnSellRefused = slot => _brain?.Town?.NoteSellRefused(slot);
            _connection.OnSellOrderRefused = () =>
            {
                _brain?.Town?.NoteSellOrderRefused();
                NoteDivergence();
            };
            _connection.OnHarvested = id => _brain?.NoteHarvested(id);
            _connection.OnItemUseVerdict = (slot, accepted) =>
                _brain?.Town?.NoteScrollOutcome(slot, accepted);
            _brain.Travel = new Journey(_host.World)
            {
                OnFailed = message =>
                {
                    _log.Write($"Travel: {message}");
                    EscapeOnScroll(message);
                },
                TalkRange = Config.VendorTalkRange,
                GoldFloor = Config.TeleportGoldFloor,
                MaxGoldPercent = Config.TeleportMaxGoldPercent
            };
            _town = new TownTrip(Config, _host.Vendors, _host.Books, _host.SafeZones);
            _brain.Town = _town;

            _connection.OnNPCPage = page =>
            {
                // The trip has to see the page FIRST: PageChanged is what moves it from Talking to
                // Trading, and every later step - sell, repair, restock, buy a book - hangs off
                // that transition. Wiring this callback to logging alone (as it was when the
                // single-bot Program.Main was split into BotHost/BotInstance) leaves the bot
                // standing on the vendor's screen until the page times out, which is exactly what
                // it did: NPCCall, NPCButton, then NPCClose (timeout) twenty seconds later, with a
                // bag full of sellable gear and one town scroll left.
                //
                // Safe here: packets are drained by _connection.Process() on this bot's own thread,
                // so this runs on the same thread as Decide and touches no shared state.
                _town.PageChanged(page);

                string kind = page == null ? "(none)" : page.DialogType.ToString();
                _log.Write($"NPC page: {kind} | trip {_town.Phase} {_town.Status}" +
                           (string.IsNullOrEmpty(_town.SellDiagnostic) ? "" : $" | {_town.SellDiagnostic}") +
                           (string.IsNullOrEmpty(_town.BookDiagnostic) ? "" : $" | books: {_town.BookDiagnostic}"));
            };

            _connection.OnRepairResult = success =>
            {
                // Either way the request is answered, so the trip may send its next one. Without
                // this the special and ordinary repairs would serialise only by luck of timing.
                _town?.RepairAnswered();

                if (success)
                {
                    _log.Write("Repair succeeded.");
                    return;
                }

                // Tell the TRIP, not just the log. Broken gear bypasses the trip cooldown, which is
                // right when the repair will work and a trap when it will not: a bot that cannot
                // pay the bill otherwise walks to the repairer, is refused, walks home, and sets
                // off again, forever, never hunting long enough to earn the money.
                _town?.RepairRefused();
                _log.Write("Repair refused (usually not enough gold) - backing off so the bot can " +
                           "go and earn some.");
            };

            _connection.OnResync = _brain.Resynced;

            _connection.OnMagicToggle = (magic, canUse) => _brain?.Skills.Toggled(magic, canUse);

            _connection.OnMagicCooldown = (infoIndex, delay) =>
                _brain?.Spells.Cooldown(infoIndex, delay);

            _connection.OnObjectGone = id =>
            {
                _brain?.Spells.ForgetPoison(id);
                _brain?.ForgetObject(id);
            };

            _connection.OnDamaged = (monster, damage) =>
            {
                _lastAttacker = monster;
                _host.Danger.RecordHit(monster, damage, _connection?.World.MapIndex ?? 0);
            };

            _stopDeadline = DateTime.MinValue;
            _startedAt = DateTime.UtcNow;
            _decisions = 0;
            _actionCounts.Clear();
            _lastLogged = BotAction.Idle;

            _log.Write($"Connecting to {Config.ServerAddress}:{Config.ServerPort} (attempt {_attempts}).");
        }

        private string _lastTripStatus = "";

        /// <summary>Say what the town trip is doing, or why it is not, but only when it changes.</summary>
        private void LogTripStatus()
        {
            if (_town == null) return;

            string line = $"{_town.Phase} {_town.Status}".Trim();

            if (!string.IsNullOrEmpty(_town.WeightDiagnostic))
                line += $" | {_town.WeightDiagnostic}";

            if (line == _lastTripStatus) return;

            _lastTripStatus = line;

            if (line.Length > 0) _log.Write($"Trip: {line}");
        }

        private DateTime _idleSince = DateTime.MinValue;
        private DateTime _idleSaid = DateTime.MinValue;

        private void RunBrain()
        {
            _connection.CheckPendingItemUse();

            UpdateRecoveryState(false);

            bool frugalRecovery = _recovery.Active &&
                                   RecoveryMapIndexes(Config.RecoveryMaps)
                                       .Contains(_connection.World.MapIndex);

            _brain.FrugalRecoveryCombat = frugalRecovery;

            if (frugalRecovery != _lastFrugalRecoveryCombat)
            {
                _lastFrugalRecoveryCombat = frugalRecovery;
                _log.Write(frugalRecovery
                    ? $"Recovery: frugal melee on {_connection.World.MapName}; spells, skills, " +
                      "summons and poison are reserved for stronger ground."
                    : "Recovery: ordinary combat spending restored.");
            }

            // PENDING **OR** ON COOLDOWN.
            //
            // TryItemUse refuses to send while the server's one-second UseItemTime is still
            // running, which is correct - but a silently dropped send is worse than a refused one
            // if the caller has already committed. The town trip enters TownPhase.Teleporting the
            // moment it decides to scroll, so a dropped scroll left it waiting six seconds and
            // then concluding "the scroll did not move us" - every time, deterministically, where
            // before the fix it merely failed sometimes.
            //
            // Telling the brain about the cooldown as well means the trip does not choose to
            // scroll until the scroll can actually be sent. TownTrip.Next already turns this into
            // "no scroll slot this tick" and simply tries again.
            Decision decision = _brain.Decide(
                _connection.World,
                _connection.Items,
                _connection.ItemUsePending || _connection.ItemUseOnCooldown);

            // Deliberately ABOVE the early return, because the interesting case is the tick that
            // decides to do nothing.
            //
            // TownTrip.Status was previously only ever written out when an NPC page arrived, so
            // every abort and every declined trip was invisible. A wizard was watched hunting at
            // 6% health with an empty bag for 111 seconds before a trip finally started, and the
            // log had nothing whatsoever to say about the delay - which is how this went unnoticed
            // long enough to be observed by eye rather than read.
            LogTripStatus();

            if (decision == null || decision.Action == BotAction.Idle)
            {
                // Silence is a state, and an unlogged one is unfindable. Only after it has gone
                // on long enough to matter, and at most once every few seconds, so an ordinary
                // sub-second action gate never reaches the log.
                if (_idleSince == DateTime.MinValue) _idleSince = DateTime.UtcNow;

                // Being dead is a legitimate idle state with its own handling, and saying so
                // every four seconds until the revive timer fires is pure noise.
                bool worthSaying = !_connection.World.Dead;

                if (worthSaying &&
                    DateTime.UtcNow - _idleSince > TimeSpan.FromSeconds(4) &&
                    DateTime.UtcNow - _idleSaid > TimeSpan.FromSeconds(4))
                {
                    _idleSaid = DateTime.UtcNow;

                    _log.Write($"Deciding NOTHING for " +
                               $"{(int)(DateTime.UtcNow - _idleSince).TotalSeconds}s - " +
                               $"{_brain.IdleReason ?? decision?.Reason ?? "no reason recorded"}");
                }

                return;
            }

            _idleSince = DateTime.MinValue;

            _connection.Act(decision);
            _brain.Issued(decision, _connection.World);

            _decisions++;
            _actionCounts.TryGetValue(decision.Action, out int count);
            _actionCounts[decision.Action] = count + 1;

            if (_connection.World.Level != _lastLevel && _lastLevel > 0)
                _history.Note("Level up", $"now {_connection.World.Level}");
            _lastLevel = _connection.World.Level;

            // Learning a book changes what the bot can do, and it is the one moment worth saying
            // so: a wizard that reads Fire Ball and never casts it is the bug this replaced.
            int magics = _connection.World.KnownMagicCount;

            if (magics != _lastMagicCount)
            {
                _lastMagicCount = magics;
                _log.Write("Spells: " + _brain.Spells.Describe(_connection.World));
            }

            _connection.CheckPendingBuy();
            CheckPendingLearn(decision);

            SampleExperience();
            ConsiderTravel();

            _lastDecision = decision;
            _history.Record(decision);

            if (decision.Action != _lastLogged)
            {
                _log.Write($"{decision} | {_connection.World.Describe()}");
                _lastLogged = decision.Action;
            }

            // The trip's own explanations are written when it decides, but the page callback that
            // used to carry them only fires on a page change - so a reason produced at the last
            // stop of a trip was never logged, or surfaced one trip late. Log each one once, when
            // it changes.
            if (_town != null && _town.BookDiagnostic != _lastBookDiagnostic)
            {
                _lastBookDiagnostic = _town.BookDiagnostic;

                if (!string.IsNullOrEmpty(_lastBookDiagnostic))
                    _log.Write("Books: " + _lastBookDiagnostic);
            }

            if (_town != null && _town.GearDiagnostic != _lastGearDiagnostic)
            {
                _lastGearDiagnostic = _town.GearDiagnostic;

                if (!string.IsNullOrEmpty(_lastGearDiagnostic))
                    _log.Write("Gear: " + _lastGearDiagnostic);
            }

            // Keyed on the trip NUMBER, not the plan text: a bot that shops in one town produces
            // the same plan every time, and comparing strings hid every trip after the first.
            if (_town != null && _town.TripSequence != _lastTripSequence)
            {
                _lastTripSequence = _town.TripSequence;

                if (!string.IsNullOrEmpty(_town.Itinerary))
                    _log.Write($"Trip plan #{_lastTripSequence}: {_town.Itinerary}");
            }

            // Repair and Supply were both being computed and then thrown away - Supply has existed
            // unlogged since it was written, which is why "why did it not restock" has always meant
            // reading inventory dumps instead of the log.
            if (_town != null && _town.RepairDiagnostic != _lastRepairDiagnostic)
            {
                _lastRepairDiagnostic = _town.RepairDiagnostic;

                if (!string.IsNullOrEmpty(_lastRepairDiagnostic))
                    _log.Write("Repair: " + _lastRepairDiagnostic);
            }

            if (_town != null && _town.SupplyDiagnostic != _lastSupplyDiagnostic)
            {
                _lastSupplyDiagnostic = _town.SupplyDiagnostic;

                if (!string.IsNullOrEmpty(_lastSupplyDiagnostic))
                    _log.Write("Supply: " + _lastSupplyDiagnostic);
            }

            if (_town != null && _town.BankDiagnostic != _lastBankDiagnostic)
            {
                _lastBankDiagnostic = _town.BankDiagnostic;

                if (!string.IsNullOrEmpty(_lastBankDiagnostic))
                    _log.Write("Bank: " + _lastBankDiagnostic);
            }

            if (_town != null && _town.SellDiagnostic != _lastSellDiagnostic)
            {
                _lastSellDiagnostic = _town.SellDiagnostic;

                if (!string.IsNullOrEmpty(_lastSellDiagnostic))
                    _log.Write("Sell: " + _lastSellDiagnostic);
            }
        }

        private string _lastBookDiagnostic = "";
        private string _lastSellDiagnostic = "";
        private string _lastGearDiagnostic = "";
        private string _lastBankDiagnostic = "";
        private int _lastMagicCount = -1;
        private int _lastTripSequence;
        private string _lastRepairDiagnostic = "";
        private string _lastSupplyDiagnostic = "";

        /// <summary>
        /// Send the bot to the best map we know of for its class and level, or to somewhere it has
        /// never measured when we know of nowhere good yet.
        ///
        /// This is the exploration/exploitation split the Mir 2 agents use, and it is the reason
        /// the memory banks exist: with no records the only sensible move is to go and make some.
        /// Kept manual for now - the button is the trigger, not a timer - so the first journeys can
        /// be watched rather than discovered in a log.
        /// </summary>
        private void StartTravel(string requestedMap = null)
        {
            if (_connection == null || _connection.Stage != BotStage.InGame || _brain?.Travel == null)
            {
                _log.Write("Travel requested but the bot is not in game.");
                return;
            }

            WorldModel world = _connection.World;
            string mirClass = world.Class.ToString();

            // An explicit destination overrides everything: this is the operator saying "go here".
            if (!string.IsNullOrWhiteSpace(requestedMap))
            {
                MapInfo asked = Globals.MapInfoList?.Binding?.FirstOrDefault(x =>
                    string.Equals(x.Description, requestedMap, StringComparison.OrdinalIgnoreCase) ||
                    x.Index.ToString() == requestedMap);

                if (asked == null)
                {
                    _log.Write($"Travel: no map called '{requestedMap}'.");
                    return;
                }

                Begin(asked.Index, asked.Description, "requested");
                return;
            }

            // Where we could get to, and what each would cost in map transitions. One
            // breadth-first pass prices every candidate at once.
            // Anywhere that has killed this character and never paid is not a place to cross, so
            // it is excluded from the reachability question itself. A map only reachable THROUGH
            // such a place is not really reachable: costing it as though the crossing were free is
            // how a bot talks itself into a journey it does not survive.
            HashSet<int> deadly = _host.Hunting.Lethal(mirClass, world.Level);

            Dictionary<int, int> hops = _host.World.HopCounts(world.MapIndex, world.Class,
                world.Level, world.Gold, Config.TeleportGoldFloor, world.PKPoints,
                             Config.TeleportMaxGoldPercent, avoid: deadly);

            // The same question again with the paid exits removed: where could we get to without
            // spending anything? Used to price a journey honestly below, and to decide what a poor
            // bot is allowed to consider at all. One more breadth-first pass over a graph we have
            // already loaded - it is cheaper than the decision it informs.
            Dictionary<int, int> freeHops = _host.World.HopCounts(world.MapIndex, world.Class,
                world.Level, world.Gold, Config.TeleportGoldFloor, world.PKPoints,
                             Config.TeleportMaxGoldPercent, freeOnly: true, avoid: deadly);

            // Too poor to be anywhere but home.
            //
            // Poverty is the one state in which wandering is actively harmful: the bot cannot buy
            // potions, cannot buy a way back, and cannot pay a repair bill, so every problem it
            // meets is permanent. Restricting it to the named town maps is the same whitelist
            // reasoning that fixed vendor selection - a list cannot be outsmarted by the next
            // clever ranking rule.
            // SHORT OF SUPPLIES: the only acceptable destination is somewhere that sells them.
            //
            // Travel runs when a town trip ends - including when one ABORTS, which is exactly the
            // moment the bot is least ready to hunt. A scroll that silently did nothing dropped
            // Sindo out of its restock trip and the very next decision was "heading for Banya
            // Village", nine healing potions in the bag, through the corner of Bichon Town that
            // has killed three bots. It died ninety seconds later.
            //
            // So when the reason for a trip is still true, travel goes to the nearest town that
            // has vendors, and nowhere else. Affordability and danger still apply - this narrows
            // the choice, it does not force a journey the bot cannot make.
            if (_brain.Town != null &&
                (_brain.Town.ShortOfSupplies || _brain.Town.NeedsStorage) &&
                !_host.Vendors.TownMaps.Contains(world.MapIndex))
            {
                int best = -1, bestHops = int.MaxValue;

                foreach (int townMap in _host.Vendors.TownMaps)
                {
                    if (!hops.TryGetValue(townMap, out int distance)) continue;
                    if (distance >= bestHops) continue;
                    if (!Affordable(townMap, out _)) continue;

                    best = townMap;
                    bestHops = distance;
                }

                if (best > 0)
                {
                    string why = _brain.Town.ShortOfSupplies
                        ? "out of supplies - going to town, not hunting"
                        : "banked item ready - going to a safe town";
                    Begin(best, _host.Profiles.For(best)?.MapName ?? $"map {best}", why);
                    return;
                }

                _log.Write(_brain.Town.ShortOfSupplies
                    ? "Travel: out of supplies and no town reachable - staying put rather than hunting on empty."
                    : "Travel: storage work is ready but no safe town is reachable - staying put.");
                return;
            }

            bool poor = world.Gold < Config.PoorGold;

            HashSet<int> allowed = null;

            if (poor)
            {
                allowed = new HashSet<int>(_host.Vendors.TownMaps);

                if (allowed.Count == 0)
                {
                    _log.Write($"Travel: only {world.Gold} gold and no TownMaps configured - staying put.");
                    return;
                }

                // Plus anywhere we can WALK to.
                //
                // The previous comment said the deadlock shape - "cannot leave until rich, cannot
                // get rich because home is poor" - was worth logging so it would be visible if it
                // ever happened. It happened. A level 18 assassin sat between Bichon Town and Banya
                // Village for over an hour at 42k and 49k exp/hour with Ant Cave and Deserted Mine
                // two free steps away, because the whitelist is the whole of the world when you are
                // broke, and the towns are where the gold is worst.
                //
                // Being broke is a reason not to SPEND, not a reason not to move. Everything the
                // whitelist was actually protecting against is a cost: the fare out is priced by
                // HopCounts and by Affordable, both of which still apply, and the way home is a
                // town scroll the bot already carries. So the restriction is now the honest one -
                // no paid teleports while poor - and a free walk to a better hunting ground, which
                // is the only thing that ends the deadlock, is allowed.
                foreach (int mapIndex in freeHops.Keys) allowed.Add(mapIndex);

                _log.Write($"Travel: only {world.Gold} gold (poor below {Config.PoorGold}) - " +
                           $"no paid teleports, {allowed.Count} map(s) reachable on foot.");
            }

            bool Affordable(int mapIndex, out string why)
            {
                why = "";

                if (!hops.TryGetValue(mapIndex, out int distance))
                {
                    why = "no route";
                    return false;
                }

                // What a journey needs in hand depends on whether any of it is BOUGHT.
                //
                // One flat rate per hop was the whole test, and at 3,000 it is sized for a trip
                // whose legs come from a teleport NPC - fare out, fare home, and enough left over
                // to be a long way from a vendor. Applied to a walk it is nonsense: the leg costs
                // nothing, the way back is the same leg, and the bot is carrying a town scroll.
                //
                // This is where the poverty deadlock really lived. The whitelist above got the
                // blame and the log line, but even with the whitelist lifted a bot with 907 gold
                // was refused a single free step to a better hunting ground because it could not
                // afford 3,000 - so it stayed on its two worst measured maps and stayed poor.
                //
                // Note hops was already built under the gold constraints, so a poor bot's routes
                // are mostly free ones anyway; charging them the paid rate taxed a fare it was
                // never going to be asked for.
                bool onFoot = freeHops.TryGetValue(mapIndex, out int walked);

                if (onFoot) distance = walked;

                long rate = onFoot ? Config.TravelGoldPerFreeHop : Config.TravelGoldPerHop;
                long needed = distance * rate;

                // A single walked map is always affordable, because it genuinely costs nothing.
                //
                // TravelGoldPerFreeHop is a risk float - each map crossed is another to fight back
                // across - not a price, and charging any float at all recreates the deadlock this
                // whole branch exists to remove, just with a smaller number. An assassin reduced to
                // 39 gold could not afford 250 to step one map to a better hunting ground, which is
                // "cannot leave until rich, cannot get rich because home is poor" wearing its third
                // disguise tonight. One hop has a guaranteed way back: the way it came.
                if (onFoot && distance <= 1) return true;

                if (world.Gold < needed)
                {
                    why = $"{distance} {(onFoot ? "walked" : "paid")} hop(s) needs {needed:N0} " +
                          $"gold, we have {world.Gold:N0}";
                    return false;
                }

                return true;
            }

            bool Permitted(int mapIndex) => allowed == null || allowed.Contains(mapIndex);

            // BROKE: the beginner ground and nowhere else, until we can afford to leave.
            //
            // Placed above every other choice - exploration, the experience ranking, the book
            // bonus - because all of them are about earning MORE, and none of them is any use to
            // a character that cannot buy a potion. See BotConfig.RecoveryGold.
            //
            // Already standing on one? Then stay: returning without travelling is what keeps the
            // bot farming rather than walking, and the whole failure being fixed here was a bot
            // that walked instead of fighting.
            if (_recovery.Active)
            {
                bool caveReady;
                HashSet<int> recovery = RecoveryTargetMapIndexes(out caveReady);

                if (recovery.Count > 0)
                {
                    if (recovery.Contains(world.MapIndex)) return;

                    int best = -1, bestHops = int.MaxValue;

                    foreach (int mapIndex in recovery)
                    {
                        if (!hops.TryGetValue(mapIndex, out int distance)) continue;
                        if (distance >= bestHops) continue;
                        if (!Affordable(mapIndex, out _)) continue;

                        best = mapIndex;
                        bestHops = distance;
                    }

                    if (best > 0)
                    {
                        string stage = caveReady ? "supplied recovery" : "undersupplied recovery";

                        _log.Write($"Travel: recovery active at {world.Gold:N0} gold " +
                                   $"({stage}) - going to " +
                                   $"{_host.Profiles.For(best)?.MapName ?? $"map {best}"} to earn " +
                                   $"until {Config.RecoveryExitGold:N0} survives a restock.");

                        Begin(best, _host.Profiles.For(best)?.MapName ?? $"map {best}",
                              caveReady
                                  ? $"supplied but broke - money recovery until " +
                                    $"{Config.RecoveryExitGold:N0} survives a restock"
                                  : "broke and undersupplied - rebuilding on free beginner ground");
                        return;
                    }

                    // Cannot reach one. Fall through rather than stand still - anywhere we can
                    // fight beats nowhere, and the ordinary rules below still apply.
                    _log.Write($"Travel: recovery active at {world.Gold:N0} gold but no recovery map is " +
                               "reachable - falling back to the ordinary choice.");
                }
            }

            // Explore or exploit?
            //
            // The Mir 2 agents roll a flat 1-in-20 to explore, which suits hundreds of agents
            // sharing one memory. We reconsider only when a town trip ends - the warrior had four
            // decision points in five hours - so a flat roll that rare means one exploration a day.
            // Coverage-driven instead: keep exploring until enough maps have been measured to be
            // worth choosing between, then mostly exploit with a residual chance of looking further.
            int known = _host.Hunting.MeasuredCount(mirClass, world.Level);
            bool wantExplore = known < Config.ExploreUntilMapsKnown ||
                               _random.Next(100) < Config.ExploreChancePercent;

            if (wantExplore && TryExplore(world, mirClass, hops, Affordable, Permitted, known)) return;

            // Exploit: pick at RANDOM from the best few rather than always the single best.
            //
            // Always taking the top one lets one early sample decide where the bot lives forever,
            // which is precisely what happened - a freak 171,656 exp/hour window on Bichon Town
            // outranked everything for five hours and pulled the warrior back out of the only other
            // map it ever tried.
            // Skills that can only be got by killing something, that this character could learn
            // today, and has not. Empty for a character with nothing left to want - at which point
            // everything below is a no-op and the ranking is about experience again.
            HashSet<int> wantedBooks = Config.BookHuntBonusPercent > 0
                ? _host.BookDrops.Wanted(world.Class, world.Level, world.PlayerStats, world)
                : new HashSet<int>();

            List<HuntingEntry> candidates = new List<HuntingEntry>();
            List<HuntingEntry> outgrown = new List<HuntingEntry>();

            // EVERY MEASURED MAP WHEN BOOKS ARE WANTED.
            //
            // Best() ranks on experience and truncates. A merely wider shortlist still lets a weak
            // but essential book map fall off the end as soon as enough faster grounds have been
            // measured. While a drop-only skill is wanted, inspect every measured map so its source
            // cannot disappear from consideration simply because the character is already strong
            // enough to earn better experience elsewhere.
            int shortlist = wantedBooks.Count > 0
                ? int.MaxValue
                : Math.Max(1, Config.HuntingChoices);

            foreach (HuntingEntry entry in _host.Hunting.Best(mirClass, world.Level, shortlist))
            {
                if (entry.MapIndex == world.MapIndex) continue;
                if (!Permitted(entry.MapIndex)) continue;
                if (_host.Danger.TooDangerous(entry.MapIndex, world.MaxHealth)) continue;
                if (!Affordable(entry.MapIndex, out _)) continue;

                // Outgrown maps are held back rather than dropped - see the fallback below.
                //
                // BEING BROKE SUSPENDS THIS ENTIRELY. The filter exists to stop a healthy
                // character wasting its time on a tier it has left behind; it must not become a
                // trap. A bot that keeps dying in caves loses gold each time, and the cheap safe
                // ground it needs to earn its way back to potions and repairs is exactly the
                // low-level map this rule would refuse it - "cannot afford to hunt, cannot hunt
                // where it can afford to" is the same deadlock the travel pricing and the potion
                // budget each produced in their own way earlier today. PoorGold is already the
                // line the rest of the bot uses for "too broke to be choosy", so it is the line
                // here too.
                if (!poor && _host.Profiles.OutgrownBy(entry.MapIndex, world.Level,
                        Config.HuntLevelsBelow, out string outgrownWhy))
                {
                    _log.Write($"Travel: skipping {entry.MapName} - {outgrownWhy}.");
                    outgrown.Add(entry);
                    continue;
                }

                candidates.Add(entry);
            }

            // A DROP-ONLY SKILL IS A REQUIREMENT, NOT A SOFT XP PREFERENCE.
            //
            // The old bonus only moved book maps upward before RANDOMLY choosing from the top
            // three. Jill could therefore know that Bichon Cave supplied Summon Skeleton and still
            // choose Flea Cave two times out of three. Worse, once a low real sample pushed the cave
            // outside the enlarged XP shortlist, the bonus could no longer see it at all.
            //
            // If at least one measured, safe, affordable and reachable map supplies a wanted book,
            // constrain the choice to those maps until the skill is learned. Randomness remains
            // among valid book grounds, so the bot can vary its hunt without walking away from the
            // progression goal. If none qualifies, the exploration path above remains responsible
            // for finding and measuring one, and ordinary hunting remains the final fallback.
            if (wantedBooks.Count > 0 && candidates.Count > 0)
            {
                List<HuntingEntry> bookCandidates = candidates
                    .Where(x => _host.BookDrops.Supplies(x.MapIndex, wantedBooks) > 0)
                    .ToList();

                if (bookCandidates.Count > 0)
                {
                    candidates = bookCandidates;
                    _log.Write($"Travel: {candidates.Count} measured map(s) supply a wanted " +
                               "drop-only skill - restricting this hunt to those maps.");
                }

                candidates.Sort((a, b) => BookRanked(b, wantedBooks)
                                              .CompareTo(BookRanked(a, wantedBooks)));

                int keep = Math.Max(1, Config.HuntingChoices);
                if (candidates.Count > keep) candidates.RemoveRange(keep, candidates.Count - keep);
            }

            if (candidates.Count > 0)
            {
                HuntingEntry pick = candidates[_random.Next(candidates.Count)];

                int supplies = _host.BookDrops.Supplies(pick.MapIndex, wantedBooks);

                Begin(pick.MapIndex, pick.MapName,
                      $"{pick.AverageExperiencePerHour:N0} exp/hour average over " +
                      $"{pick.HoursSampled:N1}h, {pick.Deaths} death(s) - " +
                      $"chosen from {candidates.Count} candidate(s)" +
                      (supplies > 0
                          ? $"; drops {_host.BookDrops.Names(pick.MapIndex, wantedBooks)}"
                          : ""));
                return;
            }

            // Nothing worth exploiting. Explore even if the roll said otherwise.
            if (!wantExplore && TryExplore(world, mirClass, hops, Affordable, Permitted, known)) return;

            // NEVER STRIKE OUT EVERYTHING - and only once everything else has been tried.
            //
            // A filter that can empty the list has to hand the list back when it does. This sits
            // AFTER both exploration attempts on purpose: an outgrown map is better than standing
            // in town, and worse than anywhere new, so it must not pre-empt the search for
            // somewhere new the way it did when it was folded into the exploit block above.
            if (outgrown.Count > 0)
            {
                HuntingEntry fallback = outgrown[_random.Next(outgrown.Count)];

                _log.Write($"Travel: nothing else reachable, so taking {fallback.MapName} " +
                           $"despite having outgrown it - {outgrown.Count} such map(s) known.");

                Begin(fallback.MapIndex, fallback.MapName,
                      $"{fallback.AverageExperiencePerHour:N0} exp/hour average, outgrown but " +
                      "the only thing left");
                return;
            }

            _log.Write($"Travel: nowhere to go - {hops.Count} maps reachable, " +
                       $"{known} measured, {(poor ? "poor so no paid teleports, " : "")}" +
                       "none affordable, safe and unmeasured.");
        }

        /// <summary>
        /// Go somewhere we have never measured, to find out what it is worth.
        ///
        /// The database filter is what makes this safe to do often. MonsterMemory only knows maps
        /// that have already hurt us, so it is silent about anywhere new - which is exactly when the
        /// question is asked. MapProfile reads what actually spawns on a map before we go.
        /// </summary>
        /// <summary>
        /// A hunting entry's ranking score, lifted for each drop-only book the map supplies.
        ///
        /// Multiplicative so it scales with what the map is actually worth: doubling a good map
        /// beats doubling a bad one, which is the behaviour wanted. An ADDITIVE bonus would have
        /// let a worthless map outrank a good one simply by stocking a book.
        /// </summary>
        private double BookRanked(HuntingEntry entry, HashSet<int> wanted)
        {
            double score = entry.Score(HuntingMemory.DefaultDeathPenalty);
            int supplies = _host.BookDrops.Supplies(entry.MapIndex, wanted);

            if (supplies <= 0) return score;

            return score * (1 + Config.BookHuntBonusPercent / 100.0 * supplies);
        }

        private string _learningBook;
        private DateTime _learnAt;
        private int _magicsBeforeLearn;

        /// <summary>
        /// Did a book we tried to read actually teach us anything?
        ///
        /// C.ItemUse on a book answers with S.NewMagic on success and NOTHING on refusal, so the
        /// bot logged "LearnBook (learning from slot 6)" and moved on whether or not it worked.
        /// Summon Skeleton was looted six times across the fleet and learnt none of them, and the
        /// only evidence was a magic count that did not move - which nothing was comparing.
        ///
        /// The book is consumed either way as far as the bag is concerned, so a silent failure
        /// destroys the drop. That is worth a line in the log even now the cause is fixed.
        /// </summary>
        private void CheckPendingLearn(Decision decision)
        {
            if (decision != null && decision.Action == BotAction.LearnBook)
            {
                _learningBook = _connection.Items.InSlot(decision.PotionSlot)?.Info?.ItemName
                                ?? $"slot {decision.PotionSlot}";
                _magicsBeforeLearn = _connection.World.KnownMagicCount;
                _learnAt = DateTime.UtcNow;
                return;
            }

            if (_learningBook == null) return;
            if (DateTime.UtcNow - _learnAt < TimeSpan.FromSeconds(3)) return;

            string book = _learningBook;
            _learningBook = null;

            if (_connection.World.KnownMagicCount > _magicsBeforeLearn)
            {
                _log.Write($"Learned {book} - now {_connection.World.KnownMagicCount} skills.");
                return;
            }

            _log.Write($"LEARN REFUSED: {book} taught us nothing - still " +
                       $"{_magicsBeforeLearn} skills. The book is gone and the skill is not.");
        }

        private bool TryExplore(WorldModel world, string mirClass, Dictionary<int, int> hops,
            AffordableCheck affordable, Func<int, bool> permitted, int known)
        {
            HashSet<int> measured = new HashSet<int>(
                _host.Hunting.Best(mirClass, world.Level, 500).Select(x => x.MapIndex));

            // "Measured" and "been there" are not the same thing, and the difference is where a
            // map that kills us on arrival hides. Best() has nothing to say about a map we never
            // survived long enough to measure, so without this it reads as unexplored for ever.
            HashSet<int> lethal = _host.Hunting.Lethal(mirClass, world.Level);

            List<int> options = new List<int>();
            int tooStrong = 0, tooFar = 0, killers = 0, tooWeak = 0;

            foreach (int mapIndex in hops.Keys)
            {
                if (mapIndex == world.MapIndex) continue;
                if (measured.Contains(mapIndex)) continue;
                if (lethal.Contains(mapIndex)) { killers++; continue; }
                if (!permitted(mapIndex)) continue;
                if (_host.Danger.TooDangerous(mapIndex, world.MaxHealth)) continue;

                if (!_host.Profiles.WorthExploring(mapIndex, world.Level,
                        Config.ExploreLevelsAbove, out _))
                {
                    tooStrong++;
                    continue;
                }

                // And the other end of the same scale. Without this, exploration happily spends a
                // journey measuring a map the character has already outgrown, then stores a rate
                // that keeps pulling it back.
                if (_host.Profiles.OutgrownBy(mapIndex, world.Level, Config.HuntLevelsBelow, out _))
                {
                    tooWeak++;
                    continue;
                }

                if (!affordable(mapIndex, out _)) { tooFar++; continue; }

                options.Add(mapIndex);
            }

            if (options.Count == 0)
            {
                _log.Write($"Travel: nothing new worth exploring - {hops.Count} reachable, " +
                           $"{measured.Count} already measured, {killers} killed us before, " +
                           $"{tooStrong} too strong, {tooWeak} outgrown, " +
                           $"{tooFar} too far to afford.");
                return false;
            }

            // Shallowest floor of a cave first.
            //
            // The levels of a cave are nested: getting to Flea Cave Lv 3 means crossing the whole
            // of Lv 1 and Lv 2 on the way, fighting or fleeing the entire distance, and arriving
            // at the hardest floor with the least health left. Choosing a deep floor to "explore"
            // is really choosing three unmeasured maps at once, in ascending order of difficulty,
            // with no way to stop partway.
            //
            // So a floor is only a candidate when every shallower floor of the same cave has
            // already been measured. Explore Lv 1, learn what it is worth, and Lv 2 becomes
            // available on the next decision.
            int deeper = options.RemoveAll(mapIndex =>
            {
                (string cave, int depth) = MapProfile.SplitDepth(_host.Profiles.For(mapIndex)?.MapName);

                if (depth <= 1) return false;

                foreach (int other in hops.Keys)
                {
                    if (other == mapIndex || measured.Contains(other)) continue;

                    (string otherCave, int otherDepth) =
                        MapProfile.SplitDepth(_host.Profiles.For(other)?.MapName);

                    if (otherDepth > 0 && otherDepth < depth &&
                        string.Equals(otherCave, cave, StringComparison.OrdinalIgnoreCase))
                        return true;   // a shallower floor is still unmeasured - start there
                }

                return false;
            });

            // Nearer before further - measured from TOWN, not from where the bot is standing.
            //
            // Those are the same thing only while the bot is at home, and they diverge exactly when
            // it matters. Once it is three maps down a cave, the nearest unexplored map is deeper
            // still, so ranking by distance-from-here walks it steadily further from every vendor
            // it depends on. The real cost of a hunting ground is the round trip: the bot has to
            // come back for potions, repairs and to sell, over and over, and each of those journeys
            // pays the distance again.
            //
            // So the frontier is anchored to the towns and expands outward in rings from them.
            Dictionary<int, int> fromTown = TownDistances(world);

            int Distance(int mapIndex) =>
                fromTown.TryGetValue(mapIndex, out int d) ? d : hops[mapIndex];

            // A MAP THAT DROPS A SKILL WE CANNOT BUY JUMPS THE QUEUE.
            //
            // Applied before the distance ring, not after, because the whole point is to accept a
            // longer walk for something the shops cannot supply. It only ever narrows the list to
            // maps that already passed every safety, level and affordability test above, so this
            // cannot send a character somewhere it was not already willing to go.
            //
            // Nothing to want means nothing changes - the list is untouched and exploration works
            // exactly as it did.
            HashSet<int> wantedHere = Config.BookHuntBonusPercent > 0
                ? _host.BookDrops.Wanted(world.Class, world.Level, world.PlayerStats, world)
                : new HashSet<int>();

            if (wantedHere.Count > 0)
            {
                List<int> bookMaps = options
                    .Where(x => _host.BookDrops.Supplies(x, wantedHere) > 0).ToList();

                if (bookMaps.Count > 0)
                {
                    _log.Write($"Travel: {bookMaps.Count} unmeasured map(s) drop a skill book we " +
                               "cannot buy - looking there first.");
                    options = bookMaps;
                }
            }

            int nearest = options.Min(Distance);
            options.RemoveAll(x => Distance(x) > nearest);

            string why = known < Config.ExploreUntilMapsKnown
                ? $"exploring - only {known} of {Config.ExploreUntilMapsKnown} maps measured"
                : "exploring anyway";

            if (deeper > 0) why += $", {deeper} deeper floor(s) held back";

            // Named maps first. This biases which map gets measured FIRST; it does not fake a
            // measurement. Seeding invented rates would poison every real observation ranked
            // against them, and the whole point of the memory is that it is observed.
            foreach (string name in PreferredMapNames())
            {
                MapInfo info = Globals.MapInfoList?.Binding?.FirstOrDefault(x =>
                    string.Equals(x.Description, name, StringComparison.OrdinalIgnoreCase));

                if (info == null || !options.Contains(info.Index)) continue;

                Begin(info.Index, info.Description, $"preferred, {why}");
                return true;
            }

            int chosen = options[_random.Next(options.Count)];
            MapProfileEntry profile = _host.Profiles.For(chosen);

            Begin(chosen, profile?.MapName ?? $"map {chosen}",
                  $"{why} (median monster level {profile?.MedianLevel ?? 0}, " +
                  $"{hops[chosen]} hop(s) away, {Distance(chosen)} from town)");

            return true;
        }

        /// <summary>
        /// How far every map is from the nearest town, in map transitions.
        ///
        /// One breadth-first pass per town map - there are two - computed fresh per decision. The
        /// towns are where the vendors are, so this is the number that actually prices a hunting
        /// ground: not "how far is it from here" but "how far will it be from help, every time I
        /// have to restock".
        ///
        /// Falls back to an empty map when no towns are configured, and callers then use distance
        /// from the bot instead, which is the old behaviour.
        /// </summary>
        private Dictionary<int, int> TownDistances(WorldModel world)
        {
            Dictionary<int, int> best = new Dictionary<int, int>();

            foreach (int town in _host.Vendors.TownMaps)
            {
                best[town] = 0;

                foreach (KeyValuePair<int, int> pair in _host.World.HopCounts(town, world.Class,
                             world.Level, world.Gold, Config.TeleportGoldFloor, world.PKPoints,
                             Config.TeleportMaxGoldPercent))
                {
                    if (!best.TryGetValue(pair.Key, out int known) || pair.Value < known)
                        best[pair.Key] = pair.Value;
                }
            }

            return best;
        }

        /// <summary>Signature for the local affordability check, which needs an out parameter.</summary>
        private delegate bool AffordableCheck(int mapIndex, out string why);

        private readonly Random _random = new Random();

        private bool _tripWasActive;
        private DateTime _nextBarrenCheck = DateTime.MinValue;
        private DateTime _nextOutgrownCheck = DateTime.MinValue;
        private DateTime _nextUnproductiveCheck = DateTime.MinValue;
        private DateTime _nextScrollEscape = DateTime.MinValue;

        /// <summary>
        /// Since when have we earned nothing: the last experience gain, or failing that, the
        /// moment we entered the world.
        ///
        /// The first version of this required LastExperienceGainUtc to be set, which quietly
        /// excluded the worst case it was written for. A bot that has killed NOTHING since login
        /// has no gain timestamp at all, so "no experience for twelve minutes" could never become
        /// true - and a relog into a dead pocket is exactly how a bot ends up with no kills. Two
        /// bots sat in Bichon Cave reporting "last kill: never" while the check that should have
        /// moved them was structurally unable to fire.
        /// </summary>
        private DateTime UnproductiveSince()
        {
            WorldModel world = _connection?.World;
            DateTime since = _inGameAt;

            if (world == null) return since;
            if (world.MapEnteredUtc > since) since = world.MapEnteredUtc;
            if (world.LastExperienceGainUtc > since) since = world.LastExperienceGainUtc;

            return since;
        }

        /// <summary>
        /// A journey gave up. If we are carrying a town scroll, use it instead of walking.
        ///
        /// Every observed journey failure has been "stuck N tiles from the exit" - the bot boxed
        /// into part of a cave it cannot path out of. Walking is the only thing the travel system
        /// knows how to do, so it re-plans the same walk, fails the same way, and the bot goes
        /// back to roaming a pocket it has already exhausted. A level 24 Taoist did this for
        /// eighteen minutes carrying EIGHT town scrolls, planning a five-leg walk to a town one
        /// scroll would have reached instantly.
        ///
        /// The town trip is the only code that knows how to scroll, so forcing it is the escape:
        /// it scrolls to the bind point, and a bind point without vendors is already handled
        /// there by walking instead. From a town the travel graph is dense and the next journey
        /// has a real chance.
        ///
        /// Rate-limited so a run of failures cannot burn the bag.
        /// </summary>
        private void EscapeOnScroll(string why)
        {
            if (_brain?.Town == null || _connection == null) return;
            if (DateTime.UtcNow < _nextScrollEscape) return;
            if (_connection.World.MapIndex <= 0 || _connection.World.Dead) return;

            // Only when a scroll is actually carried; otherwise this is noise on every failure.
            if (_connection.Items.FindTownTeleportSlot() < 0) return;

            _nextScrollEscape = DateTime.UtcNow.AddMinutes(2);

            _log.Write($"Travel gave up ({why}) but we are carrying a town scroll - " +
                       "forcing a town trip to scroll out rather than walking it again.");

            _brain.Town.Force();
        }

        /// <summary>
        /// Look for somewhere better to hunt, but only at the end of a town trip.
        ///
        /// That is the one moment travelling is safe and sensible: the bag is empty, potions and
        /// scrolls are back at their reserves, nothing is mid-fight, and it comes round naturally
        /// every twenty minutes or so. The Mir 2 agents instead re-pick on a bare hourly clock,
        /// which can fire in the middle of anything.
        /// </summary>
        private void ConsiderTravel()
        {
            bool active = _town != null && _town.Active;

            bool tripJustFinished = _tripWasActive && !active;
            _tripWasActive = active;

            UpdateRecoveryState(tripJustFinished);

            bool recoveryRoute = !active && RecoveryRouteNeeded();

            // A storage-only trip can begin and abort in the same tick when no town scroll is in
            // the bag, so tripJustFinished never observes an active phase. Promote the published
            // storage need directly into a journey to the nearest safe town instead.
            bool storageTravel = !active &&
                                 _connection != null &&
                                 _connection.Stage == BotStage.InGame &&
                                 !_connection.World.Dead &&
                                 _town != null && _town.NeedsStorage &&
                                 (_brain?.Travel == null || !_brain.Travel.Active) &&
                                 !_host.Vendors.TownMaps.Contains(_connection.World.MapIndex);

            // Standing somewhere with nothing to kill.
            //
            // Travel routes cross maps that have no monsters on them - Sabuk Keep joins Bichon Town
            // to Banya Temple and has no respawns at all, and 49 of the server's 244 maps are like
            // it. Crossing one is fine; being LEFT on one is not. A journey that ends or is
            // abandoned partway leaves the bot roaming an empty map indefinitely, which is what one
            // warrior did for most of a session: no targets, no experience, no reason to leave,
            // because the only thing that reconsidered a hunting ground was the end of a town trip
            // and a bot with nothing to fight never fills a bag to trigger one.
            //
            // Deliberately checked here rather than in the travel planner: the planner already
            // refuses to CHOOSE such a map, and WorldGraph must stay ignorant of monsters or the
            // routes through them would vanish.
            bool barren = !active &&
                          _connection != null && _connection.Stage == BotStage.InGame &&
                          !_connection.World.Dead &&
                          _connection.World.MapIndex > 0 &&
                          !_host.Profiles.HasMonsters(_connection.World.MapIndex) &&
                          (_brain?.Travel == null || !_brain.Travel.Active) &&
                          DateTime.UtcNow >= _nextBarrenCheck;

            // STANDING SOMEWHERE WE HAVE OUTGROWN. The sibling of the barren check above, and
            // needed for the same reason: the end of a town trip was the ONLY thing that ever
            // reconsidered a hunting ground.
            //
            // The level filter added to the travel planner is consulted when the bot CHOOSES a
            // map. It has nothing to say about the map the bot is already standing on, and a bot
            // arrives on one without choosing it constantly - a trip walks it to town, a journey
            // is abandoned partway, the host restarts. A level 27 warrior sat in Bichon Town
            // killing scarecrows for as long as it was left there, because it had no trip to
            // finish and killing scarecrows never fills a bag fast enough to start one.
            //
            // Not while poor, for the same reason the planner's filter lifts then: a cheap safe
            // map is where a broke character rebuilds, and chasing it off one is the deadlock.
            // Declared up here because the call below sits inside a short-circuiting && chain,
            // where the compiler cannot prove the out parameter was ever reached.
            string outgrownHereWhy = "";

            bool outgrownHere = !active &&
                                _connection != null && _connection.Stage == BotStage.InGame &&
                                !_connection.World.Dead &&
                                _connection.World.MapIndex > 0 &&
                                _connection.World.Gold >= Config.PoorGold &&
                                (_brain?.Travel == null || !_brain.Travel.Active) &&
                                DateTime.UtcNow >= _nextOutgrownCheck &&
                                _host.Profiles.OutgrownBy(_connection.World.MapIndex,
                                    _connection.World.Level, Config.HuntLevelsBelow,
                                    out outgrownHereWhy);

            // EARNING NOTHING HERE. See BotConfig.UnproductiveMinutes.
            //
            // The last of the four "should I still be here?" checks, and the general one: barren
            // asks whether the map has spawns, outgrown asks whether they are worth killing, and
            // this asks whether any of it is actually happening. A map can pass both of the others
            // and still yield nothing - walled into a pocket, every reachable monster already
            // dead, a spawn region we cannot path to.
            bool unproductive = !active &&
                                Config.UnproductiveMinutes > 0 &&
                                _connection != null && _connection.Stage == BotStage.InGame &&
                                !_connection.World.Dead &&
                                _connection.World.MapIndex > 0 &&
                                (_brain?.Travel == null || !_brain.Travel.Active) &&
                                DateTime.UtcNow >= _nextUnproductiveCheck &&
                                UnproductiveSince() != DateTime.MinValue &&
                                DateTime.UtcNow - UnproductiveSince() >
                                    TimeSpan.FromMinutes(Config.UnproductiveMinutes);

            if (!tripJustFinished && !barren && !outgrownHere && !storageTravel &&
                !unproductive && !recoveryRoute)
                return;

            if (unproductive)
            {
                // Rate-limited like its siblings: if there is nowhere better we must not ask again
                // every tick for the rest of the session.
                _nextUnproductiveCheck = DateTime.UtcNow.AddMinutes(
                    Math.Max(2, Config.UnproductiveMinutes / 2));

                int minutes = (int)(DateTime.UtcNow - UnproductiveSince()).TotalMinutes;

                _log.Write($"No experience at all on {_connection.World.MapName} for {minutes} " +
                           "minutes - looking for somewhere that actually pays.");
            }

            if (barren)
            {
                // Rate-limited: a bot that cannot find anywhere to go must not retry every tick.
                _nextBarrenCheck = DateTime.UtcNow.AddMinutes(2);

                _log.Write($"Nothing spawns on {_connection.World.MapName} - looking for somewhere " +
                           "to hunt.");
            }

            if (outgrownHere)
            {
                // Same rate limit, same reason: if there is nowhere better we must not ask again
                // every tick for the rest of the session.
                _nextOutgrownCheck = DateTime.UtcNow.AddMinutes(2);

                _log.Write($"Hunting {_connection.World.MapName} but {outgrownHereWhy} - " +
                           "looking for somewhere better.");
            }
            if (_connection == null || _connection.Stage != BotStage.InGame) return;
            if (_connection.World.Dead) return;

            if (storageTravel)
            {
                StartTravel();
                return;
            }

            // A journey already running wins. A town trip can pre-empt one mid-way, so the map the
            // trip started from may be a stopover rather than a hunting ground, and starting a
            // competing return would abandon the real destination.
            if (_brain?.Travel != null && _brain.Travel.Active)
            {
                _town?.ClearReturn();
                return;
            }

            // The trip finished somewhere other than where it started - it scrolled to a town to
            // shop. Walking back is meaningless across a map boundary, so the trip hands the map
            // over and the journey does the work.
            int returnTo = _town?.ReturnToMap ?? -1;

            if (returnTo >= 0)
            {
                _town.ClearReturn();

                // With AutoTravel on, the ordinary end-of-trip choice runs instead: the map just
                // left is measured and can win on merit, and something better may have been learned
                // in the meantime. Only when that choice is switched off does the bot need telling
                // explicitly, and then the alternative is being stranded in town.
                if (!Config.AutoTravel)
                {
                    MapInfo back = Globals.MapInfoList?.Binding?
                        .FirstOrDefault(x => x.Index == returnTo);

                    Begin(returnTo, back?.Description ?? $"map {returnTo}",
                          "returning to the hunting ground");
                    return;
                }
            }

            // AutoTravel off means "the operator picks the hunting ground", not "strand the bot on
            // an empty map". Leaving one is recovery from a stuck state, so it happens regardless.
            if (!Config.AutoTravel && !barren) return;

            StartTravel();
        }

        private void UpdateRecoveryState(bool tripJustFinished)
        {
            if (_connection == null || _connection.Stage != BotStage.InGame) return;

            bool before = _recovery.Active;
            bool active = _recovery.Update(_connection.World.Gold, Config.RecoveryGold,
                Config.RecoveryExitGold, tripJustFinished, _town?.ShortOfSupplies ?? false);

            _recovery.UpdateMoneyCave(RecoveryCaveReady(), tripJustFinished);

            if (before == active) return;

            if (active)
                _log.Write($"Recovery: entered at {_connection.World.Gold:N0} gold; staying in " +
                           $"recovery until a stocked town trip leaves {Config.RecoveryExitGold:N0}.");
            else
                _log.Write($"Recovery: complete with {_connection.World.Gold:N0} gold after " +
                           "restocking; ordinary hunting restored.");
        }

        private bool RecoveryCaveReady()
        {
            if (!_recovery.Active || _connection == null || _brain == null) return false;

            WorldModel world = _connection.World;
            Backpack items = _connection.Items;
            bool spendsMana = _brain.Spells.UsesMana(world) || _brain.Skills.NeedsMana(world);

            return RecoveryPolicy.SuppliesReady(world.Level, Config.RecoveryCaveMinimumLevel,
                Config.RecoveryCaveSupplyPercent,
                items.HealthPotionLoad(), Config.HealthPotionTarget(world.MaxBagWeight),
                spendsMana, items.ManaPotionLoad(), Config.ManaPotionTarget(world.MaxBagWeight),
                items.CountTownScrolls(), Config.TownScrollReserve);
        }

        private bool RecoveryRouteNeeded()
        {
            if (!_recovery.Active || _connection == null) return false;

            HashSet<int> wanted = RecoveryTargetMapIndexes(out _);

            return wanted.Count > 0 && !wanted.Contains(_connection.World.MapIndex);
        }

        private HashSet<int> RecoveryTargetMapIndexes(out bool caveStage)
        {
            caveStage = _recovery.MoneyCaveActive;
            HashSet<int> wanted = RecoveryMapIndexes(caveStage
                ? Config.RecoveryCaveMaps
                : Config.RecoveryMaps);

            // A typo or removed map in the optional cave list must not disable the proven
            // beginner-ground fallback.
            if (wanted.Count == 0 && caveStage)
            {
                caveStage = false;
                wanted = RecoveryMapIndexes(Config.RecoveryMaps);
            }

            return wanted;
        }

        /// <summary>Configured map names resolved against the game's map list.</summary>
        private HashSet<int> RecoveryMapIndexes(string configured)
        {
            HashSet<int> found = new HashSet<int>();

            if (string.IsNullOrWhiteSpace(configured)) return found;

            foreach (string part in configured.Split(','))
            {
                string wanted = part.Trim();
                if (wanted.Length == 0) continue;

                foreach (Library.SystemModels.MapInfo info in
                         Library.Globals.MapInfoList?.Binding
                         ?? System.Linq.Enumerable.Empty<Library.SystemModels.MapInfo>())
                    if (string.Equals(info.Description, wanted, StringComparison.OrdinalIgnoreCase))
                        found.Add(info.Index);
            }

            return found;
        }

        /// <summary>Map names from config, in the order they were written.</summary>
        private IEnumerable<string> PreferredMapNames()
        {
            if (string.IsNullOrWhiteSpace(Config.PreferredMaps)) yield break;

            foreach (string part in Config.PreferredMaps.Split(','))
            {
                string name = part.Trim();
                if (name.Length > 0) yield return name;
            }
        }

        private void Begin(int mapIndex, string mapName, string why)
        {
            // Refreshed per journey rather than held, because the set is a function of our level
            // and of what has happened since - both of which move.
            _brain.Travel.Avoid = _host.Hunting.Lethal(_connection.World.Class.ToString(),
                _connection.World.Level);
            _brain.Travel.Detour = "";

            if (_brain.Travel.Begin(_connection.World, mapIndex, mapName))
            {
                if (!string.IsNullOrEmpty(_brain.Travel.Detour))
                    _log.Write($"Travel: {_brain.Travel.Detour}");

                _log.Write($"Travel: heading for {mapName} ({why}). {_brain.Travel.Status}");
                _history.Note("Travel", mapName);
            }
            else
            {
                _log.Write($"Travel: {_brain.Travel.Status}");
            }
        }

        private string _lastAttacker;
        private DateTime _reviveAt = DateTime.MinValue;
        private bool _tripAfterRevive;

        /// <summary>
        /// Notice a death, exactly once, and act on it.
        ///
        /// This used to live inside RunBrain, after the decision had been acted on - where it could
        /// never fire. Decide returns Idle for a dead character and RunBrain returns early on Idle,
        /// so every line of the post-mortem was unreachable from the moment the bot actually died.
        /// It belongs on the tick, not on the decision.
        /// </summary>
        private void WatchForDeath()
        {
            if (_connection == null || _connection.Stage != BotStage.InGame) return;

            if (!_connection.World.Dead)
            {
                // Back on our feet: go and sort ourselves out before hunting again.
                //
                // Dying costs gear durability, drops part of the bag on the floor and leaves the
                // potion stock wherever it happened to be - and none of that trips the ordinary
                // trip triggers, which watch bag weight and potion counts. An assassin revived at
                // 74% bag, below the 90% weight trigger, and went straight back to the same cave
                // that had just killed it with damaged gear and whatever potions were left.
                //
                // A death is the one event that reliably invalidates all three assumptions at
                // once, so it earns a trip on its own account.
                if (_wasDead && _tripAfterRevive)
                {
                    _tripAfterRevive = false;

                    if (_brain?.Town != null)
                    {
                        _log.Write("Died - going to town to sell, repair and restock before " +
                                   "hunting again.");
                        _brain.Town.Force();
                    }
                }

                _wasDead = false;
                _reviveAt = DateTime.MinValue;
                return;
            }

            if (!_wasDead)
            {
                PostMortem();
                _tripAfterRevive = true;
            }

            AutoRevive();
        }

        /// <summary>
        /// What killed us, where, and at what level.
        ///
        /// The danger model previously learned only from damage TAKEN. That misses the case that
        /// matters most: a monster that chips a third of our health twenty times is recorded as
        /// merely unpleasant, and the one that finished the job is recorded identically. A death
        /// already accounts for everything the damage numbers leave out - adds, poison, being
        /// cornered - so it is recorded separately, with the place, and weighted harder.
        /// </summary>
        private void PostMortem()
        {
            WorldModel world = _connection.World;

            string killer = string.IsNullOrEmpty(_lastAttacker) ? "something unseen" : _lastAttacker;

            _wasDead = true;
            _history.Note("Died", $"killed by {killer} on {world.MapName} at {world.Location.X},{world.Location.Y}");

            _log.Write($"DIED: level {world.Level} {world.Class} killed by {killer} on " +
                       $"{world.MapName} ({world.MapIndex}) at {world.Location.X},{world.Location.Y}, " +
                       $"bag {world.WeightPercent}%, {world.Gold} gold.");

            if (!string.IsNullOrEmpty(_lastAttacker))
                _host.Danger.RecordKill(_lastAttacker, world.MapName, world.Location, world.Level);

            _host.Hunting.RecordDeath(world.MapIndex, world.MapName,
                world.Class.ToString(), world.Level);

            // The count above ranks the map; this remembers the event. Which monster, at what
            // level, carrying how much - the questions a bare count cannot answer.
            _host.Deaths.Record(new DeathEntry
            {
                Bot = Id,
                Character = world.Name,
                Class = world.Class.ToString(),
                Level = world.Level,
                MapIndex = world.MapIndex,
                MapName = world.MapName,
                Killer = killer,
                Gold = world.Gold.ToString(),
                X = world.Location.X,
                Y = world.Location.Y,
                Utc = DateTime.UtcNow
            });

            // The window is void: time spent dead and running back is not hunting.
            ResetExperienceSample();

            // Whatever we were travelling to, we are no longer on our way there.
            if (_brain?.Travel != null && _brain.Travel.Active)
                _brain.Travel.Abort("died on the way");

            // Revive on our own after a pause rather than instantly: whatever killed us is still
            // standing over the corpse, and the pause keeps a death loop legible in the log as a
            // rhythm rather than a flood.
            _reviveAt = Config.ReviveAfterSeconds > 0
                ? DateTime.UtcNow.AddSeconds(Config.ReviveAfterSeconds)
                : DateTime.MinValue;
        }

        /// <summary>
        /// Get back up without being asked. The whole point of an overnight run is that nobody is
        /// watching, and a dead bot earns nothing for eight hours.
        /// </summary>
        private void AutoRevive()
        {
            if (_reviveAt == DateTime.MinValue || DateTime.UtcNow < _reviveAt) return;

            _log.Write("Auto-revive.");
            _history.Note("Revive", "automatic");
            _connection.Revive();

            // Rearmed rather than cleared. The request can be refused or lost, and a bot that
            // asked once and then waited forever is the failure this exists to prevent.
            _reviveAt = DateTime.UtcNow.AddSeconds(Math.Max(5, Config.ReviveAfterSeconds));
        }

        // The open experience-per-hour window: which map and character it belongs to, when it
        // started, and the running total at that moment.
        private int _sampleMapIndex = -1;
        private string _sampleMapName = "";
        private int _sampleLevel;
        private DateTime _sampleStarted = DateTime.MinValue;
        private decimal _sampleStartExperience;

        private void ResetExperienceSample()
        {
            _sampleMapIndex = _connection?.World.MapIndex ?? -1;
            _sampleMapName = _connection?.World.MapName ?? "";
            _sampleLevel = _connection?.World.Level ?? 0;
            _sampleStarted = DateTime.UtcNow;
            _sampleStartExperience = _connection?.World.TotalExperienceGained ?? 0m;
        }

        /// <summary>
        /// Write down what the open window measured, if it ran long enough to mean anything.
        ///
        /// Called when the window fills AND when it is cut short by leaving the map or levelling.
        /// That second case used to discard everything: a warrior hunted Deserted Mine for thirteen
        /// minutes twenty-one seconds, was pulled away 99 seconds before the fifteen-minute window
        /// closed, and the map was left with no measurement at all - so the only figure it could be
        /// judged against was the one it never earned.
        ///
        /// Shortening the window would have been the obvious fix and the wrong one. Short windows
        /// are noisier, and while the ranking kept the BEST sample, more windows simply meant more
        /// chances to roll a freak high one - the very thing that anchored Bichon Town. Closing the
        /// window on departure captures the same thirteen minutes at full fidelity and adds no
        /// noise at all.
        ///
        /// Recorded against the map and level the window STARTED on, which is why both are held:
        /// by the time this runs, the world model has usually already moved on.
        /// </summary>
        private void CloseExperienceSample(string why)
        {
            if (_sampleStarted == DateTime.MinValue || _sampleMapIndex <= 0) return;
            if (_connection == null) return;

            TimeSpan elapsed = DateTime.UtcNow - _sampleStarted;

            if (elapsed < TimeSpan.FromMinutes(Math.Max(1, Config.MinimumSampleMinutes))) return;

            decimal gained = _connection.World.TotalExperienceGained - _sampleStartExperience;
            double hours = elapsed.TotalHours;

            if (gained <= 0 || hours <= 0) return;

            double rate = (double)gained / hours;

            _host.Hunting.Record(_sampleMapIndex, _sampleMapName, _connection.World.Class.ToString(),
                _sampleLevel, rate, hours);

            _log.Write($"Hunting: {_sampleMapName} at level {_sampleLevel} = {rate:N0} exp/hour " +
                       $"over {elapsed.TotalMinutes:N0} min ({why}).");
        }

        /// <summary>
        /// Measure what this map is actually worth, in experience per hour, for this class at this
        /// level. Nothing in System.db can answer that: it knows what each monster is worth, not
        /// what an hour of trying to kill them yields once travel, town trips and dying are counted.
        ///
        /// A window is closed and recorded when it is old enough, and abandoned without recording
        /// whenever the map or the level changes under it - a rate that straddles two maps describes
        /// neither, and one that straddles a level-up is not comparable with the rest.
        /// </summary>
        private void SampleExperience()
        {
            WorldModel world = _connection.World;

            if (world.MapIndex == 0 || world.SelfID == 0) return;

            if (_sampleStarted == DateTime.MinValue)
            {
                ResetExperienceSample();
                return;
            }

            // Cut short rather than completed - but the data is still real, so it is banked before
            // the window is restarted rather than thrown away.
            if (_sampleMapIndex != world.MapIndex)
            {
                CloseExperienceSample("left the map");
                ResetExperienceSample();
                return;
            }

            // A level-up ends the window too, because a rate that straddles two level bands
            // describes neither. What it measured up to that point is still good.
            if (_sampleLevel != world.Level)
            {
                CloseExperienceSample("levelled up");
                ResetExperienceSample();
                return;
            }

            if (DateTime.UtcNow - _sampleStarted <
                TimeSpan.FromMinutes(Math.Max(1, Config.ExperienceSampleMinutes)))
                return;

            CloseExperienceSample("window complete");
            ResetExperienceSample();
        }

        private void Backoff(string why)
        {
            LastError = why;

            // Full jitter, and a ceiling deliberately longer than the server's 5-minute IP ban so a
            // banned fleet waits it out rather than hammering.
            double seconds = Math.Min(_host.MaxBackoffSeconds, 10 * Math.Pow(2, _attempts - 1));
            seconds *= 0.5 + Random.Shared.NextDouble() * 0.5;

            // Never retry before the server said it would let us in. The relog cooldown comes back
            // as a Duration on S.StartGame, and a retry sent inside it is refused identically,
            // burning an attempt and doubling the wait for no reason.
            if (_minimumRetryWait > TimeSpan.Zero)
            {
                seconds = Math.Max(seconds, _minimumRetryWait.TotalSeconds + 2);
                _minimumRetryWait = TimeSpan.Zero;
            }

            _retryAt = DateTime.UtcNow.AddSeconds(seconds);
            _state = BotRunState.Backoff;

            _log.Write($"{why} - retrying in {(int)seconds}s.");
        }

        /// <summary>A wait the server itself asked for, applied to the next backoff.</summary>
        private TimeSpan _minimumRetryWait = TimeSpan.Zero;

        /// <summary>When each recent wholesale sell refusal happened. See ResyncAfterSellRefusals.</summary>
        private readonly List<DateTime> _divergenceSignals = new List<DateTime>();

        /// <summary>
        /// A sell order was voided wholesale - the strongest available signal that our inventory
        /// model no longer matches the server's. Enough of them and we relog to resynchronise.
        ///
        /// Blunt on purpose. There is no packet that asks the server for the current inventory,
        /// so a login is the only thing that rebuilds the model from the truth. It costs a few
        /// seconds of downtime against a bot that would otherwise keep failing every sale, keep
        /// mis-addressing every slot, and keep the divergence for the rest of the session.
        /// </summary>
        private void NoteDivergence()
        {
            if (Config.ResyncAfterSellRefusals <= 0) return;

            DateTime now = DateTime.UtcNow;
            TimeSpan window = TimeSpan.FromMinutes(Math.Max(1, Config.ResyncWindowMinutes));

            _divergenceSignals.Add(now);
            _divergenceSignals.RemoveAll(x => now - x > window);

            if (_divergenceSignals.Count < Config.ResyncAfterSellRefusals) return;

            _divergenceSignals.Clear();

            _log.Write($"Inventory looks out of step with the server - {Config.ResyncAfterSellRefusals} " +
                       $"sell orders voided within {Config.ResyncWindowMinutes} minutes. " +
                       "Relogging to rebuild the model from the server's own copy.");

            try { _connection?.TryDisconnect(); } catch { }
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

        private DateTime _nextGoldSample = DateTime.MinValue;
        private long _lastGoldSampled = long.MinValue;

        /// <summary>
        /// One point on the gold curve, on the ordinary cadence or whenever the balance jumps.
        ///
        /// Called from the bot thread BEFORE the snapshot's staleness check, deliberately: a bot
        /// that has wedged still has gold worth plotting, and hanging this off snapshot rebuilds
        /// would stop sampling at the moment the chart became interesting.
        /// </summary>
        private void SampleGold()
        {
            if (_connection == null || _connection.World.SelfID == 0) return;

            long gold = _connection.World.Gold;
            DateTime now = DateTime.UtcNow;

            bool due = now >= _nextGoldSample;
            bool jumped = _lastGoldSampled != long.MinValue &&
                          Math.Abs(gold - _lastGoldSampled) >= GoldLog.NotableChange;

            if (!due && !jumped) return;

            _nextGoldSample = now + GoldLog.SampleInterval;
            _lastGoldSampled = gold;
            _host.Gold.Append(Id, gold, now);
        }

        /// <summary>How long a snapshot may sit unrebuilt while the world is not moving.</summary>
        private static readonly TimeSpan StaleSnapshotAfter = TimeSpan.FromSeconds(1);

        private DateTime _lastSnapshotAt = DateTime.MinValue;

        private void PublishIfDue()
        {
            SampleGold();

            DateTime now = DateTime.UtcNow;
            if (now < _nextSnapshot) return;

            // Skip the rebuild when nothing has changed AND nothing time-based is going stale.
            //
            // The version check alone was a trap. It suppresses the rebuild whenever WorldModel
            // stops mutating - which is precisely what a wedged bot looks like. Anything derived
            // from the clock rather than from world state (uptime, seconds since the last
            // experience gain, the ten-minute stuck threshold) would therefore freeze at the exact
            // moment it became worth reading, and the status dot would show green on a bot that
            // had done nothing for an hour.
            //
            // So: rebuild on change, or once a second regardless. The version check survives as
            // what it always really was - a way to avoid rebuilding faster than the page polls,
            // not a reason to stop rebuilding at all.
            long worldVersion = _connection?.World.Version ?? -1;

            bool changed = worldVersion != _snapshotWorldVersion ||
                           _history.Version != _snapshotHistoryVersion;

            bool stale = _status == null || now - _lastSnapshotAt >= StaleSnapshotAfter;

            _nextSnapshot = now.AddSeconds(1);   // the page polls at 1Hz
            if (!changed && !stale) return;

            _lastSnapshotAt = now;

            _snapshotWorldVersion = worldVersion;
            _snapshotHistoryVersion = _history.Version;

            System.Threading.Volatile.Write(ref _status, Build());
        }

        /// <summary>
        /// What the bot is busy with, in one word, for the status dot.
        ///
        /// Computed here because only the bot thread may look at TownTrip and Journey. Order
        /// matters: a town trip that is itself travelling between maps still counts as town, since
        /// the errand is the reason for the journey.
        /// </summary>
        /// <summary>What happened to the last config change, for the snapshot.</summary>
        private string _configResult = "";

        /// <summary>
        /// Change one setting, on the bot thread, then make it stick.
        ///
        /// Three steps, and skipping any of them leaves a setting that half works:
        ///   1. apply it to the shared BotConfig, which most code reads every tick;
        ///   2. re-push the values that were COPIED elsewhere at connect time, or the edit is
        ///      invisible until the next reconnect;
        ///   3. write it back to the ini, or it is forgotten at the next restart.
        ///
        /// The value has already been validated against ConfigSchema in the HTTP endpoint - this
        /// re-checks anyway, because the queue is reachable from anywhere in the process and a
        /// method that trusts its caller is a method that will eventually be called by someone
        /// else.
        /// </summary>
        private void ApplyConfigChange(string argument)
        {
            int split = argument?.IndexOf('=') ?? -1;

            if (split <= 0)
            {
                _configResult = "malformed config change";
                _log.Write("Config: malformed change request.");
                return;
            }

            string key = argument.Substring(0, split).Trim();
            string value = argument.Substring(split + 1).Trim();

            if (!ConfigSchema.Validate(key, value, out string why))
            {
                _configResult = why;
                _log.Write($"Config: refused {key}={value} - {why}");
                return;
            }

            if (!BotConfig.Apply(Config, key, value, out string error))
            {
                _configResult = error ?? $"{key} is not a known setting";
                _log.Write($"Config: could not apply {key}={value} - {_configResult}");
                return;
            }

            ReapplyConfig();

            string saved = PersistConfig(key, value);

            _configResult = $"{key} = {value}" + (saved == null ? "" : $" ({saved})");
            _history.Note("Config", $"{key} = {value}");
            _log.Write($"Config: {key} = {value}." + (saved == null ? " Saved to the ini." : $" {saved}"));
        }

        /// <summary>
        /// Push the settings that other objects took COPIES of at connect time.
        ///
        /// Journey snapshots three of them and ScriptedBrain's loot rule five more. Without this an
        /// edit to, say, TeleportMaxGoldPercent would sit in BotConfig looking correct while the
        /// Journey that actually decides fares carried on using the value it read at login.
        /// </summary>
        private void ReapplyConfig()
        {
            if (_brain?.Travel != null)
            {
                _brain.Travel.TalkRange = Config.VendorTalkRange;
                _brain.Travel.GoldFloor = Config.TeleportGoldFloor;
                _brain.Travel.MaxGoldPercent = Config.TeleportMaxGoldPercent;
            }

            _brain?.ReapplyLootRule();
        }

        /// <summary>
        /// Write one key back to the ini, preserving everything else in it.
        ///
        /// Rewritten rather than appended: the file is hand-edited and full of comments explaining
        /// why each number is what it is, and losing those would be losing most of the value of the
        /// file. Written beside and renamed, so an interrupted save cannot leave a bot with a
        /// truncated config it will refuse to start from.
        ///
        /// Returns null on success, or a human-readable reason. Never throws: a setting that
        /// applied but could not be saved is still better than a dead bot thread.
        /// </summary>
        private string PersistConfig(string key, string value)
        {
            string path = Config.SourcePath;

            if (string.IsNullOrWhiteSpace(path)) return "not saved - no ini path known";

            try
            {
                List<string> lines = new List<string>(File.ReadAllLines(path));
                bool replaced = false;

                for (int i = 0; i < lines.Count; i++)
                {
                    string line = lines[i];
                    string trimmed = line.TrimStart();

                    if (trimmed.Length == 0 || trimmed.StartsWith("#") || trimmed.StartsWith(";") ||
                        trimmed.StartsWith("["))
                        continue;

                    int split = line.IndexOf('=');
                    if (split <= 0) continue;

                    if (!string.Equals(line.Substring(0, split).Trim(), key,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    lines[i] = $"{key}={value}";
                    replaced = true;
                    break;
                }

                if (!replaced) lines.Add($"{key}={value}");

                string temporary = path + ".tmp";
                File.WriteAllLines(temporary, lines);

                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);

                return null;
            }
            catch (Exception ex)
            {
                return $"not saved - {ex.Message}";
            }
        }

        /// <summary>The editable settings with their current values, for the snapshot.</summary>
        private List<ConfigField> ConfigView()
        {
            List<ConfigField> view = new List<ConfigField>();

            foreach (ConfigField field in ConfigSchema.All)
                view.Add(field with { Value = ConfigValueOf(field.Key) });

            return view;
        }

        private string ConfigValueOf(string key)
        {
            System.Reflection.FieldInfo info = typeof(BotConfig).GetField(key,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.IgnoreCase);

            object value = info?.GetValue(Config);

            return value is bool flag ? (flag ? "true" : "false") : value?.ToString() ?? "";
        }

        private string DescribeActivity()
        {
            if (_state != BotRunState.Playing) return "offline";
            if (_town != null && _town.Active) return "town";
            if (_brain?.Travel != null && _brain.Travel.Active) return "travel";

            return "hunting";
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
                    History = BuildHistory(),

                    // Settings belong to the ini, not to the game session. Leaving them out while
                    // a bot was offline meant the host-wide view simply could not see that bot's
                    // values - so two bots could disagree about a setting and nothing would say
                    // so, because one of them happened to be reconnecting at the time.
                    Config = ConfigView(),
                    ConfigResult = _configResult
                };

            WorldModel world = _connection.World;
            Backpack items = _connection.Items;

            // The live step first, the wander target second. A town errand or a travel leg sets
            // Destination on the decision; ordinary roaming does not, and only the brain knows
            // where it drifted off to.
            System.Drawing.Point destination = _lastDecision != null && _lastDecision.Destination != System.Drawing.Point.Empty
                ? _lastDecision.Destination
                : _brain?.RoamTarget ?? System.Drawing.Point.Empty;

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
                    Backpack.Displayed(item.CurrentDurability),
                    Backpack.Displayed(item.MaxDurability),
                    Backpack.IsBroken(item),
                    Backpack.IsWorn(item, Config.RepairAtDurability))
                {
                    // Slot -1: a worn item has no inventory slot, and pretending otherwise would
                    // put "slot 3" on a tooltip for something in the helmet position.
                    Item = DescribeItem(-1, item)
                });
            }

            List<ItemStatus> inventory = new List<ItemStatus>();

            foreach (KeyValuePair<int, ClientUserItem> pair in items.Carried)
                inventory.Add(DescribeItem(pair.Key, pair.Value));

            inventory.Sort((a, b) => a.Slot.CompareTo(b.Slot));

            // Storage was invisible everywhere - status page, API, logs - which is part of why
            // banking being completely broken went unnoticed for so long. What the bot deposits
            // should be as readable as what it carries.
            List<ItemStatus> storage = new List<ItemStatus>();

            foreach (KeyValuePair<int, ClientUserItem> pair in items.Stored)
                storage.Add(DescribeItem(pair.Key, pair.Value));

            // Parts live in their own grid; shown with the server's own offset so the two cannot
            // be confused and a part that failed to bank is obvious at a glance.
            foreach (KeyValuePair<int, ClientUserItem> pair in items.PartsStored)
                storage.Add(DescribeItem(Globals.PartsStorageOffset + pair.Key, pair.Value));

            storage.Sort((a, b) => a.Slot.CompareTo(b.Slot));

            ExplorationSnapshot exploration = _brain?.ExplorationStatus()
                ?? new ExplorationSnapshot(0, 0);

            return new BotStatus
            {
                Id = Id,
                State = _state.ToString(),
                CurrentAction = _lastDecision?.Action.ToString() ?? "",
                CurrentSubject = _lastDecision?.Subject ?? "",
                CurrentDetail = _lastDecision?.Reason ?? "",
                ExitReason = _connection.ExitReason,
                LastError = LastError,

                CharacterName = world.Name,
                Class = world.Class.ToString(),
                Level = world.Level,
                Dead = world.Dead,

                Health = world.Health,
                MaxHealth = world.MaxHealth,
                HealthPercent = world.HealthPercent,
                Mana = world.Mana,
                MaxMana = world.MaxMana,
                ManaPercent = world.ManaPercent,

                Experience = world.Experience.ToString("0"),
                MaxExperience = world.MaxExperience.ToString("0"),
                ExperiencePercent = percent,
                AtMaxLevel = world.AtMaxLevel,
                Gold = world.Gold.ToString(),

                MapIndex = world.MapIndex,
                MapName = world.MapName,
                X = world.Location.X,
                Y = world.Location.Y,
                InSafeZone = world.InSafeZone,
                DestX = destination == System.Drawing.Point.Empty ? (int?)null : destination.X,
                DestY = destination == System.Drawing.Point.Empty ? (int?)null : destination.Y,
                ExplorationMode = _brain?.ExplorationMode ?? "none",
                ExplorationVisitedSectors = exploration.VisitedSectors,
                ExplorationTotalSectors = exploration.TotalSectors,
                ExplorationTargetLastVisitedUtc = _brain?.ExplorationTargetLastVisitedUtc
                    ?.ToString("o"),

                BagWeight = world.BagWeight,
                MaxBagWeight = world.MaxBagWeight,
                BagPercent = world.WeightPercent,

                TripPhase = _town?.Phase.ToString() ?? "",
                TripStatus = _town?.Status ?? "",
                TripSequence = _town?.TripSequence ?? 0,
                Activity = DescribeActivity(),
                SecondsSinceGain = world.LastExperienceGainUtc == DateTime.MinValue
                    ? (int?)null
                    : (int)(DateTime.UtcNow - world.LastExperienceGainUtc).TotalSeconds,
                SecondsInGame = _inGameAt == DateTime.MinValue
                    ? (int?)null
                    : (int)(DateTime.UtcNow - _inGameAt).TotalSeconds,
                SellRefusals = _connection.SellRefusals,
                Config = ConfigView(),
                ConfigResult = _configResult,

                UptimeSeconds = (int)(DateTime.UtcNow - _startedAt).TotalSeconds,
                Decisions = _decisions,
                Resyncs = _connection.ResyncCount,
                Detours = _brain?.DetoursTaken ?? 0,
                DroppedPackets = _connection.DroppedPackets,
                KnownMagics = world.KnownMagicCount,

                Casts = _brain?.Spells.CastsIssued ?? 0,
                FightsAbandoned = _brain?.FightsAbandoned ?? 0,
                DangerAvoided = _brain?.AvoidedDangerous ?? 0,
                LearnedBlockedCells = _brain?.LearnedBlockedCells ?? 0,
                DoorwaysCleared = _brain?.DoorwaysCleared ?? 0,
                BankStatus = _town?.BankDiagnostic ?? "",

                SellDiagnostic = _town?.SellDiagnostic ?? "",
                WeightDiagnostic = _town?.WeightDiagnostic ?? "",
                ReagentDiagnostic = _town?.ReagentDiagnostic ?? "",
                BookDiagnostic = _town?.BookDiagnostic ?? "",
                GearDiagnostic = _town?.GearDiagnostic ?? "",
                SupplyDiagnostic = _town?.SupplyDiagnostic ?? "",
                RepairDiagnostic = _town?.RepairDiagnostic ?? "",

                Equipment = equipment,
                Inventory = inventory,
                Storage = storage,
                History = BuildHistory()
            };
        }

        /// <summary>
        /// Flatten one item for the snapshot.
        ///
        /// Every collection is COPIED here, on the bot thread. ItemInfo.Stats belongs to the shared
        /// game database and ClientUserItem.AddedStats is rewritten in place whenever the server
        /// re-sends the item, so handing either to the web thread would be the "collection was
        /// modified" crash the rule at the top of BotStatus.cs exists to prevent.
        /// </summary>
        private static ItemStatus DescribeItem(int slot, ClientUserItem item)
        {
            ItemInfo info = item.Info;

            // An item PART carries its own near-useless ItemInfo - every one of them is called
            // "[Part]" and shares one generic icon, so a storage grid full of them is four
            // identical cells that say nothing. The real identity is on the instance, in
            // AddedStats[Stat.ItemIndex], and Backpack already knows how to follow it.
            //
            // Name and image come from the target; weight, price, durability and flags stay the
            // PART's own, because those are facts about the thing actually being carried.
            ItemInfo part = Backpack.PartTarget(item);

            return new ItemStatus
            {
                Slot = slot,
                Name = Backpack.Describe(item),
                Count = item.Count,
                Type = info.ItemType.ToString(),
                Flags = item.Flags == 0 ? "" : item.Flags.ToString(),
                CanSell = info.CanSell,

                Image = part?.Image ?? info.Image,
                Durability = Backpack.Displayed(item.CurrentDurability),
                MaxDurability = Backpack.Displayed(item.MaxDurability),
                Weight = item.Weight,
                Price = info.Price,

                // RequiredType and RequiredAmount, not a level: the server has no single "required
                // level" field, and the same pair expresses a level, an AC, an attack power or any
                // of the other RequiredType cases.
                //
                // There is no RequiredType.None either - the enum starts at Level, so an item with
                // no requirement is Level 0 and the amount is what actually says "none".
                Requirement = info.RequiredAmount <= 0
                    ? ""
                    : $"{info.RequiredType} {info.RequiredAmount}",

                Description = info.Description ?? "",
                Stats = FlattenStats(info.Stats),
                Added = FlattenStats(item.AddedStats),
                Sockets = FlattenSockets(item)
            };
        }

        private static List<ItemStat> FlattenStats(Stats stats)
        {
            List<ItemStat> flat = new List<ItemStat>();

            if (stats?.Values == null) return flat;

            foreach (KeyValuePair<Stat, int> pair in stats.Values)
            {
                if (pair.Value == 0) continue;

                // ItemIndex is plumbing, not a stat: it is how an item PART points at the thing it
                // builds towards, and the name already says "(part 2/25) Wooden Shield". Printing
                // "ItemIndex +1112" beneath that is a database row leaking into a tooltip.
                if (pair.Key == Stat.ItemIndex) continue;

                flat.Add(new ItemStat(pair.Key.ToString(), pair.Value));
            }

            return flat;
        }

        private static List<string> FlattenSockets(ClientUserItem item)
        {
            List<string> gems = new List<string>();

            if (item.Sockets == null) return gems;

            foreach (ClientUserItemSocket socket in item.Sockets)
                if (socket?.Gem?.Info != null) gems.Add(socket.Gem.Info.ItemName);

            return gems;
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
            // WHAT WE THREW AWAY.
            //
            // The counter behind this has existed all along and UnhandledSummary() was never
            // called from anywhere, so the one place that knew the bot was ignoring S.ItemDelete
            // kept it to itself. An unhandled packet is a silent divergence between our model and
            // the server's, which is the most expensive class of bug in this program.
            string ignored = string.Join(", ",
                _connection.UnhandledSummary().Take(12).Select(x => $"{x.Key} x{x.Value}"));

            _log.Write($"Unhandled packets: {_connection.UnhandledCount}" +
                       (string.IsNullOrEmpty(ignored) ? "" : $"   [{ignored}]"));

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
