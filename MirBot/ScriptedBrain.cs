using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Library;

namespace MirBot
{
    public enum BotAction
    {
        Idle, Equip, Heal, Flee, Attack, Loot, Approach, Roam,
        TownTeleport, AutoPath, AutoPathPoint, AutoPathCancel, WalkTo, Deposit, Withdraw,
        NPCRepair, LearnBook, Logout, NPCCall, NPCButton, NPCSell, NPCBuy, NPCClose,
        MagicToggle, Unlock
    }

    public sealed class Decision
    {
        public BotAction Action;
        public string Reason;

        /// <summary>
        /// The stable noun for this action, used as the collapse key in the action
        /// history. Reason carries the volatile detail ("Cow at 5") which changes every
        /// tick and so cannot group repeats; Subject ("Cow") can.
        /// </summary>
        public string Subject;
        public uint TargetID;
        public MirDirection Direction;
        public int PotionSlot = -1;
        public EquipRequest Equip;

        /// <summary>Tiles to move: 1 walks, 2 runs. 3 requires a horse and is rejected without one.</summary>
        public int Distance = 1;

        public int ButtonID;

        /// <summary>Attack: the skill to ride along on this swing. MagicToggle: the skill to enable.</summary>
        public MagicType Magic = MagicType.None;
        public int BuyIndex;
        public long BuyAmount;
        public System.Drawing.Point Point;

        /// <summary>WalkTo: where we are heading.</summary>
        public System.Drawing.Point Destination;

        /// <summary>NPCRepair: ask for a special repair, which does not cost maximum durability.</summary>
        public bool Special;

        /// <summary>Deposit/Withdraw: source and destination slots.</summary>
        public int FromSlot;
        public int ToSlot;
        public System.Collections.Generic.List<int> SellSlots;
        public System.Collections.Generic.List<int> RepairSlots;

        public override string ToString() =>
            $"{Action}" + (string.IsNullOrEmpty(Reason) ? "" : $" ({Reason})");
    }

    /// <summary>
    /// Deterministic AI. This is not a placeholder: it is the permanent fallback layer that keeps
    /// the bot playing whenever a model-driven decision is absent, stale, or the API is unreachable.
    /// Everything here is cheap, predictable and offline.
    ///
    /// Server action gating (ServerLibrary/Models/PlayerObject.cs) means actions sent too early are
    /// either queued or answered with S.UserLocation to snap the client back. The brain therefore
    /// paces itself against the same constants the server uses, plus a margin for clock skew.
    /// </summary>
    public sealed class ScriptedBrain
    {
        private static readonly TimeSpan MoveTime = TimeSpan.FromMilliseconds(600);
        private static readonly TimeSpan TurnTime = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan AttackDelay = TimeSpan.FromMilliseconds(1500);
        private static readonly TimeSpan ItemUseDelay = TimeSpan.FromMilliseconds(1000);
        private static readonly TimeSpan Margin = TimeSpan.FromMilliseconds(120);

        private readonly BotConfig _config;
        private readonly Random _random = new Random();
        private readonly LootValueRule _lootValue;

        private DateTime _nextAction = DateTime.MinValue;
        private DateTime _nextPotion = DateTime.MinValue;
        private MirDirection _roamDirection;
        private int _roamStepsLeft;
        private Point _lastLocation;
        private bool _lastWasMove;

        // Consecutive move attempts that did not change our position. The server silently refuses a
        // blocked move (it just answers S.UserLocation), so a wall produces no error - only a
        // position that stops changing. Without this the bot walks into it forever; the first live
        // run spent two minutes doing exactly that.
        private int _blockedMoves;

        // A committed sidestep around an obstacle.
        private MirDirection _detourDirection;
        private int _detourStepsLeft;
        private int _detourSide = 1;

        public int DetoursTaken;

        // Targets we could not reach, parked for a while so we stop re-picking them every tick.
        private readonly Dictionary<uint, DateTime> _unreachable = new Dictionary<uint, DateTime>();
        private static readonly TimeSpan UnreachableFor = TimeSpan.FromSeconds(30);

