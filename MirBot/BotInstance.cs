using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    public enum BotCommandKind { Start, Stop, ForceTownTrip, Revive, Travel }

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
            _brain = new ScriptedBrain(Config)
            {
                Books = _host.Books,
                Maps = _host.Maps,
                Danger = _host.Danger,
                Nav = _host.Nav
            };
            _brain.Travel = new Journey(_host.World)
            {
                TalkRange = Config.VendorTalkRange,
                GoldFloor = Config.TeleportGoldFloor
            };
            _town = new TownTrip(Config, _host.Vendors, _host.Books);
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

        private void RunBrain()
        {
            Decision decision = _brain.Decide(_connection.World, _connection.Items);
            if (decision == null || decision.Action == BotAction.Idle) return;

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
            Dictionary<int, int> hops = _host.World.HopCounts(world.MapIndex, world.Class,
                world.Level, world.Gold, Config.TeleportGoldFloor);

            // Too poor to be anywhere but home.
            //
            // Poverty is the one state in which wandering is actively harmful: the bot cannot buy
            // potions, cannot buy a way back, and cannot pay a repair bill, so every problem it
            // meets is permanent. Restricting it to the named town maps is the same whitelist
            // reasoning that fixed vendor selection - a list cannot be outsmarted by the next
            // clever ranking rule.
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

                // Said out loud rather than silently endured. "Cannot leave until rich, cannot get
                // rich because home is poor" is exactly the deadlock shape that has bitten this
                // codebase repeatedly, and the log is what makes it visible if it ever happens.
                _log.Write($"Travel: only {world.Gold} gold (poor below {Config.PoorGold}) - " +
                           "restricted to the town maps until that improves.");
            }

            bool Affordable(int mapIndex, out string why)
            {
                why = "";

                if (!hops.TryGetValue(mapIndex, out int distance))
                {
                    why = "no route";
                    return false;
                }

                long needed = distance * Config.TravelGoldPerHop;

                if (world.Gold < needed)
                {
                    why = $"{distance} hops needs {needed:N0} gold, we have {world.Gold:N0}";
                    return false;
                }

                return true;
            }

            bool Permitted(int mapIndex) => allowed == null || allowed.Contains(mapIndex);

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
            List<HuntingEntry> candidates = new List<HuntingEntry>();

            foreach (HuntingEntry entry in _host.Hunting.Best(mirClass, world.Level,
                         Math.Max(1, Config.HuntingChoices)))
            {
                if (entry.MapIndex == world.MapIndex) continue;
                if (!Permitted(entry.MapIndex)) continue;
                if (_host.Danger.TooDangerous(entry.MapIndex, world.MaxHealth)) continue;
                if (!Affordable(entry.MapIndex, out _)) continue;

                candidates.Add(entry);
            }

            if (candidates.Count > 0)
            {
                HuntingEntry pick = candidates[_random.Next(candidates.Count)];

                Begin(pick.MapIndex, pick.MapName,
                      $"{pick.AverageExperiencePerHour:N0} exp/hour average over " +
                      $"{pick.HoursSampled:N1}h, {pick.Deaths} death(s) - " +
                      $"chosen from {candidates.Count} candidate(s)");
                return;
            }

            // Nothing worth exploiting. Explore even if the roll said otherwise.
            if (!wantExplore && TryExplore(world, mirClass, hops, Affordable, Permitted, known)) return;

            _log.Write($"Travel: nowhere to go - {hops.Count} maps reachable, " +
                       $"{known} measured, {(poor ? "restricted to town maps, " : "")}" +
                       "none affordable, safe and unmeasured.");
        }

        /// <summary>
        /// Go somewhere we have never measured, to find out what it is worth.
        ///
        /// The database filter is what makes this safe to do often. MonsterMemory only knows maps
        /// that have already hurt us, so it is silent about anywhere new - which is exactly when the
        /// question is asked. MapProfile reads what actually spawns on a map before we go.
        /// </summary>
        private bool TryExplore(WorldModel world, string mirClass, Dictionary<int, int> hops,
            AffordableCheck affordable, Func<int, bool> permitted, int known)
        {
            HashSet<int> measured = new HashSet<int>(
                _host.Hunting.Best(mirClass, world.Level, 500).Select(x => x.MapIndex));

            List<int> options = new List<int>();
            int tooStrong = 0, tooFar = 0;

            foreach (int mapIndex in hops.Keys)
            {
                if (mapIndex == world.MapIndex) continue;
                if (measured.Contains(mapIndex)) continue;
                if (!permitted(mapIndex)) continue;
                if (_host.Danger.TooDangerous(mapIndex, world.MaxHealth)) continue;

                if (!_host.Profiles.WorthExploring(mapIndex, world.Level,
                        Config.ExploreLevelsAbove, out _))
                {
                    tooStrong++;
                    continue;
                }

                if (!affordable(mapIndex, out _)) { tooFar++; continue; }

                options.Add(mapIndex);
            }

            if (options.Count == 0)
            {
                _log.Write($"Travel: nothing new worth exploring - {hops.Count} reachable, " +
                           $"{measured.Count} already measured, {tooStrong} too strong, " +
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
                             world.Level, world.Gold, Config.TeleportGoldFloor))
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

            if (!tripJustFinished && !barren) return;

            if (barren)
            {
                // Rate-limited: a bot that cannot find anywhere to go must not retry every tick.
                _nextBarrenCheck = DateTime.UtcNow.AddMinutes(2);

                _log.Write($"Nothing spawns on {_connection.World.MapName} - looking for somewhere " +
                           "to hunt.");
            }
            if (_connection == null || _connection.Stage != BotStage.InGame) return;
            if (_connection.World.Dead) return;

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
            if (_brain.Travel.Begin(_connection.World, mapIndex, mapName))
            {
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
                _wasDead = false;
                _reviveAt = DateTime.MinValue;
                return;
            }

            if (!_wasDead) PostMortem();

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
                    pair.Value.Count, pair.Value.Info.ItemType.ToString(),
                    pair.Value.Flags == 0 ? "" : pair.Value.Flags.ToString(),
                    pair.Value.Info.CanSell));

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
                world.MapIndex, world.MapName, world.Location.X, world.Location.Y, world.InSafeZone,
                world.BagWeight, world.MaxBagWeight, world.WeightPercent,
                _town?.Phase.ToString() ?? "", _town?.Status ?? "",
                (int)(DateTime.UtcNow - _startedAt).TotalSeconds,
                _decisions, _connection.ResyncCount, _brain?.DetoursTaken ?? 0,
                _connection.DroppedPackets, world.KnownMagicCount,
                _brain?.Spells.CastsIssued ?? 0, _brain?.FightsAbandoned ?? 0,
                _brain?.AvoidedDangerous ?? 0, _brain?.LearnedBlockedCells ?? 0,
                _town?.BankDiagnostic ?? "",
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
