using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    public enum TownPhase
    {
        None,
        Teleporting,   // used a scroll, waiting to land
        Travelling,    // heading for the vendor
        Talking,       // NPC open, walking its dialogue tree
        Trading,       // on a trading page: sell then buy
        Banking,       // in the safe zone: stow future upgrades, reclaim usable ones
        Returning      // heading back to where we were hunting
    }

    /// <summary>
    /// The bag-full round trip: get to town, sell what is not worth keeping, restock town scrolls,
    /// walk back out to where the hunting was.
    ///
    /// The vendor is NOT configured. VendorDirectory reads every NPC's dialogue out of System.db and
    /// records what each page buys and sells, so the bot picks whichever NPC actually buys the most
    /// of what it is carrying. Naming one in config went stale the moment the bag held something
    /// that NPC would not take.
    ///
    /// Travel is on foot. Server-side auto-path (C.AutoPathStart) would be preferable, but
    /// AutoPathRoutePlanner refuses any route unless both maps have MapInfo.CanAutoPath, and Bichon
    /// Town does not.
    /// </summary>
    public sealed class TownTrip
    {
        private readonly BotConfig _config;
        private readonly VendorDirectory _directory;
        private readonly MagicBooks _books;

        public TownPhase Phase { get; private set; } = TownPhase.None;
        public string Status { get; private set; } = "";

        private VendorEntry _target;
        private readonly Queue<VendorEntry> _itinerary = new Queue<VendorEntry>();
        private NPCPage _currentPage;
        private readonly Queue<int> _buttonPath = new Queue<int>();
        private bool _sold;
        private bool _boughtScrolls;
        private bool _boughtPotions;
        private bool _boughtMana;
        private bool _boughtTorch;
        private bool _repaired;
        private bool _repairedSpecial;
        private bool _boughtBook;
        private bool _boughtGear;

        /// <summary>Latched when no NPC in System.db repairs what we carry, so a broken item
        /// does not loop begin -> abort -> cooldown forever.</summary>
        private bool _noRepairerKnown;

        /// <summary>The map the trip started from, so Returning knows whether walking back means
        /// anything. A scroll can put the bot in a different town entirely.</summary>
        private int _huntingMap = -1;

        /// <summary>Where we were when the scroll was used, so landing can be detected.</summary>
        private int _mapBeforeTeleport = -1;

        /// <summary>The map to travel back to once the trip is done, or -1. Consumed by the caller,
        /// and cleared by Abort so an emergency scroll cannot leave a stale destination behind.</summary>
        public int ReturnToMap { get; private set; } = -1;

        public void ClearReturn() => ReturnToMap = -1;

        /// <summary>
        /// A repair is out and we have not heard back.
        ///
        /// Self-expiring rather than waiting on the callback, because the Trading phase has no
        /// watchdog of its own: if the answer were lost the trip would sit here for ever. The
        /// connection already times a silent refusal out after six seconds, so eight is a backstop
        /// behind a backstop rather than the primary mechanism.
        ///
        /// It matters because the special and ordinary repairs are two separate requests and
        /// nothing in the state machine serialises them. BotConnection holds ONE _pendingRepair
        /// slot list and overwrites it, and the local durability model is only corrected when
        /// S.NPCRepair arrives - so an ordinary request issued before the special reply lands would
        /// price itself against stale gold, re-offer items the server had just repaired, and orphan
        /// the pending list so the reply is attributed to the wrong request.
        /// </summary>
        private DateTime _repairPendingUntil = DateTime.MinValue;

        private bool RepairPending => DateTime.Now < _repairPendingUntil;

        /// <summary>What the repair step did, or why it could not. Logged on change.</summary>
        public string RepairDiagnostic = "";

        /// <summary>The server answered a repair, or the connection gave up waiting for one.</summary>
        public void RepairAnswered() => _repairPendingUntil = DateTime.MinValue;

        // ONE URGENT TRIP PER PROBLEM.
        //
        // Two triggers are allowed to jump the trip cooldown or to start a trip on their own:
        // broken gear, and running out of healing potions. Both are right the first time and both
        // become traps if the trip does not actually fix them, because the condition that started
        // the trip is still true when it ends - so the next trip begins at once, forever.
        //
        // Measured, not guessed: a level 14 wizard ran 396 town trips in five hours at a median of
        // 43 seconds apart, against a two-minute cooldown. It cast 29 spells in that time, went
        // from 22 gold to 4, and stayed level 14 all night. The warrior, on the same code but able
        // to afford its restocks, made 4 trips and gained two levels.
        //
        // A time-based backoff was tried first and was not enough: it only re-armed when a repair
        // was actually ATTEMPTED and refused, and the trip never reached a repairer for the broken
        // item at all - so it re-armed twice in five hours and the bypass stayed open in between.
        // A latch needs no timer and cannot be out-waited: the urgency is spent by the trip that
        // acts on it, and only comes back when the problem is genuinely gone.
        private bool _brokenUrgencySpent;
        private bool _supplyUrgencySpent;

        /// <summary>
        /// The server would not repair what we asked it to - almost always the bill.
        ///
        /// The latch below already covers this; this exists so the reason is recorded rather than
        /// inferred from a trip that quietly stopped being urgent.
        /// </summary>
        public void RepairRefused()
        {
            _brokenUrgencySpent = true;
            Status = "repair refused - hunting for the money first";
        }

        /// <summary>We went to town for supplies and could not afford them.</summary>
        public void SupplyRefused(string why)
        {
            _supplyUrgencySpent = true;
            SupplyDiagnostic = why;
        }

        private DateTime _phaseSince = DateTime.MinValue;
        private int _lastRemaining = int.MaxValue;
        private DateTime _lastProgress = DateTime.MinValue;

        /// <summary>Where we were hunting when the bag filled, so we can go back to it.</summary>
        private Point _huntingSpot = Point.Empty;

        private DateTime _retryAfter = DateTime.MinValue;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(2);

        public TownTrip(BotConfig config, VendorDirectory directory, MagicBooks books)
        {
            _config = config;
            _directory = directory;
            _books = books;
        }

        /// <summary>Why the last sale was the size it was - an empty sale is not a failure.</summary>
        public string SellDiagnostic = "";

        /// <summary>Why a book stop was or was not added.</summary>
        public string BookDiagnostic = "";
        /// <summary>
        /// What the bank step did, or why it did nothing.
        ///
        /// Storage is the one part of a town trip with no visible effect on the character, so
        /// without this there is no way to tell a working reclaim from one that has been silently
        /// skipping everything for weeks.
        /// </summary>
        public string BankDiagnostic = "";

        /// <summary>The shops this trip plans to visit, in the order the money will be spent.</summary>
        public string Itinerary = "";

        /// <summary>
        /// Counts trips, so a plan identical to the last one is still reported as a new trip.
        ///
        /// Logging the itinerary only when the STRING changed made consecutive trips with the same
        /// plan invisible - which is the common case, since a bot in one town shops in one town.
        /// Two visits to the same NPC then look like one trip doubling back, which is a different
        /// and much more alarming thing.
        /// </summary>
        public int TripSequence { get; private set; }

        public string GearDiagnostic = "";
        public string SupplyDiagnostic = "";

        public bool Active => Phase != TownPhase.None;
        public bool OnCooldown => DateTime.Now < _retryAfter;

        private bool _forced;

        /// <summary>
        /// Ask for a trip regardless of bag weight and regardless of the cooldown. Cleared by Abort
        /// so a failed forced trip does not silently repeat on the next Start.
        /// </summary>
        public void Force()
        {
            _forced = true;
            _retryAfter = DateTime.MinValue;
        }
        public VendorEntry Target => _target;

        /// <summary>Asked for explicitly, from the status page. Not to be second-guessed.</summary>
        public bool Forced => _forced;

        private Point Destination =>
            Phase == TownPhase.Returning ? _huntingSpot : _target?.Point ?? Point.Empty;

        /// <summary>
        /// Clear everything that belongs to ONE trip.
        ///
        /// This exists because the same list was written out in two places and they drifted, with
        /// consequences nobody would guess from reading either one. Abort cleared all nine flags;
        /// PageChanged cleared five of them per NPC page; and the SUCCESS path cleared none at all -
        /// it set Phase = None and returned. So _repaired, _repairedSpecial and _boughtBook, the
        /// three that only Abort touched, stayed true for the rest of the connection after the
        /// first trip that completed normally.
        ///
        /// The effect: a bot whose trips succeed repairs exactly once per login and then silently
        /// never again, while a bot whose trips keep failing repairs every time, because aborting is
        /// what reset it. Bot 2 was aborting constantly, which hid it; bot 1 was not, which meant
        /// bot 1 had it.
        ///
        /// One method, called from both the start of a trip and from Abort, so the two cannot
        /// disagree again.
        /// </summary>
        private void ResetTripState()
        {
            _currentPage = null;
            _buttonPath.Clear();
            _itinerary.Clear();
            _target = null;

            _sold = false;
            _boughtScrolls = false;
            _boughtPotions = false;
            _boughtMana = false;
            _boughtTorch = false;
            _boughtGear = false;
            _boughtBook = false;
            _repaired = false;
            _repairedSpecial = false;
            _repairPendingUntil = DateTime.MinValue;
        }

        public void Abort(string why)
        {
            // Returning is best-effort; failing to get back out should not lock out the next trip.
            if (Phase != TownPhase.None && Phase != TownPhase.Returning)
                _retryAfter = DateTime.Now + RetryDelay;

            Phase = TownPhase.None;
            Status = why;
            _forced = false;        // a failed forced trip must not repeat on the next Start

            // Operational, not historical: the emergency scroll path aborts the trip and then
            // teleports, and a stale destination left here would send the bot travelling back to a
            // hunting ground it fled on purpose.
            ReturnToMap = -1;

            ResetTripState();
        }

        private void Enter(TownPhase phase, string status)
        {
            Phase = phase;
            Status = status;
            _phaseSince = DateTime.Now;
            _lastRemaining = int.MaxValue;
            _lastProgress = DateTime.MinValue;
        }

        private bool StuckIn(TimeSpan limit) => DateTime.Now - _phaseSince > limit;

        public Decision Next(WorldModel world, Backpack items)
        {
            bool overweight = world.MaxBagWeight > 0 &&
                              world.WeightPercent >= _config.TownAtWeightPercent;

            // Broken gear gives no stats at all, so this is urgent enough to bypass the cooldown -
            // otherwise a break just after a trip locks the bot out of repair for two minutes while
            // it fights with a dead weapon.
            //
            // But "urgent" has to expire, because bypassing the cooldown assumes the trip will FIX
            // the break, and a repair we cannot pay for does not. A level 14 wizard with a broken
            // ring, 22 gold and a 611-gold repair bill walked the same four-NPC circuit every 43
            // seconds, indefinitely: it never hunted, so it never earned the money, so the ring
            // stayed broken, so the next trip started immediately. Six trips in four minutes before
            // anyone noticed - and the only reason anyone did notice is that the itinerary had just
            // been made visible in the log.
            //
            // So a refused repair puts the bypass to sleep. The ordinary cooldown then applies, the
            // bot goes back to hunting, and it tries again once it has had a chance to earn
            // something.
            bool anyBroken = _config.RepairEnabled && items.AnyBrokenEquipment();

            // Nothing broken any more: the next break gets its urgent trip back.
            if (!anyBroken) _brokenUrgencySpent = false;

            bool repairable = anyBroken && !_noRepairerKnown;
            bool brokenUrgent = repairable && !_brokenUrgencySpent;

            // Out of the thing that keeps us alive.
            //
            // Until now the ONLY reasons to go to town were a full bag, broken gear, or the button.
            // That assumes the bag fills faster than the potions empty, which is true for a warrior
            // and false for a caster: a wizard drinks its way through a stack while looting almost
            // nothing. One was watched at 23% health with no potions, no scroll and a 66% bag,
            // casting at chickens with nothing on the map able to change its situation - because
            // nothing in the trip trigger was looking at supplies at all.
            //
            // This is the same shape as the three survival deadlocks already fixed here, one level
            // up: the earlier fixes made sure a bot with nothing to drink could REACH the town
            // block, and this is what makes the town block do anything when it gets there.
            bool shortOfPotions = false;

            if (_config.RestockAtPotionPercent > 0 && world.MaxBagWeight > 0)
            {
                int target = _config.HealthPotionTarget(world.MaxBagWeight);
                int floor = Math.Max(1, target * _config.RestockAtPotionPercent / 100);

                shortOfPotions = items.CountHealthPotions() < floor;
            }

            // Stocked up again: the next shortage gets its urgent trip back.
            if (!shortOfPotions) _supplyUrgencySpent = false;

            bool supplyUrgent = shortOfPotions && !_supplyUrgencySpent;

            if (!Active)
            {
                // TWO different questions, and running them together was a trap.
                //
                // "Is there a reason to go?" and "may I jump the cooldown?" are not the same, but
                // the urgency latches were wired into the first. So once a trip had been made about
                // running out of potions, being out of potions stopped counting as a reason to go at
                // all - and since only buying potions clears that latch, and buying needs a trip,
                // the only ways out were the bag filling up or looting a potion off the ground.
                //
                // A caster empties potions far faster than it fills a bag, which is the same
                // mismatch that made the supply trigger necessary in the first place. The latch now
                // governs only the cooldown bypass, which is all it was ever meant to do: one
                // trip that jumps the queue per problem, and after that the ordinary cadence.
                bool reason = overweight || repairable || shortOfPotions || _forced;
                bool urgent = brokenUrgent || supplyUrgent || _forced;

                if (!reason) return null;
                if (OnCooldown && !urgent) return null;

                bool forced = _forced;
                _forced = false;

                return Begin(world, items, brokenUrgent || forced, supplyUrgent);
            }

            switch (Phase)
            {
                case TownPhase.Teleporting:
                {
                    // The scroll drops us at the bind point, and only now do we know where that is.
                    // The plan is built here, for this map, rather than before departure - which is
                    // what left the bot walking toward vendors in the town it had just left.
                    bool landed = world.MapIndex != _mapBeforeTeleport;

                    // Six seconds with no map change means either the bind point IS this map or the
                    // scroll silently failed. Those are indistinguishable from here and, usefully,
                    // the response to both is the same: plan for where we actually are.
                    if (!landed && !StuckIn(TimeSpan.FromSeconds(6))) return null;

                    switch (PlanItinerary(world, items, world.MapIndex))
                    {
                        case PlanOutcome.Banking:
                            Enter(TownPhase.Banking, "banking only - nothing to trade here");
                            return null;

                        case PlanOutcome.Travelling:
                            Enter(TownPhase.Travelling, $"landed, walking to {Itinerary}");
                            return WalkStep(world, "walking to vendor");
                    }

                    Abort(landed
                        ? $"landed on {world.MapName} with nothing to do there"
                        : "the scroll did not move us and there is nothing to do here");
                    return null;
                }

                case TownPhase.Travelling:
                {
                    int remaining = WorldModel.Distance(world.Location, _target.Point);

                    if (remaining > _config.VendorTalkRange)
                    {
                        if (StuckIn(TimeSpan.FromMinutes(2)))
                        {
                            Abort("walking to the vendor took too long");
                            return null;
                        }

                        return WalkStep(world, "walking to vendor");
                    }

                    WorldObject npc = FindVendorObject(world);

                    if (npc == null)
                    {
                        if (StuckIn(TimeSpan.FromSeconds(20)))
                            Abort("arrived but could not see the vendor");
                        return null;
                    }

                    Enter(TownPhase.Talking, $"at {_target.NPC.NPCName}");

                    // The button path was worked out from System.db when the directory was built.
                    foreach (int button in _target.ButtonPath) _buttonPath.Enqueue(button);

                    return new Decision
                    {
                        Action = BotAction.NPCCall,
                        Reason = _target.NPC.NPCName,
                        TargetID = npc.ObjectID
                    };
                }

                case TownPhase.Talking:
                    if (StuckIn(TimeSpan.FromSeconds(20)))
                    {
                        Abort("NPC did not respond");
                        return new Decision { Action = BotAction.NPCClose, Reason = "timeout" };
                    }

                    if (_buttonPath.Count > 0)
                    {
                        int button = _buttonPath.Dequeue();
                        return new Decision
                        {
                            Action = BotAction.NPCButton,
                            Reason = $"button {button}",
                            ButtonID = button
                        };
                    }

                    return null;

                case TownPhase.Trading:
                {
                    // Unlock anything we mean to sell before offering it. One per tick: the server
                    // answers each with S.ItemLock and there is no batch form.
                    if (_config.UnlockToSell)
                    {
                        List<int> disposable = items.DisposableIncludingLocked(
                            world.Class, world.Gender, world.Level, world.PlayerStats,
                            _config.HealthPotionTarget(world.MaxBagWeight),
                            _config.ManaPotionTarget(world.MaxBagWeight),
                            _config.TownScrollReserve, _books, world);

                        List<int> locked = items.LockedSlots(disposable);

                        if (locked.Count > 0)
                        {
                            _sold = false;      // the sell list changes once these are free
                            return new Decision
                            {
                                Action = BotAction.Unlock,
                                Reason = $"unlocking {items.InSlot(locked[0])?.Info?.ItemName}",
                                Subject = "unlocking",
                                FromSlot = locked[0]
                            };
                        }
                    }

                    if (!_sold)
                    {
                        _sold = true;

                        List<ItemType> accepted = AcceptedTypes();

                        List<int> slots = items
                            .DisposableSlots(world.Class, world.Gender, world.Level, world.PlayerStats,
                                _config.HealthPotionTarget(world.MaxBagWeight),
                                _config.ManaPotionTarget(world.MaxBagWeight),
                                _config.TownScrollReserve, _books, world)
                            .Where(slot => Backpack.Sellable(items.InSlot(slot), accepted))
                            .ToList();

                        SellDiagnostic = $"{slots.Count} sellable of {items.InventoryCount} slots " +
                                         $"({items.CountHealthPotions()} health potions, " +
                                         $"{items.CountTownScrolls()} scrolls held)";

                        if (slots.Count > 0)
                            return new Decision
                            {
                                Action = BotAction.NPCSell,
                                Reason = $"{slots.Count} slots to {_target.NPC.NPCName}",
                                SellSlots = slots
                            };
                    }

                    // Repair is driven off the LIVE page, not the routing metadata: the page
                    // governs what the server will accept, and a stale System.db read could differ.
                    //
                    // Special first, then ordinary for whatever special could not cover. Ordinary
                    // repair permanently eats the item's maximum durability; special does not, so
                    // it is the one worth paying double for on gear we mean to keep.
                    // One repair request at a time - see _repairPendingUntil.
                    if (RepairPending) return null;

                    if (!_repairedSpecial && _config.RepairEnabled && _config.PreferSpecialRepair &&
                        _currentPage?.DialogType == NPCDialogType.Repair)
                    {
                        _repairedSpecial = true;

                        List<int> special = items.AffordableRepairSlots(
                            items.SpecialRepairSlots(AcceptedTypes()), world.Gold, true,
                            out long specialCost, out int specialSkipped);

                        if (special.Count > 0)
                        {
                            _repairPendingUntil = DateTime.Now + RepairReplyWindow;
                            RepairDiagnostic = DescribeRepair(items, special, specialCost,
                                specialSkipped, true, world.Gold);

                            return new Decision
                            {
                                Action = BotAction.NPCRepair,
                                Reason = $"{special.Count} items (special) at {_target.NPC.NPCName}",
                                RepairSlots = special,
                                Special = true
                            };
                        }

                        if (specialSkipped > 0)
                            RepairDiagnostic = $"cannot afford any special repair at " +
                                               $"{_target.NPC.NPCName} with {world.Gold:N0} gold";
                    }

                    if (!_repaired && _config.RepairEnabled &&
                        _currentPage?.DialogType == NPCDialogType.Repair)
                    {
                        _repaired = true;

                        List<int> slots = items.AffordableRepairSlots(
                            items.DamagedEquipmentSlots(_config.RepairAtDurability, AcceptedTypes()),
                            world.Gold, false, out long cost, out int skipped);

                        if (slots.Count > 0)
                        {
                            _repairPendingUntil = DateTime.Now + RepairReplyWindow;
                            RepairDiagnostic = DescribeRepair(items, slots, cost, skipped, false,
                                world.Gold);

                            return new Decision
                            {
                                Action = BotAction.NPCRepair,
                                Reason = $"{slots.Count} items at {_target.NPC.NPCName}",
                                RepairSlots = slots
                            };
                        }

                        if (skipped > 0)
                            RepairDiagnostic = $"cannot afford any repair at {_target.NPC.NPCName} " +
                                               $"with {world.Gold:N0} gold ({skipped} item(s) damaged)";
                    }

                    if (!_boughtPotions)
                    {
                        _boughtPotions = true;

                        // Topping up healing potions matters more than the scrolls: without them the
                        // heal branch has nothing to drink and the bot fights until it dies.
                        NPCGood good = BestPotion(world, items, true);

                        // Two separate numbers, because "buy none" has two very different causes
                        // and they need opposite responses: we already hold enough (fine, carry
                        // on), or we cannot pay for a single one (a problem this trip has NOT
                        // solved, so the urgency that sent us here must not survive it).
                        int needed = good == null ? 0
                            : _config.HealthPotionTarget(world.MaxBagWeight,
                                  Math.Max(1, good.Item.Weight)) - items.CountHealthPotions();

                        int wanted = Affordable(good, needed, world.Gold);

                        if (good != null && needed > 0 && wanted <= 0)
                            SupplyRefused($"cannot afford {good.Item.ItemName} at " +
                                          $"{good.Item.Price} each with {world.Gold} gold");

                        if (good != null && wanted > 0)
                        {
                            SupplyDiagnostic = $"{wanted} x {good.Item.ItemName} " +
                                               $"(heals {good.Item.Stats[Stat.Health]} of {world.MaxHealth})";

                            return new Decision
                            {
                                Action = BotAction.NPCBuy,
                                Reason = $"{wanted} x {good.Item.ItemName}",
                                BuyIndex = good.Index,
                                BuyAmount = wanted
                            };
                        }
                    }

                    // Scrolls AFTER potions, for the same reason the itinerary visits the potion
                    // seller first: on a page that sells both, whichever is bought first gets the
                    // money. An escape is worth less than not needing one.
                    if (!_boughtScrolls)
                    {
                        _boughtScrolls = true;

                        NPCGood good = FindGood(IsTownScroll);

                        // Trimmed to the purse like every other order: the server refuses an
                        // unaffordable C.NPCBuy silently AND entirely, so asking for three when we
                        // can pay for one gets us none.
                        int wanted = Affordable(good,
                            _config.TownScrollReserve - items.CountTownScrolls(), world.Gold);

                        if (good != null && wanted > 0)
                            return new Decision
                            {
                                Action = BotAction.NPCBuy,
                                Reason = $"{wanted} x {good.Item.ItemName}",
                                BuyIndex = good.Index,
                                BuyAmount = wanted
                            };
                    }

                    if (!_boughtTorch && _config.KeepTorchLit &&
                        !items.HasTorchEquipped && !items.CarryingTorch)
                    {
                        _boughtTorch = true;

                        // Cheapest will do: a torch is a torch, and PendingEquips puts it on by
                        // itself on the next tick because the slot is empty.
                        NPCGood good = FindGood(info =>
                            info.ItemType == ItemType.Torch &&
                            Backpack.CanEquipInfo(info, world.Class, world.Gender) &&
                            Backpack.MeetsRequirement(info, world.Level, world.PlayerStats));

                        if (good != null && good.Item.Price <= world.Gold)
                            return new Decision
                            {
                                Action = BotAction.NPCBuy,
                                Reason = $"1 x {good.Item.ItemName}",
                                BuyIndex = good.Index,
                                BuyAmount = 1
                            };
                    }

                    if (!_boughtMana && _config.ManaPotionWeightPercent > 0)
                    {
                        _boughtMana = true;

                        NPCGood good = BestPotion(world, items, false);

                        int wanted = good == null ? 0
                            : Affordable(good,
                                  _config.ManaPotionTarget(world.MaxBagWeight,
                                      Math.Max(1, good.Item.Weight)) - items.CountManaPotions(),
                                  world.Gold);

                        if (good != null && wanted > 0)
                            return new Decision
                            {
                                Action = BotAction.NPCBuy,
                                Reason = $"{wanted} x {good.Item.ItemName}",
                                BuyIndex = good.Index,
                                BuyAmount = wanted
                            };
                    }

                    // Books only once potions AND scrolls are at their reserves - evaluated NOW,
                    // after the restock steps above have run, not from the values at Begin.
                    //
                    // Every branch here records why, because the whole step is silent otherwise:
                    // the bot walks to the book seller, opens the page, buys nothing and closes,
                    // which is indistinguishable in the log from the step never running at all.
                    if (!_boughtBook && _config.BuyBooks)
                    {
                        int potions = items.CountHealthPotions();
                        int scrolls = items.CountTownScrolls();

                        if (potions < _config.HealthPotionReserve ||
                            scrolls < _config.TownScrollReserve)
                        {
                            BookDiagnostic =
                                $"not buying at {_target?.NPC?.NPCName}: restock first " +
                                $"({potions}/{_config.HealthPotionReserve} potions, " +
                                $"{scrolls}/{_config.TownScrollReserve} scrolls)";
                        }
                        else
                        {
                            NPCGood good = FindGood(info => WantedBook(info, world));

                            if (good != null)
                            {
                                // Latched only on an actual purchase. Setting it merely because the
                                // step RAN meant the first vendor past the restock gate consumed
                                // the trip's one book - and on a Joeban/Lennard/Isaac itinerary
                                // that was Lennard, who sells no books, so the block was already
                                // marked done by the time the bot reached the actual book seller.
                                // It closed Isaac's page in silence, without even a refusal line.
                                _boughtBook = true;

                                return new Decision
                                {
                                    Action = BotAction.NPCBuy,
                                    Reason = $"1 x {good.Item.ItemName}",
                                    BuyIndex = good.Index,
                                    BuyAmount = 1     // one per trip: a bought book is Locked and
                                                      // NonRefinable, so a wrong one is dead weight
                                };
                            }

                            BookDiagnostic = $"nothing to buy at {_target?.NPC?.NPCName} - " +
                                             DescribeBookGoods(world);
                        }
                    }

                    // Gear last of all: potions, scrolls, repairs and books are survival and
                    // progression, an upgrade is a luxury. It is also the only purchase that can
                    // empty the purse, so it spends what is left above the reserve.
                    if (!_boughtGear && _config.BuyGear)
                    {
                        _boughtGear = true;

                        NPCGood good = BestUpgradeOnPage(world, items, out string why);

                        if (good != null)
                        {
                            GearDiagnostic = $"buying {good.Item.ItemName} at {_target?.NPC?.NPCName} - {why}";

                            return new Decision
                            {
                                Action = BotAction.NPCBuy,
                                Reason = $"1 x {good.Item.ItemName}",
                                BuyIndex = good.Index,
                                BuyAmount = 1
                            };
                        }

                        GearDiagnostic = $"no upgrade at {_target?.NPC?.NPCName} - {why}";
                    }

                    if (_itinerary.Count > 0)
                    {
                        _target = _itinerary.Dequeue();
                        Enter(TownPhase.Travelling, $"next stop: {_target.NPC.NPCName}");
                        return new Decision
                        {
                            Action = BotAction.NPCClose,
                            Reason = $"done here, on to {_target.NPC.NPCName}"
                        };
                    }

                    Enter(TownPhase.Banking, "banking");
                    return new Decision { Action = BotAction.NPCClose, Reason = "trade done" };
                }

                case TownPhase.Banking:
                {
                    // Storage is only reachable inside a safe zone (PlayerObject.cs:7421). Vendors
                    // normally stand in one; if this vendor does not, skip banking rather than
                    // sending moves the server will refuse.
                    if (!world.InSafeZone || StuckIn(TimeSpan.FromSeconds(20)))
                    {
                        Enter(TownPhase.Returning, world.InSafeZone
                            ? "banking done"
                            : "not in a safe zone, skipping the bank");
                        return null;
                    }

                    // Reclaim anything levelling has unlocked since it was stored, and that is
                    // still better than whatever we have found in the meantime.
                    List<Backpack.Reclaim> reclaims = items.StorageReclaims(world.Class,
                        world.Gender, world.Level, world.PlayerStats, _books, world);

                    if (reclaims.Count > 0)
                    {
                        int free = items.FirstFreeInventorySlot();

                        if (free < 0)
                        {
                            BankDiagnostic = $"{reclaims.Count} to reclaim but the bag is full";
                        }
                        else
                        {
                            Backpack.Reclaim take = reclaims[0];

                            BankDiagnostic = $"reclaiming {take.Item.Info.ItemName} ({take.Why}), " +
                                             $"{reclaims.Count - 1} more to follow";

                            return new Decision
                            {
                                Action = BotAction.Withdraw,
                                Reason = $"{take.Item.Info.ItemName}: {take.Why}",
                                Subject = take.Item.Info.ItemName,
                                FromSlot = take.Slot,
                                ToSlot = free
                            };
                        }
                    }

                    // Stow what we cannot use yet but will.
                    foreach (KeyValuePair<int, ClientUserItem> carried in items.Carried.ToList())
                    {
                        if (!Backpack.WorthStoring(carried.Value, world.Class, world.Gender,
                                world.Level, world.PlayerStats, _books, world)) continue;

                        int free = items.FirstFreeStorageSlot(_config.StorageSize);

                        if (free < 0)
                        {
                            BankDiagnostic = $"storage is full at {_config.StorageSize} slots";
                            break;
                        }

                        BankDiagnostic = $"storing {carried.Value.Info.ItemName} for later";

                        return new Decision
                        {
                            Action = BotAction.Deposit,
                            Reason = $"{carried.Value.Info.ItemName} for later",
                            Subject = carried.Value.Info.ItemName,
                            FromSlot = carried.Key,
                            ToSlot = free
                        };
                    }

                    if (string.IsNullOrEmpty(BankDiagnostic))
                        BankDiagnostic = $"nothing to move: {items.StoredCount} stored, " +
                                         "none usable yet and nothing worth storing";

                    Enter(TownPhase.Returning, "banking done");
                    return null;
                }

                case TownPhase.Returning:
                {
                    // A coordinate only means something on the map it was taken from. After a
                    // scroll the bot is in a different town, and walking to the old map's x,y would
                    // send it to an arbitrary spot here - so the walk is skipped entirely and the
                    // map is handed back for a proper cross-map journey.
                    if (_huntingMap >= 0 && world.MapIndex != _huntingMap)
                    {
                        // Still a success, so it still takes the success cooldown. Without it an
                        // overweight bot begins another trip on the very next tick.
                        _retryAfter = DateTime.Now + RetryDelay;
                        ReturnToMap = _huntingMap;
                        Phase = TownPhase.None;
                        Status = "shopping complete, return needs cross-map travel";
                        return null;
                    }

                    // Back to where the hunting was. Anything met on the way is fair game - the
                    // brain interrupts this for combat by itself, and arriving is not urgent.
                    if (_huntingSpot == Point.Empty ||
                        WorldModel.Distance(world.Location, _huntingSpot) <= _config.ReturnWithin ||
                        StuckIn(TimeSpan.FromMinutes(2)))
                    {
                        // Cool down on success too, not just on failure. If the sale did not
                        // lighten us below the threshold - because what is left is all reserves and
                        // unsellable items - the trip would otherwise restart the instant it ends.
                        _retryAfter = DateTime.Now + RetryDelay;
                        Phase = TownPhase.None;
                        Status = "back at the hunting ground";
                        return null;
                    }

                    return WalkStep(world, "returning to hunt");
                }
            }

            return null;
        }

        /// <summary>What a planning attempt concluded. Deliberately not a nullable Decision: the
        /// caller has to branch on all three, and the Teleporting case must not fall through into
        /// stale phase logic after this has moved Phase and _target.</summary>
        private enum PlanOutcome { Travelling, Banking, NothingToDo }

        /// <summary>
        /// Build an itinerary for ONE map - the map the trip will actually be spent on.
        ///
        /// Split out of Begin because a town scroll teleports to the character's BIND POINT, which
        /// is frequently a different town from the one being hunted. The old order planned first
        /// and scrolled second, so the plan named vendors the bot then could not reach. Observed:
        /// "Trip plan #2: Dakota -> Kacy -> Seven" (Banya Village), "Map changed to index 1"
        /// (Bichon Town), "arrived but could not see the vendor" - nothing sold, nothing bought,
        /// one scroll gone.
        ///
        /// Everything here is map-local by construction: each selector is pinned with onlyMap, so
        /// a stop on another map cannot enter the itinerary in the first place rather than being
        /// filtered out afterwards.
        ///
        /// Owns NO trip-level state. _huntingMap, _huntingSpot and the urgency latches belong to
        /// the trip and are set once by Begin, because this can run a second time after landing and
        /// must not overwrite where the trip started from.
        /// </summary>
        private PlanOutcome PlanItinerary(WorldModel world, Backpack items, int mapIndex)
        {
            _itinerary.Clear();
            _target = null;
            _currentPage = null;
            _buttonPath.Clear();

            // Who actually buys what we are carrying?
            List<ItemType> junk = items
                .DisposableSlots(world.Class, world.Gender, world.Level, world.PlayerStats,
                    _config.HealthPotionTarget(world.MaxBagWeight),
                    _config.ManaPotionTarget(world.MaxBagWeight),
                    _config.TownScrollReserve, _books, world)
                .Select(slot => items.InSlot(slot)?.Info)
                .Where(info => info != null)
                .Select(info => info.ItemType)
                .Distinct()
                .ToList();

            // The null-coalesce that used to live above is why crafting junk never sold.
            //
            // It read `?.ItemType ?? ItemType.Nothing` and then filtered ItemType.Nothing out - but
            // Nothing is a SENTINEL here ("the slot lookup failed") and a genuine item type there
            // (Brown Chestnut, Zombie Bone, Snake Gall - the ordinary crafting drops). Stripping
            // one stripped the other, so no buyer was ever sought for them and Bichon's Loy, who
            // buys nothing but ItemType.Nothing, was never visited. A warrior accumulated 96
            // chestnuts, 45 galls and 19 bones with a vendor for them in the same town.
            //
            // The filter is now on the lookup itself, which is the thing that can actually fail.

            _itinerary.Clear();

            // Cover EVERY item type we are carrying, not just whichever single buyer covers the
            // most of them.
            //
            // Exactly the flaw already fixed on the buying side, still present on the selling side.
            // A town splits its trade: in Bichon, Joeban buys thirty-seven item types and Loy buys
            // one - ItemType.Nothing, the crafting junk. Asking for the best single buyer always
            // returns Joeban, so Loy is never visited and the junk accumulates for ever. A warrior
            // was carrying 96 Brown Chestnut, 45 Snake Gall and 19 Zombie Bone, all sellable, all
            // stuck, with a vendor for them standing in the same town.
            //
            // Resolved by coverage instead: take the best buyer, strike off what it takes, and keep
            // going until nothing saleable is left unmatched. This does not multiply the stops in
            // the ordinary case - one NPC usually covers everything, and the loop ends after one
            // pass - and BestBuyerFor already prefers an NPC that is on the itinerary.
            List<ItemType> uncovered = new List<ItemType>(junk);
            List<VendorEntry> extraBuyers = new List<VendorEntry>();
            VendorEntry buyer = null;

            while (uncovered.Count > 0)
            {
                VendorEntry next = _directory.BestBuyerFor(uncovered, mapIndex, mapIndex);
                if (next == null) break;

                if (buyer == null) buyer = next;              // the primary stop
                else if (!extraBuyers.Contains(next)) extraBuyers.Add(next);

                int before = uncovered.Count;
                uncovered.RemoveAll(next.Buys.Contains);

                // No progress means this buyer takes nothing we still hold; stop rather than spin.
                if (uncovered.Count == before) break;
            }

            // Restock only when we are actually short, and only from someone who sells scrolls.
            // The best buyer usually does not - Joeban buys every item type but sells nothing.
            //
            // The count is simply the count now. This used to subtract a scroll the trip was about
            // to spend teleporting, guessed by WillTeleport, because planning happened before the
            // teleport; planning after it means the scroll is already gone and CountTownScrolls is
            // just true. One guess fewer.
            bool needScrolls = items.CountTownScrolls() < _config.TownScrollReserve;
            bool needPotions = items.CountHealthPotions() <
                               _config.HealthPotionTarget(world.MaxBagWeight);

            List<VendorEntry> restockers = _directory.BestRestockersFor(needScrolls, needPotions,
                mapIndex, _itinerary, world.Location, true);

            // Selling comes first in the itinerary so the gold is in hand before anything is
            // bought, and the extra buyers follow the primary for the same reason.
            if (buyer != null) _itinerary.Enqueue(buyer);

            foreach (VendorEntry extra in extraBuyers)
                if (!_itinerary.Contains(extra)) _itinerary.Enqueue(extra);

            foreach (VendorEntry restocker in restockers)
            {
                if (buyer != null && restocker.Page == buyer.Page) continue;
                if (_itinerary.Contains(restocker)) continue;

                _itinerary.Enqueue(restocker);
            }

            // Repair, when anything is broken or worn past the threshold.
            if (_config.RepairEnabled)
            {
                List<ItemType> damaged = items.DamagedTypes(_config.RepairAtDurability).ToList();

                if (damaged.Count > 0)
                {
                    VendorEntry repairer = _directory.BestRepairerFor(damaged, mapIndex, mapIndex);

                    if (repairer != null) _itinerary.Enqueue(repairer);
                    else if (items.AnyBrokenEquipment() &&
                             _directory.BestRepairerFor(damaged, mapIndex) == null)
                    {
                        // Nothing ANYWHERE can repair it, which is what makes this permanent latch
                        // safe. The selector above is pinned to this map, so its miss only means
                        // "not in this town" - latching on that would write off repair for the rest
                        // of the session because one town lacked the right NPC.
                        _noRepairerKnown = true;
                        Status = "no repair NPC found for the damaged gear";
                    }
                }
            }

            // Somewhere that sells torches, when the light has gone out. Cheap, and the bot is
            // otherwise blind in caves - which is where the good hunting is.
            if (_config.KeepTorchLit && !items.HasTorchEquipped && !items.CarryingTorch)
            {
                VendorEntry torchSeller = _directory.BestGearSellerFor(
                    new List<ItemType> { ItemType.Torch }, mapIndex, _itinerary, world.Location,
                    _config.MaxShoppingDistance, mapIndex);

                if (torchSeller != null && !_itinerary.Contains(torchSeller))
                    _itinerary.Enqueue(torchSeller);
            }

            // A shop worth browsing for an upgrade, when there is money spare to spend.
            if (_config.BuyGear && world.Gold > _config.GoldReserve)
            {
                List<ItemType> gearTypes = new List<ItemType>
                {
                    ItemType.Weapon, ItemType.Armour, ItemType.Helmet, ItemType.Necklace,
                    ItemType.Bracelet, ItemType.Ring, ItemType.Shoes, ItemType.Shield
                };

                // One shop per KIND of gear, not one shop overall.
                //
                // Asking for the best seller of all eight types at once returns a single NPC, and a
                // town splits its trade between several: in Bichon, Mr. Kang sells weapons, Linda
                // sells armour, helmets and shoes, and Amy sells the accessories. Picking one meant
                // the other two were never visited - the bot bought a helmet from Linda's second
                // page and went on wearing level one armour that her FIRST page would have
                // replaced, because the whole NPC was chosen on a single page's stock.
                //
                // Each type is resolved separately and the pages are pooled. This does not multiply
                // the stops: an NPC already on the itinerary outranks every alternative, so once
                // Linda is queued for armour she wins for helmets and shoes as well.
                foreach (ItemType type in gearTypes)
                {
                    List<ItemType> one = new List<ItemType> { type };

                    VendorEntry seller = _directory.BestGearSellerFor(one, mapIndex,
                        _itinerary, world.Location, _config.MaxShoppingDistance, mapIndex);

                    if (seller == null) continue;

                    // An NPC can also split one kind across dialogue options, so take every page of
                    // theirs that stocks it.
                    foreach (VendorEntry page in _directory.PagesOf(seller, one))
                        if (!_itinerary.Contains(page)) _itinerary.Enqueue(page);
                }
            }

            // Books last: potions and scrolls have spending priority, and buying at a stop where we
            // also sold risks a slot desync (a gained item takes the lowest free slot while
            // NoteSold has optimistically freed one).
            if (_config.BuyBooks)
            {
                List<int> wanted = _books?.WantedMagicIndexes(world.Class, world.Level, world).ToList()
                                   ?? new List<int>();

                VendorEntry bookSeller = wanted.Count > 0
                    ? _directory.BestBookSellerFor(wanted, mapIndex, mapIndex)
                    : null;

                if (bookSeller != null) _itinerary.Enqueue(bookSeller);

                BookDiagnostic = $"{wanted.Count} skills wanted at level {world.Level}, " +
                                 $"seller: {bookSeller?.NPC.NPCName ?? "none"}";
            }

            if (_itinerary.Count == 0)
            {
                // No vendor to visit is not the same as nothing to do. Storage is reachable
                // anywhere inside a safe zone and needs no NPC at all, so a trip whose only
                // purpose is to reclaim something levelling has unlocked is a real trip - and one
                // that could never happen while an empty itinerary aborted before the bank step.
                List<Backpack.Reclaim> waiting = items.StorageReclaims(world.Class, world.Gender,
                    world.Level, world.PlayerStats, _books, world);

                if (waiting.Count > 0 && world.InSafeZone)
                {
                    BankDiagnostic = $"{waiting.Count} banked item(s) usable now";
                    return PlanOutcome.Banking;
                }

                if (waiting.Count > 0)
                    BankDiagnostic = $"{waiting.Count} banked item(s) usable, " +
                                     "but we are not in a safe zone to collect them";

                return PlanOutcome.NothingToDo;
            }

            _target = _itinerary.Dequeue();

            // Write the plan down before walking it.
            //
            // Every argument about this code has been settled by looking at data rather than by
            // reasoning about the selection rules, and the one piece of data nobody could see was
            // the plan itself: which shops, in which order. Without it "it never bought potions"
            // and "it bought potions last and ran out of money on the way" look identical in the
            // log, and they need opposite fixes. Order matters because the purse is spent as the
            // itinerary is walked.
            // Published once, for the plan that will actually be walked. A scroll trip plans only
            // after landing, so there is exactly one of these per trip and its stops are real.
            TripSequence++;
            Itinerary = string.Join(" -> ",
                new[] { _target }.Concat(_itinerary).Select(x => x.NPC?.NPCName ?? "?"));

            return PlanOutcome.Travelling;
        }

        /// <summary>
        /// Start a trip: work out where it can be done, and get there.
        ///
        /// Transport is decided by whether THIS map can serve us, not by how far the vendor is.
        /// The old rule compared the vendor's map with the bot's map and scrolled when the walk
        /// looked long - but a town scroll does not shorten a walk, it teleports to the bind point,
        /// which is a different town whenever the bot is hunting away from home. The bind point is
        /// never consulted because the bot does not know it; planning after arrival makes knowing
        /// it unnecessary.
        /// </summary>
        private Decision Begin(WorldModel world, Backpack items, bool broken,
            bool outOfSupplies = false)
        {
            ResetTripState();

            // Captured ONCE, before any transport, and never by the planner - which can run again
            // after landing and would otherwise record the town as the hunting ground.
            _huntingMap = world.MapIndex;
            _huntingSpot = world.Location;

            // Spent at commitment, not on success. A trip interrupted mid-teleport by the low
            // health branch leaves an unspent latch, and an unspent latch bypasses the cooldown -
            // which is the forty-three-second circuit this codebase has already had once.
            if (broken) _brokenUrgencySpent = true;
            if (outOfSupplies) _supplyUrgencySpent = true;

            switch (PlanItinerary(world, items, world.MapIndex))
            {
                case PlanOutcome.Banking:
                    Enter(TownPhase.Banking, "banking only - nothing to trade");
                    return null;

                case PlanOutcome.Travelling:
                    Enter(TownPhase.Travelling, $"walking to {Itinerary}");
                    return WalkStep(world, $"bag {world.WeightPercent}% full");
            }

            // Nothing to do where we stand. A scroll is now the only way to reach a town that can
            // serve us - and it is spent WITHOUT a plan, because where it lands decides the plan.
            int scroll = items.FindTownTeleportSlot();

            if (scroll >= 0)
            {
                _mapBeforeTeleport = world.MapIndex;
                Enter(TownPhase.Teleporting, "scrolling to town to shop");

                return new Decision
                {
                    Action = BotAction.TownTeleport,
                    Reason = $"bag {world.WeightPercent}% full",
                    PotionSlot = scroll
                };
            }

            Abort("nothing to do on this map and no town scroll to reach one");
            return null;
        }

        /// <summary>
        /// One step toward the current destination. Progress is measured rather than assumed: if the
        /// distance stops shrinking we are wedged against geometry this crude navigation cannot
        /// solve, and the trip is abandoned rather than looping forever.
        /// </summary>
        private Decision WalkStep(WorldModel world, string why)
        {
            Point destination = Destination;
            if (destination == Point.Empty) return null;

            int remaining = WorldModel.Distance(world.Location, destination);

            if (remaining < _lastRemaining)
            {
                _lastRemaining = remaining;
                _lastProgress = DateTime.Now;
            }
            else if (_lastProgress != DateTime.MinValue &&
                     DateTime.Now - _lastProgress > TimeSpan.FromSeconds(25))
            {
                Abort($"stopped making progress {remaining} tiles out");
                return null;
            }

            if (_lastProgress == DateTime.MinValue) _lastProgress = DateTime.Now;

            return new Decision
            {
                Action = BotAction.WalkTo,
                Reason = $"{why} ({remaining} tiles)",
                Subject = why,          // stable: the tile count changes every tick
                Destination = destination
            };
        }

        private WorldObject FindVendorObject(WorldModel world)
        {
            // ObjectNPC carries no name, so the vendor is identified by standing position. Matched
            // by proximity, not equality: an NPC's reported tile does not always equal the first
            // point of its region, and an exact match left the bot standing beside him unable to
            // recognise him.
            Point spot = _target?.Point ?? Point.Empty;
            if (spot == Point.Empty) return null;

            WorldObject best = null;
            int bestDistance = int.MaxValue;

            foreach (WorldObject ob in world.Objects)
            {
                if (ob.Kind != ObjectKind.NPC) continue;

                int distance = WorldModel.Distance(ob.Location, spot);
                if (distance > _config.VendorTalkRange || distance >= bestDistance) continue;

                best = ob;
                bestDistance = distance;
            }

            return best;
        }

        public void PageChanged(NPCPage page)
        {
            _currentPage = page;
            if (page == null) return;

            bool tradable = (page.Types != null && page.Types.Count > 0) ||
                            (page.Goods != null && page.Goods.Count > 0);

            // Only once the button path is drained and this is the page we routed to. Flipping on
            // any page with Types or Goods strands the bot on a multi-page NPC: Trading never
            // dequeues the remaining buttons.
            if (tradable && Phase == TownPhase.Talking &&
                _buttonPath.Count == 0 && page == _target?.Page)
            {
                Enter(TownPhase.Trading, "on a trading page");

                // Per STOP, not per trip. A town splits repair across several NPCs by item type -
                // in Banya, Mr. Kim takes weapons, Sara armour and shoes, Dakota the accessories -
                // so a trip-wide latch means one trip can only ever use one of them. That is the
                // same shape as the gear bug where one shop was consulted for eight item types.
                // Observed directly: trip #1 repaired a weapon at Mr. Kim and trip #2 was needed
                // for the armour at Sara.
                //
                // Safe here because PageChanged only reaches this point on the final routed page of
                // a vendor, so it fires once per stop rather than once per dialogue page.
                _sold = _boughtScrolls = _boughtPotions = _boughtGear = _boughtMana = false;
                _boughtTorch = false;
                _repaired = _repairedSpecial = false;
            }
        }

        private static readonly TimeSpan RepairReplyWindow = TimeSpan.FromSeconds(8);

        /// <summary>
        /// Say what is being repaired and what is being left behind.
        ///
        /// A partial repair is as confusing as a refused one unless it says so: the durability
        /// simply does not move on the items that were skipped, and there is nothing in the log to
        /// explain why.
        /// </summary>
        private static string DescribeRepair(Backpack items, List<int> slots, long cost, int skipped,
            bool special, long gold)
        {
            string names = string.Join(", ", slots.Select(items.WornName));
            string kind = special ? "special " : "";

            return skipped == 0
                ? $"{kind}repairing {slots.Count} item(s) ({names}) for {cost:N0} of {gold:N0} gold"
                : $"{kind}repairing {slots.Count} of {slots.Count + skipped} ({names}) for " +
                  $"{cost:N0} of {gold:N0} gold; {skipped} left for a later trip";
        }

        /// <summary>The item types this page will buy. Empty means it buys nothing at all.</summary>
        private List<ItemType> AcceptedTypes()
        {
            List<ItemType> types = new List<ItemType>();
            if (_currentPage?.Types == null) return types;

            foreach (NPCType type in _currentPage.Types)
                types.Add(type.ItemType);

            return types;
        }

        /// <summary>
        /// A book worth buying: our class, castable at this level, not already known. Re-evaluated
        /// immediately before the purchase, because a bought book is Locked and NonRefinable and
        /// therefore unsellable if it turns out to be wrong.
        /// </summary>
        private bool WantedBook(ItemInfo info, WorldModel world) => WhyNotBook(info, world) == null;

        /// <summary>
        /// Why this book is not worth buying, or null when it is.
        ///
        /// Note the last two checks are different gates and routinely disagree. MagicInfo.NeedLevel1
        /// is the level needed to CAST the skill; the book ITEM carries its own RequiredType /
        /// RequiredAmount, which is what the server's CanUseItem enforces when the book is used. A
        /// character can be high enough to cast a skill and still too low to open the book that
        /// teaches it, so the trip routes to the seller and then correctly declines to buy.
        /// </summary>
        private string WhyNotBook(ItemInfo info, WorldModel world)
        {
            if (info == null) return "no item";
            if (info.ItemType != ItemType.Book) return "not a book";
            if (_books == null) return "no magic index";

            MagicInfo magic = _books.For(info);

            if (magic == null) return "no skill behind it";
            if (magic.School == MagicSchool.None || magic.School == MagicSchool.Discipline)
                return "not a combat school";
            if (!magic.MatchesClass(world.Class)) return "another class";
            if (magic.NeedLevel1 > world.Level) return $"needs level {magic.NeedLevel1} to cast";
            if (world.Knows(magic.Index)) return "already known";

            if (!Backpack.MeetsRequirement(info, world.Level, world.PlayerStats))
                return $"book needs {info.RequiredType} {info.RequiredAmount}";

            return null;
        }

        /// <summary>
        /// The best upgrade this shop sells that we can afford, or null.
        ///
        /// "Best" is the largest score gain over the slot it would replace, not the most expensive
        /// item - a shop full of high-level gear we cannot wear scores zero throughout. Three guards
        /// keep it from being a money pit: the class, gender and level requirements the server will
        /// enforce anyway; a gold reserve that repairs and potions get first call on; and a minimum
        /// improvement, so the bot does not spend its entire purse on a one percent gain.
        ///
        /// Only the definition is known for something on a shelf, so the score is a floor. That is
        /// the right way round: we may under-rate a shop item, never over-rate it.
        /// </summary>
        private NPCGood BestUpgradeOnPage(WorldModel world, Backpack items, out string why)
        {
            why = "";

            if (_currentPage?.Goods == null) { why = "page sells nothing"; return null; }

            long spendable = world.Gold - _config.GoldReserve;

            if (spendable <= 0)
            {
                why = $"only {world.Gold:N0} gold, reserve is {_config.GoldReserve:N0}";
                return null;
            }

            NPCGood best = null;
            int bestGain = 0;
            int considered = 0;

            foreach (NPCGood good in _currentPage.Goods)
            {
                ItemInfo info = good.Item;

                if (info == null) continue;
                if (_target?.NPC != null && good.GoodsIndex != _target.NPC.GoodsIndex) continue;
                if (info.Price <= 0 || info.Price > spendable) continue;

                int gain = items.UpgradeGain(info, world.Class, world.Gender, world.Level,
                    world.PlayerStats);

                if (gain <= 0) continue;

                considered++;

                // A tiny gain is not worth the gold; measured against what it replaces, so the bar
                // scales with how good the current item already is.
                int worn = items.WeakestWornScore(info.ItemType, world.Class);

                if (worn > 0 && gain * 100 < worn * _config.MinimumUpgradePercent) continue;

                if (gain <= bestGain) continue;

                bestGain = gain;
                best = good;
            }

            if (best != null)
            {
                why = $"+{bestGain} for {best.Item.Price:N0} gold, {world.Gold:N0} held";
                return best;
            }

            why = considered > 0
                ? $"{considered} wearable, none worth {_config.MinimumUpgradePercent}% more"
                : "nothing here we can wear and afford";

            return null;
        }

        /// <summary>
        /// The best potion this page sells: the largest one that both heals a worthwhile share of
        /// the health pool and can be bought in quantity without eating the gold reserve.
        ///
        /// FindGood picks the CHEAPEST match, which is right for town scrolls and wrong for
        /// potions - it left a level 16 warrior with a 422 health pool buying tier one potions that
        /// restore thirty, so a full stack healed less than two hits. Biggest first, dropping a tier
        /// whenever the stack would cost more than we can spare.
        /// </summary>
        /// <summary>
        /// Trim an order to what the purse will cover.
        ///
        /// The server refuses an unaffordable C.NPCBuy in SILENCE and refuses the WHOLE order, so
        /// asking for more than we can pay for does not get us a smaller stack - it gets us none,
        /// with no error and a bot that believes it restocked. A broke wizard was watched ordering
        /// 22 Life Pills with 914 gold, receiving nothing, and setting off for town again two
        /// minutes later with the same result: a restock loop that never restocks and never hunts.
        ///
        /// The tier was already chosen against a budget inside BestPotion; this is the quantity,
        /// which was not checked against anything at all.
        /// </summary>
        private static int Affordable(NPCGood good, int wanted, long gold)
        {
            if (good?.Item == null || wanted <= 0) return 0;

            long price = Math.Max(1, good.Item.Price);

            return (int)Math.Min(wanted, gold / price);
        }

        private NPCGood BestPotion(WorldModel world, Backpack items, bool healing)
        {
            if (_currentPage?.Goods == null) return null;

            List<NPCGood> candidates = new List<NPCGood>();

            foreach (NPCGood good in _currentPage.Goods)
            {
                if (good.Item == null) continue;
                if (_target?.NPC != null && good.GoodsIndex != _target.NPC.GoodsIndex) continue;
                if (healing ? !IsHealthPotion(good.Item) : !IsManaPotion(good.Item)) continue;

                candidates.Add(good);
            }

            if (candidates.Count == 0) return null;

            int Restores(NPCGood g) =>
                healing ? g.Item.Stats[Stat.Health] : g.Item.Stats[Stat.Mana];

            candidates.Sort((a, b) => Restores(b).CompareTo(Restores(a)));

            int want = healing
                ? _config.HealthPotionTarget(world.MaxBagWeight) - items.CountHealthPotions()
                : _config.ManaPotionTarget(world.MaxBagWeight) - items.CountManaPotions();

            if (want <= 0) want = 1;

            // Everything the character has, NOT gold minus the reserve.
            //
            // The reserve exists to stop optional gear shopping eating the survival budget - the
            // gear step checks it separately, which is the right place. Applying it here made the
            // reserve block the one purchase it is being held for: a wizard with 914 gold and a
            // 5,000 reserve had nothing "spendable" at all, so it could never buy a healing potion
            // while broke, which is exactly when it needs one.
            long spendable = Math.Max(0, world.Gold);

            // A potion that barely dents the pool is mostly bag weight; prefer one that does real
            // work, but never refuse the only thing on sale.
            int pool = healing ? world.MaxHealth : world.MaxMana;
            int meaningful = pool * _config.PotionHealPercentTarget / 100;

            NPCGood affordable = null;

            foreach (NPCGood good in candidates)
            {
                long cost = (long)good.Item.Price * want;

                if (cost > spendable) continue;

                affordable ??= good;

                if (Restores(good) >= meaningful) return good;
            }

            // Nothing reaches the target share: take the biggest we can actually pay for.
            return affordable ?? candidates[candidates.Count - 1];
        }

        private static bool IsManaPotion(ItemInfo info) =>
            info.ItemType == ItemType.Consumable &&
            info.Shape != Backpack.TownTeleportShape &&
            info.Stats[Stat.Health] <= 0 &&
            info.Stats[Stat.Mana] > 0;

        /// <summary>Every book on the current page and why each was passed over.</summary>
        private string DescribeBookGoods(WorldModel world)
        {
            if (_currentPage?.Goods == null) return "this page sells nothing";

            List<string> notes = new List<string>();
            int wrongIndex = 0;

            foreach (NPCGood good in _currentPage.Goods)
            {
                if (good.Item == null || good.Item.ItemType != ItemType.Book) continue;

                // FindGood also requires the goods index to match the NPC's own, or the server
                // silently ignores the purchase.
                if (_target?.NPC != null && good.GoodsIndex != _target.NPC.GoodsIndex)
                {
                    wrongIndex++;
                    continue;
                }

                string why = WhyNotBook(good.Item, world);
                if (why != null) notes.Add($"{good.Item.ItemName}: {why}");
            }

            if (wrongIndex > 0) notes.Add($"{wrongIndex} on another goods index");

            if (notes.Count == 0) return "no books on this page";

            return string.Join("; ", notes.Take(8));
        }

        private static bool IsTownScroll(ItemInfo info) =>
            info.ItemType == ItemType.Consumable && info.Shape == Backpack.TownTeleportShape;

        private static bool IsHealthPotion(ItemInfo info) =>
            info.ItemType == ItemType.Consumable &&
            info.Shape != Backpack.TownTeleportShape &&
            info.Stats[Stat.Health] > 0;

        /// <summary>
        /// The cheapest matching item this page stocks. GoodsIndex must match the NPC's own
        /// (PlayerObject.NPCBuy:10171) or the purchase is silently ignored.
        /// </summary>
        private NPCGood FindGood(Func<ItemInfo, bool> wanted)
        {
            if (_currentPage?.Goods == null) return null;

            NPCGood best = null;

            foreach (NPCGood good in _currentPage.Goods)
            {
                if (good.Item == null || !wanted(good.Item)) continue;
                if (_target?.NPC != null && good.GoodsIndex != _target.NPC.GoodsIndex) continue;

                if (best == null || good.Item.Price < best.Item.Price)
                    best = good;
            }

            return best;
        }
    }
}