        // Whatever we are currently walking towards, and the closest we have ever got to it.
        // _blockedMoves cannot detect the failure that matters most: oscillating in front of a wall
        // with a gap in it, where every move SUCCEEDS (so the counter resets to zero every tick)
        // while the bot alternates forwards and backwards and never arrives. One live run logged
        // "Approach x405" against a chicken behind a wall on exactly this. Progress towards the
        // target - not the success of individual steps - is the thing worth measuring.
        private uint _pursuitID;
        private int _pursuitBest;
        private int _pursuitAttempts;

        public int PursuitsAbandoned;

        // The monster we committed to. Without this, target choice is recomputed from scratch every
        // tick by nearest-distance alone, so two mobs at equal range make the bot alternate between
        // them and neither dies. Commitment is what makes it finish a fight.
        private uint _committedTarget;

        public uint CommittedTarget => _committedTarget;
        public int TargetSwitches;

        public int BlockedMoves => _blockedMoves;
        public int UnreachableTargets => _unreachable.Count;

        public BotAction LastAction { get; private set; } = BotAction.Idle;

        public ScriptedBrain(BotConfig config)
        {
            _config = config;
            _roamDirection = (MirDirection)_random.Next(8);

            _lootValue = new LootValueRule
            {
                PoorGold = config.LootPoorGold,
                RichGold = config.LootRichGold,
                PerWeightWhenPoor = config.LootGoldPerWeightPoor,
                PerWeightWhenRich = config.LootGoldPerWeightRich,
                HeavyMultiplier = config.LootHeavyMultiplier
            };
        }

        public bool Ready => DateTime.Now >= _nextAction;

        /// <summary>
        /// Decide what to do now. Returns null when the action cooldown has not elapsed - the caller
        /// simply does nothing this tick rather than queueing up commands the server will reject.
        /// </summary>
        private Backpack _items;

        /// <summary>Set by Program once the magic index is built.</summary>
        public MagicBooks Books;

        /// <summary>Shared walkability grids. Null or empty means steer blind.</summary>
        public MapLibrary Maps;

        /// <summary>Attack-skill arming, fed by S.MagicToggle.</summary>
        public readonly SkillSet Skills = new SkillSet();

        /// <summary>Cross-map travel, when one is running.</summary>
        public Journey Travel;

        /// <summary>Set by Program once the vendor resolves.</summary>
        public TownTrip Town;

        /// <summary>Set by BotInstance when a Stop has been requested.</summary>
        public bool StopRequested;

