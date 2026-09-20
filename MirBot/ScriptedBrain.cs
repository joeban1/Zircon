using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Library;

namespace MirBot
{
    public enum BotAction
    {
        Idle, Equip, Heal, Flee, Attack, Loot, Approach, Roam, SetPetMode,
        TownTeleport, AutoPath, AutoPathPoint, AutoPathCancel, WalkTo, Deposit, Withdraw,
        NPCRepair, LearnBook, Logout, NPCCall, NPCButton, NPCSell, NPCBuy, NPCClose,
        MagicToggle, Unlock, Cast
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

        /// <summary>Deposit/Withdraw against the parts grid rather than ordinary storage.</summary>
        public bool PartsGrid;

        /// <summary>For SetPetMode.</summary>
        public PetMode PetMode;
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
        private static readonly TimeSpan CastTime = TimeSpan.FromMilliseconds(600);
        private static readonly TimeSpan ItemUseDelay = TimeSpan.FromMilliseconds(1000);
        private static readonly TimeSpan Margin = TimeSpan.FromMilliseconds(120);

        private readonly BotConfig _config;
        private readonly Random _random = new Random();
        private LootValueRule _lootValue;

        private DateTime _nextAction = DateTime.MinValue;
        private DateTime _nextPotion = DateTime.MinValue;

        /// <summary>Emergency scrolls are one per emergency - see the escape branch.</summary>
        private DateTime _nextEscape = DateTime.MinValue;

        private static readonly TimeSpan EscapeCooldown = TimeSpan.FromSeconds(45);
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

        /// <summary>
        /// How long to leave something alone, by WHY we gave up on it.
        ///
        /// These were all one flat thirty seconds, which is the wrong answer to two different
        /// questions. An item we could not path to this second may be reachable as soon as whatever
        /// is standing in the way moves, so waiting half a minute wastes it; an item the SERVER
        /// refused to hand over will still be refused, so asking again soon is pure noise. The Mir 2
        /// agents separate these for the same reason - two seconds against two minutes.
        ///
        /// Served through Blacklist, which back-dates into the same park list, so there is still
        /// only one expiry sweep to get wrong.
        /// </summary>
        private static readonly TimeSpan BlockedForNow = TimeSpan.FromSeconds(3);

        private static readonly TimeSpan RefusedByServer = TimeSpan.FromMinutes(2);

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

        // The fight we are currently in, and whether it is actually going anywhere.
        //
        // _pursuitBest measures whether we are getting CLOSER; nothing measured whether the target
        // was getting WEAKER. A bot was seen surrounded with a monster standing on its own cell:
        // it had committed to that target, every swing went at the cell in front of it, and the
        // thing underneath took no damage. Distance was 0, so the approach watchdog never ran and
        // the attack watchdog did not exist - it stood there swinging at nothing until it was
        // forced back to town by hand.
        //
        // The server broadcasts S.HealthChanged for monsters as well as for us
        // (MapObject.ProcessHPMP), so the target's health is genuinely observable and "is this
        // fight working" is a question we can answer rather than guess at.
        private uint _fightID;
        private int _fightHealth;
        private int _fightSwings;

        public int FightsAbandoned;

        /// <summary>Learned per-monster danger, shared by every bot. Null disables avoidance.</summary>
        public MonsterMemory Danger;

        /// <summary>Cells the server refuses although the map file says otherwise.</summary>
        public NavCorrections Nav;

        /// <summary>
        /// The map-link graph, used here only to know which cells are doors. Null means the bot
        /// steers as it always did and may wander back out of a map it just entered.
        /// </summary>
        public WorldGraph Exits;

        public int DoorwaysCleared;

        /// <summary>Which map we were on last decision, for noticing that we have just arrived.</summary>
        private int _lastMapIndex = -1;
        private Point _doorwayAim = Point.Empty;
        private DateTime _doorwayDeadline = DateTime.MinValue;

        /// <summary>The cell the last move was aimed at, for judging whether it worked.</summary>
        private Point _lastMoveTarget;
        private bool _lastMoveWasSingleStep;

        public int LearnedBlockedCells;

        public int BlockedMoves => _blockedMoves;
        public int UnreachableTargets => _unreachable.Count;

        public BotAction LastAction { get; private set; } = BotAction.Idle;

        public ScriptedBrain(BotConfig config)
        {
            _config = config;
            _roamDirection = (MirDirection)_random.Next(8);

            Spells = new SpellBook(config);

            ReapplyLootRule();
        }

