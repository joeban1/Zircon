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
        MergeParts, AssemblePart,
        NPCRepair, LearnBook, Logout, NPCCall, NPCButton, NPCSell, NPCBuy, NPCClose,
        MagicToggle, Unlock, Cast, DropItem, Butcher, QuestAccept, QuestComplete, UseItem,
        StoreBuy
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

        /// <summary>QuestAccept / QuestComplete: the QuestInfo index.</summary>
        public int QuestIndex;

        /// <summary>StoreBuy: the StoreInfo index, bought with Hunt Gold.</summary>
        public int StoreIndex;
        public EquipRequest Equip;

        /// <summary>Tiles to move: 1 walks, 2 runs. 3 requires a horse and is rejected without one.</summary>
        public int Distance = 1;

        public int ButtonID;

        /// <summary>Attack: the skill to ride along on this swing. MagicToggle: the skill to enable.</summary>
        public MagicType Magic = MagicType.None;
        public int BuyIndex;
        public long BuyAmount;
        /// <summary>Only gear and skill-book orders count as capital spending after confirmation.</summary>
        public bool CapitalPurchase;
        public int BuyItemIndex;
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

        /// <summary>ItemMove: merge the source stack into an occupied destination.</summary>
        public bool MergeItem;

        /// <summary>For SetPetMode.</summary>
        public PetMode PetMode;
        public System.Collections.Generic.List<int> SellSlots;

        /// <summary>
        /// Slot -> how many units to sell, where only PART of the stack is surplus. Slots missing
        /// from here (or a null map) are sold whole, which is every non-consumable.
        /// </summary>
        public System.Collections.Generic.Dictionary<int, long> SellCounts;
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

        /// <summary>Which monsters are worth butchering. Set by the host; null disables it.</summary>
        private ButcherIndex _butcher;

        public ButcherIndex Butcher { set => _butcher = value; }

        private DateTime _nextAction = DateTime.MinValue;
        private DateTime _nextAssassinRanged = DateTime.MinValue;
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

        /// <summary>Set whenever Decide returns nothing, so the caller can say why.</summary>
        public string IdleReason;

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

        /// <summary>
        /// RecoveryMaps contain deliberately trivial enemies. While there, preserve mana and
        /// reagents: ordinary melee, looting, butchering and every emergency survival branch stay
        /// active, but optional combat magic does not turn free recovery into another expense.
        /// </summary>
        public bool FrugalRecoveryCombat;

        /// <summary>Poverty recovery is active anywhere, not only on the recovery maps.</summary>
        public bool InRecovery;

        public Decision Decide(WorldModel world, Backpack items, bool itemUsePending)
        {
            // Why this tick produced nothing, for the caller to surface. A bot that decides
            // NOTHING is invisible in the log - there is no line to print - and that is exactly
            // the state worth seeing: an assassin arrived in Deserted Mine at 45% health, made
            // one Heal, then produced not a single decision for fifteen seconds while it was
            // killed. The other three bots were busy throughout, so it was not a host stall; its
            // own Decide simply kept returning null and nothing recorded which branch did it.
            IdleReason = null;

            // Coverage is observation, not an action, so it must keep learning while an action
            // cooldown or a fight prevents Wander from running. World.Location is authoritative:
            // it is updated only from server packets, never optimistically when a move is sent.
            ObserveExploration(world);

            if (!Ready)
            {
                IdleReason = $"action gate until {_nextAction:HH:mm:ss.fff}";
                return null;
            }

            if (world.Dead) return new Decision { Action = BotAction.Idle, Reason = "dead" };

            if (world.SelfID == 0)
            {
                IdleReason = "no self object yet";
                return null;
            }

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
                !itemUsePending &&
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
                    if (headingToTown) Town.ScrolledOut(world, escape);
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
                //
                // TELEPORTING IS NOT TRAVELLING AND MUST NOT BE INTERRUPTED.
                //
                // The rule exists to stop a bot walking across a map when it should be standing
                // still and drinking. A scroll is the opposite of that walk: it is instant, it is
                // the fastest possible way out of the danger, and it has already been paid for.
                // Cancelling it throws the scroll away and leaves the bot exactly where it was,
                // in danger, now on foot.
                //
                // An assassin in Deserted Mine did precisely this - "Teleporting scrolling to town
                // to shop" at 09:42:54, "interrupted at 57% HP" five seconds later - and then set
                // off walking to Bichon Town with three unused scrolls in its bag. The same rule
                // had already cancelled its escape at the mine doorway while it was being killed.
                if (Town.Phase == TownPhase.Travelling)
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
            MagicType pending = FrugalRecoveryCombat ? MagicType.None : Skills.PendingToggle(world);

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
            // LEARNING IS A DICE ROLL, and the book is consumed either way.
            //
            // A gate on InCombat was added here and removed again within the hour, which is worth
            // recording. The reasoning looked sound - the server refuses item use for ten seconds
            // after combat, that rule had already been found behind the dud town scrolls, and Jill
            // failed to learn a Summon Skeleton with two monsters on her. But the log says
            // otherwise: across 24 attempts with a measurable outcome, 21 succeeded. An 88% rate
            // is a per-book chance, not a combat block, because these bots are in combat for most
            // of the time they are looting.
            //
            // Worse, the gate was dangerous. Delaying a learn until the bot is out of combat means
            // carrying the book to town, where the disposal rules can decide it is surplus and
            // sell it. Reading it the instant it is picked up is the safest thing to do with
            // something that rare, and a failed roll costs exactly the same as a delayed sale.
            //
            // What survives from the episode is the outcome check in CheckPendingLearn: the bot
            // used to assume every learn worked, so six looted Summon Skeletons and zero learnt
            // skills looked identical to success in the log.
            if (!itemUsePending && _config.LearnBooks && Books != null &&
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

            // 0a-ii. Quest rewards: drink the stat buffs as soon as they are held (a timed one
            //        pauses in a safe zone, so drinking it in town wastes nothing), and read a
            //        Scroll of Boss Tracking when it can find something.
            if (!itemUsePending && (QuestBook != null || Store != null) &&
                world.NearestLiveMonster(2, _unreachable.Keys) == null)
            {
                Decision use = UseBuffItems(world, items);
                if (use != null) return use;
            }

            // 0a-iii. The game store: buy the next thing on the class's list with Hunt Gold. No NPC
            //         is involved, so this can happen anywhere - but not mid-fight, and one
            //         purchase at a time, settled by the Hunt Gold actually dropping.
            {
                Decision buy = StoreStep(world, items, itemUsePending);
                if (buy != null) return buy;
            }

            // 0a. Throw away starter kit we have already replaced.
            //
            //     It cannot be sold - the instance is flagged Worthless whatever the database says
            //     - so carrying it is permanent bag weight on bots that live near the cap. Done
            //     here, beside dressing, because that is where the bag's gear is already being
            //     reasoned about, and gated on being out of combat so a drop never costs a swing.
            if (_config.DropReplacedStarterKit && !DropPending &&
                world.NearestLiveMonster(2, _unreachable.Keys) == null)
            {
                int junk = items.FirstReplacedStarterSlot();

                if (junk >= 0 && DropRefusedRecently(junk)) junk = -1;

                if (junk >= 0)
                    return new Decision
                    {
                        Action = BotAction.DropItem,
                        Reason = $"replaced starter kit, and no vendor will take it",
                        Subject = items.InSlot(junk)?.Info?.ItemName ?? "starter kit",
                        PotionSlot = junk
                    };
            }

            // 0. Get dressed. Starter gear sits in the bag until something equips it, and a bare
            //    character fights considerably worse. One item per tick so the server's item
            //    handling is never flooded.
            EquipRequest unlockForEquip = items.PendingEquipUnlock(world.Class, world.Gender);

            if (unlockForEquip != null)
                return new Decision
                {
                    Action = BotAction.Unlock,
                    Reason = $"unlocking {unlockForEquip.ItemName} to equip in " +
                             $"{unlockForEquip.Slot}",
                    Subject = unlockForEquip.ItemName,
                    FromSlot = unlockForEquip.FromSlot
                };

            foreach (EquipRequest request in items.PendingEquips(world.Class, world.Gender))
                return new Decision
                {
                    Action = BotAction.Equip,
                    Reason = request.ToString(),
                    Subject = request.ItemName,
                    Equip = request
                };

            // 0b. Pinned on one cell for too long. See BotConfig.StuckSeconds.
            //
            //     Deliberately AFTER healing is chosen below? No - before, because the thing that
            //     makes this state so durable is that healing always has something to do. The bot
            //     drinks, gets hit, drinks again, and every tick looks purposeful.
            NoteWhereWeAre(world);

            if (StuckTooLong(world))
            {
                ClearStuck();
                _stuckStrikes++;

                // Whatever it was chasing or walking to, it is not working. Park both, so the next
                // SelectTarget is free to pick the thing actually standing on us.
                ForceNextTarget();
                if (IsCoverageTarget)
                    AbandonExploration(world, "stationary watchdog", SweepSentence);
                else
                    ClearRoamTarget();

                // And if a town trip is already running, stop it cancelling itself. The abort rule
                // is "in danger, and able to heal, and not forced" - which is exactly true of a bot
                // pinned at a doorway with a bag full of potions, so it aborted its own escape and
                // re-approached, over and over. Forcing the trip removes the last clause.
                // ESCALATE. Retargeting is the right first answer and a useless second one.
                //
                // An assassin pinned on the Deserted Mine entry cell was attacking throughout -
                // Devouring Ghost, then Ghost Mage after the first strike switched it - healing
                // between every swing, bag draining 147 to 136, and killing nothing. Eleven
                // monsters on one cell at the new spawn density is not a fight it can win, and
                // picking a different one of the eleven does not change that. The Mir 2 agents
                // reach the same conclusion and retreat or teleport out.
                //
                // So the second strike stops arguing with the fight and leaves. Force() starts a
                // trip, and on one already walking it sets the unstick scroll for the next tick -
                // bounded by _scrolledToUnstick, so this cannot burn a scroll per strike.
                if (_stuckStrikes >= 2 && Town != null)
                {
                    BrainLog?.Invoke($"stuck on {world.Location.X},{world.Location.Y} for the " +
                                     $"{_stuckStrikes}{(_stuckStrikes == 2 ? "nd" : "th")} time - " +
                                     "this fight is not winnable from here, leaving for town");
                    Town.Force();
                }
                else
                {
                    BrainLog?.Invoke($"stuck on {world.Location.X},{world.Location.Y} for " +
                                     $"{_config.StuckSeconds}s - dropped target and roam goal");
                }
            }

            // 1. Heal. Highest priority: being alive beats everything else.
            if (!itemUsePending && world.MaxHealth > 0 &&
                world.HealthPercent <= _config.HealAtPercent &&
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
            if (!FrugalRecoveryCombat && !itemUsePending &&
                _config.DrinkManaAtPercent > 0 && world.MaxMana > 0 &&
                world.ManaPercent <= _config.DrinkManaAtPercent &&
                world.HealthPercent > _config.HealAtPercent &&
                DateTime.Now >= _nextPotion &&
                (Spells.UsesMana(world) || Skills.NeedsMana(world)))
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
            // A trip may decide to spend a scroll and advance its own phase before the packet is
            // sent. Pause that state machine while another item use is outstanding; survival
            // branches above and ordinary combat below remain free to act in the meantime.
            if (Town != null && !itemUsePending)
            {
                Decision trip = Town.Next(world, items, itemUsePending);

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
                        if (!TrySteer(trip, world, trip.Destination, 0, remaining, routeKind: "town"))
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
                {
                    IdleReason = $"standing by for the town trip ({Town.Phase})";
                    return null;
                }
            }

            // 2a-ii. Quest errand (QuestErrand): only on the quest NPC's map, only between town
            //        trips. A trip that starts takes over and the errand steps aside unpunished.
            if (Quest != null)
            {
                if (Town != null && Town.Active)
                    Quest.Suspend("a town trip started");
                else if (!itemUsePending)
                {
                    Decision errand = Quest.Next(world, items);

                    if (errand != null)
                    {
                        if (errand.Action == BotAction.WalkTo)
                        {
                            int remaining = WorldModel.Distance(world.Location, errand.Destination);
                            errand.Action = BotAction.Approach;

                            // To TALKING range, not onto the NPC's own cell - the errand talks from
                            // VendorTalkRange, and the exact cell is where the NPC stands.
                            NoteNpcWalk(world, errand.Subject);
                            if (!TrySteer(errand, world, errand.Destination,
                                    Math.Max(1, _config.VendorTalkRange - 1), remaining, routeKind: "town"))
                                return new Decision
                                {
                                    Action = BotAction.Idle,
                                    Reason = $"no route to {errand.Destination.X},{errand.Destination.Y}",
                                    Subject = errand.Subject
                                };
                        }

                        return errand;
                    }
                }
            }

            // 2a-iii. Fame errand (FameErrand): only on the fame NPC's map, only between town
            //         trips, and only while Fame Points cover the next rank.
            if (Fame != null)
            {
                if (Town != null && Town.Active)
                    Fame.Suspend("a town trip started");
                else if (!itemUsePending && (Quest == null || !Quest.Active))
                {
                    Decision fame = Fame.Next(world, items);

                    if (fame != null)
                    {
                        if (fame.Action == BotAction.WalkTo)
                        {
                            int remaining = WorldModel.Distance(world.Location, fame.Destination);
                            fame.Action = BotAction.Approach;

                            NoteNpcWalk(world, fame.Subject);
                            if (!TrySteer(fame, world, fame.Destination,
                                    Math.Max(1, _config.VendorTalkRange - 1), remaining, routeKind: "town"))
                                return new Decision
                                {
                                    Action = BotAction.Idle,
                                    Reason = $"no route to {fame.Destination.X},{fame.Destination.Y}",
                                    Subject = fame.Subject
                                };
                        }

                        return fame;
                    }
                }
            }

            // 2b. Cross-map travel. Below the town trip, because arriving somewhere new with a
            //     full bag and no potions is how a journey ends in a corpse; above fighting,
            //     because a bot that stops to kill everything never gets anywhere.
            //
            //     EXCEPT WHEN SOMETHING IS IN CONTACT. See BotConfig.FightThroughRange. A monster
            //     standing next to us is not a fight we are choosing - it is hitting us, and it is
            //     occupying the cell we are trying to walk through. Running is not an option the
            //     server will grant, so the choice is really "fight it" or "shuffle against it
            //     while it kills us", and the bot spent fourteen seconds picking the second one.
            // A town trip owns the bot until it finishes, even on a tick where it has no step to
            // give (Banking and Returning have those). Walking the journey in that gap is how the
            // journey's stuck clock expired mid-shop.
            // The quest errand owns movement the same way: a journey passing through Bichon is
            // held for the errand and carries on afterwards, never replaced.
            bool tripOwnsUs = Town != null && Town.Active || Quest != null && Quest.OwnsMovement ||
                              Fame != null && Fame.OwnsMovement;
            if (tripOwnsUs) Travel?.Hold();

            if (!tripOwnsUs && Travel != null && Travel.Active && FightingThrough(world))
            {
                Travel.NoteFightingThrough();

                // Said once per leg, not once per tick: this is the interesting moment, and the
                // old behaviour was invisible precisely because nothing ever mentioned it.
                if (!_saidFightingThrough)
                {
                    _saidFightingThrough = true;
                    BrainLog?.Invoke($"Travel: hostiles in contact on {world.MapName} - holding " +
                                     $"the journey until nothing is within " +
                                     $"{Math.Max(_config.FightThroughRange, _config.FightThroughClearRange)} " +
                                     "tiles rather than walking deeper into them.");
                }
                // Fall through to combat; the journey resumes once nothing is in contact.
            }
            else if (!tripOwnsUs && Travel != null && Travel.Active)
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

                    if (TrySteer(leg, world, leg.Destination, 0, remaining, routeKind: "travel"))
                    {
                        _saidBoxedIn = false;
                        _saidCrowdStep = false;
                        return leg;
                    }

                    // A route that fails only because creatures fill the gaps is a crowd, not a
                    // wall. After one bumped move every monster counts as an obstacle, so a bot
                    // surrounded 41 tiles from Deserted Mine's stairs found "no route", abandoned
                    // the journey and scrolled home at 80% HP while winning every fight.
                    if (_blockedMoves > 0 &&
                        TrySteer(leg, world, leg.Destination, 0, remaining, ignoreCreatures: true, routeKind: "travel"))
                    {
                        if (!AdjacentTarget(world))
                        {
                            if (!_saidCrowdStep)
                            {
                                _saidCrowdStep = true;
                                BrainLog?.Invoke($"Travel: route blocked by creatures on " +
                                                 $"{world.MapName} - stepping along the bare-map " +
                                                 "route instead of abandoning the journey.");
                            }

                            return leg;
                        }

                        // Only a fight we are winning holds off the journey's stall watchdog;
                        // one we are not still ends in "stuck" and the usual escape.
                        if (WinningFights(world)) Travel.NoteFightingThrough();

                        if (!_saidBoxedIn)
                        {
                            _saidBoxedIn = true;
                            BrainLog?.Invoke($"Travel: boxed in by monsters on {world.MapName} " +
                                             "- clearing a way rather than abandoning the journey.");
                        }
                        // Fall through to combat.
                    }
                    else
                    {
                        Point unreachable = leg.Destination;

                        while (Travel.TryAnotherExitCell(world))
                        {
                            leg.Destination = Travel.Aim;
                            remaining = WorldModel.Distance(world.Location, leg.Destination);

                            if (!TrySteer(leg, world, leg.Destination, 0, remaining,
                                    ignoreCreatures: true, routeKind: "travel"))
                                continue;

                            leg.Reason = $"{Travel.Status} ({remaining} tiles)";
                            BrainLog?.Invoke($"Travel: no route to the exit cell at " +
                                             $"{unreachable.X},{unreachable.Y} - trying " +
                                             $"{leg.Destination.X},{leg.Destination.Y} instead.");
                            return leg;
                        }

                        Travel.Abort($"no route to the exit at {unreachable.X},{unreachable.Y}");
                        return new Decision
                        {
                            Action = BotAction.Idle,
                            Reason = Travel.Status,
                            Subject = "travel failed"
                        };
                    }
                }
            }

            // 2b-i. Keep our own buffs up.
            //
            //       Below Heal, Flee and the town trip, above combat: a missing Magic Shield is
            //       worth a turn but never worth dying for. "Missing" is the server's answer -
            //       WorldModel.HasBuff is fed by S.BuffAdd, S.BuffRemove and the login dump - so
            //       there is no timer here to drift, and a buff that is actually held simply stops
            //       being selected.
            if (!world.Dead && !FrugalRecoveryCombat)
            {
                ClientUserMagic buff = Spells.ChooseBuff(world, out bool atOwnFeet);

                if (buff != null)
                {
                    Spells.BuffIssued(buff);

                    // Timer-tracked effects need their clock started here: they grant no buff, so
                    // nothing else will ever tell us the cast happened.
                    Spells.TimedSelfEffectIssued(buff);

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

            // 2b-i-c. Keep our own pet alive. Heal accepts an owned monster through the same
            // server CanHelpTarget path as a friendly player. This is allowed even in frugal
            // recovery combat: preserving an existing skeleton is cheaper than losing it and
            // spending another amulet and summon cast.
            ClientUserMagic petHeal = Spells.ChoosePetHeal(world, out WorldObject hurtPet);

            if (petHeal != null && hurtPet != null)
            {
                Spells.PetHealIssued(hurtPet.ObjectID);

                int petPercent = hurtPet.MaxHealth > 0
                    ? hurtPet.Health * 100 / hurtPet.MaxHealth
                    : 100;

                return new Decision
                {
                    Action = BotAction.Cast,
                    Reason = $"{petHeal.Info.Name} on {hurtPet.Name} at {petPercent}% HP",
                    Subject = petHeal.Info.Name,
                    TargetID = hurtPet.ObjectID,
                    Direction = WorldModel.DirectionTo(world.Location, hurtPet.Location),
                    Point = hurtPet.Location,
                    Magic = petHeal.Info.Magic
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

            // 2d. GATHER BEFORE PICKING A NEW FIGHT.
            //
            //     Looting used to sit at step 5, reachable only when SelectTarget returned null -
            //     that is, only once there was nothing left alive worth attacking anywhere in
            //     range. On a populated map that moment never arrives, so the bot walked away from
            //     the corpse it had just made to start on the next monster, and the drops expired
            //     where they fell. A wizard burned its way through a bag of mana potions killing
            //     things and came home poorer than it left, because the gold it was killing FOR
            //     was still on the floor behind it.
            //
            //     The distinction that matters is not "is anything alive" but "is anything ON us".
            //     A monster in contact is a fight already in progress and wins outright; a monster
            //     four tiles away that has not touched us is a fight the bot is choosing, and the
            //     loot underfoot is worth more than that choice. Heal and Flee both sit above
            //     this, so reaching here already means we are not in trouble.
            //
            //     Butchering rides along for the same reason and under the same guard: a carcass
            //     is only worth anything while it is still there.
            if (_config.GatherSafeRange > 0 &&
                world.NearestLiveMonster(_config.GatherSafeRange, _unreachable.Keys) == null)
            {
                Decision pickup = TryLoot(world);
                if (pickup != null) return pickup;

                Decision carve = TryButcher(world);
                if (carve != null) return carve;
            }

            WorldObject target = SelectTarget(world);

            if (target != null)
            {
                int distance = world.DistanceTo(target.Location);

                // A QUEST TREE (Chestnut Tree, AI 4) is only ever a target while a gather quest
                // wants it, and it is hit by hand: spells do nothing to it (a wizard once spent
                // 200 mana on one) and the server takes exactly 1 health per blow, 7 blows a tree.
                if (target.IsSceneryNode)
                {
                    if (distance == 1)
                        return new Decision
                        {
                            Action = BotAction.Attack,
                            Reason = $"{target.Name} (quest)",
                            Subject = target.Name,
                            TargetID = target.ObjectID,
                            Direction = WorldModel.DirectionTo(world.Location, target.Location)
                        };

                    Decision toTree = new Decision
                    {
                        Action = BotAction.Approach,
                        Reason = $"{target.Name} at {distance} (quest)",
                        Subject = target.Name,
                        TargetID = target.ObjectID
                    };
                    if (distance > 1 && TrySteer(toTree, world, target.Location, 1, distance, routeKind: "target"))
                        return toTree;

                    _unreachable[target.ObjectID] = DateTime.Now;
                    _committedTarget = 0;
                    return new Decision { Action = BotAction.Idle, Reason = $"{target.Name} out of reach" };
                }

                // 3a. Cast, if we know something worth casting and can pay for it.
                //
                //    Above the melee branch and above closing the distance, both deliberately. A
                //    wizard that walks into contact to punch things has thrown away the entire
                //    reason it is a wizard, and a spell that reaches ten tiles should be thrown
                //    from ten tiles rather than after a walk.
                // Poison first: it ticks for the rest of the fight, so a turn spent applying it
                // early is worth more than the same turn spent on one direct hit.
                // Summon Puppet is a short-lived explosive decoy. Unlike Summon Skeleton it
                // appears as a player object, moves us a few cells and dies after five seconds;
                // treating it as a persistent pet would cause endless resummoning.
                ClientUserMagic puppet = FrugalRecoveryCombat ? null :
                    Spells.ChoosePuppet(world, target, distance);
                if (puppet != null)
                {
                    Spells.PuppetIssued();
                    return new Decision
                    {
                        Action = BotAction.Cast,
                        Reason = $"summoning an explosive puppet near {target.Name}",
                        Subject = puppet.Info.Name,
                        TargetID = 0,
                        Direction = WorldModel.DirectionTo(world.Location, target.Location),
                        Point = world.Location,
                        Magic = puppet.Info.Magic
                    };
                }

                ClientUserMagic wraith = FrugalRecoveryCombat ? null :
                    Spells.ChooseWraithGrip(world, target, distance);
                if (wraith != null)
                {
                    Spells.WraithIssued(target.ObjectID);
                    return new Decision
                    {
                        Action = BotAction.Cast,
                        Reason = $"{wraith.Info.Name} on {target.Name} at {distance}",
                        Subject = wraith.Info.Name,
                        TargetID = target.ObjectID,
                        Direction = WorldModel.DirectionTo(world.Location, target.Location),
                        Point = target.Location,
                        Magic = wraith.Info.Magic
                    };
                }

                ClientUserMagic venom = FrugalRecoveryCombat
                    ? null
                    : Spells.ChoosePoison(world, items, target, distance);

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

                // EXPEL IS ONE KILL; AN AREA SPELL IS SEVERAL. Expel Undead was checked first and
                // always offered on an undead pack, so Wizzler single-targeted a floor of ghosts
                // for twenty minutes with its area spells idle. Only when no area aim covers
                // enough of them (AoeMinimumFor the class) is the instant kill the better cast.
                bool areaAvailable = !FrugalRecoveryCombat &&
                    Spells.ChooseArea(world, target, Maps?.For(world.MapIndex)) != null;
                ClientUserMagic expel = FrugalRecoveryCombat || areaAvailable ? null :
                    Spells.ChooseExpel(world, target, distance);
                if (expel != null)
                {
                    Spells.ExpelIssued(target.ObjectID);
                    NoteAttacked();
                    return new Decision
                    {
                        Action = BotAction.Cast,
                        Reason = $"{expel.Info.Name} on {target.Name} at {distance}",
                        Subject = expel.Info.Name,
                        TargetID = target.ObjectID,
                        Direction = WorldModel.DirectionTo(world.Location, target.Location),
                        Point = target.Location,
                        Magic = expel.Info.Magic
                    };
                }

                // Assassins with melee skills need to close the gap. Hell Fire remains useful
                // as an opening hit, but casting it every two seconds kept Sindo permanently
                // at range and made every AttackMagic skill unreachable.
                bool assassinMelee = Skills.HasAssassinMelee(world);
                bool rangedWindow = !assassinMelee ||
                    (distance > 1 && DateTime.UtcNow >= _nextAssassinRanged);
                SpellBook.AreaAim? area = FrugalRecoveryCombat || !rangedWindow ? null :
                    Spells.ChooseArea(world, target, Maps?.For(world.MapIndex));
                ClientUserMagic spell = FrugalRecoveryCombat || !rangedWindow
                    ? null
                    : Spells.Choose(world, target, distance);

                if (area != null || spell != null)
                {
                    // IS THIS GOING ANYWHERE? The identical question the melee branch asks below,
                    // and the reason it is asked here too.
                    //
                    // NoDamageTo was called from exactly one place - the distance <= 1 branch -
                    // so the entire protection existed only for characters in contact. A caster
                    // returns from HERE every tick and never reaches it, which means a wizard or
                    // a Taoist attacking anything immune, invulnerable or simply out of its depth
                    // had no way out at all. A Chestnut Tree took 200 mana off a level 23 wizard
                    // before anyone noticed.
                    //
                    // Counted per SPELL rather than per tick, so the approach turns that carry a
                    // caster into range are not charged against its patience.
                    if (NoDamageTo(target))
                    {
                        Blacklist(target.ObjectID, GiveUpFor);

                        return new Decision
                        {
                            Action = BotAction.Roam,
                            Reason = $"{target.Name} took no damage from " +
                                     $"{_config.AttackPatience} casts",
                            Subject = "giving up",
                            Direction = NextRoamDirection(world, true),
                            Distance = 1
                        };
                    }

                    if (assassinMelee)
                        _nextAssassinRanged = DateTime.UtcNow.AddSeconds(12);

                    if (area != null)
                    {
                        SpellBook.AreaAim aim = area.Value;
                        return new Decision
                        {
                            Action = BotAction.Cast,
                            Reason = $"{aim.Magic.Info.Name} at {aim.Point.X},{aim.Point.Y} " +
                                     $"{aim.Direction}; covers {aim.Covered} hostiles (predicted)",
                            Subject = aim.Magic.Info.Name,
                            TargetID = target.ObjectID,
                            Direction = aim.Direction,
                            Point = aim.Point,
                            Magic = aim.Magic.Info.Magic
                        };
                    }

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
                Decision back = FrugalRecoveryCombat || assassinMelee
                    ? null : Kite(world, target, distance);
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

                    // Arm a one-shot charge (Blade Storm, Dragon Rise, Flaming Sword) for the
                    // coming swing. The server answers with S.MagicToggle and the next swing names it.
                    MagicType charge = FrugalRecoveryCombat
                        ? MagicType.None
                        : Skills.PendingCharge(world);

                    if (charge != MagicType.None)
                    {
                        Skills.ChargeSent();

                        return new Decision
                        {
                            Action = BotAction.MagicToggle,
                            Reason = $"charging {charge} for {target.Name}",
                            Subject = charge.ToString(),
                            Magic = charge
                        };
                    }

                    MagicType magic = FrugalRecoveryCombat
                        ? MagicType.None
                        : Skills.ChooseAttackMagic(world, target, distance);

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
                               !TrySteer(approach, world, target.Location, 1, distance, routeKind: "target");

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

            // 5. Nothing to fight - loot, butcher, then wander.
            Decision idleLoot = TryLoot(world);
            if (idleLoot != null) return idleLoot;

            Decision butcher = TryButcher(world);
            if (butcher != null) return butcher;

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
            // MovableThingAt, not SomethingAt: a tree standing on the cell is a reason to learn
            // it, not a reason to assume the blockage is temporary. See WorldModel.SceneryCells.
            if (world.MovableThingAt(_lastMoveTarget, 0)) return;

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
            int straightDistance, bool ignoreCreatures = false, string routeKind = "move")
        {
            // Only a journey's own leg may walk onto a map exit. Anything else steered while a
            // journey merely EXISTS - held by a quest errand or a town trip - must still treat
            // doors as walls. See Steer.
            _steeringJourney = routeKind == "travel";
            bool steered = Steer(decision, world, destination, goalRange, straightDistance,
                ignoreCreatures, out List<Point> path);
            _steeringJourney = false;

            if (steered)
            {
                _route = RouteSnapshot(path, destination);
                _routeKind = routeKind;
                _routeAt = DateTime.UtcNow;
            }

            return steered;
        }

        private Point[] _route = Array.Empty<Point>();
        private string _routeKind = "";

        /// <summary>True only while TrySteer is steering a journey leg (routeKind "travel").</summary>
        private bool _steeringJourney;
        private DateTime _routeAt = DateTime.MinValue;

        /// <summary>
        /// The route the bot last steered along, for the status map: town, travel, target, roam,
        /// loot or move. Empty once it is a few seconds old, so a bot that has stopped walking
        /// does not keep showing where it used to be going.
        /// </summary>
        public (Point[] Cells, string Kind) CurrentRoute =>
            DateTime.UtcNow - _routeAt < TimeSpan.FromSeconds(3)
                ? (_route, _routeKind)
                : (Array.Empty<Point>(), "");

        /// <summary>
        /// At most ~200 points so eight bots polled every second stay cheap: long routes keep every
        /// Nth step plus the final cell. With no grid (straight-line steering) it is just the goal.
        /// </summary>
        internal static Point[] RouteSnapshot(List<Point> path, Point destination)
        {
            if (path == null) return new[] { destination };
            if (path.Count == 0) return Array.Empty<Point>();

            const int max = 200;
            if (path.Count <= max) return path.ToArray();

            int step = (path.Count + max - 1) / max;
            List<Point> kept = new List<Point>(max + 1);

            for (int i = 0; i < path.Count; i += step) kept.Add(path[i]);
            if (kept[kept.Count - 1] != path[path.Count - 1]) kept.Add(path[path.Count - 1]);

            return kept.ToArray();
        }

        private bool Steer(Decision decision, WorldModel world, Point destination, int goalRange,
            int straightDistance, bool ignoreCreatures, out List<Point> route)
        {
            route = null;
            MapGrid grid = Maps?.For(world.MapIndex);

            if (grid != null)
            {
                // Only route around other creatures once something has actually stopped us. Doing
                // it always makes a monster standing in a doorway look like a wall, and the extra
                // set costs a full sweep of the object list on every decision.
                HashSet<Point> avoid = _blockedMoves > 0 && !ignoreCreatures
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

                // Scenery is avoided on sight rather than after a refusal. Learning works, but it
                // costs a blocked move per cell to get there and a tree is wide: the bot would
                // discover the trunk one cell at a time while shuffling in front of it. These are
                // visible in the world model already, so there is no reason to find out the hard
                // way.
                HashSet<Point> scenery = world.SceneryCells();

                if (scenery.Count > 0)
                {
                    // Never avoid the cell we are actually heading for - if the destination IS a
                    // scenery cell, refusing to route to it would strand the caller entirely.
                    scenery.Remove(destination);

                    if (scenery.Count > 0)
                    {
                        if (avoid == null) avoid = scenery;
                        else
                        {
                            avoid = new HashSet<Point>(avoid);
                            avoid.UnionWith(scenery);
                        }
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
                //
                // But only the JOURNEY's own steps (_steeringJourney). The gate used to be the
                // mere existence of a journey, and a journey held by a quest errand is still
                // Active: Wizzler, quest-hunting in Banya Village with a Deserted Mine journey on
                // hold, walked to a Chestnut Tree across the Banya Cave entrance - then the held
                // journey stepped it back out, the errand walked it back in, 4,328 map changes
                // in 2h45m and not one kill.
                if (Exits != null && _config.AvoidMapExits &&
                    (Travel == null || !Travel.Active || !_steeringJourney))
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

                route = path;

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
            if (!TrySteer(step, world, _doorwayAim, 0, remaining, routeKind: "roam"))
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

        /// <summary>Summons strongest first, with the MonsterFlag of the pet each one creates.</summary>
        /// Amulets is what each one takes (Player.UseAmulet in the server's summon sources).
        private static readonly (MagicType Magic, MonsterFlag Pet, int Amulets)[] Summons =
        {
            (MagicType.SummonJinSkeleton, MonsterFlag.JinSkeleton, 2),
            (MagicType.SummonShinsu, MonsterFlag.Shinsu, 5),
            (MagicType.SummonSkeleton, MonsterFlag.Skeleton, 1)
        };

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
            if (FrugalRecoveryCombat) return null;
            if (!_config.KeepSummon || !_config.CastSpells) return null;
            if (world.Dead || DateTime.UtcNow < _nextSummon) return null;
            if (Town != null && Town.Active) return null;

            // The strongest summon we know. Summon Shinsu (30) and Summon Jin Skeleton (33) drop
            // from the Lv 3 cave bosses; each one keys its pet by MonsterFlag, so recasting the SAME
            // summon recalls that pet rather than adding one, and the server caps pets at two.
            ClientUserMagic magic = null;
            MonsterFlag flag = MonsterFlag.None;
            int amulets = 1;
            foreach ((MagicType type, MonsterFlag petFlag, int cost) in Summons)
            {
                if (!world.TryGetMagic(type, out ClientUserMagic known) || known.Info == null ||
                    known.ItemRequired || world.Level < known.Info.NeedLevel1) continue;
                // A better summon we cannot pay amulets for yields to the next one down.
                if (items.EquippedReagentCount(ItemType.Amulet) < cost) continue;
                magic = known;
                flag = petFlag;
                amulets = cost;
                break;
            }
            if (magic == null)
            {
                if (Summons.Any(s => world.CanUseMagic(s.Magic)))
                {
                    SummonDiagnostic = "no amulet equipped or carried";
                    _nextSummon = DateTime.UtcNow.AddSeconds(Math.Max(5, _config.SummonRetrySeconds));
                }
                return null;
            }

            // Already have the best pet out - or two of anything, the server's cap. One weaker pet
            // (the old skeleton) is kept and the better summon added beside it.
            int pets = 0;
            foreach (WorldObject pet in world.OwnPets)
            {
                pets++;
                if (BotConnection.Monsters?.Find(pet.MonsterIndex)?.Flag == flag) return null;
            }
            if (pets >= 2) return null;
            if (pets == 1 && magic.Info.Magic == MagicType.SummonSkeleton) return null;

            // No reagent, no summon - and the server would take the amulet it does not have and
            // tell us nothing. Checked here so the refusal is ours and is logged.
            if (items.EquippedReagentCount(ItemType.Amulet) < amulets)
            {
                SummonDiagnostic = "no amulet equipped or carried";
                _nextSummon = DateTime.UtcNow.AddSeconds(Math.Max(5, _config.SummonRetrySeconds));
                return null;
            }

            int floor = world.MaxMana * Math.Clamp(_config.SpellManaFloorPercent, 0, 90) / 100;
            if (world.Mana - magic.Cost < floor) return null;

            _nextSummon = DateTime.UtcNow.AddSeconds(Math.Max(5, _config.SummonRetrySeconds));
            SummonsCast++;
            SummonDiagnostic = $"summoning ({magic.Info.Name})";

            return new Decision
            {
                Action = BotAction.Cast,
                Reason = $"summoning ({magic.Info.Name})",
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
            if (!Spells.CanCastSingleNow(world) &&
                Spells.ChooseArea(world, target, Maps?.For(world.MapIndex),
                    ignoreGlobalDelay: true) == null) return null;

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

            if (!TrySteer(step, world, spot, 0, remaining, routeKind: "roam")) return null;

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

        private enum RoamTargetKind
        {
            None,
            LocalCoverage,
            MapWideCoverage,
            FallbackSweep,
            Random,
            BossLair,
            QuestSpawn
        }

        /// <summary>
        /// Coverage belongs to one brain and one connection. It is intentionally neither shared
        /// nor persisted: Jill visiting a sector must not convince Wizzler that he has hunted it,
        /// and reconnecting starts a fresh view of where this session has actually searched.
        /// </summary>
        private readonly ExplorationCoverage _coverage = new ExplorationCoverage();
        private ExplorationChoice _explorationChoice;
        private RoamTargetKind _roamKind;
        private int _roamMapIndex = -1;

        public string ExplorationMode => _roamKind == RoamTargetKind.LocalCoverage ? "local"
            : _roamKind == RoamTargetKind.MapWideCoverage ? "map-wide"
            : "none";

        public DateTime? ExplorationTargetLastVisitedUtc =>
            _explorationChoice?.LastVisitedUtc;

        public ExplorationSnapshot ExplorationStatus()
        {
            if (_roamMapIndex < 0) return new ExplorationSnapshot(0, 0);
            return _coverage.Snapshot(_roamMapIndex, Maps?.For(_roamMapIndex));
        }

        /// <summary>
        /// Where the bot is currently wandering to, or empty.
        ///
        /// Published so the map can show it. Read-only on purpose: the target is chosen and
        /// cleared inside PickRoamSpot and nothing outside the brain has any business setting it.
        /// </summary>
        public Point RoamTarget => _roamTarget;
        private DateTime _roamUntil = DateTime.MinValue;

        private void ObserveExploration(WorldModel world)
        {
            if (world == null || world.SelfID == 0) return;

            DateTime now = DateTime.UtcNow;

            if (_roamMapIndex != world.MapIndex)
            {
                ClearRoamTarget();
                _roamMapIndex = world.MapIndex;
            }

            _coverage.Observe(world.MapIndex, world.Location, now);

            // The objective is coverage of a sector, not standing on one arbitrary coordinate in
            // it. Reaching any cell in the chosen sector completes that exploration leg; insisting
            // on the exact sampled point would add travel without adding knowledge and could turn
            // one awkward cell into a false stall.
            if (_explorationChoice != null &&
                world.Location.X / ExplorationCoverage.SectorSize == _explorationChoice.SectorX &&
                world.Location.Y / ExplorationCoverage.SectorSize == _explorationChoice.SectorY)
            {
                BrainLog?.Invoke($"Exploration: reached {_roamKind.ToString().ToLowerInvariant()} " +
                                 $"sector {_explorationChoice.SectorX},{_explorationChoice.SectorY}.");
                ClearRoamTarget();
            }

            // A fight is useful work, not failed navigation. Keep the long-target watchdog paused
            // while combat owns the decision so a two-minute fight cannot make exploration look
            // stalled the instant Wander gets control again.
            if (_sweeping && world.InCombat) _sweepProgressAt = now;
        }

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

            if (forceNew && _roamTarget != Point.Empty)
            {
                // A caller asking for a different destination has not proved the sector
                // unreachable. Suppress it only briefly, so equal-age selection cannot hand the
                // exact same target straight back on the next line.
                if (IsCoverageTarget)
                    _coverage.Bar(world.MapIndex, _roamTarget, DateTime.UtcNow.AddSeconds(30));

                ClearRoamTarget();
            }

            if (SweepStalled(world))
                AbandonSweep($"no closer than {_sweepBest} tiles for " +
                             $"{SweepPatience.TotalSeconds:0}s");

            if (_roamKind == RoamTargetKind.QuestSpawn &&
                (!QuestHunting || WorldModel.Distance(world.Location, _roamTarget) <= 3))
                ClearRoamTarget();

            if (_roamKind == RoamTargetKind.BossLair &&
                WorldModel.Distance(world.Location, _roamTarget) <= LairSightRange)
            {
                // Standing in it with nothing to fight - a live boss in sight would have been
                // targeted before Wander was reached - so it is dead or not yet respawned.
                MarkLairChecked(_lair, "reached - nothing there");
                _lairFoundEmpty = true;
                ClearRoamTarget();
            }

            if (_roamTarget != Point.Empty &&
                (ShouldExpireRoamTarget(DateTime.UtcNow, _roamUntil, IsLongRoamTarget) ||
                 WorldModel.Distance(world.Location, _roamTarget) <= 1))
            {
                if (IsCoverageTarget && WorldModel.Distance(world.Location, _roamTarget) <= 1)
                    BrainLog?.Invoke($"Exploration: reached {_roamKind.ToString().ToLowerInvariant()} " +
                                     $"target {_roamTarget.X},{_roamTarget.Y}.");

                ClearRoamTarget();
            }

            if (_roamTarget == Point.Empty && QuestHunting)
            {
                Point spot = QuestSpawnSpot(world);

                if (spot != Point.Empty)
                {
                    _roamTarget = spot;
                    _roamKind = RoamTargetKind.QuestSpawn;
                    _sweeping = true;
                    _sweepBest = WorldModel.Distance(world.Location, _roamTarget);
                    _sweepProgressAt = DateTime.UtcNow;
                    _roamUntil = DateTime.UtcNow.AddMinutes(5);
                }
            }

            if (_roamTarget == Point.Empty)
            {
                BossLair lair = NextLair(world);

                if (lair != null)
                {
                    _lair = lair;
                    _roamTarget = lair.Centre;
                    _roamKind = RoamTargetKind.BossLair;
                    _sweeping = true;
                    _sweepBest = WorldModel.Distance(world.Location, _roamTarget);
                    _sweepProgressAt = DateTime.UtcNow;
                    _roamUntil = DateTime.UtcNow.AddMinutes(10);

                    BrainLog?.Invoke($"Boss hunt: heading for the {lair.MonsterName} lair at " +
                                     $"{lair.Centre.X},{lair.Centre.Y} ({_sweepBest} tiles).");
                }
            }

            if (_roamTarget == Point.Empty)
            {
                bool mapWide = Sweeping(world);
                int radius = Math.Max(4, _config.RoamRadius);
                HashSet<Point> excluded = CoverageExcluded(world);

                _explorationChoice = _coverage.Choose(
                    world.MapIndex,
                    grid,
                    world.Location,
                    radius,
                    mapWide,
                    excluded,
                    _random,
                    DateTime.UtcNow);

                if (_explorationChoice != null)
                {
                    _roamTarget = _explorationChoice.Target;
                    _roamKind = mapWide
                        ? RoamTargetKind.MapWideCoverage
                        : RoamTargetKind.LocalCoverage;

                    BrainLog?.Invoke($"Exploration: {(mapWide ? "map-wide" : "local")} target " +
                                     $"{_roamTarget.X},{_roamTarget.Y}, " +
                                     CoverageAge(_explorationChoice) + ".");
                }

                // Retain the existing next-floor/far-side sweep as a recovery fallback. Coverage
                // normally supplies the target; this remains useful when every eligible sector is
                // temporarily barred or the map data offers no suitable cell.
                if (_roamTarget == Point.Empty && mapWide)
                {
                    _roamTarget = PickSweepSpot(world, grid);
                    if (_roamTarget != Point.Empty) _roamKind = RoamTargetKind.FallbackSweep;
                }

                if (_roamTarget == Point.Empty)
                {
                    _roamTarget = PickRoamSpot(world, grid);
                    if (_roamTarget != Point.Empty) _roamKind = RoamTargetKind.Random;
                }

                _sweeping = IsLongRoamTarget;

                if (_sweeping)
                {
                    _sweepBest = WorldModel.Distance(world.Location, _roamTarget);
                    _sweepProgressAt = DateTime.UtcNow;
                }

                // A sweep is a long walk across the map and must not be re-rolled on the ordinary
                // roam cadence, or it never arrives - which is the milling it exists to stop.
                _roamUntil = DateTime.UtcNow.AddSeconds(_sweeping
                    ? Math.Max(30, _config.RoamRetargetSeconds * 6)
                    : Math.Max(5, _config.RoamRetargetSeconds));
            }

            if (_roamTarget == Point.Empty) return Drift(world, reason, forceNew);

            int remaining = WorldModel.Distance(world.Location, _roamTarget);

            Decision step = new Decision
            {
                Action = BotAction.Roam,
                Reason = RoamReason(reason, remaining),
                Subject = "roaming",
                Destination = _roamTarget
            };

            // No route: the spot is walkable but walled off from here. Forget it and drift this
            // turn rather than searching again on the bot's own thread.
            if (!TrySteer(step, world, _roamTarget, 0, remaining, routeKind: "roam"))
            {
                if (IsCoverageTarget)
                    AbandonExploration(world, "no route", SweepSentence);
                else if (_roamKind == RoamTargetKind.FallbackSweep ||
                         _roamKind == RoamTargetKind.BossLair ||
                         _roamKind == RoamTargetKind.QuestSpawn)
                    AbandonSweep("no route");
                else
                    ClearRoamTarget();

                return Drift(world, reason, forceNew);
            }

            return step;
        }

        private bool IsCoverageTarget =>
            _roamKind == RoamTargetKind.LocalCoverage ||
            _roamKind == RoamTargetKind.MapWideCoverage;

        private bool IsLongRoamTarget =>
            _roamKind == RoamTargetKind.MapWideCoverage ||
            _roamKind == RoamTargetKind.FallbackSweep ||
            _roamKind == RoamTargetKind.BossLair ||
            _roamKind == RoamTargetKind.QuestSpawn;

        // ---- boss lairs ------------------------------------------------------------------------

        /// <summary>
        /// Set by BotInstance: the lairs on this map whose boss drops a skill book this character
        /// wants (unlearned, or a level 4 training copy). Empty when there is nothing to seek.
        /// </summary>
        public Func<WorldModel, IReadOnlyList<BossLair>> WantedLairs;

        /// <summary>Set by BotInstance: the quest errand and the quest policy behind it.</summary>
        public QuestErrand Quest;
        public QuestBook QuestBook;

        /// <summary>Set by BotInstance: the game store shopping list and this bot's purchases.</summary>
        public GameStore Store;
        public StoreShopper Shopper;

        /// <summary>Set by BotInstance: buying fame ranks at the fame NPC.</summary>
        public FameErrand Fame;

        /// <summary>Set by BotInstance: walkable spawn cells of a monster on a map.</summary>
        public Func<int, int, IReadOnlyList<Point>> SpawnCells;

        /// <summary>
        /// A cell in the quest targets' own spawn region, some way from here: where to look when
        /// none is in sight. Random, so repeated walks cover a ring-shaped region rather than
        /// returning to one corner of it.
        /// </summary>
        private Point QuestSpawnSpot(WorldModel world)
        {
            if (SpawnCells == null || Quest == null) return Point.Empty;

            List<Point> cells = new List<Point>();
            foreach (int monster in Quest.HuntTargets)
                cells.AddRange(SpawnCells(monster, world.MapIndex));

            List<Point> away = cells.Where(c => WorldModel.Distance(world.Location, c) >= 8).ToList();
            List<Point> pool = away.Count > 0 ? away : cells;
            return pool.Count == 0 ? Point.Empty : pool[_random.Next(pool.Count)];
        }

        /// <summary>
        /// Every wanted boss the tracker showed us, by object id, kept after the scroll ends and
        /// they vanish from the data channel. It used to be ONE sighting - the last to vanish -
        /// for three minutes, so a scroll revealing several bosses led to one of them and the
        /// rest fell back to spawn-region guesses, which on Deserted Mine Lv 3 were empty four
        /// times while the Ghoul Champion stood 90 tiles away. Bosses barely wander, so ten
        /// minutes; visited nearest-first, and dropped once killed or found gone.
        /// </summary>
        private readonly Dictionary<uint, (int Map, Point At, string Name, DateTime Utc)> _trackedBosses =
            new Dictionary<uint, (int, Point, string, DateTime)>();
        private static readonly TimeSpan TrackedBossMemory = TimeSpan.FromMinutes(10);

        /// <summary>A lair found empty since the last tracker use - the moment a scroll is worth it.</summary>
        private bool _lairFoundEmpty;
        private DateTime _trackerTriedAt = DateTime.MinValue;
        private bool _trackerConfirmed;

        /// <summary>Reward items tried recently, by ItemInfo index, so a refused use is not spammed.</summary>
        private readonly Dictionary<int, DateTime> _rewardTried = new Dictionary<int, DateTime>();

        /// <summary>A data-only object vanished; remember a wanted boss's last position.</summary>
        public void NoteDataObjectGone(WorldObject ob, int mapIndex)
        {
            if (ob == null || ob.MonsterIndex < 0 || ob.Dead) return;
            if (!_lairsHere.Any(l => l.MonsterIndex == ob.MonsterIndex)) return;
            _trackedBosses[ob.ObjectID] = (mapIndex, ob.Location, ob.Name, DateTime.UtcNow);
        }

        /// <summary>A tracked boss died (whoever killed it): nothing left to walk to.</summary>
        public void NoteBossDied(uint objectId) => _trackedBosses.Remove(objectId);

        /// <summary>When each lair was last checked, so a cleared one waits out its respawn.</summary>
        private readonly Dictionary<(int Map, Point Centre), DateTime> _lairChecked =
            new Dictionary<(int, Point), DateTime>();

        private BossLair _lair;
        private IReadOnlyList<BossLair> _lairsHere = Array.Empty<BossLair>();
        private int _lairsMap = -1;
        private DateTime _lairsAt = DateTime.MinValue;

        /// <summary>Close enough to a lair to see whatever lives in it.</summary>
        private const int LairSightRange = 6;

        private IReadOnlyList<BossLair> LairsHere(WorldModel world)
        {
            if (WantedLairs == null) return Array.Empty<BossLair>();
            if (_lairsMap != world.MapIndex || DateTime.UtcNow - _lairsAt > TimeSpan.FromSeconds(30))
            {
                _lairsHere = WantedLairs(world) ?? Array.Empty<BossLair>();
                _lairsMap = world.MapIndex;
                _lairsAt = DateTime.UtcNow;
            }
            return _lairsHere;
        }

        /// <summary>The nearest wanted lair not checked within its respawn time, or null.</summary>
        private BossLair NextLair(WorldModel world)
        {
            BossLair best = null;
            int bestDistance = int.MaxValue;
            DateTime now = DateTime.UtcNow;

            // KNOWN beats GUESSED. A boss the tracker is showing (any distance), or showed within
            // the last three minutes, is a better destination than a spawn region.
            IReadOnlyList<BossLair> lairs = LairsHere(world);
            if (lairs.Count > 0)
            {
                WorldObject seen = world.Objects
                    .Where(x => x.IsLiveMonster && !x.IsPet && x.MonsterIndex >= 0 &&
                                !_unreachable.ContainsKey(x.ObjectID) &&
                                lairs.Any(l => l.MonsterIndex == x.MonsterIndex))
                    .OrderBy(x => world.DistanceTo(x.Location)).FirstOrDefault();

                if (seen != null)
                    return new BossLair { MapIndex = world.MapIndex, MonsterIndex = seen.MonsterIndex,
                        MonsterName = seen.Name, Centre = seen.Location, Spawns = 1, RespawnMinutes = 5,
                        Sighted = true };

                // Forget sightings that are stale, on another map, or whose spot has been walked to
                // since (MarkLairChecked records it) - found empty, or killed there.
                foreach (uint id in _trackedBosses.Keys.ToList())
                {
                    var t = _trackedBosses[id];
                    bool stale = now - t.Utc >= TrackedBossMemory || t.Map != world.MapIndex;
                    bool visited = _lairChecked.TryGetValue((t.Map, t.At), out DateTime checkedAt) &&
                                   checkedAt >= t.Utc;
                    if (stale || visited) _trackedBosses.Remove(id);
                }

                var next = _trackedBosses.Values
                    .OrderBy(t => WorldModel.Distance(world.Location, t.At))
                    .Select(t => ((int, Point, string, DateTime)?)t).FirstOrDefault();

                if (next is (int map, Point at, string name, DateTime _))
                    return new BossLair { MapIndex = map, Centre = at, Spawns = 1,
                        MonsterName = name ?? "tracked boss", RespawnMinutes = 5, Sighted = true };
            }

            foreach (BossLair lair in LairsHere(world))
            {
                if (_lairChecked.TryGetValue((lair.MapIndex, lair.Centre), out DateTime at) &&
                    now - at < TimeSpan.FromMinutes(Math.Max(5, lair.RespawnMinutes)))
                    continue;

                int distance = WorldModel.Distance(world.Location, lair.Centre);
                if (distance >= bestDistance) continue;
                best = lair;
                bestDistance = distance;
            }

            return best;
        }

        private void MarkLairChecked(BossLair lair, string outcome)
        {
            if (lair == null) return;
            _lairChecked[(lair.MapIndex, lair.Centre)] = DateTime.UtcNow;
            BrainLog?.Invoke($"Boss hunt: {lair.MonsterName} lair at {lair.Centre.X},{lair.Centre.Y} " +
                             $"{outcome}; next check in {Math.Max(5, lair.RespawnMinutes)} minutes.");
        }

        /// <summary>
        /// A wanted boss we can see and are willing to fight: taken ahead of the nearest monster,
        /// because clearing the crowd in between first is how it drifts back out of sight. Never
        /// ahead of something already hitting us, and never one the danger memory says will
        /// take us apart.
        /// </summary>
        private Decision StoreStep(WorldModel world, Backpack items, bool itemUsePending)
        {
            if (Store == null || Shopper == null) return null;

            DateTime now = DateTime.UtcNow;
            Shopper.Update(world.HuntGold, now);

            if (!_config.EnableStore || world.Dead)
            {
                Shopper.NextWant = null;
                return null;
            }

            TimeSpan window = TimeSpan.FromMinutes(Math.Max(1, _config.StoreRebuyMinutes));
            StoreWant want = Store.Next(world.Class, w =>
                GameStore.Satisfied(w, world, items, window) || Shopper.JustBought(w, now));
            Shopper.NextWant = want;

            if (want?.Item == null || Shopper.Pending || itemUsePending) return null;
            if (world.HuntGold < want.Price || Shopper.InBackoff(want, now)) return null;
            if (world.NearestLiveMonster(2, _unreachable.Keys) != null) return null;
            if (world.HealthPercent <= _config.HealAtPercent) return null;   // healing comes first
            if (!items.HasRoomForRewards(new[] { (want.Item, 1L, true, false) })) return null;

            Shopper.NoteSent(want, world.HuntGold, now);
            return new Decision
            {
                Action = BotAction.StoreBuy,
                StoreIndex = want.StoreIndex,
                Reason = $"buying {want.Name} for {want.Price:N0} Hunt Gold ({world.HuntGold:N0} held)",
                Subject = want.Name
            };
        }

        /// <summary>
        /// Quest rewards and store purchases that are item buffs: drink them when their buff is not
        /// running (a temporary also when it is about to run out - a reuse extends it), and read a
        /// Scroll of Boss Tracking when it can find something.
        /// </summary>
        private Decision UseBuffItems(WorldModel world, Backpack items)
        {
            DateTime now = DateTime.UtcNow;
            TimeSpan window = TimeSpan.FromMinutes(Math.Max(1, _config.StoreRebuyMinutes));

            if (_trackerTriedAt != DateTime.MinValue && !_trackerConfirmed &&
                world.PlayerStats != null && world.PlayerStats[Stat.BossTracker] > 0)
                _trackerConfirmed = true;

            foreach (KeyValuePair<int, ClientUserItem> pair in items.Carried)
            {
                Library.SystemModels.ItemInfo info = pair.Value?.Info;
                if (info == null) continue;

                if (_rewardTried.TryGetValue(info.Index, out DateTime tried) &&
                    now - tried < TimeSpan.FromMinutes(1)) continue;

                StoreWant bought = Store?.WantFor(world.Class, info);

                if (bought != null && !bought.IsMark && info.ItemType == ItemType.Consumable &&
                    info.Shape == 1 && _config.EnableStore)
                {
                    TimeSpan? left = GameStore.ItemBuffRemaining(world, info.Index, out bool running);

                    // A permanent can be used once; a temporary tops up when nearly spent.
                    bool use = !running || (!bought.Permanent && left != null && left.Value <= window);
                    if (!use) continue;

                    _rewardTried[info.Index] = now;
                    return new Decision
                    {
                        Action = BotAction.UseItem,
                        PotionSlot = pair.Key,
                        Reason = $"using store item {info.ItemName}",
                        Subject = info.ItemName
                    };
                }

                if (QuestBook == null || !QuestBook.IsQuestReward(info)) continue;

                if (QuestBook.IsOrdinaryBuffReward(info))
                {
                    // A paused buff is still running; drinking again would only extend it.
                    if (world.HasItemBuff(info.Index)) continue;

                    _rewardTried[info.Index] = now;
                    return new Decision
                    {
                        Action = BotAction.UseItem,
                        PotionSlot = pair.Key,
                        Reason = $"using quest reward {info.ItemName}",
                        Subject = info.ItemName
                    };
                }

                if (QuestBook.IsBossTrackerReward(info) && TrackerWorthUsing(world, now))
                {
                    _rewardTried[info.Index] = now;
                    _trackerTriedAt = now;
                    _trackerConfirmed = false;
                    _lairFoundEmpty = false;
                    BrainLog?.Invoke($"Boss hunt: reading {info.ItemName} on {world.MapName}.");
                    return new Decision
                    {
                        Action = BotAction.UseItem,
                        PotionSlot = pair.Key,
                        Reason = $"reading {info.ItemName} to find the boss",
                        Subject = info.ItemName
                    };
                }
            }

            return null;
        }

        /// <summary>
        /// A Scroll of Boss Tracking lasts two minutes: read it only where there is a wanted boss
        /// to find, outside a safe zone (the buff would pause), when none is already in sight or
        /// being tracked, and either on arrival or after a lair turned out empty. Ten minutes
        /// between scrolls once one has worked; two after one that did not take.
        /// </summary>
        private bool TrackerWorthUsing(WorldModel world, DateTime now)
        {
            if (world.InSafeZone || LairsHere(world).Count == 0) return false;
            if (world.PlayerStats != null && world.PlayerStats[Stat.BossTracker] > 0) return false;
            if (WantedBossInSight(world) != null) return false;

            TimeSpan wait = _trackerConfirmed ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(2);
            if (_trackerTriedAt != DateTime.MinValue && now - _trackerTriedAt < wait) return false;

            bool justArrived = now - world.MapEnteredUtc < TimeSpan.FromMinutes(1);
            return _lairFoundEmpty || justArrived;
        }

        /// <summary>
        /// While the quest errand is hunting: its target monsters, nearest first, ahead of other
        /// monsters - but never ahead of something already hitting us, and never one the danger
        /// memory refuses.
        /// </summary>
        private WorldObject QuestTargetInSight(WorldModel world)
        {
            if (Quest == null || Quest.Phase != QuestErrand.ErrandPhase.Hunting ||
                Quest.HuntTargets.Count == 0) return null;
            if (world.NearestLiveMonster(1, _unreachable.Keys) != null) return null;

            return world.Objects
                .Where(x => x.IsLiveMonster && !x.IsPet && Quest.HuntTargets.Contains(x.MonsterIndex) &&
                            !_unreachable.ContainsKey(x.ObjectID) &&
                            world.DistanceTo(x.Location) <= _config.AggroRange + 10 &&
                            !TooDangerous(world, x))
                .OrderBy(x => world.DistanceTo(x.Location)).FirstOrDefault();
        }

        private WorldObject WantedBossInSight(WorldModel world)
        {
            IReadOnlyList<BossLair> lairs = LairsHere(world);
            if (lairs.Count == 0) return null;
            if (world.NearestLiveMonster(1, _unreachable.Keys) != null) return null;

            WorldObject best = null;
            int bestDistance = int.MaxValue;

            foreach (WorldObject ob in world.Objects)
            {
                if (!ob.IsLiveMonster || ob.IsPet || ob.MonsterIndex < 0) continue;
                if (_unreachable.ContainsKey(ob.ObjectID)) continue;
                if (!lairs.Any(l => l.MonsterIndex == ob.MonsterIndex)) continue;

                int distance = world.DistanceTo(ob.Location);
                if (distance > _config.AggroRange + 10 || distance >= bestDistance) continue;
                if (TooDangerous(world, ob)) continue;

                best = ob;
                bestDistance = distance;
            }

            return best;
        }

        internal static bool ShouldExpireRoamTarget(DateTime now, DateTime until,
            bool longTarget) => !longTarget && now > until;

        private static string CoverageAge(ExplorationChoice choice)
        {
            if (choice?.LastVisitedUtc == null) return "target unseen";

            TimeSpan age = DateTime.UtcNow - choice.LastVisitedUtc.Value;

            if (age.TotalMinutes < 1) return $"last visited {Math.Max(0, age.TotalSeconds):0}s ago";
            if (age.TotalHours < 1) return $"last visited {age.TotalMinutes:0}m ago";
            return $"last visited {age.TotalHours:0.0}h ago";
        }

        private string RoamReason(string reason, int remaining)
        {
            switch (_roamKind)
            {
                case RoamTargetKind.LocalCoverage:
                    return $"{reason} - exploring least-visited local sector at " +
                           $"{_roamTarget.X},{_roamTarget.Y} ({remaining} tiles; " +
                           CoverageAge(_explorationChoice) + ")";

                case RoamTargetKind.MapWideCoverage:
                    return $"{reason} - nothing here for {_config.SweepAfterIdleSeconds}s, " +
                           $"exploring map-wide at {_roamTarget.X},{_roamTarget.Y} " +
                           $"({remaining} tiles; {CoverageAge(_explorationChoice)})";

                case RoamTargetKind.QuestSpawn:
                    return $"{reason} - searching the quest targets' spawn area at " +
                           $"{_roamTarget.X},{_roamTarget.Y} ({remaining} tiles)";

                case RoamTargetKind.BossLair:
                    return $"{reason} - heading for the {_lair?.MonsterName ?? "boss"} lair at " +
                           $"{_roamTarget.X},{_roamTarget.Y} ({remaining} tiles)";

                case RoamTargetKind.FallbackSweep:
                    return $"{reason} - nothing here for {_config.SweepAfterIdleSeconds}s, " +
                           $"fallback sweep to {_roamTarget.X},{_roamTarget.Y} ({remaining} tiles)";

                default:
                    return $"{reason} - random fallback to {_roamTarget.X},{_roamTarget.Y} " +
                           $"({remaining} tiles)";
            }
        }

        private HashSet<Point> CoverageExcluded(WorldModel world)
        {
            HashSet<Point> excluded = new HashSet<Point>();
            HashSet<Point> doors = Doors(world);
            HashSet<Point> learned = Nav?.BlockedOn(world.MapIndex);

            if (doors != null) excluded.UnionWith(doors);
            if (learned != null) excluded.UnionWith(learned);

            return excluded;
        }

        private void AbandonExploration(WorldModel world, string why, TimeSpan sentence)
        {
            if (_roamTarget != Point.Empty)
                _coverage.Bar(world.MapIndex, _roamTarget, DateTime.UtcNow + sentence);

            BrainLog?.Invoke($"Exploration: abandoned {_roamTarget.X},{_roamTarget.Y} - {why}; " +
                             $"sector barred for {sentence.TotalMinutes:0} minutes.");

            ClearRoamTarget();
        }

        private void ClearRoamTarget()
        {
            _lair = null;
            _roamTarget = Point.Empty;
            _roamKind = RoamTargetKind.None;
            _explorationChoice = null;
            _sweeping = false;
            _sweepBest = int.MaxValue;
            _sweepProgressAt = DateTime.MinValue;
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
        private bool _sweeping;

        /// <summary>When the current journey leg first had to stop and fight.</summary>
        private DateTime _fightingThroughSince = DateTime.MinValue;
        private bool _saidFightingThrough;
        private bool _saidBoxedIn;
        private bool _saidCrowdStep;

        /// <summary>
        /// Should travel stand down this tick and let combat run?
        ///
        /// True only while something is genuinely in contact, and only for as long as
        /// FightThroughSeconds allows. Past that the bot stops defending the leg and lets the
        /// journey's own stall watchdog abort it, which hands the problem to the re-plan and,
        /// failing that, to the town-scroll escape.
        /// </summary>
        /// <summary>AI numbers that never start a fight. See BotConfig.HarmlessAIs.</summary>
        private HashSet<int> _harmlessAI;

        private HashSet<int> HarmlessAI()
        {
            if (_harmlessAI != null) return _harmlessAI;

            _harmlessAI = new HashSet<int>();

            foreach (string part in (_config.HarmlessAIs ?? "").Split(','))
                if (int.TryParse(part.Trim(), out int ai)) _harmlessAI.Add(ai);

            return _harmlessAI;
        }

        /// <summary>
        /// The nearest monster in range that is actually worth stopping a journey for.
        ///
        /// Three tests, cheapest first: the always-passive AI list, then what the thing has
        /// actually done to us, then - only when we have never been hit by it - its level.
        /// </summary>
        private WorldObject NearestRealThreat(WorldModel world, int range)
        {
            WorldObject best = null;
            int bestDistance = int.MaxValue;

            foreach (WorldObject ob in world.Objects)
            {
                if (!ob.IsValidTarget) continue;
                if (_unreachable.ContainsKey(ob.ObjectID)) continue;
                if (HarmlessAI().Contains(ob.AI)) continue;

                int distance = world.DistanceTo(ob.Location);
                if (distance > range || distance >= bestDistance) continue;

                if (Danger != null && Danger.Harmless(ob.Name, world.MaxHealth,
                        _config.FightThroughHarmlessPercent, _config.DangerMinimumHits))
                    continue;

                if (BeneathUs(world, ob)) continue;

                best = ob;
                bestDistance = distance;
            }

            return best;
        }

        /// <summary>
        /// Level fallback, used ONLY where the damage record is silent.
        ///
        /// WorldObject carries no level - the spawn packet does not send one - so it comes from
        /// the monster database via the index the packet does carry.
        /// </summary>
        private bool BeneathUs(WorldModel world, WorldObject ob)
        {
            if (_config.FightThroughLevelsBelow <= 0) return false;
            // Only stand down when the damage record can actually answer. A monster that has
            // hit us once tells us nothing, and treating that as "we have data" was what kept a
            // level 27 wizard fighting its way across a starter town.
            if (Danger != null && Danger.Judged(ob.Name, _config.DangerMinimumHits)) return false;

            Library.SystemModels.MonsterInfo info = BotConnection.Monsters?.Find(ob.MonsterIndex);

            if (info == null || info.Level <= 0) return false;

            return info.Level + _config.FightThroughLevelsBelow <= world.Level;
        }

        private bool FightingThrough(WorldModel world)
        {
            if (_config.FightThroughRange <= 0 || world.InSafeZone) return Settle();

            bool engaged = _fightingThroughSince != DateTime.MinValue;

            // ENTER on contact; LEAVE only when the area is clear.
            //
            // Two different ranges on purpose. Starting a fight is about what is on top of us;
            // ending one is about whether walking away would simply restart it. Using the contact
            // range for both made travel resume the instant the adjacent monster died, with the
            // rest of the room still coming - see BotConfig.FightThroughClearRange.
            int range = engaged && _config.FightThroughClearRange > 0
                ? Math.Max(_config.FightThroughRange, _config.FightThroughClearRange)
                : _config.FightThroughRange;

            if (NearestRealThreat(world, range) == null)
            {
                // NOTHING LEFT TO FIGHT - BUT PICK UP WHAT WE KILLED FIRST.
                //
                // Loot sits below travel in this method, so a journey resuming the instant the
                // last monster dies walks away from every drop the fight produced. That makes
                // fighting through pure cost: the mana and potions are spent, the corpses are
                // left behind, and the bot arrives poorer than if it had run past.
                //
                // Only after a fight (engaged), only for loot the value rule actually wants, and
                // still bounded by FightThroughSeconds below - so a field of junk cannot hold a
                // journey indefinitely.
                bool heavy = world.MaxBagWeight > 0 &&
                             world.WeightPercent >= _config.HeavyWeightPercent;
                bool full = world.MaxBagWeight > 0 && world.WeightPercent >= 100;

                if (!engaged || FindWorthwhileLoot(world, heavy, full) == null)
                    return Settle();
            }

            if (_fightingThroughSince == DateTime.MinValue)
                _fightingThroughSince = DateTime.UtcNow;

            // Still fighting, but stop shielding the journey from its watchdog: after a stretch
            // with no kill, or after the hard ceiling however well it is going.
            if (!FightThroughProgressing(world, _fightingThroughSince, DateTime.UtcNow,
                    _config.FightThroughSeconds, _config.FightThroughMaxSeconds))
                return false;

            return true;
        }

        /// <summary>
        /// Is a fight-through still earning its hold on the journey? Measured from the later of
        /// the engagement's start and the last kill, so a bot clearing a pack keeps its shield
        /// and one trading blows with something it cannot kill loses it.
        /// </summary>
        internal static bool FightThroughProgressing(WorldModel world, DateTime since, DateTime now,
            int idleSeconds, int maxSeconds)
        {
            if (maxSeconds > 0 && now - since > TimeSpan.FromSeconds(maxSeconds)) return false;
            if (idleSeconds <= 0) return true;

            DateTime progress = world.LastExperienceGainUtc > since
                ? world.LastExperienceGainUtc
                : since;

            return now - progress <= TimeSpan.FromSeconds(idleSeconds);
        }

        /// <summary>Killed something recently enough that the fight is going our way.</summary>
        private bool WinningFights(WorldModel world) =>
            world.LastExperienceGainUtc != DateTime.MinValue &&
            DateTime.UtcNow - world.LastExperienceGainUtc <=
                TimeSpan.FromSeconds(_config.FightThroughSeconds > 0 ? _config.FightThroughSeconds : 90);

        /// <summary>A live monster we could attack standing right next to us.</summary>
        private static bool AdjacentTarget(WorldModel world) =>
            world.Objects.Any(ob => ob.IsValidTarget && world.DistanceTo(ob.Location) <= 1);

        /// <summary>Nothing in contact: clear the fight-through clock and let travel proceed.</summary>
        private bool Settle()
        {
            _fightingThroughSince = DateTime.MinValue;
            _saidFightingThrough = false;
            return false;
        }

        /// <summary>Closest we have been to the current sweep target, and when.</summary>
        private int _sweepBest = int.MaxValue;
        private DateTime _sweepProgressAt = DateTime.MinValue;

        /// <summary>Sweep targets abandoned as unreachable, and when they may be tried again.</summary>
        private readonly Dictionary<Point, DateTime> _sweepGaveUp = new Dictionary<Point, DateTime>();

        /// <summary>How long a sweep may make no progress before it is abandoned.</summary>
        private static readonly TimeSpan SweepPatience = TimeSpan.FromSeconds(45);

        /// <summary>And how long that target is left alone afterwards.</summary>
        private static readonly TimeSpan SweepSentence = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Is the sweep getting anywhere? Abandon it if not.
        ///
        /// The sweep walks the length of a cave towards the next floor, so it deliberately
        /// survives the ordinary roam re-roll - otherwise it never arrives. That exemption had no
        /// counterpart: a sweep that could NEVER arrive was immortal. Two bots spent forty minutes
        /// three and ten tiles from a stairway they could not reach, re-picking the same target
        /// every thirty seconds.
        ///
        /// Invisible, too, on two counts. The action type never changed, and the log only writes
        /// when it does, so both went silent mid-session and looked crashed. And the stuck
        /// watchdog measures a bot pinned on ONE CELL - these were walking the whole time, just
        /// not arriving. Distance to the goal is the thing that was not being watched.
        /// </summary>
        private bool SweepStalled(WorldModel world)
        {
            if (!_sweeping || _roamTarget == Point.Empty) return false;

            int distance = WorldModel.Distance(world.Location, _roamTarget);

            if (distance < _sweepBest)
            {
                _sweepBest = distance;
                _sweepProgressAt = DateTime.UtcNow;
                return false;
            }

            if (_sweepProgressAt == DateTime.MinValue)
            {
                _sweepProgressAt = DateTime.UtcNow;
                return false;
            }

            return DateTime.UtcNow - _sweepProgressAt > SweepPatience;
        }

        private void AbandonSweep(string why)
        {
            if (_roamKind == RoamTargetKind.BossLair)
            {
                MarkLairChecked(_lair, $"abandoned - {why}");
                ClearRoamTarget();
                return;
            }

            if (_roamKind == RoamTargetKind.QuestSpawn)
            {
                ClearRoamTarget();
                return;
            }

            if (IsCoverageTarget)
            {
                if (_roamTarget != Point.Empty && _roamMapIndex >= 0)
                    _coverage.Bar(_roamMapIndex, _roamTarget, DateTime.UtcNow + SweepSentence);

                BrainLog?.Invoke($"Exploration: abandoned {_roamTarget.X},{_roamTarget.Y} - {why}; " +
                                 $"sector barred for {SweepSentence.TotalMinutes:0} minutes.");
                ClearRoamTarget();
                return;
            }

            if (_roamTarget != Point.Empty)
                _sweepGaveUp[_roamTarget] = DateTime.UtcNow + SweepSentence;

            BrainLog?.Invoke($"Sweep abandoned - {why}. That stairway is left alone for " +
                             $"{SweepSentence.TotalMinutes:0} minutes.");

            ClearRoamTarget();
        }

        private int _stuckStrikes;
        private Point _stuckAt = Point.Empty;
        private DateTime _stuckSince = DateTime.MinValue;

        /// <summary>Remember where we are, and since when, for the stationary watchdog.</summary>
        private void NoteWhereWeAre(WorldModel world)
        {
            if (world.Location == _stuckAt) return;

            // Moving clears the record entirely: whatever was wrong, it is not wrong now.
            _stuckAt = world.Location;
            _stuckSince = DateTime.UtcNow;
            _stuckStrikes = 0;
        }

        /// <summary>
        /// Standing on one cell AND getting nothing for it.
        ///
        /// Movement alone was the first version and it was wrong within a minute of shipping: a
        /// bot fighting things that are already adjacent does not move, and it should not. With
        /// the cave spawn counts raised - Deserted Mine went from a median of 4 monsters in view
        /// to 25 - standing still surrounded is now the NORMAL way to hunt well. It fired three
        /// times in thirty seconds on a taoist that was looting and killing throughout, throwing
        /// away a committed target each time, which costs every hit already landed on it.
        ///
        /// Experience is the honest progress signal, and it is already tracked for the status
        /// page. Pinned and earning is a bot doing its job; pinned and earning nothing for twenty
        /// seconds is the doorway case this exists for - an assassin at 51,292 swinging at ghosts
        /// it never killed, its bag draining two weight a potion.
        /// </summary>
        private bool StuckTooLong(WorldModel world)
        {
            if (_config.StuckSeconds <= 0 || _stuckSince == DateTime.MinValue) return false;

            TimeSpan limit = TimeSpan.FromSeconds(_config.StuckSeconds);

            if (DateTime.UtcNow - _stuckSince <= limit) return false;

            // Nothing earned at all yet - a fresh login, usually.
            //
            // This previously returned true, which was meant to avoid calling a new session
            // permanently stuck and did exactly the opposite: with no experience recorded, the
            // test collapsed back to movement alone, the very rule the experience condition was
            // added to replace. A warrior fired it twenty-four seconds after logging in.
            //
            // Three times the limit before the movement-only reading is trusted. Long enough that
            // connecting, loading a map and picking a first target cannot trip it; short enough
            // that a bot which genuinely arrives into a wall still gets rescued.
            if (world.LastExperienceGainUtc == DateTime.MinValue)
                return DateTime.UtcNow - _stuckSince > TimeSpan.FromTicks(limit.Ticks * 3);

            return DateTime.UtcNow - world.LastExperienceGainUtc > limit;
        }

        /// <summary>Restart the clock, so one trigger does not fire every tick afterwards.</summary>
        private void ClearStuck() => _stuckSince = DateTime.UtcNow;

        /// <summary>Long enough since the last swing or cast to stop searching locally.</summary>
        private bool Sweeping(WorldModel world)
        {
            if (_config.SweepAfterIdleSeconds <= 0 || world == null) return false;

            // A fresh connection or map can have no hit at all. Requiring _lastHitAt to be set
            // made that worst case exempt forever: Wizzler roamed an empty Bichon Cave for five
            // minutes after reconnect while the configured 45-second sweep never armed.
            //
            // Map entry is also a floor for an old hit timestamp. A kill on the previous map must
            // not make a newly reached map look as though it has already been idle for minutes.
            DateTime idleSince = _lastHitAt;
            if (world.MapEnteredUtc > idleSince) idleSince = world.MapEnteredUtc;

            return idleSince != DateTime.MinValue &&
                   DateTime.UtcNow - idleSince >
                       TimeSpan.FromSeconds(_config.SweepAfterIdleSeconds);
        }

        /// <summary>
        /// A cell on the way to the next floor down, or empty when this map has no deeper floor.
        ///
        /// "Deeper" is decided on the map NAME - the same cave with a higher trailing number - so
        /// the sweep can only ever go further in. Anything else would let an idle bot wander back
        /// to town, or into an unrelated map the travel system never chose.
        ///
        /// The target is the exit cell itself when SweepEntersNextFloor is set, and otherwise a
        /// walkable cell a few tiles short of it: far enough that getting there crosses the length
        /// of the map, which is the point, without stepping through a door the brain has no
        /// danger information about.
        /// </summary>
        private bool SweepTargetBarred(Point spot)
        {
            if (!_sweepGaveUp.TryGetValue(spot, out DateTime until)) return false;
            if (DateTime.UtcNow < until) return true;

            _sweepGaveUp.Remove(spot);
            return false;
        }

        private Point PickSweepSpot(WorldModel world, MapGrid grid)
        {
            if (Exits == null) return Point.Empty;

            string here = Globals.MapInfoList?.Binding
                ?.FirstOrDefault(x => x.Index == world.MapIndex)?.Description ?? "";

            int hereFloor = FloorNumber(here);

            // NO NUMBERED FLOOR IS NOT THE SAME AS NOWHERE TO GO.
            //
            // This returned empty for any map whose name does not end in a floor number, which
            // silently disabled the whole idle sweep on every cave that is not numbered. "Ant Cave
            // North" is one: a Taoist roamed it for two and a half minutes without a single kill,
            // wandering nineteen tiles at a time inside the same empty pocket, because the one
            // mechanism meant to walk it out of there declined to produce a destination.
            //
            // The useful half of a sweep is crossing the map, not the door at the end of it. When
            // there is no deeper floor to aim at, aim at the far side of the map instead.
            if (hereFloor <= 0)
            {
                Point far = FarSideOf(world, grid);
                return SweepTargetBarred(far) ? Point.Empty : far;
            }

            string stem = here.Substring(0, here.Length - hereFloor.ToString().Length).TrimEnd();

            Point best = Point.Empty;
            int bestDistance = int.MaxValue;

            foreach (MapExit exit in Exits.ExitsFrom(world.MapIndex))
            {
                if (exit == null || exit.IsTeleport || exit.Cells == null) continue;

                string there = exit.ToMapName ?? "";

                if (FloorNumber(there) != hereFloor + 1) continue;
                if (!there.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;

                foreach (Point cell in exit.Cells)
                {
                    int distance = WorldModel.Distance(world.Location, cell);

                    if (distance >= bestDistance || distance < 4) continue;

                    Point aim = _config.SweepEntersNextFloor ? cell : StandOffFrom(cell, grid);

                    if (aim == Point.Empty) continue;

                    // Already proved unreachable from here. See AbandonSweep: without this the
                    // give-up is pointless, because the very next pick returns the same stairway.
                    if (SweepTargetBarred(aim)) continue;

                    best = aim;
                    bestDistance = distance;
                }
            }

            return best;
        }

        /// <summary>
        /// The most distant walkable cell we can find, for sweeping a map with no deeper floor.
        ///
        /// Sampled rather than scanned: the grid can be several hundred cells square and this runs
        /// on the decision tick. Sixty tries reliably lands somewhere across the map, which is all
        /// the sweep needs - it is looking for monsters on the way, not for a particular tile.
        /// </summary>
        private Point FarSideOf(WorldModel world, MapGrid grid)
        {
            if (grid == null) return Point.Empty;

            Point best = Point.Empty;
            int bestDistance = 0;

            for (int tries = 0; tries < 60; tries++)
            {
                Point cell = new Point(_random.Next(grid.Width), _random.Next(grid.Height));

                if (!grid.Walkable(cell)) continue;

                int distance = WorldModel.Distance(world.Location, cell);

                // Far enough to be worth the walk, and further than anything else we found.
                if (distance <= bestDistance || distance < 25) continue;

                best = cell;
                bestDistance = distance;
            }

            return best;
        }

        /// <summary>A walkable cell a few tiles off a door, so the sweep stops beside it.</summary>
        private static Point StandOffFrom(Point cell, MapGrid grid)
        {
            for (int radius = 3; radius <= 6; radius++)
                for (int dx = -radius; dx <= radius; dx++)
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius) continue;

                        Point candidate = new Point(cell.X + dx, cell.Y + dy);

                        if (grid.Walkable(candidate)) return candidate;
                    }

            return Point.Empty;
        }

        /// <summary>The trailing floor number of a map name, or 0. "Deserted Mine Lv 2" -> 2.</summary>
        private static int FloorNumber(string mapName)
        {
            if (string.IsNullOrWhiteSpace(mapName)) return 0;

            int end = mapName.Length;

            while (end > 0 && char.IsDigit(mapName[end - 1])) end--;

            return end == mapName.Length ||
                   !int.TryParse(mapName.Substring(end), out int floor) ? 0 : floor;
        }

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
        /// <summary>
        /// The nearest floor item an accepted gather task wants (Skeletal Spine, Venom, a key, a
        /// chestnut). No value, weight or journey rule applies: it takes no slot.
        /// </summary>
        private WorldObject QuestItemOnFloor(WorldModel world, HashSet<uint> skip)
        {
            if (QuestBook == null || !_config.EnableQuests) return null;

            int range = Math.Max(_config.LootRange, 10);
            return world.Objects
                .Where(x => x.Kind == ObjectKind.Item && !skip.Contains(x.ObjectID) &&
                            world.DistanceTo(x.Location) <= range &&
                            QuestBook.QuestItemWanted(x.ItemInfo, world))
                .OrderBy(x => world.DistanceTo(x.Location)).FirstOrDefault();
        }

        private WorldObject FindWorthwhileLoot(WorldModel world, bool heavy, bool full)
        {
            HashSet<uint> skip = new HashSet<uint>(_unreachable.Keys);

            // A quest item first of all: picked up, it credits the quest and is deleted, so it
            // never costs bag space, and it is only on the floor because this bot killed for it.
            WorldObject questItem = QuestItemOnFloor(world, skip);
            if (questItem != null) return questItem;

            // A book we want goes first, however much else is on the floor: nearest-first would
            // pick up a pile of potions and rings while the one Blade Storm in the drop sat there,
            // and a boss drop is exactly that kind of pile. Unlearned skills before level 4
            // training copies; the ordinary loot rules still have to agree.
            WorldObject book = WantedBookOnFloor(world, skip, heavy, full);
            if (book != null) return book;

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
                {
                    if (!JourneyLootFilter(world, candidate.Location) ||
                        _items.WorthLootingOnJourney(candidate.ItemInfo, candidate.Item, world.Class,
                            _config.JourneyLootMinValue, Books, world))
                        return candidate;

                    if (!_saidJourneyLoot)
                    {
                        _saidJourneyLoot = true;
                        BrainLog?.Invoke($"Travel: leaving cheap drops (e.g. {candidate.Name}) - " +
                                         $"only learnable books, parts, stackables, potions, upgrades and items worth " +
                                         $"{_config.JourneyLootMinValue:N0}+ gold while travelling.");
                    }
                }

                skip.Add(candidate.ObjectID);
            }
        }

        private WorldObject WantedBookOnFloor(WorldModel world, HashSet<uint> skip, bool heavy,
            bool full)
        {
            if (Books == null) return null;

            List<(WorldObject Item, int Rank, int Distance)> books =
                new List<(WorldObject, int, int)>();

            foreach (WorldObject ob in world.Objects)
            {
                if (ob.Kind != ObjectKind.Item || ob.ItemInfo?.ItemType != ItemType.Book) continue;
                if (skip.Contains(ob.ObjectID)) continue;

                int distance = world.DistanceTo(ob.Location);
                if (distance > _config.LootRange) continue;

                ClientUserItem probe = ob.Item ?? new ClientUserItem { Info = ob.ItemInfo, Count = 1 };
                if (Books.Judge(probe, world.Class, world.Level, world.PlayerStats, world) !=
                    BookVerdict.Wanted) continue;

                Library.SystemModels.MagicInfo magic = Books.For(ob.ItemInfo);
                books.Add((ob, magic != null && world.Knows(magic.Index) ? 1 : 0, distance));
            }

            foreach ((WorldObject item, int _, int _) in books.OrderBy(x => x.Rank)
                         .ThenBy(x => x.Distance))
            {
                int unit = Math.Max(1, item.ItemInfo?.Weight ?? 1);
                if (_items.WorthLooting(item.ItemInfo, item.Item, world.Class, world.Gender,
                        heavy, full,
                        _config.HealthPotionTarget(world.MaxBagWeight, unit),
                        _config.ManaPotionTarget(world.MaxBagWeight, unit),
                        world.Gold, _lootValue))
                    return item;
            }

            return null;
        }

        /// <summary>
        /// Loot strictly while a journey is under way - unless poor, when every sale counts and
        /// poverty recovery owns the decision.
        /// </summary>
        private bool JourneyLootFilter(WorldModel world, Point itemAt)
        {
            // Also on the way to a boss lair: the same bag-space problem, one map deep. Close to
            // it, everything the boss drops is picked up as usual.
            //
            // Judged by where the ITEM lies, not where we stand. It used to be our own distance
            // to the lair, and a Glass Ring lying just outside the 12-tile line was refused while
            // we stood outside it and wanted as soon as we stepped in - so Banner walked in, turned
            // back for the ring, was outside again, and did that for twelve minutes on Bichon Cave
            // Lv 3 without a kill. An item's answer must not depend on which side we are on.
            bool lairWalk = _roamKind == RoamTargetKind.BossLair && _roamTarget != Point.Empty;
            bool farFromLair = lairWalk && WorldModel.Distance(itemAt, _roamTarget) > 12;
            bool travelling = Travel != null && Travel.Active;
            bool eligible = _config.JourneyLootMinValue > 0 && !InRecovery &&
                            world.Gold >= _config.PoorGold;

            // Said once per walk, not once per flip.
            if (!(travelling || lairWalk) || !eligible) _saidJourneyLoot = false;

            return (travelling || farFromLair) && eligible;
        }

        private bool _saidJourneyLoot;

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

                // Committed for a long time without ever hitting it.
                //
                // The existing give-ups all need a specific symptom: blocked moves, distance that
                // stops shrinking, no route at all. A bot boxed in by other monsters has none of
                // them - it steps sideways, the distance changes, a route exists on paper - and it
                // will keep trying indefinitely while something it COULD hit stands next to it.
                //
                // So this is the catch-all: a target held for longer than the give-up time with no
                // attack landed on it is written off, whatever the reason. The clock only runs
                // while the target is committed, and any attack on it resets it, so a long fight
                // against something tough is never cut short.
                if (current != null && ShouldAbandonCommitted())
                {
                    string name = current.Name;

                    Blacklist(current.ObjectID, GiveUpFor);
                    BrainLog?.Invoke($"gave up on {name} - {_config.AttackGiveUpSeconds}s " +
                                      "committed without landing a hit");

                    return SelectTarget(world);      // pick again, now that it is parked
                }

                bool stillGood = current != null && (current.IsValidTarget || IsQuestTree(current)) &&
                                 !_unreachable.ContainsKey(current.ObjectID) &&
                                 world.DistanceTo(current.Location) <= _config.AggroRange + 2;

                // Commitment survives a monster becoming frightening mid-fight. Walking away from
                // something that is already on us just means being hit in the back; the health
                // thresholds above decide when to actually disengage.
                //
                // But commitment does NOT survive something else standing on us.
                //
                // Staying on a target is about not throwing away damage already dealt by switching
                // mid-fight, and that reasoning quietly assumes the target is reachable. Surrounded
                // by monsters while committed to one two tiles beyond them, it becomes the opposite
                // of what it is for: the bot walks at something it cannot touch while three things
                // it could hit take turns on its back.
                //
                // The watchdog below catches this eventually, but "eventually" is
                // AttackGiveUpSeconds - forty-five seconds of free damage. An adjacent monster is
                // a fight we are already in whether we like it or not, so it simply wins.
                if (stillGood && world.DistanceTo(current.Location) > 1)
                {
                    WorldObject onTopOfUs = world.NearestLiveMonster(1, _unreachable.Keys);

                    if (onTopOfUs != null && onTopOfUs.ObjectID != current.ObjectID)
                    {
                        TargetSwitches++;
                        ForgetFight();

                        _committedTarget = onTopOfUs.ObjectID;
                        _committedAt = DateTime.UtcNow;
                        _lastHitAt = DateTime.UtcNow;

                        return onTopOfUs;
                    }
                }

                if (stillGood) return current;

                _committedTarget = 0;
                ForgetFight();
            }

            // QUEST HUNTING IS SINGLE-MINDED. While the errand hunts, only its targets are chosen
            // (plus anything already hitting us); cows and chickens that do not count are left
            // alone, or every one becomes a corpse to butcher and a detour - Sindo spent minutes
            // walking between a Pig and a Cow carcass in Bichon Town.
            //
            // CHASING A SIGHTED BOSS IS TOO. A boss the tracker revealed is a known position, and
            // the walk to it only ran when nothing else was in range - on a packed floor that is
            // never: Wizzler fought ghosts for fifteen minutes 28 tiles from a Ghoul Champion it
            // had just paid a scroll to find. So on the way, only what is in contact is fought.
            WorldObject next = QuestHunting
                ? QuestTargetInSight(world) ?? world.NearestLiveMonster(1, _unreachable.Keys)
                : BossChasing
                    ? WantedBossInSight(world) ?? world.NearestLiveMonster(1, _unreachable.Keys)
                    : WantedBossInSight(world) ?? NearestWorthFighting(world);

            if (next != null)
            {
                if (_committedTarget != 0 && next.ObjectID != _committedTarget)
                {
                    TargetSwitches++;
                    ForgetFight();
                }

                if (_committedTarget != next.ObjectID)
                {
                    _committedTarget = next.ObjectID;
                    _committedAt = DateTime.UtcNow;
                    _lastHitAt = DateTime.UtcNow;      // a fresh target starts with a clean clock
                }
            }

            return next;
        }

        private DateTime _committedAt = DateTime.MinValue;
        private DateTime _lastHitAt = DateTime.MinValue;

        /// <summary>
        /// Called whenever we actually swing or cast at the committed target, to reset the
        /// abandon clock. Trying does not count - only attacking.
        /// </summary>
        private void NoteAttacked() => _lastHitAt = DateTime.UtcNow;

        private bool ShouldAbandonCommitted()
        {
            if (_committedAt == DateTime.MinValue) return false;

            DateTime since = _lastHitAt > _committedAt ? _lastHitAt : _committedAt;

            return DateTime.UtcNow - since > GiveUpFor;
        }

        /// <summary>
        /// Drop the current target and take the next one, on request.
        ///
        /// Parked only briefly: this is "not that one, the other one", not "this thing is
        /// dangerous". The short sentence is long enough for SelectTarget to choose something else
        /// and short enough that the bot comes back to it if it is genuinely the only thing there.
        /// </summary>
        public bool ForceNextTarget()
        {
            bool did = false;

            // Whatever it is chasing, not just monsters.
            //
            // The first version parked only the committed monster, which did nothing visible when
            // the bot was stuck on GROUND LOOT - the operator pressed the button, the log said the
            // target was parked, and the bot carried on walking at the same unreachable potion.
            // "Next target" means "stop doing that", so both are skipped.
            if (_lootTarget != 0)
            {
                Park(_lootTarget, TimeSpan.FromSeconds(30));
                _lootTarget = 0;
                did = true;
            }

            if (_committedTarget != 0)
            {
                Blacklist(_committedTarget, TimeSpan.FromSeconds(20));
                did = true;
            }

            return did;
        }

        /// <summary>Set by BotInstance so the brain can explain a retarget in the log.</summary>
        public Action<string> BrainLog;

        /// <summary>Set by BotInstance: true while a drop is out and unanswered.</summary>
        public Func<bool> IsDropPending;

        private bool DropPending => IsDropPending != null && IsDropPending();

        /// <summary>
        /// Slots the server has refused to let go of, and when to stop sulking about it.
        ///
        /// Keyed by slot rather than by item, because the refusal is usually about WHERE the bot
        /// is standing - no free cell to drop onto - rather than about the item itself. Five
        /// minutes is long enough to have walked somewhere else.
        /// </summary>
        private readonly Dictionary<int, DateTime> _dropRefused = new Dictionary<int, DateTime>();

        public void NoteDropRefused(int slot) =>
            _dropRefused[slot] = DateTime.UtcNow + TimeSpan.FromMinutes(5);

        private bool DropRefusedRecently(int slot) =>
            _dropRefused.TryGetValue(slot, out DateTime until) && DateTime.UtcNow < until;

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

                // ADJACENT BEATS DANGEROUS. Always.
                //
                // Refusing to START a fight with something that would take us apart is right, and
                // the comment above this method says so: "we simply do not initiate. If it comes
                // to us anyway, the ordinary heal/flee rules take over." That last sentence was
                // never true. Nothing below made an exception for a monster already standing on
                // us, so a bot that got surrounded by things it had judged dangerous refused every
                // single one of them and stood there being hit.
                //
                // Observed: an assassin at 19% health, out of healing potions, boxed in, with 245
                // danger refusals across 79 decisions and ZERO attacks - walking at a Scarecrow
                // two tiles away and a Snake Gall it could not reach while four monsters killed it.
                //
                // At range 1 the choice is not "fight this or avoid it". The damage is arriving
                // either way, fleeing through a wall of monsters is not available, and killing the
                // thing is the only action that makes it stop.
                if (world.DistanceTo(candidate.Location) <= 1) return candidate;

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
        /// How many times we have failed to REACH a given drop, for the escalating backoff in
        /// TryLoot. Distinct from _lootAttempts, which counts refusals while standing on it.
        /// </summary>
        private readonly Dictionary<uint, int> _lootBlocked = new Dictionary<uint, int>();

        /// <summary>Blocked this many times and the short sentence becomes the long one.</summary>
        private const int LootBlockedLimit = 4;

        /// <summary>The drop we are currently walking to, so a manual skip can park it.</summary>
        private uint _lootTarget;

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
            _butcherAttempts.Remove(objectID);
            _butchered.Remove(objectID);
            _lootAttempts.Remove(objectID);
            _lootBlocked.Remove(objectID);
            if (_lootTarget == objectID) _lootTarget = 0;
            _unreachable.Remove(objectID);

            if (_committedTarget == objectID) _committedTarget = 0;
            if (_pursuitID == objectID) ForgetPursuit();
            if (_fightID == objectID) ForgetFight();
        }

        /// <summary>Cuts made per corpse, so a corpse that will not yield is abandoned.</summary>
        private readonly Dictionary<uint, int> _butcherAttempts = new Dictionary<uint, int>();

        /// <summary>Corpses the server has told us are finished with.</summary>
        private readonly HashSet<uint> _butchered = new HashSet<uint>();

        /// <summary>S.ObjectHarvested - this corpse has given up everything it is going to.</summary>
        public void NoteHarvested(uint objectID)
        {
            _butchered.Add(objectID);
            _butcherAttempts.Remove(objectID);
        }

        /// <summary>
        /// Butcher a nearby carcass.
        ///
        /// Sits below fighting and looting and above wandering: it is worth a short detour and
        /// never worth taking a hit for. A chicken or a deer drops nothing at all when killed -
        /// the meat is only reachable this way - so for a bot working low-level ground this is
        /// the difference between earning gold and earning none.
        /// </summary>
        /// <summary>The quest errand is hunting its targets on this map.</summary>
        private bool QuestHunting => Quest != null && Quest.Phase == QuestErrand.ErrandPhase.Hunting;

        private Point _npcWalkFrom = Point.Empty;
        private DateTime _npcWalkSince = DateTime.MinValue;
        private string _npcWalkTo = "";

        /// <summary>
        /// Diagnostic: an errand walk to an NPC that has not moved us for five seconds is logged
        /// once, with where we are and how long the route is - Jane was seen "approaching" Sara for
        /// forty seconds without visibly moving, and the log had nothing to say about it.
        /// </summary>
        private void NoteNpcWalk(WorldModel world, string npc)
        {
            DateTime now = DateTime.UtcNow;
            if (npc != _npcWalkTo || world.Location != _npcWalkFrom || now - _npcWalkSince > TimeSpan.FromSeconds(30))
            {
                _npcWalkTo = npc;
                _npcWalkFrom = world.Location;
                _npcWalkSince = now;
                return;
            }

            if (now - _npcWalkSince >= TimeSpan.FromSeconds(5))
            {
                BrainLog?.Invoke($"Errand walk to {npc} has not moved us for 5s at " +
                                 $"{world.Location.X},{world.Location.Y} (route {_route.Length} cells, " +
                                 $"{world.Objects.Count(o => o.IsLiveMonster)} live monsters in view).");
                _npcWalkSince = now + TimeSpan.FromSeconds(25);     // once per stall, not every tick
            }
        }

        /// <summary>A scenery node (AI 4) that the quest hunt wants - the Chestnut Tree.</summary>
        private bool IsQuestTree(WorldObject ob) =>
            ob != null && ob.IsSceneryNode && ob.IsLiveMonster && QuestHunting &&
            Quest.HuntTargets.Contains(ob.MonsterIndex);

        /// <summary>Walking to a boss that was actually seen (in view or on the tracker).</summary>
        private bool BossChasing =>
            _roamKind == RoamTargetKind.BossLair && _roamTarget != Point.Empty && _lair?.Sighted == true;

        private Decision TryButcher(WorldModel world)
        {
            if (!_config.ButcherEnabled || _butcher == null) return null;

            // While hunting quest targets, only the quest's OWN corpses: a harvest monster (Spitting
            // Spider, Spider Bat) keeps its quest item - Venom, Spider Curare - in the corpse until
            // it is butchered. Any other carcass is a detour, and the target moves on.
            bool questOnly = QuestHunting;

            // No room for what it yields, so there is no point making it - unless it is the quest
            // corpse, whose item takes no slot.
            bool heavy = world.MaxBagWeight > 0 && world.WeightPercent >= _config.HeavyWeightPercent;
            if (heavy && !questOnly) return null;

            // Anything alive nearby wins. Standing still over a corpse is how a bot gets killed,
            // and the Mir 2 agents break off harvesting for exactly this at the same range.
            if (world.NearestLiveMonster(2) != null) return null;

            WorldObject corpse = null;
            int best = int.MaxValue;

            foreach (WorldObject ob in world.Objects)
            {
                if (ob.Kind != ObjectKind.Monster || !ob.Dead) continue;
                if (_butchered.Contains(ob.ObjectID)) continue;
                if (!_butcher.IsButcherable(ob.MonsterIndex)) continue;
                if (questOnly && !Quest.HuntTargets.Contains(ob.MonsterIndex)) continue;

                _butcherAttempts.TryGetValue(ob.ObjectID, out int tries);
                if (tries >= _config.ButcherAttempts) continue;

                int distance = world.DistanceTo(ob.Location);
                if (distance > _config.ButcherRange || distance >= best) continue;

                corpse = ob;
                best = distance;
            }

            if (corpse == null) return null;

            if (best <= 1)
            {
                _butcherAttempts.TryGetValue(corpse.ObjectID, out int tries);
                _butcherAttempts[corpse.ObjectID] = tries + 1;

                return new Decision
                {
                    Action = BotAction.Butcher,
                    Reason = $"butchering {corpse.Name}",
                    Subject = $"butcher {corpse.Name}",
                    TargetID = corpse.ObjectID,
                    Direction = WorldModel.DirectionTo(world.Location, corpse.Location)
                };
            }

            Decision walk = new Decision
            {
                Action = BotAction.Approach,
                Reason = $"butcher {corpse.Name} at {best}",
                Subject = $"butcher {corpse.Name}",
                TargetID = corpse.ObjectID
            };

            // goalRange 1: harvesting is done from an adjacent tile, facing the corpse.
            if (!TrySteer(walk, world, corpse.Location, 1, best, routeKind: "loot"))
            {
                // Cannot reach it this tick. Spend an attempt rather than orbiting it for ever -
                // the loot path learned this lesson the expensive way.
                _butcherAttempts.TryGetValue(corpse.ObjectID, out int tries);
                _butcherAttempts[corpse.ObjectID] = tries + 1;
                return null;
            }

            return walk;
        }

        private const int LootAttemptLimit = 4;

        private Decision TryLoot(WorldModel world)
        {
            bool heavy = world.MaxBagWeight > 0 && world.WeightPercent >= _config.HeavyWeightPercent;
            bool full = world.MaxBagWeight > 0 && world.WeightPercent >= 100;

            // Quest items are picked up even with looting off: they are quest progress, not loot.
            WorldObject item = _config.LootEnabled
                ? FindWorthwhileLoot(world, heavy, full)
                : QuestItemOnFloor(world, new HashSet<uint>(_unreachable.Keys));
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

                // The ground packet carries the full instance, including ItemIndex for a [Part].
                // Show its actual target rather than making every part drop look identical.
                string lootName = item.Item != null ? Backpack.Describe(item.Item) : item.Name;
                return new Decision { Action = BotAction.Loot, Reason = lootName, Subject = lootName };
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
                !TrySteer(walk, world, item.Location, 0, distance, routeKind: "loot"))
            {
                // Could not get there THIS TICK. Usually something is standing in the way, and
                // standing is a temporary condition - so the first sentence is short, or the bot
                // walks away from loot it could have had a moment later.
                //
                // But a FIXED short sentence is a loop when the obstruction is not temporary.
                // A potion under a monster, or a bot boxed in by a crowd, produced exactly that:
                // park three seconds, retry, park three seconds, retry, for as long as the crowd
                // lasted. One assassin spent its time alternating between drinking a potion and
                // walking at a potion one tile away it could never reach, taking damage the whole
                // time and never once attacking - 84 server resyncs in 81 seconds.
                //
                // So the sentence doubles each time the same object is blocked, and after enough
                // goes it becomes the long one. Something genuinely temporary still gets its
                // quick retry; something that is not stops being asked about.
                _lootBlocked.TryGetValue(item.ObjectID, out int blocked);
                _lootBlocked[item.ObjectID] = blocked + 1;

                TimeSpan sentence = blocked >= LootBlockedLimit
                    ? RefusedByServer
                    : TimeSpan.FromTicks(BlockedForNow.Ticks * (1L << blocked));

                Park(item.ObjectID, sentence);
                _blockedMoves = 0;
                ForgetPursuit();
                return null;
            }

            // Reaching it clears the record: the next time it is blocked starts from a short
            // sentence again.
            _lootBlocked.Remove(item.ObjectID);
            _lootTarget = item.ObjectID;

            return walk;
        }

        /// <summary>Record that an action was issued, and pace the next one accordingly.</summary>
        public void Issued(Decision decision, WorldModel world)
        {
            LastAction = decision.Action;

            // One place, rather than at each of the five sites that can attack. An attack ON THE
            // COMMITTED TARGET resets the abandon clock; approaching it, kiting around it and
            // failing to reach it all deliberately do not, because those are exactly the states
            // the watchdog exists to time out.
            if ((decision.Action == BotAction.Attack || decision.Action == BotAction.Cast) &&
                decision.TargetID != 0 && decision.TargetID == _committedTarget)
                NoteAttacked();

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
                case BotAction.MergeParts:
                    _nextAction = DateTime.Now + TurnTime + Margin;
                    break;
                case BotAction.LearnBook:
                case BotAction.AssemblePart:
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
                    Spells.ReserveIssued(decision.Magic, world, decision.Point,
                        decision.Direction, Maps?.For(world.MapIndex));
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
                case BotAction.Butcher:
                    // Globals.HarvestTime is 600ms on the server; asking faster is throwing
                    // packets away and counts against the flood threshold.
                    _nextAction = DateTime.Now + TimeSpan.FromMilliseconds(600) + Margin;
                    break;
                case BotAction.Equip:
                case BotAction.Loot:
                    _nextAction = DateTime.Now + TurnTime + Margin;
                    break;
                case BotAction.Heal:
                    // DRINKING DOES NOT COST THE SWING.
                    //
                    // The server keeps two clocks: AttackTime for swings, and ItemTime with its
                    // own one-second gate for potions (PlayerObject.cs:561). A player drinks and
                    // keeps hitting. The bot funnelled both through _nextAction, so every potion
                    // bought a full second of silence - roughly half its damage output whenever it
                    // was taking damage, which is exactly when it can least afford to lose it. An
                    // assassin pinned at the mine entrance alternated Heal, Attack, Heal, Attack
                    // once a second and killed nothing.
                    //
                    // _nextPotion already models the item cooldown correctly and is checked by
                    // both drink branches, so the item side loses nothing. _nextAction drops to
                    // packet spacing, which is all it was ever needed for here.
                    _nextAction = DateTime.Now + Margin;
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