        public Decision Decide(WorldModel world, Backpack items)
        {
            if (!Ready) return null;
            if (world.Dead) return new Decision { Action = BotAction.Idle, Reason = "dead" };
            if (world.SelfID == 0) return null;

            _items = items;
            UpdateBlockage(world);
            ExpireUnreachable();

            // 1b. Out of potions and dying. Fleeing on foot only works if something slower is
            //     chasing us; a town scroll ends the fight outright. Deliberately gated on having
            //     no potion left, so the bot drinks while it still can and only spends the scroll
            //     when drinking is no longer an option.
            // Out of potions, the bar is whichever is higher: the emergency level, or the level at
            // which we would otherwise start running. Running from a fight we cannot win, with
            // nothing to drink, only postpones the death - the scroll actually ends it.
            int escapeAt = Math.Max(_config.EmergencyScrollAtPercent, _config.FleeAtPercent);

            if (_config.EmergencyScrollAtPercent > 0 && world.MaxHealth > 0 &&
                world.HealthPercent <= escapeAt &&
                !world.InSafeZone &&
                items.FindHealthPotionSlot() < 0 &&
                (Town == null || Town.Phase != TownPhase.Teleporting))
            {
                int escape = items.FindTownTeleportSlot();

                if (escape >= 0)
                {
                    Town?.Abort("escaped on a scroll");

                    return new Decision
                    {
                        Action = BotAction.TownTeleport,
                        Reason = $"no potions at {world.HealthPercent}% - scrolling out",
                        Subject = "escaping",
                        PotionSlot = escape
                    };
                }
            }

            // A town trip NEVER outranks staying alive. Heal and flee are evaluated first, below;
            // the trip is consulted after them and may still be interrupted at any moment.
            bool inDanger = world.MaxHealth > 0 && world.HealthPercent <= _config.HealAtPercent;

            // Interrupting only helps if there is something better to do with the danger, and
            // that means having a potion to drink - Heal runs a few lines below and would fire on
            // the very next tick. With the bag empty, walking to town IS the answer to low health:
            // aborting the trip then leaves the bot permanently unable to restock, because it can
            // never climb back above the heal threshold to be allowed to travel.
            //
            // That deadlock was live. Both bots sat out of potions and out of scrolls, ignoring the
            // Town trip button entirely, and the only trace was a trip status reading
            // "interrupted at 30% HP" - set, and reset, every time the button was pressed.
            bool canHeal = items.FindHealthPotionSlot() >= 0;

            if (Town != null && Town.Active && inDanger && canHeal && !Town.Forced)
            {
                // Under pressure mid-trip: abandon travel and let normal survival behaviour run.
                // Auto-path movement is the server's, so it is told to stop too.
                if (Town.Phase == TownPhase.Travelling || Town.Phase == TownPhase.Teleporting)
                {
                    Town.Abort($"interrupted at {world.HealthPercent}% HP");
                    return new Decision { Action = BotAction.AutoPathCancel, Reason = "in danger" };
                }
            }

            // Bag full: head back to town rather than wander around unable to pick anything up.
            // Without a scroll there is nothing sensible to do about it yet - the bot has no
            // pathfinding, so walking home across a map is not an option. See the design doc.

            // 0. Switch on any sustained attack toggle we have learnt but not enabled. Once per
            //    session per skill; the server keeps them on from there.
            MagicType pending = Skills.PendingToggle(world);

            if (pending != MagicType.None)
            {
                Skills.ToggleSent(pending);

                return new Decision
                {
                    Action = BotAction.MagicToggle,
                    Reason = $"enabling {pending}",
                    Subject = pending.ToString(),
                    Magic = pending
                };
            }

            // 0a. Learn any book we can use. Done here rather than inside the town trip so that
            //     looted books are learnt too, and so it happens before the next DisposableSlots is
            //     computed - learning changes what is sellable. Held off while a fight is on,
            //     because the server gates item use behind UseItemTime.
            if (_config.LearnBooks && Books != null &&
                world.NearestLiveMonster(2, _unreachable.Keys) == null)
            {
                int slot = items.FindLearnableBookSlot(Books, world);

                if (slot >= 0)
                    return new Decision
                    {
                        Action = BotAction.LearnBook,
                        Reason = $"learning from slot {slot}",
                        PotionSlot = slot
                    };
            }

            // 0. Get dressed. Starter gear sits in the bag until something equips it, and a bare
            //    character fights considerably worse. One item per tick so the server's item
            //    handling is never flooded.
            foreach (EquipRequest request in items.PendingEquips(world.Class, world.Gender))
                return new Decision
                {
                    Action = BotAction.Equip,
                    Reason = request.ToString(),
                    Subject = request.ItemName,
                    Equip = request
                };

            // 1. Heal. Highest priority: being alive beats everything else.
            if (world.MaxHealth > 0 && world.HealthPercent <= _config.HealAtPercent &&
                DateTime.Now >= _nextPotion)
            {
                int slot = items.FindHealthPotionSlot();
                if (slot >= 0)
                    return new Decision
                    {
                        Action = BotAction.Heal,
                        Reason = $"HP {world.HealthPercent}% <= {_config.HealAtPercent}%",
                        Subject = "potion",
                        PotionSlot = slot
                    };
            }

            // 2. Flee. Below the flee threshold, disengage regardless of what is nearby.
            //
            // This MUST sit above the town block. It used to sit below it, which meant a town trip
            // outranked fleeing: once the trip reached Travelling/Talking/Trading the block below
            // returns early and this was never reached, so a bot walking to town would not flee at
            // 10% HP. The comment claiming survival outranked town was simply false.
            //
            // The flee direction is taken from NearestLiveMonster rather than SelectTarget, because
            // SelectTarget mutates _committedTarget and increments TargetSwitches - hoisting it here
            // would change normal combat behaviour as a side effect.
            if (world.MaxHealth > 0 && world.HealthPercent <= _config.FleeAtPercent &&
                !world.InSafeZone)
            {
                WorldObject threat = world.NearestLiveMonster(_config.AggroRange, _unreachable.Keys);

                // Fleeing only ever buys time for something else: a potion to drink, or a
                // scroll to escape on. With neither, it buys nothing - the monsters follow, the
                // health never comes back, and because this branch sits above the town block the
                // bot can never start the trip that would restock it.
                //
                // Gating this on "a trip is already running" was not enough, because flee stops the
                // trip from ever STARTING. One wizard logged 145 Flee decisions and 21 of anything
                // else, stuck at 16% health with an empty bag. With nothing to drink and nothing to
                // escape on, walking to town is the only move that changes the situation.
                bool nothingToFleeTo = items.FindHealthPotionSlot() < 0 &&
                                       items.FindTownTeleportSlot() < 0;

                // Fleeing needs something to flee FROM. Running in a random direction when nothing
                // is chasing us is not caution, it is a deadlock: this branch sits above the town
                // block, so a bot below the flee threshold never reaches the step that would buy it
                // potions. One sat in a safe zone at 22% health for five minutes, "fleeing", with
                // no monsters anywhere near it and no way to ever restock. The safe-zone test above
                // is the same rule stated once more: nothing there can hurt us.
                if (threat != null && !nothingToFleeTo)
                {
                    MirDirection away =
                        WorldModel.Opposite(WorldModel.DirectionTo(world.Location, threat.Location));

                    return new Decision
                    {
                        Action = BotAction.Flee,
                        Reason = $"HP {world.HealthPercent}% <= {_config.FleeAtPercent}%",
                        Subject = "low health",
                        Direction = Unstick(away),
                        Distance = RunDistance(world, 99)
                    };
                }
            }

            // 1b. Stopping. After Heal so it cannot suicide while settling, and BEFORE the town
            //     block: Town.Next returns null during Travelling/Talking/Trading, so a stop placed
            //     below it would never fire during a town trip and Stop would only ever end via the
            //     hard-close timeout.
            if (StopRequested)
            {
                Town?.Abort("stopping");

                if (world.InCombat)
                {
                    WorldObject threat = world.NearestLiveMonster(_config.AggroRange, _unreachable.Keys);

                    return new Decision
                    {
                        Action = BotAction.Flee,
                        Reason = "stopping - leaving combat",
                        Subject = "stopping",
                        Direction = Unstick(threat != null
                            ? WorldModel.Opposite(WorldModel.DirectionTo(world.Location, threat.Location))
                            : _roamDirection),
                        Distance = RunDistance(world, 99)
                    };
                }

                return new Decision
                {
                    Action = BotAction.Logout,
                    Reason = "stopping",
                    Subject = "stopping"
                };
            }

            // Trip steps come after survival but before picking a fight.
            if (Town != null)
            {
                Decision trip = Town.Next(world, items);

                if (trip != null)
                {
                    // WalkTo names a destination; turn it into one step here so that blocked-move
                    // handling and run/walk selection stay in a single place.
                    if (trip.Action == BotAction.WalkTo)
                    {
                        int remaining = WorldModel.Distance(world.Location, trip.Destination);

                        trip.Action = BotAction.Approach;

                        // No route at all. Stand still rather than issue a move with no direction,
                        // and let TownTrip's own progress watchdog abort the trip - it is the only
                        // code that knows how to unwind one.
                        if (!TrySteer(trip, world, trip.Destination, 0, remaining))
                            return new Decision
                            {
                                Action = BotAction.Idle,
                                Reason = $"no route to {trip.Destination.X},{trip.Destination.Y}",
                                Subject = trip.Subject
                            };
                    }

                    return trip;
                }

                // Travelling is server-driven; stand by rather than fighting our way across town.
                if (Town.Phase == TownPhase.Travelling || Town.Phase == TownPhase.Talking ||
                    Town.Phase == TownPhase.Trading)
                    return null;
            }

            // 2b. Cross-map travel. Below the town trip, because arriving somewhere new with a
            //     full bag and no potions is how a journey ends in a corpse; above fighting,
            //     because a bot that stops to kill everything never gets anywhere.
            if (Travel != null && Travel.Active)
            {
                Decision leg = Travel.Next(world);

                if (leg != null)
                {
                    int remaining = WorldModel.Distance(world.Location, leg.Destination);

                    leg.Action = BotAction.Approach;

                    if (!TrySteer(leg, world, leg.Destination, 0, remaining))
                    {
                        Travel.Abort($"no route to the exit at {leg.Destination.X},{leg.Destination.Y}");
                        return new Decision
                        {
                            Action = BotAction.Idle,
                            Reason = Travel.Status,
                            Subject = "travel failed"
                        };
                    }

                    return leg;
                }
            }

            WorldObject target = SelectTarget(world);

            if (target != null)
            {
                int distance = world.DistanceTo(target.Location);

                // 3. Attack anything adjacent.
                if (distance <= 1)
                {
                    MagicType magic = Skills.ChooseAttackMagic(world, target, distance);

                    return new Decision
                    {
                        Action = BotAction.Attack,
                        Reason = magic == MagicType.None ? target.Name : $"{target.Name} ({magic})",
                        Subject = target.Name,
                        TargetID = target.ObjectID,
                        Direction = WorldModel.DirectionTo(world.Location, target.Location),
                        Magic = magic
                    };
                }

                // 4a. Nothing adjacent to fight - grab loot first if any is close.
                Decision loot = TryLoot(world);
                if (loot != null) return loot;

                // 4b. Close the gap - unless we have been failing to, either because the way is
                //     blocked outright or because we are moving without ever getting nearer.
                Decision approach = new Decision
                {
                    Action = BotAction.Approach,
                    Reason = $"{target.Name} at {distance}",
                    Subject = target.Name,
                    TargetID = target.ObjectID
                };

                // Three ways a chase is over: the way is blocked, we are moving without ever
                // getting closer, or the map itself says there is no route to stand beside it.
                bool blocked = _blockedMoves >= 6;
                bool futile = NoProgressTowards(target.ObjectID, distance);
                bool noRoute = !blocked && !futile &&
                               !TrySteer(approach, world, target.Location, 1, distance);

                if (blocked || futile || noRoute)
                {
                    _unreachable[target.ObjectID] = DateTime.Now;
                    _committedTarget = 0;
                    _blockedMoves = 0;
                    ForgetPursuit();

                    string why = blocked ? "unreachable"
                        : futile ? $"no progress in {_config.PursuitPatience} moves"
                        : "no route";

                    return new Decision
                    {
                        Action = BotAction.Roam,
                        Reason = $"gave up on {target.Name} ({why})",
                        Subject = "roaming",
                        Direction = NextRoamDirection(world, true)
                    };
                }

                return approach;
            }

            // 5. Nothing to fight - loot, then wander.
            Decision idleLoot = TryLoot(world);
            if (idleLoot != null) return idleLoot;

            return new Decision
            {
                Action = BotAction.Roam,
                Reason = "no targets",
                Subject = "roaming",
                Direction = NextRoamDirection(world, false),
                Distance = RunDistance(world, _config.AggroRange)
            };
        }