        /// <summary>
        /// Rebuild the loot rule from the current config.
        ///
        /// The rule is a COPY, taken once at construction, which is fine while config never
        /// changes and wrong the moment it can. Five settings live in here - LootPoorGold,
        /// LootRichGold, the two per-weight figures and the heavy multiplier - and without this an
        /// edit to any of them would sit in BotConfig looking applied while the bot went on using
        /// the values it read at startup.
        /// </summary>
        public void ReapplyLootRule()
        {
            _lootValue = new LootValueRule
            {
                PoorGold = _config.LootPoorGold,
                RichGold = _config.LootRichGold,
                PerWeightWhenPoor = _config.LootGoldPerWeightPoor,
                PerWeightWhenRich = _config.LootGoldPerWeightRich,
                HeavyMultiplier = _config.LootHeavyMultiplier
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

        /// <summary>Cast spells, fed by S.MagicCooldown. Built in the constructor.</summary>
        public SpellBook Spells;

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

            // The potion gate is dropped when a town trip is already under way.
            //
            // Out of a cave, running and scrolling are not two ways of doing the same thing. The
            // scroll IS the errand: the bot is trying to reach a town, and a scroll puts it in one
            // instantly, while fleeing on foot means fighting back up a cave it entered at full
            // health. Worse, this branch sits above the town block, so once health drops the bot
            // flees every tick and the trip it is fleeing towards never gets another turn - it
            // runs in circles holding the scroll that would have finished the job.
            //
            // Still gated on being hurt, on not already teleporting, and on the one-per-emergency
            // cooldown below, so a healthy trip walks to its vendors exactly as before.
            bool headingToTown = Town != null && Town.Active;

            // Gated on having NOTHING LEFT TO DRINK, which is where it started.
            //
            // An earlier version dropped that gate whenever a town trip was running, so that a bot
            // leaving a cave would scroll rather than walk out. That was wrong in a way worth
            // recording: this branch sits ABOVE "// 1. Heal", so below the escape threshold a bot
            // with a full bag of potions scrolled instead of drinking. One was found sitting at
            // 15 HP holding thirteen of them.
            //
            // The cave case did not need this gate touched at all. A town trip that cannot be
            // served where it stands already spends a scroll in TownTrip.Begin; what it needed was
            // simply not to be starved by a branch above it. headingToTown still decides how the
            // trip is handed on below - replanned rather than abandoned - it just no longer decides
            // whether we drink.
            if (_config.EmergencyScrollAtPercent > 0 && world.MaxHealth > 0 &&
                world.HealthPercent <= escapeAt &&
                !world.InSafeZone &&
                !items.HasHealthPotion() &&
                (Town == null || Town.Phase != TownPhase.Teleporting))
            {
                int escape = items.FindTownTeleportSlot();

                if (escape >= 0 && DateTime.Now >= _nextEscape)
                {
                    // One scroll per emergency. A town scroll goes to the BIND POINT, so if the
                    // bind point is itself outside a safe zone - which Banya Village's is - the bot
                    // lands still below the threshold, still with nothing to drink, still not safe,
                    // and the same branch fires again on the very next tick. Three scrolls went in
                    // fifteen seconds that way, the last two teleporting from the bind point to the
                    // bind point.
                    //
                    // After the first one the answer is no longer "leave", it is "walk to a vendor",
                    // and holding off is what lets the town trip below get a turn.
                    _nextEscape = DateTime.Now + EscapeCooldown;

                    // A trip that was already going to town has not been interrupted by this -
                    // it has been completed by it. Aborting would throw away the plan and, worse,
                    // set the retry cooldown. Let it replan on arrival as a scrolled trip always
                    // does.
                    if (headingToTown) Town.ScrolledOut(world);
                    else Town?.Abort("escaped on a scroll");

                    return new Decision
                    {
                        Action = BotAction.TownTeleport,
                        Reason = headingToTown
                            ? $"no potions at {world.HealthPercent}% mid-trip - scrolling to town"
                            : $"no potions at {world.HealthPercent}% - scrolling out",
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
            bool canHeal = items.HasHealthPotion();

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
                int missing = world.MaxHealth - world.Health;
                int slot = items.FindHealthPotionSlot(missing);

                // Do not pour a big potion into a small gap.
                //
                // The potion is chosen as the closest fit to what is missing, but on this server
                // the tiers are 30 / 70 / 110 / 170 / 250 HP, so even the smallest one carried can
                // overshoot. Above the panic line we simply wait until the deficit is worth the
                // potion; below it we drink whatever there is, because being alive beats being
                // efficient and a second drink costs a whole potion cooldown.
                if (slot >= 0 && world.HealthPercent > _config.PanicHealPercent)
                {
                    int heals = items.RestoreAmount(slot, true);

                    if (heals > 0 && missing < heals) slot = -1;
                }
                else if (slot >= 0)
                {
                    // Desperate: take the biggest thing carried, waste be damned.
                    int biggest = items.FindBiggestHealthPotionSlot();
                    if (biggest >= 0) slot = biggest;
                }

                if (slot >= 0)
                    return new Decision
                    {
                        Action = BotAction.Heal,
                        Reason = $"HP {world.HealthPercent}% <= {_config.HealAtPercent}%",
                        Subject = "potion",
                        PotionSlot = slot
                    };
            }

            // 1a. Drink mana, if there is anything to spend it on.
            //
            //     This is what makes casting more than a single burst per login. Mana potions were
            //     already bought, carried and reserved - CountManaPotions, ManaPotionReserve,
            //     ManaPotionTarget all existed - and nothing in the brain ever drank one, so a
            //     wizard spent its pool in the first few minutes and meleed for the rest of the
            //     session. Observed directly: one logged in at 30/167, cast five times, and sat at
            //     25/167 for as long as it was watched, because mana does not regenerate while
            //     hunting.
            //
            //     Below the heal threshold this is skipped: item use is gated on one global timer,
            //     so a mana potion drunk at 20% health is a health potion not drunk.
            if (_config.DrinkManaAtPercent > 0 && world.MaxMana > 0 &&
                world.ManaPercent <= _config.DrinkManaAtPercent &&
                world.HealthPercent > _config.HealAtPercent &&
                DateTime.Now >= _nextPotion &&
                Spells.HasCastable(world))
            {
                int manaSlot = items.FindManaPotionSlot(world.MaxMana - world.Mana);

                if (manaSlot >= 0)
                    return new Decision
                    {
                        Action = BotAction.Heal,
                        Reason = $"MP {world.ManaPercent}% <= {_config.DrinkManaAtPercent}%",
                        Subject = "mana potion",
                        PotionSlot = manaSlot
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
                bool nothingToFleeTo = !items.HasHealthPotion() &&
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
                    // Only a WalkTo needs steering. A teleport leg is a conversation - NPCCall and
                    // NPCButton - and rewriting those to Approach, as this used to do
                    // unconditionally, would send the bot walking towards an empty Destination
                    // instead of talking to the NPC standing next to it.
                    if (leg.Action != BotAction.WalkTo) return leg;

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

            // 2b-i. Keep our own buffs up.
            //
            //       Below Heal, Flee and the town trip, above combat: a missing Magic Shield is
            //       worth a turn but never worth dying for. "Missing" is the server's answer -
            //       WorldModel.HasBuff is fed by S.BuffAdd, S.BuffRemove and the login dump - so
            //       there is no timer here to drift, and a buff that is actually held simply stops
            //       being selected.
            if (!world.Dead)
            {
                ClientUserMagic buff = Spells.ChooseBuff(world, out bool atOwnFeet);

                if (buff != null)
                {
                    Spells.BuffIssued(buff);

                    return new Decision
                    {
                        Action = BotAction.Cast,
                        Reason = $"keeping {buff.Info.Name} up",
                        Subject = buff.Info.Name,
                        // Target 0 for a self-cast: PlayerObject.Magic resolves an unknown or
                        // out-of-range id to null anyway, and every self-buff ignores the target
                        // entirely. The ground-targeted Taoist ones DO read Location and are
                        // range-checked on it, so those are aimed at our own feet - CanHelpTarget
                        // always includes the caster, so we are inside our own radius.
                        TargetID = 0,
                        Direction = world.Direction,
                        Point = atOwnFeet ? world.Location : world.Location,
                        Magic = buff.Info.Magic
                    };
                }
            }

            // 2b-i-b. Tell our pets how to behave.
            //
            //         Move, not None, while shopping. None stops the pet MOVING as well as
            //         attacking, and the server's automatic recall only runs in a mode that permits
            //         movement (MonsterObject.cs:966) - so None can strand a skeleton on the
            //         hunting map until something else happens to change the mode. Move keeps it
            //         following while stopping it starting fights among the shoppers.
            //
            //         Sent as a Decision rather than a packet from TownTrip, so it appears in the
            //         action history and obeys the same pacing as everything else.
            if (world.SelfID != 0)
            {
                PetMode wanted = Town != null && Town.Active ? PetMode.Move : PetMode.Both;

                if (world.PetMode != wanted)
                    return new Decision
                    {
                        Action = BotAction.SetPetMode,
                        Reason = $"pets to {wanted}",
                        Subject = "pet mode",
                        PetMode = wanted
                    };
            }

            // 2b-ii. Keep a summon out.
            Decision summon = KeepSummon(world, items);
            if (summon != null) return summon;

            // 2c. Step out of the doorway.
            //
            //     The server lands an arriving character at a random point of the destination
            //     region, and that region is the paired one for the exit coming back - so the bot
            //     starts every map standing ON the way out. Avoiding exit cells while pathing stops
            //     a chase or a loot run crossing one, but it cannot help with the cell we were put
            //     on, and roaming does not path at all. So walk clear before settling in.
            //
            //     Below Heal and Flee, so it can never keep the bot in a doorway while it dies.
            Decision doorway = ClearDoorway(world);
            if (doorway != null) return doorway;

            WorldObject target = SelectTarget(world);

            if (target != null)
            {
                int distance = world.DistanceTo(target.Location);

                // 3a. Cast, if we know something worth casting and can pay for it.
                //
                //    Above the melee branch and above closing the distance, both deliberately. A
                //    wizard that walks into contact to punch things has thrown away the entire
                //    reason it is a wizard, and a spell that reaches ten tiles should be thrown
                //    from ten tiles rather than after a walk.
                // Poison first: it ticks for the rest of the fight, so a turn spent applying it
                // early is worth more than the same turn spent on one direct hit.
                ClientUserMagic venom = Spells.ChoosePoison(world, items, target, distance);

                if (venom != null)
                {
                    Spells.PoisonIssued(target.ObjectID);

                    return new Decision
                    {
                        Action = BotAction.Cast,
                        Reason = $"{venom.Info.Name} on {target.Name} at {distance}",
                        Subject = venom.Info.Name,
                        TargetID = target.ObjectID,
                        Direction = WorldModel.DirectionTo(world.Location, target.Location),
                        Point = target.Location,
                        Magic = venom.Info.Magic
                    };
                }

                ClientUserMagic spell = Spells.Choose(world, target, distance);

                if (spell != null)
                {
                    return new Decision
                    {
                        Action = BotAction.Cast,
                        Reason = $"{spell.Info.Name} on {target.Name} at {distance}",
                        Subject = spell.Info.Name,
                        TargetID = target.ObjectID,
                        Direction = WorldModel.DirectionTo(world.Location, target.Location),
                        Point = target.Location,
                        Magic = spell.Info.Magic
                    };
                }

                // 3a-i. Hold the range. A caster that has something to throw should be throwing
                //       it from a distance, not letting the target close and then meleeing.
                Decision back = Kite(world, target, distance);
                if (back != null) return back;

                // 3b. In contact. Either standing beside it, or - Mir allows this - standing on
                //     the same tile, because a player and a monster can step onto one cell in the
                //     same tick.
                if (distance <= 1)
                {
                    // Is this fight going anywhere? The target losing health is the only proof
                    // that it is, and the server broadcasts S.HealthChanged for monsters as well
                    // as for us, so it is genuinely observable rather than something to infer.
                    //
                    // Asked BEFORE the same-cell step-off, and that ordering is the whole point.
                    // Monsters follow: step off, it steps onto us again, step off again. Resetting
                    // the watchdog on each step-off - which is what the first version of this did -
                    // turns the cure into its own infinite loop. A step-off is a turn that did no
                    // damage, and it is counted as one.
                    if (NoDamageTo(target))
                    {
                        Blacklist(target.ObjectID, GiveUpFor);

                        return new Decision
                        {
                            Action = BotAction.Roam,
                            Reason = $"{target.Name} took no damage in {_config.AttackPatience} turns",
                            Subject = "giving up",
                            Direction = NextRoamDirection(world, true),
                            Distance = 1
                        };
                    }

                    // Underneath us. An attack is aimed at the cell in FRONT, so a monster
                    // sharing our cell can never be hit from where we stand. One step makes it
                    // adjacent and the fight starts working. Casting is unaffected - a spell names
                    // its target by ObjectID - and sits above this, so a caster keeps hurting it
                    // on the way past.
                    if (distance == 0)
                        return new Decision
                        {
                            Action = BotAction.Roam,
                            Reason = $"{target.Name} is underneath us - stepping off",
                            Subject = "stepping off",
                            Direction = NextRoamDirection(world, true),
                            Distance = 1
                        };

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

                    return Wander(world, $"gave up on {target.Name} ({why})", true);
                }

                return approach;
            }

            // 5. Nothing to fight - loot, then wander.
            Decision idleLoot = TryLoot(world);
            if (idleLoot != null) return idleLoot;

            return Wander(world, "no targets", false);
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
                LearnRefusal(world);

                // A detour that is not moving us either is no better than the original direction.
                if (_detourStepsLeft > 0) CancelDetour();
            }
            else
            {
                _blockedMoves = 0;

                // Standing on it is proof it is passable. A gate that has since opened un-learns
                // itself here, which is the whole reason a door is worth learning about at all.
                if (Nav != null && _config.LearnBlockedCells)
                    Nav.Cleared(world.MapIndex, world.Location);
            }
        }

        /// <summary>
        /// The server silently refused a move. Remember the cell, once we are sure enough.
        ///
        /// Only single steps are used as evidence: a run covers two cells and a refusal does not
        /// say which of them was the problem, so counting it would teach us the wrong tile. And a
        /// cell with something visibly standing on it is not evidence either - that is a monster
        /// in a doorway, which moves, and blacklisting the doorway because of it would be a far
        /// worse bug than the one this is here to fix.
        /// </summary>
        private void LearnRefusal(WorldModel world)
        {
            if (Nav == null || !_config.LearnBlockedCells) return;
            if (!_lastMoveWasSingleStep) return;
            if (_lastMoveTarget == Point.Empty) return;
            if (world.SomethingAt(_lastMoveTarget, 0)) return;

            if (Nav.Refused(world.MapIndex, world.MapName, _lastMoveTarget,
                    _config.BlockedCellEvidence))
                LearnedBlockedCells++;
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

        private TimeSpan GiveUpFor =>
            TimeSpan.FromSeconds(Math.Max(1, _config.AttackGiveUpSeconds));

        /// <summary>
        /// Write a target off and forget everything we were doing about it.
        ///
        /// The park list expires on a fixed UnreachableFor, so a longer sentence is served by
        /// backdating the entry - the same list, one expiry rule, no second timer to keep in step.
        /// </summary>
        private void Blacklist(uint objectID, TimeSpan forHowLong)
        {
            Park(objectID, forHowLong);
            _committedTarget = 0;
            _blockedMoves = 0;
            ForgetPursuit();
            ForgetFight();
            FightsAbandoned++;
        }

        private void ForgetFight()
        {
            _fightID = 0;
            _fightSwings = 0;
        }

        /// <summary>
        /// Have we swung at this thing repeatedly without its health ever going down?
        ///
        /// Called once per adjacent-attack decision. Any drop at all resets the patience, so a
        /// long fight against something with a lot of health is never cut short; only a fight
        /// where NOTHING is landing runs the counter up. A string of genuine misses can trip it
        /// too, and switching target after eight consecutive whiffs is the right move anyway.
        /// </summary>
        private bool NoDamageTo(WorldObject target)
        {
            if (target.ObjectID != _fightID)
            {
                _fightID = target.ObjectID;
                _fightHealth = target.Health;
                _fightSwings = 0;
                return false;
            }

            if (target.Health < _fightHealth)
            {
                _fightHealth = target.Health;
                _fightSwings = 0;
                return false;
            }

            return ++_fightSwings >= Math.Max(1, _config.AttackPatience);
        }

        /// <summary>
        /// Would picking a fight with this be a mistake, given what it has done to us before?
        ///
        /// This is the per-monster half of the danger model. The numbers were already being
        /// collected - worst hit, average hit, kills - and then used only to judge whole MAPS,
        /// which is far too blunt: one nasty thing wandering through a good hunting ground should
        /// be walked around, not cause the ground to be abandoned.
        ///
        /// Only applied when CHOOSING a fight. Something already adjacent and hitting us is dealt
        /// with by healing and fleeing, which sit above this; refusing to hit back at that point
        /// would be the worst of both.
        /// </summary>
        private bool TooDangerous(WorldModel world, WorldObject monster) =>
            Danger != null && monster != null &&
            Danger.TooDangerousToFight(monster.Name, world.Health,
                _config.DangerHitsToDeath, _config.DangerMinimumHits);

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

                // Learned cells are avoided ALWAYS, not only after a refusal. The point of having
                // learned them is that the route never goes through one again; waiting to be
                // refused first would be re-learning the same lesson on every approach.
                HashSet<Point> learned = Nav?.BlockedOn(world.MapIndex);

                if (learned != null && learned.Count > 0)
                {
                    if (avoid == null) avoid = learned;
                    else
                    {
                        avoid = new HashSet<Point>(avoid);
                        avoid.UnionWith(learned);
                    }
                }

                // Doors are obstacles unless we are trying to use one.
                //
                // Every cell that changes the map is avoided while hunting, because the server
                // lands an arriving character on the paired region of the exit coming back - so the
                // bot starts every map standing at the way out, and a chase or a loot run that
                // crosses one tile undoes the whole journey. Straight from the Mir 2 agents, which
                // add their known movement cells to the same obstacle set as blocking creatures and
                // gate it on whether the current move is local (MovementHelper.BuildObstacles).
                //
                // Travel.Active is our version of that gate: a journey walks ONTO an exit on
                // purpose, and its aim is a cell of the very exit this would otherwise forbid.
                if (Exits != null && _config.AvoidMapExits && (Travel == null || !Travel.Active))
                {
                    HashSet<Point> doors = Exits.ExitCellsOn(world.MapIndex);

                    if (doors.Count > 0)
                    {
                        // ExitCellsOn returns the cached set itself, so it is copied rather than
                        // aliased - a later union into "avoid" would otherwise edit the cache.
                        if (avoid == null) avoid = new HashSet<Point>(doors);
                        else
                        {
                            avoid = new HashSet<Point>(avoid);
                            avoid.UnionWith(doors);
                        }
                    }
                }

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

        /// <summary>
        /// Walk away from the map's exits after arriving on it, or null when there is nothing to do.
        ///
        /// Only ever runs on the decision after the map index changes, and gives up on a deadline:
        /// an entrance in a dead-end pocket may have nowhere further to go, and standing in it is
        /// better than refusing to hunt.
        /// </summary>
        private Decision ClearDoorway(WorldModel world)
        {
            bool arrived = _lastMapIndex != world.MapIndex;
            _lastMapIndex = world.MapIndex;

            if (!_config.AvoidMapExits || Exits == null) return null;
            if (Travel != null && Travel.Active) return null;

            int clearance = Math.Max(1, _config.DoorClearance);

            if (arrived)
            {
                _doorwayAim = Point.Empty;
                _doorwayDeadline = DateTime.MinValue;

                HashSet<Point> doors = Exits.ExitCellsOn(world.MapIndex);

                if (doors.Count == 0) return null;
                if (FarEnough(world.Location, doors, clearance)) return null;

                Point spot = FindClearOfDoors(world, doors, clearance);

                if (spot == Point.Empty) return null;

                _doorwayAim = spot;
                _doorwayDeadline = DateTime.UtcNow.AddSeconds(Math.Max(1, _config.DoorClearSeconds));
                DoorwaysCleared++;
            }

            if (_doorwayAim == Point.Empty) return null;

            // Something is in contact. Walking away from it just means being hit in the back for
            // the rest of the way, and the pathing avoidance already stops a fight carrying us
            // through a door - so fight it here and resume clearing once it is dead.
            WorldObject adjacent = world.NearestLiveMonster(1, _unreachable.Keys);

            if (adjacent != null) return null;

            HashSet<Point> current = Exits.ExitCellsOn(world.MapIndex);

            if (DateTime.UtcNow > _doorwayDeadline || FarEnough(world.Location, current, clearance))
            {
                _doorwayAim = Point.Empty;
                return null;
            }

            int remaining = WorldModel.Distance(world.Location, _doorwayAim);

            Decision step = new Decision
            {
                Action = BotAction.Approach,
                Reason = $"stepping clear of the exit ({remaining} tiles)",
                Subject = "leaving the doorway",
                Destination = _doorwayAim
            };

            // No route to the spot we picked - it is not worth a second search, so hunt from here.
            if (!TrySteer(step, world, _doorwayAim, 0, remaining))
            {
                _doorwayAim = Point.Empty;
                return null;
            }

            return step;
        }

        private static bool FarEnough(Point from, HashSet<Point> doors, int clearance)
        {
            foreach (Point door in doors)
                if (WorldModel.Distance(from, door) < clearance) return false;

            return true;
        }

        /// <summary>
        /// Nearest walkable cell that is at least clearance tiles from every exit on this map.
        ///
        /// Breadth-first from where we stand rather than a guessed direction, because an entrance
        /// can face any way and the inside of a cave is rarely the way the door points. The budget
        /// is small on purpose: this runs on the bot's own thread, and failing to find a spot costs
        /// nothing worse than hunting from where it landed.
        /// </summary>
        private Point FindClearOfDoors(WorldModel world, HashSet<Point> doors, int clearance)
        {
            MapGrid grid = Maps?.For(world.MapIndex);

            if (grid == null) return Point.Empty;

            Queue<Point> open = new Queue<Point>();
            HashSet<Point> seen = new HashSet<Point> { world.Location };

            open.Enqueue(world.Location);

            int examined = 0;

            while (open.Count > 0)
            {
                Point current = open.Dequeue();

                if (++examined > 4000) break;

                if (current != world.Location && FarEnough(current, doors, clearance)) return current;

                foreach (MirDirection direction in AllDirections)
                {
                    Point next = Functions.Move(current, direction);

                    if (!seen.Add(next)) continue;
                    if (!grid.Walkable(next)) continue;

                    open.Enqueue(next);
                }
            }

            return Point.Empty;
        }

        private static readonly MirDirection[] AllDirections =
        {
            MirDirection.Up, MirDirection.UpRight, MirDirection.Right, MirDirection.DownRight,
            MirDirection.Down, MirDirection.DownLeft, MirDirection.Left, MirDirection.UpLeft
        };

        public int KitesMade;
        public int SummonsCast;

        private DateTime _nextSummon = DateTime.MinValue;

        /// <summary>
        /// Put a skeleton out if we have none.
        ///
        /// Deliberately narrow, because the server makes this more dangerous than it looks:
        ///
        ///   - The amulet is consumed in MagicCast, BEFORE the two-pet cap is checked in
        ///     MagicComplete. A refused summon therefore costs a reagent and produces no error, so
        ///     repeating it is a silent drain. Hence the retry gate.
        ///   - We CANNOT count our own pets reliably. A pet outside our view, or dropped by a map
        ///     change, vanishes from WorldModel while still occupying a slot in Player.Pets on the
        ///     server. Visible counting reduces waste; it cannot prevent it. One pet only.
        ///   - Re-casting is also the recall command (SummonSkeleton.cs:51), but the server already
        ///     recalls a pet that drifts out of view on its own (MonsterObject.cs:966), and
        ///     re-casting to "fix" ordinary drift burns an amulet before that recall happens. So a
        ///     missing pet means missing from the world, not merely far away.
        ///
        /// Pets are destroyed on logout and on the owner's death, so this naturally re-summons
        /// after a reconnect and after a revive without needing to know that it should.
        /// </summary>
        private Decision KeepSummon(WorldModel world, Backpack items)
        {
            if (!_config.KeepSummon || !_config.CastSpells) return null;
            if (world.Dead || DateTime.UtcNow < _nextSummon) return null;
            if (Town != null && Town.Active) return null;

            if (!world.TryGetMagic(MagicType.SummonSkeleton, out ClientUserMagic magic)) return null;
            if (magic.Info == null || world.Level < magic.Info.NeedLevel1) return null;

            // Already have one out.
            foreach (WorldObject pet in world.OwnPets) return null;

            // No reagent, no summon - and the server would take the amulet it does not have and
            // tell us nothing. Checked here so the refusal is ours and is logged.
            if (items.CountReagent(ItemType.Amulet) <= 0)
            {
                SummonDiagnostic = "no amulet equipped or carried";
                _nextSummon = DateTime.UtcNow.AddSeconds(Math.Max(5, _config.SummonRetrySeconds));
                return null;
            }

            int floor = world.MaxMana * Math.Clamp(_config.SpellManaFloorPercent, 0, 90) / 100;
            if (world.Mana - magic.Cost < floor) return null;

            _nextSummon = DateTime.UtcNow.AddSeconds(Math.Max(5, _config.SummonRetrySeconds));
            SummonsCast++;
            SummonDiagnostic = "summoning a skeleton";

            return new Decision
            {
                Action = BotAction.Cast,
                Reason = "summoning a skeleton",
                Subject = magic.Info.Name,
                // No target and no location: MagicCast ignores both and spawns the pet on the tile
                // BEHIND the caster, chosen from the direction we send.
                TargetID = 0,
                Direction = world.Direction,
                Point = world.Location,
                Magic = magic.Info.Magic
            };
        }

        /// <summary>
        /// Leave this object alone for a while. One expiry mechanism, several sentence lengths -
        /// the park list expires on a fixed UnreachableFor, so a different duration is served by
        /// back-dating the entry rather than by adding a second table to keep in step.
        /// </summary>
        private void Park(uint objectID, TimeSpan forHowLong)
        {
            _unreachable[objectID] = DateTime.Now + forHowLong - UnreachableFor;
        }

        /// <summary>Why the last summon attempt was skipped, for the log.</summary>
        public string SummonDiagnostic = "";

        /// <summary>
        /// Back away from a target that has got too close, so the next spell can be cast rather
        /// than swung.
        ///
        /// Two tiers, taken from the Mir 2 agents. A cheap ray directly away from the target first,
        /// and only when that is blocked, a scan of the ring around us. The ray is first-acceptable
        /// rather than best: it stops at the minimum viable distance instead of walking as far as
        /// it can.
        ///
        /// Deliberately NOT gated on the learned danger model. NearestWorthFighting already refuses
        /// to engage anything TooDangerous, so a kiting branch gated the same way could never
        /// receive such a target and would be dead code. The gate is what the class can do and how
        /// close the thing is.
        ///
        /// No line-of-sight test anywhere, because Zircon has none: FireBall.MagicCast checks
        /// CanAttackTarget and range and nothing else, and there is no LineOfSight or ray-cast in
        /// the server at all. Half of the Mir 2 kiting code exists to serve a rule this game does
        /// not have.
        /// </summary>
        private Decision Kite(WorldModel world, WorldObject target, int distance)
        {
            if (!_config.KiteWhileCasting || target == null) return null;
            if (distance <= 0 || distance >= Math.Max(1, _config.KiteWhenCloserThan)) return null;

            // Nothing to gain by backing off if we have no ranged answer. A caster out of mana is
            // a melee character for the moment and should behave like one.
            //
            // This used to ask HasCastable, which only answers whether the character KNOWS a spell.
            // It said yes to a Taoist sitting at 0 of 74 mana, so the bot spent its fights walking
            // backwards to a range it could do nothing from, then forwards again, and killed
            // almost nothing for hours. CanCastNow applies the same gates as the spell chooser -
            // mana, the mana floor, cooldowns - so the retreat is only ever made for a spell that
            // could actually follow it.
            if (!Spells.CanCastNow(world)) return null;

            MapGrid grid = Maps?.For(world.MapIndex);
            if (grid == null) return null;

            int want = Math.Max(distance + 1, _config.KiteRetreatRange);
            Point spot = RetreatRay(world, grid, target, want);

            if (spot == Point.Empty) spot = SafestRing(world, grid, target, want);
            if (spot == Point.Empty) return null;

            int remaining = WorldModel.Distance(world.Location, spot);

            Decision step = new Decision
            {
                Action = BotAction.Approach,
                Reason = $"backing off {target.Name} at {distance} to cast from {want}",
                Subject = "keeping range",
                Destination = spot
            };

            if (!TrySteer(step, world, spot, 0, remaining)) return null;

            KitesMade++;
            return step;
        }

        /// <summary>
        /// Walk the straight line directly away from the target and take the first cell that is far
        /// enough. A wall or an occupied cell ENDS the ray - everything beyond it is unreachable in
        /// a straight line - while being merely too close only skips.
        /// </summary>
        private Point RetreatRay(WorldModel world, MapGrid grid, WorldObject target, int want)
        {
            MirDirection away = WorldModel.DirectionTo(target.Location, world.Location);
            HashSet<Point> blocked = world.OccupiedCells(target.ObjectID);
            HashSet<Point> doors = Doors(world);

            Point at = world.Location;

            for (int i = 1; i <= want; i++)
            {
                at = WorldModel.Step(at, away);

                if (!grid.Walkable(at)) break;
                if (blocked.Contains(at)) break;
                if (doors != null && doors.Contains(at)) break;
                if (Nav?.BlockedOn(world.MapIndex)?.Contains(at) == true) break;

                if (WorldModel.Distance(at, target.Location) < want) continue;

                return at;
            }

            return Point.Empty;
        }

        /// <summary>
        /// The ring of cells at roughly the distance we want to move, scored on how clear of OTHER
        /// monsters each one is.
        ///
        /// The current target is excluded from that scan on purpose - we are not running from the
        /// thing we are shooting, we are running from everything else - but a hard minimum distance
        /// from it is still enforced, or the "safest" cell could be one that is further from the
        /// pack and closer to the target, which defeats the entire point.
        /// </summary>
        private Point SafestRing(WorldModel world, MapGrid grid, WorldObject target, int want)
        {
            int reach = Math.Max(2, want - WorldModel.Distance(world.Location, target.Location));
            HashSet<Point> blocked = world.OccupiedCells(target.ObjectID);
            HashSet<Point> doors = Doors(world);
            HashSet<Point> learned = Nav?.BlockedOn(world.MapIndex);

            Point best = Point.Empty;
            int bestScore = int.MinValue;

            for (int dx = -reach; dx <= reach; dx++)
                for (int dy = -reach; dy <= reach; dy++)
                {
                    Point candidate = new Point(world.Location.X + dx, world.Location.Y + dy);
                    int step = WorldModel.Distance(world.Location, candidate);

                    if (step < reach - 1 || step > reach) continue;
                    if (!grid.Walkable(candidate)) continue;
                    if (blocked.Contains(candidate)) continue;
                    if (doors != null && doors.Contains(candidate)) continue;
                    if (learned != null && learned.Contains(candidate)) continue;

                    // The whole purpose of the move.
                    if (WorldModel.Distance(candidate, target.Location) < want) continue;

                    // Distance to the nearest OTHER hostile, saturated: past six tiles one threat
                    // is as irrelevant as another, and without the cap empty space dominates the
                    // score and swamps the difference between candidates.
                    int nearest = 6;

                    foreach (WorldObject other in world.Objects)
                    {
                        if (!other.IsValidTarget || other.ObjectID == target.ObjectID) continue;

                        int d = WorldModel.Distance(candidate, other.Location);
                        if (d <= 6 && d < nearest) nearest = d;
                    }

                    int score = nearest - step;

                    if (score <= bestScore) continue;

                    bestScore = score;
                    best = candidate;
                }

            return best;
        }

        private HashSet<Point> Doors(WorldModel world) =>
            _config.AvoidMapExits && Exits != null && (Travel == null || !Travel.Active)
                ? Exits.ExitCellsOn(world.MapIndex)
                : null;

        private Point _roamTarget = Point.Empty;

        /// <summary>
        /// Where the bot is currently wandering to, or empty.
        ///
        /// Published so the map can show it. Read-only on purpose: the target is chosen and
        /// cleared inside PickRoamSpot and nothing outside the brain has any business setting it.
        /// </summary>
        public Point RoamTarget => _roamTarget;
        private DateTime _roamUntil = DateTime.MinValue;

        /// <summary>
        /// Wander by walking somewhere, not by walking some way.
        ///
        /// The old roam picked one of eight compass directions and committed to 3-12 steps,
        /// re-rolling after two blocked moves. It is a hill-climber, and the shape it cannot solve
        /// is the common one: a tree, a wall, a cave mouth. The bot bumps, re-rolls, and draws
        /// again - so it mills in whatever pocket it happens to be in. Bot 1 did this in Banya
        /// Cave for minutes at a time.
        ///
        /// Picking a destination and routing to it with the same A* everything else uses fixes the
        /// shape rather than the symptom: the path goes AROUND the tree. Doors are already in the
        /// avoid set, so a wander cannot leave the map either.
        ///
        /// It also does not tow a train. This is the last branch of Decide, so a monster coming
        /// into range preempts the whole thing on the next decision - the bot fights what it meets
        /// on the way and resumes afterwards, rather than sprinting past a dozen monsters and
        /// arriving somewhere surrounded.
        /// </summary>
        private Decision Wander(WorldModel world, string reason, bool forceNew)
        {
            MapGrid grid = Maps?.For(world.MapIndex);

            if (grid == null || !_config.RoamToDestinations)
                return Drift(world, reason, forceNew);

            if (forceNew) _roamTarget = Point.Empty;

            if (_roamTarget != Point.Empty &&
                (DateTime.UtcNow > _roamUntil ||
                 WorldModel.Distance(world.Location, _roamTarget) <= 1))
                _roamTarget = Point.Empty;

            if (_roamTarget == Point.Empty)
            {
                _roamTarget = PickRoamSpot(world, grid);
                _roamUntil = DateTime.UtcNow.AddSeconds(Math.Max(5, _config.RoamRetargetSeconds));
            }

            if (_roamTarget == Point.Empty) return Drift(world, reason, forceNew);

            int remaining = WorldModel.Distance(world.Location, _roamTarget);

            Decision step = new Decision
            {
                Action = BotAction.Roam,
                Reason = $"{reason} - wandering to {_roamTarget.X},{_roamTarget.Y} ({remaining} tiles)",
                Subject = "roaming",
                Destination = _roamTarget
            };

            // No route: the spot is walkable but walled off from here. Forget it and drift this
            // turn rather than searching again on the bot's own thread.
            if (!TrySteer(step, world, _roamTarget, 0, remaining))
            {
                _roamTarget = Point.Empty;
                return Drift(world, reason, forceNew);
            }

            return step;
        }

        /// <summary>The old blind walk, kept for maps with no grid and as the fallback when no
        /// destination can be found or reached.</summary>
        private Decision Drift(WorldModel world, string reason, bool forceNew) => new Decision
        {
            Action = BotAction.Roam,
            Reason = reason,
            Subject = "roaming",
            Direction = NextRoamDirection(world, forceNew),
            Distance = RunDistance(world, _config.AggroRange)
        };

        /// <summary>
        /// A random walkable cell within RoamRadius. Sampled rather than enumerated: building the
        /// set of every walkable cell in range costs a square of the radius on the bot's own
        /// thread, and a handful of darts finds open ground on any map that has any.
        /// </summary>
        private Point PickRoamSpot(WorldModel world, MapGrid grid)
        {
            int radius = Math.Max(4, _config.RoamRadius);
            HashSet<Point> doors = _config.AvoidMapExits && Exits != null
                ? Exits.ExitCellsOn(world.MapIndex)
                : null;

            for (int attempt = 0; attempt < 24; attempt++)
            {
                int x = world.Location.X + _random.Next(-radius, radius + 1);
                int y = world.Location.Y + _random.Next(-radius, radius + 1);
                Point candidate = new Point(x, y);

                if (!grid.Walkable(candidate)) continue;
                if (WorldModel.Distance(world.Location, candidate) < 4) continue;
                if (doors != null && doors.Contains(candidate)) continue;
                if (Nav != null && Nav.BlockedOn(world.MapIndex)?.Contains(candidate) == true) continue;

                return candidate;
            }

            return Point.Empty;
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

                // Commitment survives a monster becoming frightening mid-fight. Walking away from
                // something that is already on us just means being hit in the back; the health
                // thresholds above decide when to actually disengage.
                if (stillGood) return current;

                _committedTarget = 0;
                ForgetFight();
            }

            WorldObject next = NearestWorthFighting(world);

            if (next != null)
            {
                if (_committedTarget != 0 && next.ObjectID != _committedTarget)
                {
                    TargetSwitches++;
                    ForgetFight();
                }

                _committedTarget = next.ObjectID;
            }

            return next;
        }

        /// <summary>
        /// The nearest monster we are willing to START a fight with, skipping anything the danger
        /// memory says would take us apart at the health we currently have.
        ///
        /// Walking past one is deliberate and not the same as fleeing: we simply do not initiate.
        /// If it comes to us anyway, the ordinary heal/flee rules take over.
        /// </summary>
        private WorldObject NearestWorthFighting(WorldModel world)
        {
            if (Danger == null || _config.DangerHitsToDeath <= 0)
                return world.NearestLiveMonster(_config.AggroRange, _unreachable.Keys);

            HashSet<uint> skip = new HashSet<uint>(_unreachable.Keys);

            while (true)
            {
                WorldObject candidate = world.NearestLiveMonster(_config.AggroRange, skip);
                if (candidate == null) return null;

                if (!TooDangerous(world, candidate)) return candidate;

                // Not parked in _unreachable: the judgement depends on our CURRENT health, so it
                // has to be re-made every tick rather than latched. After a few potions the same
                // monster becomes a fair fight again.
                AvoidedDangerous++;
                skip.Add(candidate.ObjectID);
            }
        }

        public int AvoidedDangerous;

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
        /// <summary>Pick-ups asked for at zero range that did not remove the item.</summary>
        private readonly Dictionary<uint, int> _lootAttempts = new Dictionary<uint, int>();

        /// <summary>
        /// An object left our view, so everything remembered about that id must go with it.
        ///
        /// This closes a real leak as well as a correctness hole. _lootAttempts was only ever
        /// cleared when the give-up limit was REACHED, so every item the bot successfully picked up
        /// left its counter behind for the life of the connection. And because the server recycles
        /// object ids, a stale counter does not merely waste memory - it applies to whatever
        /// inherits the number, which can make a fresh drop unlootable on sight.
        /// </summary>
        public void ForgetObject(uint objectID)
        {
            _lootAttempts.Remove(objectID);
            _unreachable.Remove(objectID);

            if (_committedTarget == objectID) _committedTarget = 0;
            if (_pursuitID == objectID) ForgetPursuit();
            if (_fightID == objectID) ForgetFight();
        }

        private const int LootAttemptLimit = 4;

        private Decision TryLoot(WorldModel world)
        {
            if (!_config.LootEnabled) return null;

            bool heavy = world.MaxBagWeight > 0 && world.WeightPercent >= _config.HeavyWeightPercent;
            bool full = world.MaxBagWeight > 0 && world.WeightPercent >= 100;

            WorldObject item = FindWorthwhileLoot(world, heavy, full);
            if (item == null) return null;

            int distance = world.DistanceTo(item.Location);

            if (distance == 0)
            {
                // Standing on it and asking again. A refused pick-up is silent - the server's
                // ItemObject.PickUpItem just returns false - so the only evidence that it failed
                // is that the item is still here. HasRoomFor should mean we never get here, but
                // this is the backstop for every OTHER reason a pick-up can be refused (ownership
                // timers on another player's drop, an expired item, a rule we have not modelled),
                // and without it the bot stands still for ever.
                _lootAttempts.TryGetValue(item.ObjectID, out int tries);
                _lootAttempts[item.ObjectID] = tries + 1;

                if (tries >= LootAttemptLimit)
                {
                    // We stood on it and asked repeatedly and it is still there, so the server is
                    // refusing - ownership timer, an expiry, a rule we have not modelled. Whatever
                    // it is will not change in the next few seconds, so this is the long sentence.
                    _lootAttempts.Remove(item.ObjectID);
                    Park(item.ObjectID, RefusedByServer);
                    return null;
                }

                return new Decision { Action = BotAction.Loot, Reason = item.Name, Subject = item.Name };
            }

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
                // Could not get there THIS TICK. Usually something is standing in the way, and
                // standing is a temporary condition - so a short sentence, or the bot walks away
                // from loot it could have had a moment later.
                Park(item.ObjectID, BlockedForNow);
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

            _lastMoveWasSingleStep = _lastWasMove && decision.Distance == 1;
            _lastMoveTarget = _lastWasMove
                ? Functions.Move(world.Location, decision.Direction, decision.Distance)
                : Point.Empty;

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

                case BotAction.SetPetMode:
                    _nextAction = DateTime.Now + TurnTime + Margin;
                    break;

                case BotAction.Cast:
                    // Globals.CastTime, the ACTION gate. The longer between-spells gate
                    // (Globals.MagicDelay) is SpellBook's own, because it applies to casting and
                    // not to moving or drinking.
                    Spells.Issued();
                    _nextAction = DateTime.Now + CastTime + Margin;
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
