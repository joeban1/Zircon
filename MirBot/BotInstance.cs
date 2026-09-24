using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
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

    public enum BotCommandKind { Start, Stop, ForceTownTrip, Revive, Travel, SetConfig, ForceRepair, NextTarget, DoQuests }

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
        private string _farmingDestinationName = "";
        private string _farmingDestinationReason = "";
        private MapTripEntry _mapTrip;
        private long _mapTripXpAwardBaseline;
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

                    case BotCommandKind.DoQuests:
                        DoQuests();
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
                if (Config.NotifyFault)
                    Notify("fault", $"{Id} stopped", reason + " - needs attention.",
                        TimeSpan.FromMinutes(30));
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
            _profitPolicy = new ProfitPolicy(Path.Combine(_host.MemoryFolder,
                "profit-" + Id + ".json"));
            if (_profitPolicy.LoadDiagnostic.Length > 0)
                _log.Write("Money: " + _profitPolicy.LoadDiagnostic);
            _goldHistory.Clear();
            foreach (GoldPoint point in _host.Gold.Read(Id))
            {
                if (!point.HasCapitalSpent ||
                    !long.TryParse(point.Gold, out long priorGold) || priorGold <= 0 ||
                    !DateTime.TryParse(point.Utc, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out DateTime priorUtc))
                    continue;
                _goldHistory.Add(new GoldSample(priorUtc.ToUniversalTime(), priorGold,
                    point.CapitalSpent));
            }
            if (_goldHistory.Count > 0) _capitalSpent = _goldHistory[^1].CapitalSpent;
            else _capitalSpent = 0;
            _nextGoldSample = DateTime.MinValue;
            _lastGoldSampled = long.MinValue;
            _lastTravelPenaltyPercent = Config.HuntingDeathPenaltyPercent;
            _connection.OnCapitalBought = cost =>
            {
                if (cost <= 0 || cost > long.MaxValue - _capitalSpent) return;
                _capitalSpent += cost;
                _log.Write($"Capital purchase confirmed: {cost:N0} gold (cumulative {_capitalSpent:N0}).");
                SampleGold(force: true);
            };
            _xpSession = Guid.NewGuid().ToString("N");
            _nextXpSample = DateTime.MinValue;
            _lastXpTotal = decimal.MinValue;
            _xpPace = new XpPace(null, 0);
            _connection.OnLevelUp = (from, to) =>
            {
                WorldModel world = _connection.World;
                _host.Levels.Record(new LevelEntry
                {
                    Bot = Id, Character = world.Name, Class = world.Class.ToString(),
                    FromLevel = from, ToLevel = to, Utc = DateTime.UtcNow
                });
                if (Config.NotifyLevelUp)
                    Notify("level", $"{world.Name} reached level {to}",
                        $"{world.Class} on {world.MapName}", TimeSpan.FromMinutes(2));
            };
            _connection.OnUpgradeConfirmed = (previous, next, gain) =>
            {
                WorldModel world = _connection.World;
                _host.Progress.Record(new ProgressEntry
                {
                    Kind = "upgrade", Bot = Id, Character = world.Name,
                    Class = world.Class.ToString(), Utc = DateTime.UtcNow,
                    PreviousGear = previous, NewGear = next, ScoreIncrease = gain
                });
                if (Config.NotifyUpgrade)
                    Notify("upgrade", $"{world.Name} equipped {next}",
                        string.IsNullOrEmpty(previous)
                            ? $"new gear (score +{gain})"
                            : $"replaced {previous} (score +{gain})", TimeSpan.FromMinutes(1));
            };
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
            // Boss lairs worth walking to: this map's bosses that drop a book we want - an
            // unlearned skill or a level 4 training copy. Independent of BookHuntBonusPercent,
            // which only weighs map choice; finding the boss once here is always worth it.
            //
            // MINI-BOSSES ONLY: several spawns that return within the hour (the Lv 3 cave bosses,
            // the Warlords). Tainted Terror (7,700 health, one spawn every nine hours) also drops
            // warrior books in the Underground maps, and walking a level 42 bot to its lair would
            // be walking it to its death; the world bosses are the same only more so.
            _brain.WantedLairs = world =>
            {
                IReadOnlyList<BossLair> here = _host.BossLairs.On(world.MapIndex);
                if (here.Count == 0) return here;
                HashSet<int> wanted = _host.BookDrops.Wanted(world.Class, world.Level,
                    world.PlayerStats, world);
                // Plus the bosses accepted quests need, each only from its own level (mini-bosses
                // QuestMiniBossMinLevel, Crazed Warrior QuestBossMinLevel) - checked here as well as
                // at acceptance, because a quest taken under an older rule is still in the log.
                QuestRules rules = QuestRulesNow();
                HashSet<int> questBosses = Config.EnableQuests
                    ? _host.Quests.KillTargets(world)
                        .Where(t => t.IsBoss && world.Level >= QuestBook.BossLevelFor(
                            _host.Monsters.Find(t.MonsterIndex), rules))
                        .Select(t => t.MonsterIndex).ToHashSet()
                    : new HashSet<int>();
                return here.Where(l => questBosses.Contains(l.MonsterIndex) ||
                                       IsMiniBoss(here, l.MonsterIndex) &&
                                       _host.BookDrops.BossTeaches(l.MonsterIndex, wanted).Any())
                    .ToList();
            };
            _brain.IsDropPending = () => _connection != null && _connection.DropPending;
            _connection.OnDropRefused = slot => _brain?.NoteDropRefused(slot);
            _connection.OnObjectDied = NoteObjectDied;
            _connection.OnItemGained = item =>
            {
                BossKillEntry kill = _lastBossKill;
                if (kill == null || item?.Info == null || !BossKillMemory.LootWindowOpen(kill)) return;
                _host.BossKills.NoteLoot(kill, _connection.World.MapIndex,
                    item.Count > 1 ? $"{item.Info.ItemName} x{item.Count}" : item.Info.ItemName);
            };
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
                    NoteJourneyFailure(message);
                    EscapeOnScroll(message);
                },
                TalkRange = Config.VendorTalkRange,
                GoldFloor = Config.TeleportGoldFloor,
                MaxGoldPercent = Config.TeleportMaxGoldPercent
            };
            _town = new TownTrip(Config, _host.Vendors, _host.Books, _host.SafeZones);
            _brain.Town = _town;

            // Quests (QuestBook/QuestErrand). Everything here runs on this bot's thread - packets
            // are drained by _connection.Process() in Advance - so no locking is needed.
            _questErrand = new QuestErrand(Config, _host.Quests, message => _log.Write(message));
            _questErrand.Rules = QuestRulesNow;
            _brain.Quest = _questErrand;
            _brain.QuestBook = _host.Quests;
            _brain.SpawnCells = (monster, map) => _host.BossLairs.SpawnCells(monster, map);
            _questsLogged = false;
            _connection.OnQuestChanged = NoteQuestChanged;
            _connection.OnQuestCancelled = quest => _log.Write(
                $"Quest: '{quest?.Quest?.QuestName}' cleared by the server (daily reset) - " +
                "it can be taken again.");
            _connection.OnDataObjectGone = ob => _brain?.NoteDataObjectGone(ob, _connection.World.MapIndex);

            // The game store (GameStore/StoreShopper), same thread rules as the quests above.
            _shopper = new StoreShopper
            {
                Log = message => _log.Write(message),
                OnBought = (want, left) => _history.Note("Store", $"bought {want.Name}")
            };
            _brain.Store = _host.Store;
            _brain.Shopper = _shopper;

            // Fame ranks (FameBook/FameErrand). The system chat line explains refusals to BOTH the
            // store and the fame errand, so it goes to each.
            _fameErrand = new FameErrand(Config, _host.Fame, message => _log.Write(message))
            {
                OnPromoted = NotePromoted
            };
            _brain.Fame = _fameErrand;
            _connection.OnSystemChat = text =>
            {
                _shopper.NoteServerLine(text);
                _fameErrand.NoteServerLine(text);
            };

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
                _questErrand?.PageChanged(page);
                _fameErrand?.PageChanged(page);

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
            {
                _brain?.Spells.Cooldown(infoIndex, delay);
                _brain?.Skills.Cooldown(infoIndex, delay);
            };

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
            _brain.InRecovery = _recovery.Active;

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
            System.Diagnostics.Stopwatch decideTimer = System.Diagnostics.Stopwatch.StartNew();
            Decision decision = _brain.Decide(
                _connection.World,
                _connection.Items,
                _connection.ItemUsePending || _connection.ItemUseOnCooldown);
            NoteSlowTick("Decide", decideTimer.ElapsedMilliseconds, decision);

            // Deliberately ABOVE the early return, because the interesting case is the tick that
            // decides to do nothing.
            //
            // TownTrip.Status was previously only ever written out when an NPC page arrived, so
            // every abort and every declined trip was invisible. A wizard was watched hunting at
            // 6% health with an empty bag for 111 seconds before a trip finally started, and the
            // log had nothing whatsoever to say about the delay - which is how this went unnoticed
            // long enough to be observed by eye rather than read.
            LogTripStatus();
            ObserveMapTrip();

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

            if (!_questsLogged && _connection.Stage == BotStage.InGame)
            {
                _questsLogged = true;
                List<string> quests = _connection.World.Quests.Where(q => q.Quest != null)
                    .Select(q => $"{q.Quest.QuestName} ({(q.Completed ? "done" : QuestBook.ProgressText(q))})")
                    .ToList();
                _log.Write("Quests: " + (quests.Count == 0 ? "none in the log" : string.Join("; ", quests)));
                _log.Write($"Store: {_connection.World.HuntGold:N0} Hunt Gold; {StoreStatusText()}.");
            }

            if (decision.TargetID != 0 &&
                (decision.Action == BotAction.Attack || decision.Action == BotAction.Cast))
                _attackedAt[decision.TargetID] = DateTime.UtcNow;
            WatchBossDrops(decision);

            SampleExperience();
            System.Diagnostics.Stopwatch travelTimer = System.Diagnostics.Stopwatch.StartNew();
            ConsiderTravel();
            NoteSlowTick("ConsiderTravel", travelTimer.ElapsedMilliseconds, decision);
            CheckIdleNotify();

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
        /// <summary>
        /// The operator's "Do quests" button: forget an earlier give-up and go to the quest NPC's
        /// map if there is anything to do there. The errand itself starts on arrival, exactly as
        /// it does when a bot passes through.
        /// </summary>
        private void DoQuests()
        {
            if (_connection == null || _connection.Stage != BotStage.InGame || _questErrand == null)
            {
                _log.Write("Do quests requested but the bot is not in game.");
                return;
            }

            WorldModel world = _connection.World;

            if (!Config.EnableQuests)
            {
                _log.Write("Do quests: quests are off.");
                return;
            }

            MapInfo map = NearestQuestMap(world);

            if (map == null)
            {
                _log.Write("Do quests: nothing to do in any quest town (nothing to hand in, nothing " +
                           "acceptable, nothing to hunt there).");
                return;
            }

            _questErrand.ClearCooldown();
            _history.Note("Quest", "requested");

            if (world.MapIndex == map.Index)
            {
                _log.Write($"Do quests: already on {map.Description} - starting the errand.");
                return;
            }

            // Walking there from a hunting map is the wrong way round: the first press did exactly
            // that from the Desert, and the journey sat on "hostiles in contact" for as long as the
            // map kept spawning. Go home the way the Town button does - a scroll if one is
            // carried, the walk-to-town fallback if not, and a shop on the way - then head for the
            // quest NPC when the trip ends (ConsiderTravel, _questRequestPending).
            _questRequestPending = true;
            _questRequestAt = DateTime.UtcNow;

            if (_town != null)
            {
                _log.Write($"Do quests: going to town first, then {map.Description}.");
                _town.Force();
            }
            else
            {
                _questRequestPending = false;
                _log.Write($"Do quests: heading for {map.Description}.");
                StartTravel(map.Index.ToString());
            }
        }

        /// <summary>
        /// The quest town (Bichon Town, Banya Village, Lost Paradise) with work for us that is
        /// fewest map hops away - the current map if it has work - or null.
        /// </summary>
        private MapInfo NearestQuestMap(WorldModel world)
        {
            List<int> maps = _questErrand?.MapsWithWork(world) ?? new List<int>();
            if (maps.Count == 0) return null;

            int best = maps.Contains(world.MapIndex) ? world.MapIndex : -1;

            if (best < 0)
            {
                Dictionary<int, int> hops = _host.World.HopCounts(world.MapIndex, world.Class,
                    world.Level, world.Gold, Config.TeleportGoldFloor, world.PKPoints,
                    Config.TeleportMaxGoldPercent);
                best = maps.Where(hops.ContainsKey).OrderBy(m => hops[m]).ThenBy(m => m)
                    .DefaultIfEmpty(-1).First();
            }

            return best < 0 ? null : Globals.MapInfoList?.Binding?.FirstOrDefault(m => m.Index == best);
        }

        private DateTime _lastQuestTownTrip = DateTime.MinValue;

        private DateTime _lastSlowTickLog = DateTime.MinValue;

        /// <summary>
        /// A decision tick is normally a few milliseconds. One that takes half a second stalls
        /// everything on this bot's thread - packets are read late, steps are sent late - which is
        /// what a bot "approaching without moving" looks like from outside. Logged, rate-limited.
        /// </summary>
        private void NoteSlowTick(string stage, long ms, Decision decision)
        {
            if (ms < 500 || DateTime.UtcNow - _lastSlowTickLog < TimeSpan.FromSeconds(10)) return;
            _lastSlowTickLog = DateTime.UtcNow;
            _log.Write($"Slow tick: {stage} took {ms} ms (decision {decision?.Action} - " +
                       $"{decision?.Reason}; quest {_questErrand?.Status}; route {_brain?.CurrentRoute.Cells.Length ?? 0} cells).");
        }

        /// <summary>Do quests was pressed away from the quest NPC: travel there after the town trip.</summary>
        private bool _questRequestPending;
        private DateTime _questRequestAt;

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

                Begin(asked.Index, asked.Description, "requested",
                      displayReason: "manual destination");
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
                (_brain.Town.ShortOfSupplies || _brain.Town.NeedsStorage ||
                 _brain.Town.NeedsVendor || _brain.Town.WalkToTownRequested) &&
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
                        : _brain.Town.NeedsVendor
                        ? "bag full or gear broken - going to town, not hunting"
                        : _brain.Town.WalkToTownRequested
                        ? "asked to go to town - walking, no town scroll"
                        : "banked item ready - going to a safe town";
                    Begin(best, _host.Profiles.For(best)?.MapName ?? $"map {best}", why,
                          farmingChoice: false);
                    return;
                }

                _log.Write(_brain.Town.ShortOfSupplies
                    ? "Travel: out of supplies and no town reachable - staying put rather than hunting on empty."
                    : _brain.Town.NeedsVendor
                    ? "Travel: bag full or gear broken but no vendor town is reachable - staying put."
                    : "Travel: storage work is ready but no safe town is reachable - staying put.");
                return;
            }

            // IN TOWN, STILL SHORT, AND THE TRIP NEVER TRADED: stay and let it run again rather
            // than choose a hunting ground. The trip that brought us here was cut short (the low
            // health rule, a stuck walk), so nothing was bought - Sindo left Banya Village this way
            // with no scrolls. A trip that DID trade and is still short cannot be helped by
            // waiting, so that case goes on to the ordinary choice as before.
            if (_brain.Town != null && !_brain.Town.LastTripTraded &&
                (_brain.Town.ShortOfSupplies || _brain.Town.NeedsVendor) &&
                _host.Vendors.TownMaps.Contains(world.MapIndex))
            {
                _log.Write("Travel: the last town trip was cut short before trading and we are " +
                           "still short - staying in town for the trip to run again.");
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
                    if (recovery.Contains(world.MapIndex))
                    {
                        NoteFarmingChoice(world.MapIndex, world.MapName,
                            caveReady ? "poverty recovery - supplied cave farming"
                                      : "poverty recovery - rebuilding on beginner ground");
                        return;
                    }

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
                                  : "broke and undersupplied - rebuilding on free beginner ground",
                              displayReason: caveReady
                                  ? "poverty recovery - supplied cave farming"
                                  : "poverty recovery - rebuilding on beginner ground");
                        return;
                    }

                    // Cannot reach one. Fall through rather than stand still - anywhere we can
                    // fight beats nowhere, and the ordinary rules below still apply.
                    _log.Write($"Travel: recovery active at {world.Gold:N0} gold but no recovery map is " +
                               "reachable - falling back to the ordinary choice.");
                }
            }

            // An accepted quest whose target is a boss (Level 40 - Well done: Crazed Warrior, 45+).
            // After recovery and supplies, before the ordinary choice; never replaces a journey.
            if (TryQuestBossJourney(world, hops, Affordable, Permitted, deadly)) return;

            // Explore or exploit?
            //
            // The Mir 2 agents roll a flat 1-in-20 to explore, which suits hundreds of agents
            // sharing one memory. We reconsider only when a town trip ends - the warrior had four
            // decision points in five hours - so a flat roll that rare means one exploration a day.
            // Coverage-driven instead: keep exploring until enough maps have been measured to be
            // worth choosing between, then mostly exploit with a residual chance of looking further.
            int known = _host.Hunting.MeasuredCount(mirClass, world.Level);
            bool gearFocus = Config.GearHuntBonusPercent > 0 &&
                             !_recovery.Active && _profitPolicy?.Active != true &&
                             _consecutiveGearHunts < Math.Max(1, Config.MaxConsecutiveGearHunts);
            int exploreChance = HasSafeUnmeasuredGearMap(world, mirClass, hops,
                Affordable, Permitted, gearFocus)
                ? Math.Max(Config.ExploreChancePercent, Config.UpgradeExploreChancePercent)
                : Config.ExploreChancePercent;

            // An unmeasured map that drops a wanted book can only ever be reached by exploring -
            // the exploit list holds measured maps alone - so it gets the book-goal chance to be
            // looked at. TryExplore then applies the same book-versus-ordinary split as exploiting.
            bool bookToExplore = HasSafeUnmeasuredBookMap(world, mirClass, hops,
                Affordable, Permitted);
            if (bookToExplore)
                exploreChance = Math.Max(exploreChance, _profitPolicy?.Active == true
                    ? Config.LossBookHuntChancePercent
                    : Config.BookHuntChancePercent);
            // Likewise a quest whose targets live only on unmeasured maps can only be pursued by
            // exploring, so it lifts the explore chance to the quest chance.
            if (HasUnmeasuredQuestMap(world, mirClass))
                exploreChance = Math.Max(exploreChance, Config.QuestHuntChancePercent);
            bool wantExplore = known < Config.ExploreUntilMapsKnown ||
                               _random.Next(100) < exploreChance;

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
            HashSet<int> wantedBooks = WantedDropOnlyBooks(world);

            List<HuntingEntry> candidates = new List<HuntingEntry>();
            List<HuntingEntry> outgrown = new List<HuntingEntry>();

            // EVERY MEASURED MAP WHEN BOOKS ARE WANTED.
            //
            // Best() ranks on experience and truncates. A merely wider shortlist still lets a weak
            // but essential book map fall off the end as soon as enough faster grounds have been
            // measured. While a drop-only skill is wanted, inspect every measured map so its source
            // cannot disappear from consideration simply because the character is already strong
            // enough to earn better experience elsewhere.
            _lastTravelPenaltyPercent = Math.Max(Config.HuntingDeathPenaltyPercent,
                _profitPolicy?.Active == true ? Config.LossDeathPenaltyPercent : 0);
            double deathPenalty = _lastTravelPenaltyPercent / 100.0;
            int shortlist = int.MaxValue;

            foreach (HuntingEntry entry in _host.Hunting.Best(mirClass, world.Level, shortlist,
                         deathPenalty))
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

                if (OutgrownShoppingTown(world, entry.MapIndex, wantedBooks, out string townWhy))
                {
                    _log.Write($"Travel: deferring {entry.MapName} - {townWhy}.");
                    outgrown.Add(entry);
                    continue;
                }

                candidates.Add(entry);
            }

            // QUEST GOAL first: maps where accepted quests' targets live (only maps this bot may
            // hunt), ranked by the usual score times the quest bonus.
            Dictionary<int, List<string>> questMaps = QuestMaps(world);
            List<HuntingEntry> questCandidates = candidates
                .Where(x => QuestSupply(questMaps, x.MapIndex) > 0).ToList();
            if (PursueQuest(questCandidates.Count > 0, out int questChance))
            {
                double QuestRank(HuntingEntry entry) =>
                    _host.Hunting.Ranked(entry, world.Level, deathPenalty) *
                    QuestBonus(QuestSupply(questMaps, entry.MapIndex));
                questCandidates.Sort((a, b) => QuestRank(b).CompareTo(QuestRank(a)));
                int keepQuest = Math.Max(1, Config.HuntingChoices);
                if (questCandidates.Count > keepQuest)
                    questCandidates.RemoveRange(keepQuest, questCandidates.Count - keepQuest);

                double[] questWeights = questCandidates.Select(QuestRank).ToArray();
                int qi = WeightedChoice.Pick(questWeights, Config.HuntingPickWeightPower, _random,
                    out double qdraw);
                HuntingEntry questPick = questCandidates[qi];
                string names = QuestNames(questMaps, questPick.MapIndex);

                _log.Write($"Travel: {questCandidates.Count} quest map(s) shortlisted; " +
                           $"{questChance}% quest chance - choosing quest hunt.");
                Begin(questPick.MapIndex, questPick.MapName,
                      $"{questPick.AverageExperiencePerHour:N0} exp/hour average, " +
                      $"{questPick.Deaths} death(s) - quests: {names} " +
                      $"(score {questWeights[qi]:N0}, draw {qdraw:P1})",
                      displayReason: $"quests: {names}");
                _consecutiveQuestHunts++;
                _consecutiveBookHunts = 0;
                _consecutiveGearHunts = 0;
                return;
            }

            // Keep the book and ordinary pools separate BEFORE the shortlist. A book bonus alone
            // can push every ordinary map out of the top three, leaving a nominally weighted draw
            // that still chooses the same book map 100% of the time. Prefer the book goal 70% of
            // the time (20% under loss watch), but force an ordinary goal after two book-priority
            // decisions. Within the chosen pool the existing XP/death weighting still decides.
            List<HuntingEntry> bookCandidates = candidates
                .Where(x => BookSupply(x.MapIndex, wantedBooks) > 0).ToList();
            List<HuntingEntry> ordinaryCandidates = candidates
                .Where(x => BookSupply(x.MapIndex, wantedBooks) == 0).ToList();
            bool bookGoal = HuntingGoalChoice.PursueBook(bookCandidates.Count > 0,
                ordinaryCandidates.Count > 0, _profitPolicy?.Active == true,
                _consecutiveBookHunts, Config.BookHuntChancePercent,
                Config.LossBookHuntChancePercent, Config.MaxConsecutiveBookHunts,
                _random, out int bookChance);

            if (bookCandidates.Count > 0)
                _log.Write($"Travel: {bookCandidates.Count} book map(s), " +
                           $"{ordinaryCandidates.Count} ordinary map(s); " +
                           $"{bookChance}% book chance, {_consecutiveBookHunts} prior " +
                           $"book-priority choice(s) - choosing {(bookGoal ? "book" : "ordinary")} hunt.");

            candidates = bookGoal ? bookCandidates : ordinaryCandidates;
            Dictionary<int, GearTarget> gearTargets = new Dictionary<int, GearTarget>();
            if (!bookGoal && gearFocus)
                foreach (HuntingEntry entry in candidates)
                    gearTargets[entry.MapIndex] = GearTargetFor(entry.MapIndex, world);
            double Rank(HuntingEntry entry) => bookGoal
                ? BookRanked(entry, wantedBooks, world.Level, deathPenalty)
                : _host.Hunting.Ranked(entry, world.Level, deathPenalty) *
                  (1 + (gearTargets.TryGetValue(entry.MapIndex, out GearTarget gear) &&
                        gear != null ? Config.GearHuntBonusPercent / 100.0 * gear.Priority : 0));
            candidates.Sort((a, b) => Rank(b).CompareTo(Rank(a)));
            int keep = Math.Max(1, Config.HuntingChoices);
            if (candidates.Count > keep) candidates.RemoveRange(keep, candidates.Count - keep);

            if (candidates.Count > 0)
            {
                double[] weights = candidates.Select(Rank).ToArray();
                int index = WeightedChoice.Pick(weights, Config.HuntingPickWeightPower,
                    _random, out double chance);
                HuntingEntry pick = candidates[index];

                int supplies = BookSupply(pick.MapIndex, wantedBooks);
                GearTarget pickedGear = !bookGoal && gearTargets.TryGetValue(pick.MapIndex,
                    out GearTarget target) ? target : null;

                Begin(pick.MapIndex, pick.MapName,
                      $"{pick.AverageExperiencePerHour:N0} exp/hour average over " +
                      $"{pick.HoursSampled:N1}h, {pick.Deaths} death(s) - " +
                      $"chosen from {candidates.Count} candidate(s), " +
                      $"score {weights[index]:N0}, draw {chance:P1}, death penalty " +
                      $"{deathPenalty:P0}" +
                      (bookCandidates.Count > 0 ? $"; {bookChance}% book-goal chance" : "") +
                      (supplies > 0
                          ? $"; drops {BookNames(pick.MapIndex, wantedBooks)}"
                          : "") +
                      (pickedGear != null ? $"; {GearChoiceReason(pickedGear)}" : ""),
                      displayReason: pickedGear != null
                          ? GearChoiceReason(pickedGear)
                          : supplies > 0
                          ? $"looking for {supplies} skill book(s): " +
                            BookNames(pick.MapIndex, wantedBooks)
                          : $"hunting score {weights[index]:N0}; " +
                            $"{pick.AverageExperiencePerHour:N0} XP/hour average");
                _consecutiveBookHunts = bookGoal ? _consecutiveBookHunts + 1 : 0;
                _consecutiveGearHunts = pickedGear != null ? _consecutiveGearHunts + 1 : 0;
                _consecutiveQuestHunts = 0;
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
                // A healthy bot already in a starter town should not bounce to another starter
                // town merely because every useful destination failed the safety/route filters.
                if (OutgrownShoppingTown(world, world.MapIndex, wantedBooks, out _))
                {
                    _log.Write("Travel: no suitable alternative to this outgrown town - staying put.");
                    return;
                }

                double[] weights = outgrown.Select(x => _host.Hunting.Ranked(x, world.Level,
                    deathPenalty)).ToArray();
                HuntingEntry fallback = outgrown[WeightedChoice.Pick(weights,
                    Config.HuntingPickWeightPower, _random, out _)];

                _log.Write($"Travel: nothing else reachable, so taking {fallback.MapName} " +
                           $"despite having outgrown it - {outgrown.Count} such map(s) known.");

                Begin(fallback.MapIndex, fallback.MapName,
                      $"{fallback.AverageExperiencePerHour:N0} exp/hour average, outgrown but " +
                      "the only thing left",
                      displayReason: "only safe, reachable measured ground left");
                return;
            }

            _log.Write($"Travel: nowhere to go - {hops.Count} maps reachable, " +
                       $"{known} measured, {(poor ? "poor so no paid teleports, " : "")}" +
                       "none affordable, safe and unmeasured.");
        }

        /// <summary>
        /// A map's best worthwhile wearable drop, if its currently reachable monsters can supply it.
        /// </summary>
        private GearTarget GearTargetFor(int mapIndex, WorldModel world) =>
            Config.GearHuntBonusPercent <= 0 ? null :
            _host.GearDrops.Best(mapIndex, _connection?.Items, world,
                Config.ExploreLevelsAbove);

        private static string GearChoiceReason(GearTarget gear) =>
            $"looking for gear upgrade: {gear.ItemName} (score +{gear.ScoreGain})";

        private bool DeeperUnmeasuredFloor(int mapIndex, HashSet<int> measured,
            Dictionary<int, int> hops)
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
                    return true;
            }
            return false;
        }

        /// <summary>
        /// DeeperUnmeasuredFloor, except for a floor that supplies a wanted book in a cave the
        /// character has outgrown.
        ///
        /// Summon Shinsu (level 30) and Summon Jin Skeleton (level 33) drop only from the Skeleton
        /// Lord and the Ghoul Champion, and those live only on the Lv 3 floors of Banya Cave,
        /// Bichon Cave, Lost Paradise Cave and Deserted Mine. Both level 34-35 Taoists had a
        /// shallower floor of every one of those caves unmeasured at their level, so the Lv 3 was
        /// held back before the book goal ever saw it - the rule is written for caves that are a
        /// fight, and these have a median monster level of 18-20.
        /// </summary>
        private bool HeldBackFloor(int mapIndex, HashSet<int> measured, Dictionary<int, int> hops,
            WorldModel world, HashSet<int> wantedBooks)
        {
            if (!DeeperUnmeasuredFloor(mapIndex, measured, hops)) return false;
            if (BookSupply(mapIndex, wantedBooks) <= 0) return true;

            int median = _host.Profiles.For(mapIndex)?.MedianLevel ?? int.MaxValue;
            return median > world.Level - Config.ExploreLevelsAbove;
        }

        /// <summary>
        /// How far above our level a map's typical monster may be for us to explore it: the usual
        /// ExploreLevelsAbove, plus BookExploreExtraLevels where it drops a skill book we want.
        /// Destructive Surge, Defiance and Interchange drop only in Desert Dungeon and the
        /// Underground maps - effective level about 55 - which no bot would otherwise try before
        /// level 52, while players take them at 40-45.
        /// </summary>
        private int ExploreReach(int mapIndex, HashSet<int> wantedBooks) =>
            Config.ExploreLevelsAbove +
            (BookSupply(mapIndex, wantedBooks) > 0 ? Math.Max(0, Config.BookExploreExtraLevels) : 0);

        /// <summary>
        /// Is there a wanted-book map that exploration would accept? Mirrors TryExplore's
        /// filters; used only to raise the chance of exploring, as a gear source does.
        /// </summary>
        private bool HasSafeUnmeasuredBookMap(WorldModel world, string mirClass,
            Dictionary<int, int> hops, AffordableCheck affordable, Func<int, bool> permitted)
        {
            HashSet<int> wantedBooks = WantedDropOnlyBooks(world);
            if (wantedBooks.Count == 0) return false;

            HashSet<int> measured = new HashSet<int>(_host.Hunting.Best(mirClass,
                world.Level, 500, Config.HuntingDeathPenaltyPercent / 100.0)
                .Select(entry => entry.MapIndex));
            HashSet<int> lethal = _host.Hunting.Lethal(mirClass, world.Level);

            foreach (int map in hops.Keys)
            {
                if (BookSupply(map, wantedBooks) <= 0) continue;
                if (map == world.MapIndex || measured.Contains(map) || lethal.Contains(map) ||
                    !permitted(map) || _host.Danger.TooDangerous(map, world.MaxHealth) ||
                    !_host.Profiles.WorthExploring(map, world.Level,
                        ExploreReach(map, wantedBooks), out _) ||
                    _host.Profiles.OutgrownBy(map, world.Level, Config.HuntLevelsBelow, out _) ||
                    !affordable(map, out _) ||
                    OutgrownShoppingTown(world, map, wantedBooks, out _) ||
                    HeldBackFloor(map, measured, hops, world, wantedBooks)) continue;

                return true;
            }

            return false;
        }

        private bool HasSafeUnmeasuredGearMap(WorldModel world, string mirClass,
            Dictionary<int, int> hops, AffordableCheck affordable,
            Func<int, bool> permitted, bool gearFocus)
        {
            if (!gearFocus) return false;

            HashSet<int> measured = new HashSet<int>(_host.Hunting.Best(mirClass,
                world.Level, 500, Config.HuntingDeathPenaltyPercent / 100.0)
                .Select(entry => entry.MapIndex));
            HashSet<int> lethal = _host.Hunting.Lethal(mirClass, world.Level);
            HashSet<int> wantedBooks = WantedDropOnlyBooks(world);

            foreach (int map in hops.Keys)
            {
                if (map == world.MapIndex || measured.Contains(map) || lethal.Contains(map) ||
                    !permitted(map) || _host.Danger.TooDangerous(map, world.MaxHealth) ||
                    !_host.Profiles.WorthExploring(map, world.Level,
                        Config.ExploreLevelsAbove, out _) ||
                    _host.Profiles.OutgrownBy(map, world.Level, Config.HuntLevelsBelow, out _) ||
                    !affordable(map, out _) ||
                    OutgrownShoppingTown(world, map, wantedBooks, out _) ||
                    DeeperUnmeasuredFloor(map, measured, hops)) continue;

                if (GearTargetFor(map, world) != null) return true;
            }

            return false;
        }

        /// <summary>An accepted quest's target lives on a huntable map we have not measured.</summary>
        private bool HasUnmeasuredQuestMap(WorldModel world, string mirClass)
        {
            Dictionary<int, List<string>> questMaps = QuestMaps(world);
            if (questMaps.Count == 0) return false;
            HashSet<int> measured = new HashSet<int>(_host.Hunting.Best(mirClass, world.Level, 500,
                Config.HuntingDeathPenaltyPercent / 100.0).Select(entry => entry.MapIndex));
            return questMaps.Keys.Any(map => map != world.MapIndex && !measured.Contains(map));
        }

        private double BookRanked(HuntingEntry entry, HashSet<int> wanted,
            int level, double deathPenalty)
        {
            double score = _host.Hunting.Ranked(entry, level, deathPenalty);
            int supplies = BookSupply(entry.MapIndex, wanted);

            if (supplies <= 0) return score;

            return score * (1 + Config.BookHuntBonusPercent / 100.0 * supplies);
        }

        /// <summary>
        /// How many wanted books this map supplies - unlearned skills and level 4 training copies
        /// alike. Training books deliberately count on the early caves too: that is where most of
        /// them drop, and the book-versus-ordinary split (BookHuntChancePercent,
        /// MaxConsecutiveBookHunts) is what keeps a bot from living there.
        /// </summary>
        private int BookSupply(int mapIndex, HashSet<int> wanted) =>
            _host.BookDrops.Supplies(mapIndex, wanted);

        private string BookNames(int mapIndex, HashSet<int> wanted) =>
            _host.BookDrops.Names(mapIndex, wanted);

        private HashSet<int> WantedDropOnlyBooks(WorldModel world) =>
            Config.BookHuntBonusPercent > 0
                ? _host.BookDrops.Wanted(world.Class, world.Level, world.PlayerStats, world)
                : new HashSet<int>();

        private bool OutgrownShoppingTown(WorldModel world, int mapIndex,
            HashSet<int> wantedBooks, out string why)
        {
            MapProfileEntry profile = _host.Profiles.For(mapIndex);
            int median = profile?.MedianLevel ?? 0;
            bool defer = HuntingTownPolicy.Defer(_host.Vendors.TownMaps.Contains(mapIndex),
                median, world.Level, Config.TownHuntLevelGap, world.Gold < Config.PoorGold,
                _recovery.Active, BookSupply(mapIndex, wantedBooks) > 0);
            why = defer ? $"typical monster level {median} is far below level {world.Level}" : "";
            return defer;
        }

        /// <summary>When this bot last swung or cast at each object - set on the bot thread,
        /// read on the socket thread when something dies.</summary>
        private readonly ConcurrentDictionary<uint, DateTime> _attackedAt =
            new ConcurrentDictionary<uint, DateTime>();

        private volatile BossKillEntry _lastBossKill;

        private QuestErrand _questErrand;
        private StoreShopper _shopper;
        private FameErrand _fameErrand;

        /// <summary>What the store step is doing, for the log and the bot panel.</summary>
        private string StoreStatusText()
        {
            if (!Config.EnableStore) return "store buying is off";

            StoreWant next = _shopper?.NextWant;
            long held = _connection?.World.HuntGold ?? 0;

            if (_shopper == null || !_shopper.Evaluated) return "not checked yet";
            if (next == null) return "every store item on the list is owned";
            if (_shopper.Pending) return $"buying {next.Name}";
            if (held < next.Price) return $"saving for {next.Name}: {held:N0} / {next.Price:N0} Hunt Gold";
            return $"next {next.Name} ({next.Price:N0} Hunt Gold)";
        }
        private bool _questsLogged;

        /// <summary>
        /// S.QuestChanged, already classified. Only real transitions are logged: a quest that
        /// appears is an accept, Completed going true is a hand-in; kill updates write nothing.
        /// </summary>
        /// <summary>A fame rank was reached: log it, record it, tell the phone.</summary>
        private void NotePromoted(FameInfo rank, long fameLeft)
        {
            WorldModel world = _connection.World;
            string buffs = FameBook.Buffs(rank);

            _history.Note("Fame", rank?.Name ?? "rank");
            _host.QuestLog.Record(new QuestLogEntry
            {
                Bot = Id, Character = world.Name, Class = world.Class.ToString(), Level = world.Level,
                QuestIndex = 0, Quest = $"Fame: {rank?.Name}", Event = "fame",
                MapIndex = world.MapIndex, Map = world.MapName, Utc = DateTime.UtcNow,
                Rewards = buffs
            });

            if (Config.NotifyFame && rank != null)
                Notify($"fame:{rank.Index}", $"{world.Name} reached fame rank {rank.Name}",
                    string.IsNullOrEmpty(buffs) ? "fame rank reached" : buffs, TimeSpan.FromMinutes(5));
        }

        /// <summary>"Village Explorer (rank 2) - 740 / 2,000 FP for Regional Apprentice".</summary>
        private string FameStatusText()
        {
            WorldModel world = _connection?.World;
            if (world == null || !_host.Fame.Ready) return "";

            FameInfo current = _host.Fame.Current(world.FameIndex);
            FameInfo next = _host.Fame.Next(world.FameIndex);
            string held = current == null ? "no rank" : $"{current.Name} (rank {current.Order + 1})";

            if (next == null) return $"{held} - highest rank reached";
            if (!Config.EnableFame) return $"{held} - {world.FamePoints:N0} FP (fame buying off)";
            string progress = $"{world.FamePoints:N0} / {next.Cost:N0} FP for {next.Name}";
            if (world.FamePoints >= next.Cost && world.Level < FameMinLevel)
                return $"{held} - {progress}; {_host.Fame.Map?.Description ?? "the fame NPC"} needs level {FameMinLevel}";
            return $"{held} - {progress}";
        }

        /// <summary>
        /// The fame NPC stands in Frost Village, which the teleport stones only take a character
        /// of level 45 to (and two fares: Bichon to Lost Paradise 10,000, on to Frost 20,000).
        /// </summary>
        private const int FameMinLevel = 45;
        private const long FameRouteGold = 30000;

        private DateTime _lastFameTrip = DateTime.MinValue;
        private DateTime _fameNoRouteUntil = DateTime.MinValue;

        private void NoteQuestChanged(QuestTransition transition)
        {
            _questErrand?.QuestChanged(transition);

            ClientUserQuest quest = transition?.Quest;
            if (quest?.Quest == null || (!transition.Accepted && !transition.Completed)) return;

            WorldModel world = _connection.World;
            string rewards = QuestBook.RewardNames(quest.Quest, world.Class);
            string evt = transition.Completed ? "completed" : "accepted";

            _log.Write($"Quest: {evt} '{quest.Quest.QuestName}'" +
                       (transition.Completed ? $" - {rewards}" : $" - {QuestBook.ProgressText(quest)}") + ".");

            _host.QuestLog.Record(new QuestLogEntry
            {
                Bot = Id, Character = world.Name, Class = world.Class.ToString(), Level = world.Level,
                QuestIndex = quest.QuestIndex, Quest = quest.Quest.QuestName, Event = evt,
                MapIndex = world.MapIndex, Map = world.MapName, Utc = DateTime.UtcNow,
                Rewards = transition.Completed ? rewards : ""
            });

            if (transition.Completed && Config.NotifyQuest)
                Notify($"quest:{quest.QuestIndex}", $"{world.Name} completed {quest.Quest.QuestName}",
                    string.IsNullOrEmpty(rewards) ? "quest complete" : rewards, TimeSpan.FromMinutes(5));
        }

        /// <summary>
        /// Journey to the boss an accepted quest needs, if the rules allow: quests enabled, level
        /// at least QuestBossMinLevel, no more than two such journeys per quest in six hours
        /// (counted in the quest log, so a restart does not reset it), not already on a map it
        /// lives on, and a reachable, affordable, permitted map that has not killed us.
        /// </summary>
        private bool TryQuestBossJourney(WorldModel world, Dictionary<int, int> hops,
            AffordableCheck affordable, Func<int, bool> permitted, HashSet<int> deadly)
        {
            if (!Config.EnableQuests) return false;

            QuestRules rules = QuestRulesNow();

            // Every accepted boss target this bot is now old enough for (mini-bosses 40, Crazed
            // Warrior 50), once per quest and monster. The first one that is capped or has nowhere
            // reachable no longer blocks the rest.
            var targets = _host.Quests.KillTargets(world)
                .Where(t => t.IsBoss && world.Level >= QuestBook.BossLevelFor(
                    _host.Monsters.Find(t.MonsterIndex), rules))
                .GroupBy(t => (t.Quest.Index, t.MonsterIndex)).Select(g => g.First())
                .ToList();

            foreach (QuestKillTarget target in targets)
            {
                // Per quest: two journeys in six hours, however many bosses it has.
                if (_host.QuestLog.JourneysSince(Id, target.Quest.Index,
                        DateTime.UtcNow - TimeSpan.FromHours(6)) >= 2) continue;

                HashSet<int> maps = new HashSet<int>(_host.BossLairs.MapsForMonster(target.MonsterIndex));
                maps.IntersectWith(target.Maps.Count > 0 ? target.Maps : maps);
                if (maps.Contains(world.MapIndex)) return false;   // here already: the lairs do the rest

                int best = maps.Where(m => hops.ContainsKey(m) && permitted(m) &&
                                           (deadly == null || !deadly.Contains(m)) && affordable(m, out _))
                    .OrderBy(m => hops[m]).ThenBy(m => m).DefaultIfEmpty(-1).First();
                if (best < 0) continue;

                string mapName = _host.Profiles.For(best)?.MapName ?? $"map {best}";
                Begin(best, mapName, $"quest '{target.Quest.QuestName}': kill {target.MonsterName}",
                      displayReason: $"quest: kill {target.MonsterName}");

                if (_brain.Travel == null || !_brain.Travel.Active) continue;

                _host.QuestLog.Record(new QuestLogEntry
                {
                    Bot = Id, Character = world.Name, Class = world.Class.ToString(), Level = world.Level,
                    QuestIndex = target.Quest.Index, Quest = target.Quest.QuestName, Event = "journey",
                    MapIndex = best, Map = mapName, Utc = DateTime.UtcNow,
                    Rewards = $"setting off to kill {target.MonsterName}"
                });
                return true;
            }

            return false;
        }

        private QuestRules _questRules;
        private DateTime _questRulesAt = DateTime.MinValue;

        /// <summary>
        /// This bot's quest rules now: the boss levels, the QuestNPCs filter, and the LEVEL-AWARE
        /// map test - a quest only counts if its monsters live somewhere this bot may hunt: a
        /// quest town, or a reachable map it may explore at its level that has not proved lethal
        /// or too dangerous. Rebuilt at most every 30 seconds (it walks the travel graph).
        /// </summary>
        private QuestRules QuestRulesNow()
        {
            if (_questRules != null && DateTime.UtcNow - _questRulesAt < TimeSpan.FromSeconds(30))
                return _questRules;

            QuestRules rules = new QuestRules
            {
                MiniBossMinLevel = Config.QuestMiniBossMinLevel,
                BossMinLevel = Config.QuestBossMinLevel,
                NpcFilter = Config.QuestNpcFilter()
            };

            WorldModel world = _connection?.World;
            if (world != null && world.MapIndex > 0)
            {
                string mirClass = world.Class.ToString();
                HashSet<int> lethal = _host.Hunting.Lethal(mirClass, world.Level);
                Dictionary<int, int> hops = _host.World.HopCounts(world.MapIndex, world.Class,
                    world.Level, world.Gold, Config.TeleportGoldFloor, world.PKPoints,
                    Config.TeleportMaxGoldPercent, avoid: lethal);
                IReadOnlyCollection<int> towns = _host.Vendors.TownMaps;
                int level = world.Level, maxHealth = world.MaxHealth;

                rules.Huntable = map =>
                    towns.Contains(map) ||
                    (hops.ContainsKey(map) && !lethal.Contains(map) &&
                     !_host.Danger.TooDangerous(map, maxHealth) &&
                     _host.Profiles.WorthExploring(map, level, Config.ExploreLevelsAbove, out _));
            }

            _questRules = rules;
            _questRulesAt = DateTime.UtcNow;
            return rules;
        }

        /// <summary>
        /// The rule that separates mini-bosses from bosses in System.db, which has no flag for it:
        /// at least two spawns on the map, all returning within an hour.
        /// </summary>
        private static bool IsMiniBoss(IReadOnlyList<BossLair> lairsOnMap, int monsterIndex)
        {
            List<BossLair> mine = lairsOnMap.Where(l => l.MonsterIndex == monsterIndex).ToList();
            return mine.Count > 0 && mine.Sum(l => l.Spawns) >= 2 &&
                   mine.All(l => l.RespawnMinutes <= 60);
        }

        /// <summary>
        /// A monster died in view. Logged as a boss kill when it is boss-flagged and this bot hit
        /// it in the last 30 seconds. Classified as a mini-boss by the rule that separates them in
        /// System.db (there is no flag): several spawns that return within the hour.
        /// </summary>
        private void NoteObjectDied(uint objectID)
        {
            // Any death, ours or not: a tracked boss that died is not worth walking to.
            _brain?.NoteBossDied(objectID);

            if (!_attackedAt.TryRemove(objectID, out DateTime hit) ||
                DateTime.UtcNow - hit > TimeSpan.FromSeconds(30))
            {
                if (_attackedAt.Count > 500) _attackedAt.Clear();
                return;
            }

            WorldModel world = _connection?.World;
            WorldObject ob = world?.Objects.FirstOrDefault(x => x.ObjectID == objectID);
            MonsterInfo info = ob == null ? null : BotConnection.Monsters?.Find(ob.MonsterIndex);
            if (info == null || !info.IsBoss) return;

            bool mini = IsMiniBoss(_host.BossLairs.On(world.MapIndex), info.Index);

            // Whatever is already on the floor around it is not this boss's drop. The drop
            // itself arrives as S.ObjectItem just after S.ObjectDied, so it is read a moment later
            // on the bot thread - see WatchBossDrops.
            HashSet<uint> before = new HashSet<uint>(world.Objects
                .Where(x => x.Kind == ObjectKind.Item &&
                            WorldModel.Distance(x.Location, ob.Location) <= BossDropRadius)
                .Select(x => x.ObjectID));

            _lastBossKill = _host.BossKills.Record(new BossKillEntry
            {
                Bot = Id, Character = world.Name, Class = world.Class.ToString(),
                Level = world.Level, Monster = info.MonsterName ?? ob.Name,
                Kind = mini ? "mini-boss" : "boss", MapIndex = world.MapIndex,
                Map = world.MapName, X = ob.Location.X, Y = ob.Location.Y, Utc = DateTime.UtcNow
            });
            _log.Write($"Boss kill: {info.MonsterName} ({(mini ? "mini-boss" : "boss")}) on " +
                       $"{world.MapName} at {ob.Location.X},{ob.Location.Y}.");

            _dropWatch = new BossDropWatch
            {
                Entry = _lastBossKill, MapIndex = world.MapIndex, Centre = ob.Location,
                Before = before, CaptureAt = DateTime.UtcNow.AddSeconds(2),
                Until = DateTime.UtcNow.AddMinutes(4)
            };
        }

        private const int BossDropRadius = 8;

        private sealed class BossDropWatch
        {
            public BossKillEntry Entry;
            public int MapIndex;
            public Point Centre;
            public HashSet<uint> Before;
            public DateTime CaptureAt, Until;
            public bool Captured;
            public readonly Dictionary<uint, (BossDrop Drop, Point At)> Drops =
                new Dictionary<uint, (BossDrop, Point)>();
        }

        private volatile BossDropWatch _dropWatch;

        /// <summary>Cells we sent C.PickUp on, and when - a pickup takes the whole cell.</summary>
        private readonly Dictionary<Point, DateTime> _pickedUpAt = new Dictionary<Point, DateTime>();

        /// <summary>
        /// Build a boss kill's drop list and follow each item until it is taken or gone.
        ///
        /// Sindo's first Ghoul Champion read "health potions and bones", which could not say
        /// whether the boss dropped rubbish or the bot walked past something good. The server
        /// never names a drop's source, so the drop is whatever appears around the corpse in the
        /// two seconds after it dies; each item is then "taken" when it vanishes within five
        /// seconds of a pickup on its cell, "gone" when it vanishes otherwise, and "left" if it
        /// is still there when the watch ends or we leave the map.
        /// </summary>
        private void WatchBossDrops(Decision decision)
        {
            WorldModel world = _connection.World;

            if (decision?.Action == BotAction.Loot)
                _pickedUpAt[world.Location] = DateTime.UtcNow;

            BossDropWatch watch = _dropWatch;
            if (watch == null) return;

            DateTime now = DateTime.UtcNow;
            bool changed = false;

            if (!watch.Captured)
            {
                if (now < watch.CaptureAt) return;
                watch.Captured = true;

                foreach (WorldObject item in world.Objects)
                {
                    if (item.Kind != ObjectKind.Item || watch.Before.Contains(item.ObjectID)) continue;
                    if (WorldModel.Distance(item.Location, watch.Centre) > BossDropRadius) continue;

                    bool gold = item.ItemInfo?.ItemType == ItemType.Currency ||
                                string.Equals(item.Name, "Gold", StringComparison.OrdinalIgnoreCase);
                    watch.Drops[item.ObjectID] = (new BossDrop
                    {
                        Name = item.ItemInfo?.ItemName ?? item.Name ?? "item",
                        Count = Math.Max(1, item.Item?.Count ?? 1),
                        Gold = gold
                    }, item.Location);
                }

                changed = true;
            }

            bool over = now > watch.Until || world.MapIndex != watch.MapIndex;
            HashSet<uint> present = new HashSet<uint>(world.Objects
                .Where(x => x.Kind == ObjectKind.Item).Select(x => x.ObjectID));

            foreach (KeyValuePair<uint, (BossDrop Drop, Point At)> pair in watch.Drops)
            {
                BossDrop drop = pair.Value.Drop;
                if (drop.Outcome != "left" || over || present.Contains(pair.Key)) continue;

                drop.Outcome = _pickedUpAt.TryGetValue(pair.Value.At, out DateTime at) &&
                               now - at <= TimeSpan.FromSeconds(5)
                    ? "taken" : "gone";
                changed = true;
            }

            if (changed || over)
                _host.BossKills.SetDrops(watch.Entry, watch.Drops.Values.Select(x => x.Drop).ToList());

            if (over)
            {
                BossDrop[] drops = watch.Drops.Values.Select(x => x.Drop).ToArray();
                _log.Write($"Boss drop for {watch.Entry.Monster}: " + (drops.Length == 0
                    ? "nothing seen on the ground"
                    : string.Join(", ", drops.GroupBy(d => (d.Name, d.Outcome))
                        .Select(g => $"{g.Key.Name}" +
                                     (g.First().Gold ? $" {g.Sum(d => d.Count):N0}" :
                                      g.Count() > 1 ? $" x{g.Count()}" : "") +
                                     $" ({g.Key.Outcome})"))) + ".");
                _dropWatch = null;
                _pickedUpAt.Clear();
            }

            if (_pickedUpAt.Count > 200) _pickedUpAt.Clear();
        }

        private string _learningBook;
        private DateTime _learnAt;
        private int _magicsBeforeLearn;
        private int _trainingMagic = -1;
        private int _trainingLevelBefore;
        private long _trainingPagesBefore;

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
                // A second book read before the first was judged would overwrite it: Jill read
                // Spirit Sword then Poison Dust three seconds apart and the Spirit Sword outcome
                // was never recorded. The server answers a read at once, so judge it now.
                if (_learningBook != null) ResolvePendingLearn();

                ItemInfo info = _connection.Items.InSlot(decision.PotionSlot)?.Info;
                _learningBook = info?.ItemName ?? $"slot {decision.PotionSlot}";
                _magicsBeforeLearn = _connection.World.KnownMagicCount;
                _learnAt = DateTime.UtcNow;

                // A book for a skill we already know is a level 4 training read.
                MagicInfo magic = _brain.Books?.For(info);
                _trainingMagic = magic != null && _connection.World.Knows(magic.Index) ? magic.Index : -1;
                if (_trainingMagic >= 0)
                {
                    _trainingLevelBefore = _connection.World.MagicLevel(_trainingMagic);
                    _trainingPagesBefore = _connection.World.MagicExperience(_trainingMagic);
                }
                return;
            }

            if (_learningBook == null) return;
            if (DateTime.UtcNow - _learnAt < TimeSpan.FromSeconds(3)) return;

            ResolvePendingLearn();
        }

        private void ResolvePendingLearn()
        {
            string book = _learningBook;
            _learningBook = null;

            if (_trainingMagic >= 0)
            {
                WorldModel world = _connection.World;
                int level = world.MagicLevel(_trainingMagic);
                long pages = world.MagicExperience(_trainingMagic);

                if (level > _trainingLevelBefore)
                {
                    _log.Write($"Trained {book} to skill level {level}.");
                    RecordSkillAttempt($"{book} (level {level})", true);
                }
                else if (pages > _trainingPagesBefore)
                {
                    _log.Write($"Trained {book}: +{pages - _trainingPagesBefore} pages, " +
                               $"{pages} of {(level - 2) * 500} for level {level + 1}.");
                    _host.Progress.Record(new ProgressEntry
                    {
                        Kind = "skill", Bot = Id, Character = world.Name,
                        Class = world.Class.ToString(), Utc = _learnAt,
                        Skill = $"{book} (+{pages - _trainingPagesBefore} pages, " +
                                $"{pages}/{(level - 2) * 500})",
                        Success = true
                    });
                }
                else
                {
                    _log.Write($"Training read of {book} failed its learn roll - the book is gone.");
                    RecordSkillAttempt($"{book} (level {level + 1} pages)", false);
                }

                _trainingMagic = -1;
                return;
            }

            if (_connection.World.KnownMagicCount > _magicsBeforeLearn)
            {
                _log.Write($"Learned {book} - now {_connection.World.KnownMagicCount} skills.");
                RecordSkillAttempt(book, true);
                return;
            }

            _log.Write($"LEARN REFUSED: {book} taught us nothing - still " +
                       $"{_magicsBeforeLearn} skills. The book is gone and the skill is not.");
            RecordSkillAttempt(book, false);
        }

        private void RecordSkillAttempt(string book, bool success)
        {
            WorldModel world = _connection.World;
            _host.Progress.Record(new ProgressEntry
            {
                Kind = "skill", Bot = Id, Character = world.Name,
                Class = world.Class.ToString(), Utc = _learnAt,
                Skill = book, Success = success
            });

            if (success && Config.NotifySkill)
                Notify("skill", $"{world.Name} learned {book}",
                    $"{world.Class}, level {world.Level}", TimeSpan.FromMinutes(1));
        }

        private void Notify(string kind, string title, string message, TimeSpan cooldown) =>
            _host.Notify.Send(Id, _connection?.World?.Name ?? Config.CharacterName ?? Id, kind,
                title, message, cooldown);

        private bool _idleNotified;

        /// <summary>
        /// One alert per drought: fires once the bot has gone NotifyIdleMinutes without experience
        /// while in game, and re-arms only after it earns again.
        /// </summary>
        private void CheckIdleNotify()
        {
            if (Config.NotifyIdleMinutes <= 0 || _connection == null ||
                _connection.Stage != BotStage.InGame || _connection.World.Dead) return;

            TimeSpan idle = DateTime.UtcNow - UnproductiveSince();

            if (idle < TimeSpan.FromMinutes(Config.NotifyIdleMinutes))
            {
                _idleNotified = false;
                return;
            }

            if (_idleNotified) return;
            _idleNotified = true;

            WorldModel world = _connection.World;
            Notify("idle", $"{world.Name} has earned nothing for {(int)idle.TotalMinutes} min",
                $"on {world.MapName} at {world.Location.X},{world.Location.Y} - " +
                $"{_lastDecision?.Action.ToString() ?? "no decision"}", TimeSpan.FromMinutes(60));
        }

        private bool TryExplore(WorldModel world, string mirClass, Dictionary<int, int> hops,
            AffordableCheck affordable, Func<int, bool> permitted, int known)
        {
            HashSet<int> measured = new HashSet<int>(
                _host.Hunting.Best(mirClass, world.Level, 500,
                    Config.HuntingDeathPenaltyPercent / 100.0).Select(x => x.MapIndex));

            // "Measured" and "been there" are not the same thing, and the difference is where a
            // map that kills us on arrival hides. Best() has nothing to say about a map we never
            // survived long enough to measure, so without this it reads as unexplored for ever.
            HashSet<int> lethal = _host.Hunting.Lethal(mirClass, world.Level);
            HashSet<int> wantedHere = WantedDropOnlyBooks(world);

            List<int> options = new List<int>();
            List<int> deferredTowns = new List<int>();
            int tooStrong = 0, tooFar = 0, killers = 0, tooWeak = 0;

            foreach (int mapIndex in hops.Keys)
            {
                if (mapIndex == world.MapIndex) continue;
                if (measured.Contains(mapIndex)) continue;
                if (lethal.Contains(mapIndex)) { killers++; continue; }
                if (!permitted(mapIndex)) continue;
                if (_host.Danger.TooDangerous(mapIndex, world.MaxHealth)) continue;

                if (!_host.Profiles.WorthExploring(mapIndex, world.Level,
                        ExploreReach(mapIndex, wantedHere), out _))
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

                // Towns are still valid vendor stops and emergency recovery grounds. They are
                // merely postponed as EXPERIMENTS once their normal monsters are far below us.
                // A wanted drop-only book exempts a town; vendor-sold books do not enter Wanted.
                if (OutgrownShoppingTown(world, mapIndex, wantedHere, out _))
                {
                    deferredTowns.Add(mapIndex);
                    continue;
                }

                options.Add(mapIndex);
            }

            bool usingDeferredTowns = options.Count == 0 && deferredTowns.Count > 0 &&
                !OutgrownShoppingTown(world, world.MapIndex, wantedHere, out _);
            if (usingDeferredTowns)
            {
                _log.Write($"Travel: only {deferredTowns.Count} outgrown shopping town(s) " +
                           "remain safe and reachable - allowing one as a fallback.");
                options.AddRange(deferredTowns);
            }

            if (options.Count == 0)
            {
                _log.Write($"Travel: nothing new worth exploring - {hops.Count} reachable, " +
                           $"{measured.Count} already measured, {killers} killed us before, " +
                           $"{tooStrong} too strong, {tooWeak} outgrown, " +
                           $"{tooFar} too far to afford.");
                return false;
            }

            if (deferredTowns.Count > 0 && !usingDeferredTowns)
                _log.Write($"Travel: deferred {deferredTowns.Count} outgrown shopping " +
                           "town(s) while exploring stronger grounds.");

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
                HeldBackFloor(mapIndex, measured, hops, world, wantedHere));

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

            // QUEST GOAL: unmeasured maps where accepted quests' targets live, weighted by the
            // quests they advance and by distance from town - not the uniform draw below.
            Dictionary<int, List<string>> questHere = QuestMaps(world);
            List<int> questOptions = options.Where(x => QuestSupply(questHere, x) > 0).ToList();
            if (PursueQuest(questOptions.Count > 0, out int questExploreChance))
            {
                int nearestQuest = questOptions.Min(Distance);
                double[] qweights = questOptions.Select(map =>
                    QuestBonus(QuestSupply(questHere, map)) *
                    (Distance(map) == nearestQuest ? 1.0 : 0.6)).ToArray();
                int questMap = questOptions[WeightedChoice.Pick(qweights, 1, _random, out _)];
                MapProfileEntry questProfile = _host.Profiles.For(questMap);
                string names = QuestNames(questHere, questMap);

                _log.Write($"Travel: exploring {questOptions.Count} quest map(s); " +
                           $"{questExploreChance}% quest chance - choosing quest exploration.");
                Begin(questMap, questProfile?.MapName ?? $"map {questMap}",
                      $"exploring for quests: {names} (median monster level " +
                      $"{questProfile?.MedianLevel ?? 0}, {hops[questMap]} hop(s) away)",
                      displayReason: $"quests: {names}");
                _consecutiveQuestHunts++;
                _consecutiveBookHunts = 0;
                _consecutiveGearHunts = 0;
                return true;
            }

            // Choose the GOAL before the distance ring, just as exploitation chooses it before
            // the XP shortlist. Otherwise a book map at one hop is always discarded behind a
            // zero-hop town, or conversely the old hard book filter chooses books 100% of the
            // time and defeats the configured 70%/20% preference.
            List<int> bookMaps = options
                .Where(x => BookSupply(x, wantedHere) > 0).ToList();
            List<int> ordinaryMaps = options
                .Where(x => BookSupply(x, wantedHere) == 0).ToList();
            bool bookGoal = HuntingGoalChoice.PursueBook(bookMaps.Count > 0,
                ordinaryMaps.Count > 0, _profitPolicy?.Active == true,
                _consecutiveBookHunts, Config.BookHuntChancePercent,
                Config.LossBookHuntChancePercent, Config.MaxConsecutiveBookHunts,
                _random, out int bookChance);
            if (bookMaps.Count > 0)
                _log.Write($"Travel: exploring {bookMaps.Count} book map(s), " +
                           $"{ordinaryMaps.Count} ordinary map(s); {bookChance}% book chance, " +
                           $"{_consecutiveBookHunts} prior book-priority choice(s) - choosing " +
                           $"{(bookGoal ? "book" : "ordinary")} exploration.");
            options = bookGoal ? bookMaps : ordinaryMaps;

            int nearest = options.Min(Distance);
            bool gearFocus = !bookGoal && Config.GearHuntBonusPercent > 0 &&
                             !_recovery.Active && _profitPolicy?.Active != true &&
                             _consecutiveGearHunts < Math.Max(1, Config.MaxConsecutiveGearHunts);
            Dictionary<int, GearTarget> gearTargets = new Dictionary<int, GearTarget>();
            if (gearFocus)
                foreach (int map in options)
                    gearTargets[map] = GearTargetFor(map, world);
            bool nearGear = gearTargets.Any(pair => pair.Value != null &&
                Distance(pair.Key) <= nearest + 1);

            // Keep the normal nearest-town ring, plus a gear source at most one hop beyond it.
            // A distant drop does not justify crossing several unmeasured floors blind.
            options.RemoveAll(map => Distance(map) > nearest &&
                (!nearGear || Distance(map) > nearest + 1 || gearTargets[map] == null));

            string why = known < Config.ExploreUntilMapsKnown
                ? $"exploring - only {known} of {Config.ExploreUntilMapsKnown} maps measured"
                : "exploring anyway";

            if (deeper > 0) why += $", {deeper} deeper floor(s) held back";

            // Named maps first. This biases which map gets measured FIRST; it does not fake a
            // measurement. Seeding invented rates would poison every real observation ranked
            // against them, and the whole point of the memory is that it is observed.
            if (!nearGear)
            {
                foreach (string name in PreferredMapNames())
                {
                    MapInfo info = Globals.MapInfoList?.Binding?.FirstOrDefault(x =>
                        string.Equals(x.Description, name, StringComparison.OrdinalIgnoreCase));

                    if (info == null || !options.Contains(info.Index)) continue;

                    Begin(info.Index, info.Description, $"preferred, {why}",
                          displayReason: bookGoal
                              ? BookChoiceReason(info.Index, wantedHere, exploring: true)
                              : "exploring unmeasured preferred map");
                    _consecutiveBookHunts = bookGoal ? _consecutiveBookHunts + 1 : 0;
                    _consecutiveGearHunts = 0;
                    _consecutiveQuestHunts = 0;
                    return true;
                }
            }

            int chosen;
            if (nearGear)
            {
                HashSet<string> preferred = new HashSet<string>(PreferredMapNames(),
                    StringComparer.OrdinalIgnoreCase);
                double[] weights = options.Select(map =>
                    (Distance(map) == nearest ? 1.0 : 0.6) *
                    (preferred.Contains(_host.Profiles.For(map)?.MapName ?? "") ? 2.0 : 1.0) *
                    (1 + (gearTargets[map]?.Priority ?? 0) * 2)).ToArray();
                chosen = options[WeightedChoice.Pick(weights, 1, _random, out _)];
            }
            else chosen = options[_random.Next(options.Count)];
            MapProfileEntry profile = _host.Profiles.For(chosen);
            GearTarget chosenGear = nearGear ? gearTargets[chosen] : null;

            Begin(chosen, profile?.MapName ?? $"map {chosen}",
                  $"{why} (median monster level {profile?.MedianLevel ?? 0}, " +
                  $"{hops[chosen]} hop(s) away, {Distance(chosen)} from town)" +
                  (chosenGear == null ? "" : $"; {GearChoiceReason(chosenGear)}"),
                  displayReason: chosenGear != null
                      ? GearChoiceReason(chosenGear)
                      : bookGoal
                      ? BookChoiceReason(chosen, wantedHere, exploring: true)
                      : "exploring unmeasured hunting ground");

            _consecutiveBookHunts = bookGoal ? _consecutiveBookHunts + 1 : 0;
            _consecutiveGearHunts = chosenGear != null ? _consecutiveGearHunts + 1 : 0;
            _consecutiveQuestHunts = 0;

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

        /// <summary>The hunting ground the current journey is heading for.</summary>
        private JourneyTarget _journeyTarget;

        /// <summary>An abandoned hunting journey still to be resumed, or null.</summary>
        private JourneyTarget _resume;

        private sealed class JourneyTarget
        {
            public int MapIndex;
            public string MapName = "";
            public string Why = "";
            public string DisplayReason;
            public int Failures;
            public DateTime FirstFailureUtc;
        }

        /// <summary>
        /// Remember a hunting journey that was abandoned on the way, so the next travel decision
        /// resumes it instead of re-rolling. Every failed descent used to end in a fresh weighted
        /// draw after the scroll-out: an assassin forty tiles from Deserted Mine Lv 2's stairs
        /// scrolled home and was sent to Flea Cave, and the deeper floor was never reached.
        ///
        /// Deaths are excluded - the danger memory owns that verdict - and so are planning
        /// failures, which a retry cannot change.
        /// </summary>
        private void NoteJourneyFailure(string message)
        {
            if (_journeyTarget == null || Config.JourneyRetries <= 0) return;
            if (!message.StartsWith("journey abandoned", StringComparison.Ordinal)) return;
            if (message.Contains("died on the way")) return;

            DateTime now = DateTime.UtcNow;

            if (_resume == null || _resume.MapIndex != _journeyTarget.MapIndex ||
                now - _resume.FirstFailureUtc > TimeSpan.FromMinutes(Config.JourneyRetryWindowMinutes))
            {
                _resume = new JourneyTarget
                {
                    MapIndex = _journeyTarget.MapIndex,
                    MapName = _journeyTarget.MapName,
                    Why = _journeyTarget.Why,
                    DisplayReason = _journeyTarget.DisplayReason,
                    FirstFailureUtc = now
                };
            }

            _resume.Failures++;

            if (_resume.Failures > Config.JourneyRetries)
            {
                _log.Write($"Travel: {_resume.Failures} failed journeys to {_resume.MapName} in " +
                           $"{Config.JourneyRetryWindowMinutes} minutes - choosing somewhere else.");
                _resume = null;
                return;
            }

            _log.Write($"Travel: will resume the journey to {_resume.MapName} at the next " +
                       $"travel decision (failure {_resume.Failures} of {Config.JourneyRetries} " +
                       "allowed).");
        }

        /// <summary>Start the remembered journey again, if there is one worth resuming.</summary>
        private bool TryResumeJourney()
        {
            JourneyTarget resume = _resume;

            if (resume == null) return false;

            // Not while the trip that interrupted it still has work: a trip cut short by the
            // low-health rule used to hand straight back to the journey, and Sindo walked into
            // Deserted Mine Lv 3 with no scrolls and nothing bought. The resume waits.
            if (_town != null && (_town.ShortOfSupplies || _town.NeedsVendor)) return false;

            if (_connection.World.MapIndex == resume.MapIndex ||
                DateTime.UtcNow - resume.FirstFailureUtc >
                    TimeSpan.FromMinutes(Config.JourneyRetryWindowMinutes))
            {
                _resume = null;
                return false;
            }

            Begin(resume.MapIndex, resume.MapName,
                $"resuming after {resume.Failures} failed attempt(s): {resume.Why}",
                displayReason: resume.DisplayReason);

            if (_brain.Travel.Active) return true;

            _resume = null;
            return false;
        }

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
        private DateTime _nextTownNeedTravel = DateTime.MinValue;

        private void ConsiderTravel()
        {
            bool active = _town != null && _town.Active;

            bool tripJustFinished = _tripWasActive && !active;
            _tripWasActive = active;

            if (tripJustFinished) SampleGold(force: true, tripCompleted: true);

            if (_resume != null && _connection != null &&
                _connection.World.MapIndex == _resume.MapIndex)
            {
                _log.Write($"Travel: reached {_resume.MapName} after {_resume.Failures} failed " +
                           "attempt(s).");
                _resume = null;
            }

            UpdateRecoveryState(tripJustFinished);

            bool recoveryRoute = !active && RecoveryRouteNeeded();

            // A storage-only trip can begin and abort in the same tick when no town scroll is in
            // the bag, so tripJustFinished never observes an active phase. Promote the published
            // storage need directly into a journey to the nearest safe town instead.
            //
            // The same is true of a full bag, broken gear and running out of potions, and it was
            // only ever handled for storage: Sindo sat in Deserted Mine Lv 3 at 48 of 48 slots with
            // no town scroll for over an hour, the trip logging "no town scroll to reach one" and
            // nothing ever walking it out. Rate-limited because, unlike storage, a bot may have no
            // reachable town and must not re-plan every tick.
            if (_town != null && _town.WalkToTownRequested && _connection != null &&
                _host.Vendors.TownMaps.Contains(_connection.World.MapIndex))
                _town.WalkToTownRequested = false;

            bool townNeed = _town != null &&
                            (_town.NeedsStorage || _town.NeedsVendor || _town.ShortOfSupplies ||
                             _town.WalkToTownRequested);
            bool storageTravel = !active &&
                                 _connection != null &&
                                 _connection.Stage == BotStage.InGame &&
                                 !_connection.World.Dead &&
                                 townNeed &&
                                 DateTime.UtcNow >= _nextTownNeedTravel &&
                                 (_brain?.Travel == null || !_brain.Travel.Active) &&
                                 !_host.Vendors.TownMaps.Contains(_connection.World.MapIndex);
            if (storageTravel) _nextTownNeedTravel = DateTime.UtcNow.AddSeconds(30);

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
                                (_host.Profiles.OutgrownBy(_connection.World.MapIndex,
                                     _connection.World.Level, Config.HuntLevelsBelow,
                                     out outgrownHereWhy) ||
                                 OutgrownShoppingTown(_connection.World,
                                     _connection.World.MapIndex,
                                     WantedDropOnlyBooks(_connection.World),
                                     out outgrownHereWhy));

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

            // A quest errand that just ended (Bichon: handed in, accepted, hunted) is the moment
            // to move on - it is not a town trip, so nothing else would reconsider travel.
            bool questFinished = _questErrand != null && _questErrand.JustFinished;
            if (questFinished) _questErrand.JustFinished = false;
            if (_fameErrand != null && _fameErrand.JustFinished)
            {
                _fameErrand.JustFinished = false;
                questFinished = true;           // same meaning: an errand ended, move on
            }

            // While the errand owns the bot here, "this town is outgrown / pays nothing" must not
            // walk it off mid-errand; it ends within minutes and triggers travel itself.
            if (_questErrand != null && _questErrand.OwnsMovement ||
                _fameErrand != null && _fameErrand.OwnsMovement)
            {
                barren = false;
                outgrownHere = false;
                unproductive = false;
            }

            // DO QUESTS, second half: the town trip it forced is over (or never started - no
            // scroll, so the walk-to-town path wanted to fire instead), so head for the quest NPC.
            if (_questRequestPending && !active && _connection != null &&
                _connection.Stage == BotStage.InGame && !_connection.World.Dead)
            {
                MapInfo questMap = NearestQuestMap(_connection.World);

                if (questMap == null || _connection.World.MapIndex == questMap.Index)
                    _questRequestPending = false;          // there already: the errand takes over
                else if (tripJustFinished || storageTravel ||
                         DateTime.UtcNow - _questRequestAt > TimeSpan.FromMinutes(2))
                {
                    _questRequestPending = false;
                    _town?.ClearReturn();
                    if (_town != null) _town.WalkToTownRequested = false;
                    _log.Write($"Do quests: heading for {questMap.Description}.");
                    StartTravel(questMap.Index.ToString());
                    return;
                }
            }

            // FAME: after a successful town trip, when Fame Points cover the next rank, go to the
            // fame NPC - level 45+ (the Frost Village gate), enough gold for both fares on top of
            // the teleport floor, a real route, nothing else owed. Never mid-hunt.
            if (tripJustFinished && _town != null && _town.LastTripTraded && !recoveryRoute &&
                !townNeed && _connection != null && !_connection.World.Dead && Config.EnableFame &&
                _fameErrand != null && _host.Fame.Ready && _host.Fame.Map != null &&
                (_brain?.Travel == null || !_brain.Travel.Active) &&
                (_questErrand == null || !_questErrand.Active))
            {
                WorldModel fw = _connection.World;
                FameInfo next = _fameErrand.Affordable(fw);
                int fameMap = _host.Fame.Map.Index;

                if (next != null && fw.MapIndex != fameMap && fw.Level >= FameMinLevel &&
                    fw.Gold >= FameRouteGold + Config.TeleportGoldFloor &&
                    DateTime.UtcNow - _lastFameTrip >= TimeSpan.FromMinutes(30) &&
                    DateTime.UtcNow >= _fameNoRouteUntil)
                {
                    Dictionary<int, int> fameHops = _host.World.HopCounts(fw.MapIndex, fw.Class,
                        fw.Level, fw.Gold, Config.TeleportGoldFloor, fw.PKPoints,
                        Config.TeleportMaxGoldPercent);

                    _lastFameTrip = DateTime.UtcNow;

                    if (!fameHops.ContainsKey(fameMap))
                    {
                        _fameNoRouteUntil = DateTime.UtcNow.AddHours(2);
                        _log.Write($"Fame: {fw.FamePoints:N0} FP covers {next.Name}, but there is no " +
                                   $"route to {_host.Fame.Map.Description} - trying again in 2 hours.");
                    }
                    else
                    {
                        _log.Write($"Fame: {fw.FamePoints:N0} FP covers {next.Name} ({next.Cost:N0}) - " +
                                   $"heading for {_host.Fame.Map.Description}.");
                        _town.ClearReturn();
                        StartTravel(fameMap.ToString());
                        return;
                    }
                }
            }

            // QUEST TOWNS: after a successful town trip, go where quest work is waiting - a hand-in
            // (every 20 minutes at most) or quests to take (hourly) at another town's NPCs. Only
            // then: never mid-hunt, never instead of supplies or recovery.
            if (tripJustFinished && _town != null && _town.LastTripTraded && !recoveryRoute &&
                !townNeed && _connection != null && !_connection.World.Dead &&
                Config.EnableQuests && _questErrand != null && !_questErrand.HasWork(_connection.World) &&
                (_brain?.Travel == null || !_brain.Travel.Active))
            {
                WorldModel qw = _connection.World;
                MapInfo questTown = NearestQuestMap(qw);
                bool handIn = questTown != null && _host.Quests.ReadyToHandIn(qw)
                    .Any(q => q.Quest.FinishNPC?.Region?.Map?.Index == questTown.Index);
                TimeSpan every = handIn ? TimeSpan.FromMinutes(20) : TimeSpan.FromMinutes(60);

                if (questTown != null && questTown.Index != qw.MapIndex &&
                    DateTime.UtcNow - _lastQuestTownTrip >= every)
                {
                    _lastQuestTownTrip = DateTime.UtcNow;
                    _log.Write($"Quest: {(handIn ? "a quest to hand in" : "quests to take")} at " +
                               $"{questTown.Description} - heading there.");
                    _town.ClearReturn();
                    StartTravel(questTown.Index.ToString());
                    return;
                }
            }

            if (!tripJustFinished && !barren && !outgrownHere && !storageTravel &&
                !unproductive && !recoveryRoute && !questFinished)
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

            // A hunting journey abandoned on the way (usually followed by a scroll-out and this
            // town trip) is resumed before any fresh choice. Not while recovering: poverty
            // recovery owns the destination then.
            if (!_recovery.Active && TryResumeJourney())
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

        private string BookChoiceReason(int mapIndex, HashSet<int> wanted, bool exploring)
        {
            int count = BookSupply(mapIndex, wanted);
            return $"{(exploring ? "exploring for" : "looking for")} {count} skill " +
                   $"{(count == 1 ? "book" : "books")}: " +
                   BookNames(mapIndex, wanted);
        }

        private int MapTripKillProxy() => _mapTrip == null || !_mapTrip.ArrivedUtc.HasValue ||
            _connection == null ? 0 :
            (int)Math.Clamp(_connection.World.PositiveExperienceAwards - _mapTripXpAwardBaseline,
                0L, int.MaxValue);

        private void CloseMapTrip(string reason)
        {
            if (_mapTrip == null) return;
            int credited = _connection != null &&
                _connection.World.MapIndex == _mapTrip.MapIndex
                ? MapTripKillProxy() : _mapTrip.CreditedKills;
            _host.MapTrips.Close(_mapTrip, reason, credited);
            _mapTrip = null;
        }

        private void ObserveMapTrip()
        {
            if (_mapTrip == null || _connection == null) return;
            WorldModel world = _connection.World;
            if (world.MapIndex == _mapTrip.MapIndex)
            {
                if (!_mapTrip.ArrivedUtc.HasValue)
                    _mapTripXpAwardBaseline = world.PositiveExperienceAwards;
                _host.MapTrips.Observe(_mapTrip, world.MapIndex, MapTripKillProxy());
            }
            else if (_mapTrip.ArrivedUtc.HasValue)
            {
                string reason = world.Dead ? "died" :
                    _town != null && _town.Active ? "town trip: " + _town.Status :
                    _brain?.Travel != null && _brain.Travel.Active ? "travelling to another map" :
                    "left the selected map";
                CloseMapTrip(reason);
            }
        }

        private void NoteFarmingChoice(int mapIndex, string mapName, string reason)
        {
            // A trip to restock is not a new farming decision. Keep the selected destination
            // visible while the character is in town or crossing intermediate cave floors.
            _farmingDestinationName = mapName ?? "";
            _farmingDestinationReason = reason ?? "";
            if (_mapTrip != null && _mapTrip.MapIndex == mapIndex &&
                _mapTrip.SelectionReason == _farmingDestinationReason) return;
            CloseMapTrip("new selection: " + _farmingDestinationName);
            WorldModel world = _connection?.World;
            _mapTrip = _host.MapTrips.Start(Id, world?.Name ?? Config.CharacterName,
                world?.Class.ToString() ?? "", mapIndex, _farmingDestinationName,
                _farmingDestinationReason);
            if (world != null && world.MapIndex == mapIndex) ObserveMapTrip();
        }

        private void Begin(int mapIndex, string mapName, string why,
            bool farmingChoice = true, string displayReason = null)
        {
            // Refreshed per journey rather than held, because the set is a function of our level
            // and of what has happened since - both of which move.
            _brain.Travel.Avoid = _host.Hunting.Lethal(_connection.World.Class.ToString(),
                _connection.World.Level);

            // Deaths make a map costly to CROSS (150 tiles each, capped), so a safe route of
            // similar length wins. Snapshot now: the planner runs once per journey or re-plan.
            Dictionary<int, int> deaths = _host.Hunting.DeathsByMap(
                _connection.World.Class.ToString(), _connection.World.Level);
            _brain.Travel.DangerTiles = map =>
                deaths.TryGetValue(map, out int n) ? Math.Min(n * 150, 1500) : 0;
            _brain.Travel.Detour = "";

            if (_brain.Travel.Begin(_connection.World, mapIndex, mapName))
            {
                // Only hunting grounds are resumed after a failure; storage and return errands are
                // re-derived from state anyway. A different hunting choice supersedes a pending one.
                _journeyTarget = farmingChoice && _brain.Travel.Active
                    ? new JourneyTarget
                    {
                        MapIndex = mapIndex,
                        MapName = mapName ?? "",
                        Why = why ?? "",
                        DisplayReason = displayReason ?? why
                    }
                    : null;

                if (farmingChoice && _resume != null && _resume.MapIndex != mapIndex) _resume = null;

                if (farmingChoice)
                    NoteFarmingChoice(mapIndex, mapName, displayReason ?? why);
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

            // BANK THE EARNINGS BEFORE THE DEATH, OR THE MAP IS CHARGED AND NEVER CREDITED.
            //
            // RecordDeath fires every time, unconditionally. CloseExperienceSample refuses any
            // window shorter than MinimumSampleMinutes, and nothing closed the window on death at
            // all - so a map that killed us three minutes after we arrived kept the death and
            // threw the experience away. Deaths were therefore recorded in full and earnings only
            // sometimes, and the faster a map killed us the more completely it was slandered: the
            // rate stayed at zero, which excludes the map from Best(), and three such visits put
            // it on the Lethal() list, which excludes it from exploration too.
            //
            // Sixteen entries across the fleet were in exactly that state, including Ant Cave
            // North - written off at zero by the wizard while the warrior measured 680,656
            // exp/hour there.
            //
            // Forced past the minimum because the window did not end early by accident: it ended
            // because we died, which is the one case where the short window is the whole truth
            // about the visit. The rate it produces is honest and Record() weights it by its own
            // duration, so three minutes joins a 1.9 hour history at three minutes' worth.
            //
            // Once the map has a rate it belongs to Score() and its death discount, which is what
            // HuntingMemory.Lethal already says should happen to anywhere that has ever paid us.
            CloseExperienceSample("died", force: true);
            ResetExperienceSample();

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

            if (Config.NotifyDeath)
                Notify("death", $"{world.Name} died",
                    $"killed by {killer} on {world.MapName} at level {world.Level}",
                    TimeSpan.FromMinutes(10));

            // The window is void: time spent dead and running back is not hunting.
            ResetExperienceSample();

            // Close the selected-map trip with the death in it. A death before arrival used to
            // leave the row open until the next choice, which then closed it as "new selection" -
            // Mirbot died twice crossing Phantom Forest and the trips table showed neither.
            if (_mapTrip != null)
                CloseMapTrip(_mapTrip.ArrivedUtc.HasValue && world.MapIndex == _mapTrip.MapIndex
                    ? $"died ({killer})"
                    : $"died on the way in {world.MapName} ({killer})");

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
        private void CloseExperienceSample(string why, bool force = false)
        {
            if (_sampleStarted == DateTime.MinValue || _sampleMapIndex <= 0) return;
            if (_connection == null) return;

            TimeSpan elapsed = DateTime.UtcNow - _sampleStarted;

            // The minimum exists to keep noise out of the average. It must not also keep the
            // truth out: see the death handler for what discarding these windows cost us.
            if (!force &&
                elapsed < TimeSpan.FromMinutes(Math.Max(1, Config.MinimumSampleMinutes))) return;

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
            if (Config.NotifyFault)
                Notify("fault", $"{Id} faulted", ex.Message, TimeSpan.FromMinutes(30));

            try { Teardown("faulted"); } catch { }

            _state = BotRunState.Faulted;
        }

        private void Teardown(string why)
        {
            if (_connection == null) return;

            CloseMapTrip(why);

            SampleXp(force: true);

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
        private readonly List<GoldSample> _goldHistory = new List<GoldSample>();
        private ProfitPolicy _profitPolicy;
        private long _capitalSpent;
        private string _moneyDiagnostic = "";
        private int _lastTravelPenaltyPercent;
        private int _consecutiveBookHunts;
        private int _consecutiveGearHunts;
        private int _consecutiveQuestHunts;

        /// <summary>
        /// Map -> the accepted, unfinished quests with a (non-boss) target living there, counting
        /// only maps this bot may hunt (QuestRules.Huntable). Bosses are TryQuestBossJourney's.
        /// </summary>
        private Dictionary<int, List<string>> QuestMaps(WorldModel world)
        {
            Dictionary<int, List<string>> maps = new Dictionary<int, List<string>>();
            if (!Config.EnableQuests || Config.QuestHuntChancePercent <= 0) return maps;

            QuestRules rules = QuestRulesNow();
            foreach (QuestKillTarget target in _host.Quests.KillTargets(world, rules.Huntable))
            {
                if (target.IsBoss) continue;
                foreach (int map in target.Maps)
                {
                    if (!maps.TryGetValue(map, out List<string> quests)) maps[map] = quests = new List<string>();
                    if (!quests.Contains(target.Quest.QuestName)) quests.Add(target.Quest.QuestName);
                }
            }
            return maps;
        }

        /// <summary>Distinct quests a map advances (the QuestSupply of the plan).</summary>
        private static int QuestSupply(Dictionary<int, List<string>> questMaps, int map) =>
            questMaps.TryGetValue(map, out List<string> quests) ? quests.Count : 0;

        /// <summary>Score multiplier for the quest goal: +50% a quest, at most three counted.</summary>
        private static double QuestBonus(int supply) => 1 + 0.5 * Math.Min(3, supply);

        /// <summary>
        /// The third hunting goal. Rolled BEFORE the book/ordinary split - a multiplier applied
        /// after it could never rescue a quest map from the pool that was not chosen.
        /// </summary>
        private bool PursueQuest(bool hasQuestMap, out int chance)
        {
            chance = 0;
            if (!hasQuestMap || _recovery.Active) return false;
            if (_consecutiveQuestHunts >= Math.Max(1, Config.MaxConsecutiveQuestHunts)) return false;
            chance = Math.Clamp(Config.QuestHuntChancePercent, 0, 100);
            return chance > 0 && _random.Next(100) < chance;
        }

        private static string QuestNames(Dictionary<int, List<string>> questMaps, int map) =>
            questMaps.TryGetValue(map, out List<string> quests)
                ? string.Join(", ", quests.Take(3)) + (quests.Count > 3 ? ", ..." : "")
                : "";
        private string _xpSession = "";
        private DateTime _nextXpSample = DateTime.MinValue;
        private decimal _lastXpTotal = decimal.MinValue;
        private XpPace _xpPace;

        private void SampleXp(bool force = false)
        {
            if (_connection == null || _connection.World.SelfID == 0) return;
            WorldModel world = _connection.World;
            DateTime now = DateTime.UtcNow;
            if (!force && now < _nextXpSample) return;
            _host.Xp.Append(new XpPoint
            {
                Bot = Id, Character = world.Name, Session = _xpSession,
                Utc = now, Total = world.TotalExperienceNet
            });
            _lastXpTotal = world.TotalExperienceNet;
            _nextXpSample = now + XpLog.SampleInterval;
            _xpPace = _host.Xp.Measure(Id, world.Name, now);
        }

        /// <summary>
        /// One point on the gold curve, on the ordinary cadence or whenever the balance jumps.
        ///
        /// Called from the bot thread BEFORE the snapshot's staleness check, deliberately: a bot
        /// that has wedged still has gold worth plotting, and hanging this off snapshot rebuilds
        /// would stop sampling at the moment the chart became interesting.
        /// </summary>
        private void SampleGold(bool force = false, bool tripCompleted = false)
        {
            if (_connection == null || _connection.World.SelfID == 0) return;

            long gold = _connection.World.Gold;
            DateTime now = DateTime.UtcNow;

            bool due = now >= _nextGoldSample;
            bool jumped = _lastGoldSampled != long.MinValue &&
                          Math.Abs(gold - _lastGoldSampled) >= GoldLog.NotableChange;

            if (!force && !due && !jumped) return;

            _nextGoldSample = now + GoldLog.SampleInterval;
            _lastGoldSampled = gold;
            _host.Gold.Append(Id, gold, now, _capitalSpent);
            _goldHistory.Add(new GoldSample(now, gold, _capitalSpent));
            DateTime keepAfter = now.AddHours(-Math.Max(48, Config.LossWatchHours));
            int remove = 0;
            while (remove + 1 < _goldHistory.Count && _goldHistory[remove + 1].Utc < keepAfter)
                remove++;
            if (remove > 0) _goldHistory.RemoveRange(0, remove);
            EvaluateProfit(now, tripCompleted);
        }

        private void EvaluateProfit(DateTime now, bool tripCompleted)
        {
            if (_profitPolicy == null) return;
            GoldTrendResult trend = GoldTrend.Measure(_goldHistory, now, Config.LossWatchHours);
            string transition = _profitPolicy.Update(trend, now, tripCompleted,
                Config.LossWatchHours, Config.LossWatchDropGold,
                Config.LossWatchRecoverGold, Config.LossWatchMaxHours);
            if (transition.Length > 0) _log.Write("Money: " + transition);
            string slope = trend.Valid ? $"adjusted {trend.Change:+#,0;-#,0;0} over " +
                $"{trend.CoverageHours:N1}h" : $"warming up ({trend.CoverageHours:N1}h coverage)";
            string mode = _profitPolicy.Active ? "loss watch active" :
                now < _profitPolicy.CooldownUntilUtc ? "capped/cooling down" : "baseline";
            _moneyDiagnostic = $"{slope}; capital adjustment +{trend.CapitalAdjustment:N0}; " +
                $"{mode}; latest travel death penalty {_lastTravelPenaltyPercent}% " +
                "(manual gold grants unclassified)";
        }

        /// <summary>How long a snapshot may sit unrebuilt while the world is not moving.</summary>
        private static readonly TimeSpan StaleSnapshotAfter = TimeSpan.FromSeconds(1);

        private DateTime _lastSnapshotAt = DateTime.MinValue;

        private void PublishIfDue()
        {
            SampleGold();
            SampleXp();

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
            if (_questErrand != null && _questErrand.Active) return "quest";
            if (_fameErrand != null && _fameErrand.Active) return "quest";
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
                    ConfigResult = _configResult,
                    FarmingDestinationName = _farmingDestinationName,
                    FarmingDestinationReason = _farmingDestinationReason
                };

            WorldModel world = _connection.World;
            Backpack items = _connection.Items;

            // The live step first, the wander target second. A town errand or a travel leg sets
            // Destination on the decision; ordinary roaming does not, and only the brain knows
            // where it drifted off to.
            System.Drawing.Point destination = _lastDecision != null && _lastDecision.Destination != System.Drawing.Point.Empty
                ? _lastDecision.Destination
                : _brain?.RoamTarget ?? System.Drawing.Point.Empty;

            (System.Drawing.Point[] Cells, string Kind) route =
                _brain?.CurrentRoute ?? (Array.Empty<System.Drawing.Point>(), "");

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

            // WorldModel already tracks ownership and pet health for combat decisions. Copy the
            // same live objects into the immutable web snapshot instead of leaving Pets empty.
            List<PetStatus> pets = new List<PetStatus>();
            foreach (WorldObject pet in world.OwnPets)
                pets.Add(new PetStatus
                {
                    Name = pet.Name ?? "",
                    Level = pet.Level,
                    Health = pet.Health,
                    MaxHealth = pet.MaxHealth,
                    HealthPercent = pet.MaxHealth > 0
                        ? (int)Math.Clamp((long)pet.Health * 100 / pet.MaxHealth, 0, 100)
                        : 0,
                    Distance = WorldModel.Distance(world.Location, pet.Location)
                });

            // SKILLS. Ordered by the character level each becomes available at, which is the order
            // a player thinks of them in and puts the newest acquisition at the bottom.
            List<SkillStatus> skills = new List<SkillStatus>();

            foreach (ClientUserMagic magic in world.Magics)
            {
                if (magic?.Info == null) continue;

                MagicInfo info = magic.Info;

                // The threshold for the NEXT skill level, not the current one. Levels 1-3 come from
                // use; level 4 (Globals.MagicMaxLevel) only from reading dropped books at level 3,
                // where Experience counts book pages against (level - 2) * 500. At 4 there is no
                // next and both figures are zero rather than a bar that can never fill.
                long next = magic.Level switch
                {
                    0 => info.Experience1,
                    1 => info.Experience2,
                    2 => info.Experience3,
                    _ when magic.Level < Globals.MagicMaxLevel => (magic.Level - 2) * 500L,
                    _ => 0
                };

                int needLevel = magic.Level switch
                {
                    0 => info.NeedLevel1,
                    1 => info.NeedLevel2,
                    2 => info.NeedLevel3,
                    _ => 0
                };

                bool supported = SpellBook.IsSupported(info.Magic, out string why);

                if (supported && magic.ItemRequired)
                {
                    supported = false;
                    why = "needs an item the bot does not carry";
                }

                skills.Add(new SkillStatus
                {
                    Name = info.Name ?? "",
                    School = info.School.ToString(),
                    Level = magic.Level,
                    Experience = magic.Experience,
                    NextExperience = next,
                    Percent = next > 0
                        ? (int)Math.Clamp(magic.Experience * 100 / next, 0, 100)
                        : 100,
                    NeedLevel = needLevel,
                    Usable = world.Level >= info.NeedLevel1,
                    Castable = supported,
                    Use = supported ? "active"
                        : SpellBook.IsPassive(info.Magic) ? "passive" : "unused",
                    Why = why
                });
            }

            skills.Sort((a, b) => a.Name.CompareTo(b.Name));

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
                XpRatePerHour = _xpPace.PerHour?.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                XpCoverageSeconds = _xpPace.CoverageSeconds,
                EstimatedNextLevelSeconds = !world.MaxExperienceKnown || world.AtMaxLevel ||
                    _xpPace.PerHour == null || _xpPace.PerHour <= 0 ? null :
                    (long?)Math.Min(long.MaxValue,
                        Math.Ceiling(Math.Max(0m, world.MaxExperience - world.Experience) * 3600m /
                                     _xpPace.PerHour.Value)),
                Gold = world.Gold.ToString(),

                MapIndex = world.MapIndex,
                MapName = world.MapName,
                X = world.Location.X,
                Y = world.Location.Y,
                InSafeZone = world.InSafeZone,
                FarmingDestinationName = _farmingDestinationName,
                FarmingDestinationReason = _farmingDestinationReason,
                DestX = destination == System.Drawing.Point.Empty ? (int?)null : destination.X,
                DestY = destination == System.Drawing.Point.Empty ? (int?)null : destination.Y,
                Route = route.Cells.SelectMany(p => new[] { p.X, p.Y }).ToArray(),
                RouteKind = route.Kind,
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
                MoneyDiagnostic = _moneyDiagnostic,

                Skills = skills,
                Buffs = world.Buffs.Select(b => new BuffStatus
                {
                    Name = b.Type == BuffType.ItemBuff
                        ? Globals.ItemInfoList?.Binding?.FirstOrDefault(i => i.Index == b.ItemIndex)?.ItemName
                          ?? "Item buff"
                        : b.Type == BuffType.Fame
                        ? "Fame: " + (_host.Fame.Current(world.FameIndex)?.Name ?? "rank")
                        : b.Type.ToString(),
                    Permanent = b.RemainingTime == TimeSpan.MaxValue,
                    RemainingSeconds = world.BuffRemaining(b) is TimeSpan left ? (int)left.TotalSeconds : null,
                    Paused = b.Pause,
                    Stats = FlattenStats(b.Stats)
                }).OrderBy(b => b.Name).ToList(),
                Quests = world.Quests.Where(q => q.Quest != null).Select(q => new QuestStatus
                {
                    Name = q.Quest.QuestName ?? "",
                    Progress = QuestBook.ProgressText(q),
                    Completed = q.Completed,
                    ReadyToHandIn = !q.Completed && QuestBook.AllTasksDone(q),
                    Daily = q.Quest.QuestType == QuestType.Daily
                }).OrderBy(q => q.Completed).ThenBy(q => q.Name).ToList(),
                QuestStatusText = _questErrand?.Status ?? "",
                HuntGold = world.HuntGold,
                StoreStatusText = StoreStatusText(),
                FamePoints = world.FamePoints,
                FameStatusText = FameStatusText(),
                PetMode = world.PetMode.ToString(),
                Pets = pets,
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