        /// <summary>
        /// Did the last move actually move us? A blocked move produces no error - the server just
        /// answers S.UserLocation - so the only evidence is a position that did not change.
        /// </summary>
        private void UpdateBlockage(WorldModel world)
        {
            if (!_lastWasMove) return;

            if (world.Location == _lastLocation)
            {
                _blockedMoves++;

                // A detour that is not moving us either is no better than the original direction.
                if (_detourStepsLeft > 0) CancelDetour();
            }
            else
            {
                _blockedMoves = 0;
            }
        }

        /// <summary>
        /// Are we walking towards something without ever getting closer to it?
        ///
        /// Called once per approach decision, with the distance to whatever we are chasing. Any
        /// improvement on the best distance so far resets the patience, so a long but productive
        /// chase is never cut short; a target we simply cannot make ground on is abandoned after
        /// PursuitPatience attempts and parked as unreachable like any other.
        ///
        /// This is the backstop for local steering getting trapped by geometry. It stays useful
        /// even with real pathfinding, for the cases a path cannot express: a monster kiting us, or
        /// a drop sitting on a cell nothing can stand on.
        /// </summary>
        private bool NoProgressTowards(uint objectID, int distance)
        {
            if (objectID != _pursuitID)
            {
                _pursuitID = objectID;
                _pursuitBest = distance;
                _pursuitAttempts = 0;
                return false;
            }

            if (distance < _pursuitBest)
            {
                _pursuitBest = distance;
                _pursuitAttempts = 0;
                return false;
            }

            if (++_pursuitAttempts < Math.Max(1, _config.PursuitPatience)) return false;

            PursuitsAbandoned++;
            return true;
        }

        private void ForgetPursuit()
        {
            _pursuitID = 0;
            _pursuitAttempts = 0;
        }

        /// <summary>
        /// Point a decision at its destination, and say whether getting there is possible at all.
        ///
        /// With a map loaded this is A*: the first step of a real route, and false when no route
        /// exists - which is the answer blind steering could never give. The caller treats false as
        /// final and parks the target as unreachable rather than grinding away at it.
        ///
        /// Without a map it falls back to the old behaviour, aiming straight at the destination and
        /// sidestepping when blocked, and always returns true because it genuinely cannot tell.
        /// </summary>
        private bool TrySteer(Decision decision, WorldModel world, Point destination, int goalRange,
            int straightDistance)
        {
            MapGrid grid = Maps?.For(world.MapIndex);

            if (grid != null)
            {
                // Only route around other creatures once something has actually stopped us. Doing
                // it always makes a monster standing in a doorway look like a wall, and the extra
                // set costs a full sweep of the object list on every decision.
                HashSet<Point> avoid = _blockedMoves > 0
                    ? world.OccupiedCells(decision.TargetID)
                    : null;

                List<Point> path = PathFinder.Find(grid, world.Location, destination, goalRange, avoid);

                if (path == null) return false;

                // Already there - let the caller's own arrival handling deal with it.
                if (path.Count == 0)
                {
                    decision.Direction = WorldModel.DirectionTo(world.Location, destination);
                    decision.Distance = 1;
                    return true;
                }

                decision.Direction = WorldModel.DirectionTo(world.Location, path[0]);
                decision.Distance = PathStride(world, path, decision.Direction);
                return true;
            }

            decision.Direction = Unstick(WorldModel.DirectionTo(world.Location, destination));
            decision.Distance = RunDistance(world, straightDistance);
            return true;
        }

        /// <summary>
        /// One step or two? Running covers two tiles in one move, but only in a straight line, so it
        /// is only safe when the path's next two steps continue in the same direction.
        /// </summary>
        private int PathStride(WorldModel world, List<Point> path, MirDirection direction)
        {
            if (!_config.AllowRunning) return 1;
            if (_blockedMoves > 0) return 1;
            if (path.Count < 2) return 1;

            // An overweight character cannot run; asking anyway gets every move refused.
            if (world.MaxBagWeight > 0 && world.WeightPercent >= 100) return 1;

            return path[1] == Functions.Move(world.Location, direction, 2) ? 2 : 1;
        }

        private void ExpireUnreachable()
        {
            if (_unreachable.Count == 0) return;

            DateTime cutoff = DateTime.Now - UnreachableFor;
            List<uint> expired = null;

            foreach (KeyValuePair<uint, DateTime> pair in _unreachable)
            {
                if (pair.Value > cutoff) continue;
                (expired ??= new List<uint>()).Add(pair.Key);
            }

            if (expired == null) return;
            foreach (uint id in expired) _unreachable.Remove(id);
        }

        /// <summary>
        /// Nudge a direction sideways when the straight line is blocked. Walls in Mir are rarely
        /// square to the 8 directions, so a 45-degree sidestep usually clears them; alternating
        /// sides avoids getting wedged in a corner.
        /// </summary>
        /// <summary>
        /// Get around whatever is in the way.
        ///
        /// Fanning out one step at a time does not work: the bot steps aside, immediately re-aims at
        /// the target, is blocked again, and steps back the other way - oscillating against a wall
        /// forever without ever getting round it. So a detour is COMMITTED to: on the second
        /// consecutive blocked move it picks a perpendicular direction and holds it for several
        /// steps before re-aiming, which is enough to clear most walls. If that detour also stalls,
        /// the next one goes the other way.
        /// </summary>
        private MirDirection Unstick(MirDirection intended)
        {
            if (_detourStepsLeft > 0)
            {
                _detourStepsLeft--;
                return _detourDirection;
            }

            if (_blockedMoves == 0) return intended;

            // One blocked move might be a passing monster; just nudge 45 degrees.
            if (_blockedMoves == 1)
                return (MirDirection)(((int)intended + 1) % 8);

            // Twice in a row is geometry. Commit to going around it.
            _detourSide = -_detourSide;
            _detourDirection = (MirDirection)(((int)intended + 2 * _detourSide + 8) % 8);
            _detourStepsLeft = _config.DetourSteps;
            _blockedMoves = 0;
            DetoursTaken++;

            return _detourDirection;
        }

        /// <summary>A detour is abandoned as soon as it stops helping.</summary>
        private void CancelDetour()
        {
            _detourStepsLeft = 0;
        }

        private MirDirection NextRoamDirection(WorldModel world, bool forceNew)
        {
            if (forceNew || _blockedMoves >= 2)
            {
                _roamDirection = (MirDirection)_random.Next(8);
                _roamStepsLeft = _random.Next(3, 8);
                _blockedMoves = 0;
                return _roamDirection;
            }

            if (_roamStepsLeft-- <= 0)
            {
                _roamDirection = (MirDirection)_random.Next(8);
                _roamStepsLeft = _random.Next(4, 12);
            }

            return _roamDirection;
        }

        /// <summary>
        /// Nearest item we actually want. Once the bag is heavy the bot stops hoovering up junk and
        /// takes only consumables and genuine upgrades, which is what keeps it from going overweight.
        /// </summary>
        private WorldObject FindWorthwhileLoot(WorldModel world, bool heavy, bool full)
        {
            HashSet<uint> skip = new HashSet<uint>(_unreachable.Keys);

            while (true)
            {
                WorldObject candidate = world.NearestItem(_config.LootRange, skip);
                if (candidate == null) return null;

                // Judged against THIS item's weight: how many we should hold depends on how
                // heavy one is, so a tier four potion earns a smaller stack than a tier one.
                int unit = Math.Max(1, candidate.ItemInfo?.Weight ?? 1);

                if (_items.WorthLooting(candidate.ItemInfo, candidate.Item, world.Class, world.Gender,
                        heavy, full,
                        _config.HealthPotionTarget(world.MaxBagWeight, unit),
                        _config.ManaPotionTarget(world.MaxBagWeight, unit),
                        world.Gold, _lootValue))
                    return candidate;

                skip.Add(candidate.ObjectID);
            }
        }

        /// <summary>
        /// Stay on the current target until it dies, escapes, or proves unreachable. Only then pick
        /// a new one. Switching mid-fight wastes every hit already landed, so the bar for changing
        /// is deliberately high: a committed target is kept even when something else drifts closer.
        /// </summary>
        private WorldObject SelectTarget(WorldModel world)
        {
            if (_committedTarget != 0)
            {
                WorldObject current = world.Objects.FirstOrDefault(x => x.ObjectID == _committedTarget);

                bool stillGood = current != null && current.IsValidTarget &&
                                 !_unreachable.ContainsKey(current.ObjectID) &&
                                 world.DistanceTo(current.Location) <= _config.AggroRange + 2;

                if (stillGood) return current;

                _committedTarget = 0;
            }

            WorldObject next = world.NearestLiveMonster(_config.AggroRange, _unreachable.Keys);

            if (next != null)
            {
                if (_committedTarget != 0 && next.ObjectID != _committedTarget) TargetSwitches++;
                _committedTarget = next.ObjectID;
            }

            return next;
        }

        /// <summary>
        /// Run (2 tiles) rather than walk (1) when there is ground to cover and nothing is currently
        /// blocking us. While unsticking, single steps are far likelier to succeed.
        /// </summary>
        private int RunDistance(WorldModel world, int distanceToTarget)
        {
            if (!_config.AllowRunning) return 1;
            if (_blockedMoves > 0) return 1;

            // An overweight character cannot run. Asking anyway gets every move refused and
            // answered with S.UserLocation - 62 resyncs in one three-minute run before this check.
            if (world.MaxBagWeight > 0 && world.WeightPercent >= 100) return 1;

            return distanceToTarget > 2 ? 2 : 1;
        }

        /// <summary>
        /// PickUp collects everything within Stat.PickUpRadius of where we stand, so the bot has to
        /// walk onto the drop first. Items it cannot reach get parked like unreachable monsters.
        /// </summary>
        private Decision TryLoot(WorldModel world)
        {
            if (!_config.LootEnabled) return null;

            bool heavy = world.MaxBagWeight > 0 && world.WeightPercent >= _config.HeavyWeightPercent;
            bool full = world.MaxBagWeight > 0 && world.WeightPercent >= 100;

            WorldObject item = FindWorthwhileLoot(world, heavy, full);
            if (item == null) return null;

            int distance = world.DistanceTo(item.Location);

            if (distance == 0)
                return new Decision { Action = BotAction.Loot, Reason = item.Name, Subject = item.Name };

            Decision walk = new Decision
            {
                Action = BotAction.Approach,
                Reason = $"loot {item.Name} at {distance}",
                Subject = $"loot {item.Name}",
                TargetID = item.ObjectID
            };

            // goalRange 0: PickUp works from where we stand, so we have to reach the tile itself.
            if (_blockedMoves >= 6 || NoProgressTowards(item.ObjectID, distance) ||
                !TrySteer(walk, world, item.Location, 0, distance))
            {
                _unreachable[item.ObjectID] = DateTime.Now;
                _blockedMoves = 0;
                ForgetPursuit();
                return null;
            }

            return walk;
        }

        /// <summary>Record that an action was issued, and pace the next one accordingly.</summary>
        public void Issued(Decision decision, WorldModel world)
        {
            LastAction = decision.Action;
            _lastLocation = world.Location;
            _lastWasMove = decision.Action == BotAction.Approach ||
                           decision.Action == BotAction.Flee ||
                           decision.Action == BotAction.Roam;

            switch (decision.Action)
            {
                case BotAction.TownTeleport:
                    _nextAction = DateTime.Now + ItemUseDelay + Margin;
                    break;
                case BotAction.WalkTo:
                    _nextAction = DateTime.Now + MoveTime + Margin;
                    break;
                case BotAction.Deposit:
                case BotAction.Withdraw:
                    _nextAction = DateTime.Now + TurnTime + Margin;
                    break;
                case BotAction.LearnBook:
                    _nextAction = DateTime.Now + ItemUseDelay + Margin;
                    break;
                case BotAction.Unlock:
                    _nextAction = DateTime.Now + TurnTime + Margin;
                    break;
                case BotAction.MagicToggle:
                    // The client's own anti-spam on these is one second.
                    _nextAction = DateTime.Now + TimeSpan.FromSeconds(1);
                    break;
                case BotAction.Logout:
                    // Its own pace: the default 420ms would be ~48 logout packets in twenty
                    // seconds, right at the server's 50-packet ban threshold.
                    _nextAction = DateTime.Now + TimeSpan.FromSeconds(5);
                    break;
                case BotAction.NPCRepair:
                case BotAction.AutoPath:
                case BotAction.AutoPathPoint:
                case BotAction.AutoPathCancel:
                case BotAction.NPCCall:
                case BotAction.NPCButton:
                case BotAction.NPCSell:
                case BotAction.NPCBuy:
                case BotAction.NPCClose:
                    _nextAction = DateTime.Now + TurnTime + Margin;
                    break;
                case BotAction.Equip:
                case BotAction.Loot:
                    _nextAction = DateTime.Now + TurnTime + Margin;
                    break;
                case BotAction.Heal:
                    _nextAction = DateTime.Now + ItemUseDelay + Margin;
                    _nextPotion = DateTime.Now + ItemUseDelay + Margin;
                    break;
                case BotAction.Attack:
                    _nextAction = DateTime.Now + AttackDelay + Margin;
                    break;
                case BotAction.Flee:
                case BotAction.Approach:
                case BotAction.Roam:
                    _nextAction = DateTime.Now + MoveTime + Margin;
                    break;
                default:
                    _nextAction = DateTime.Now + TurnTime + Margin;
                    break;
            }
        }

        /// <summary>
        /// The server rejected our movement and snapped us back. Back off for a full move cycle;
        /// hammering it again just produces another correction.
        /// </summary>
        public void Resynced()
        {
            _nextAction = DateTime.Now + MoveTime + Margin;
        }
    }
}
