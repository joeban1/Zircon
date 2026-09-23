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
        private bool _boughtReagents;
        private bool _repaired;
        private bool _repairedSpecial;
        private bool _boughtBook;
        private bool _boughtGear;
        /// <summary>Banking reclaimed obsolete items after the vendor circuit; sell them before leaving.</summary>
        private bool _postBankSaleNeeded;
        private bool _postBankSaleActive;

        /// <summary>
        /// The book stop on this trip, while it is still AHEAD of us. Cleared on arrival, not on
        /// purchase - a seller with nothing we can afford must not hold the purse shut for the rest
        /// of the trip, which is the latch-gating-a-trigger shape this codebase keeps producing.
        /// </summary>
        private VendorEntry _bookSeller;

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

        public TownTrip(BotConfig config, VendorDirectory directory, MagicBooks books,
            SafeZoneDirectory safeZones)
        {
            _config = config;
            _directory = directory;
            _books = books;
            _safeZones = safeZones;
        }

        private readonly SafeZoneDirectory _safeZones;

        /// <summary>Which limit sent us to town, for the log.</summary>
        private string _tripReason = "";

        /// <summary>The trigger that actually started this trip. See where it is set.</summary>
        private string _startedBecause = "";

        /// <summary>Where we are walking to bank, while outside a safe zone.</summary>
        private Point _bankSpot = Point.Empty;
        private DateTime _bankWalkDeadline = DateTime.MinValue;

        /// <summary>Why the last sale was the size it was - an empty sale is not a failure.</summary>
        public string SellDiagnostic = "";

        /// <summary>Why a full bag did or did not send us to town.</summary>
        public string WeightDiagnostic = "";

        /// <summary>Why reagents were or were not bought.</summary>
        public string ReagentDiagnostic = "";

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

        /// <summary>
        /// A scroll was spent elsewhere - by the escape branch - while this trip was running.
        ///
        /// The trip is not over: the scroll took it to a town, which is where it was going. What
        /// is now worthless is the PLAN, because it was built for the map the bot has just left.
        /// Putting the trip into Teleporting hands it to the same replan-on-arrival path a
        /// deliberate scroll uses, rather than leaving it Travelling towards a vendor on another
        /// map - which is the exact bug the plan-after-landing rework existed to remove.
        /// </summary>
        public void ScrolledOut(WorldModel world, int scrollSlot)
        {
            if (!Active) return;

            ResetTripState();

            _mapBeforeTeleport = world.MapIndex;
            _teleportScrollSlot = scrollSlot;
            _scrollConsumed = false;
            Enter(TownPhase.Teleporting, "scrolled out of danger - replanning on arrival");
        }
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

            // Pressed while a trip is ALREADY walking, this means "you are stuck, get out of
            // there" - so the unstick scroll fires on the next tick instead of waiting out the
            // 25-second no-progress watchdog.
            //
            // The operator can see a bot is boxed in long before a timer can prove it, and until
            // now pressing the button again did nothing at all: Force on an active trip only
            // cleared a cooldown that was not holding anything up. Three presses in ten seconds
            // and a wizard carried on shuffling for another twenty.
            //
            // Still bounded by _scrolledToUnstick, so leaning on the button cannot burn a scroll
            // per press.
            if (Active && (Phase == TownPhase.Travelling || Phase == TownPhase.Returning))
                _unstickNow = true;
        }

        /// <summary>Set by Force while already travelling: skip the wait, scroll now.</summary>
        private bool _unstickNow;

        /// <summary>
        /// Go and try to repair, clearing the two latches that would otherwise refuse.
        ///
        /// Both latches exist for good reasons and neither is simply "the bill was too high".
        /// _brokenUrgencySpent is spent when a broken-gear trip COMMITS, or on an explicit refusal
        /// from the server, and it exists to stop the forty-three-second repair circuit this code
        /// has already had once. _noRepairerKnown is set only after establishing that no repairer
        /// exists ANYWHERE - hard-won evidence, gathered by walking.
        ///
        /// So the urgency latch is cleared outright, and the "nobody can repair this" latch is
        /// cleared only far enough to allow ONE more look. If that look finds nothing again the
        /// latch goes straight back on and RepairDiagnostic says so, rather than the button
        /// becoming a way to repeat a known-impossible errand on demand.
        /// </summary>
        public void ForceRepair()
        {
            _brokenUrgencySpent = false;

            if (_noRepairerKnown)
            {
                _noRepairerKnown = false;
                _repairRescan = true;
                RepairDiagnostic = "asked to look for a repairer again";
            }

            Force();
        }

        /// <summary>Set by ForceRepair: this trip gets one chance to find a repairer.</summary>
        private bool _repairRescan;
        public VendorEntry Target => _target;

        /// <summary>Asked for explicitly, from the status page. Not to be second-guessed.</summary>
        public bool Forced => _forced;

        /// <summary>
        /// Out of potions - health or mana - as judged by the trip trigger.
        ///
        /// Read by ConsiderTravel, which otherwise happily sends a bot that cannot heal to a
        /// hunting ground. That is how an assassin whose restock trip had just collapsed was
        /// dispatched to Banya Village with nine healing potions and killed crossing Bichon Town
        /// by forty-two monsters. The trip existed BECAUSE it was short; answering that by going
        /// hunting is worse than answering it slowly.
        /// </summary>
        public bool ShortOfSupplies { get; private set; }

        /// <summary>
        /// Published for cross-map travel when useful storage work exists but no town scroll is
        /// available. Storage itself needs only a safe zone; TownMaps are the reliable safe-zone
        /// destinations already known to the journey layer.
        /// </summary>
        public bool NeedsStorage { get; private set; }

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
            _sellOneAtATime = false;
            _potionGoldSpent = 0;      // the potion budget is per TRIP, not per purchase
            _scrolledToUnstick = false;
            _teleportScrollSlot = -1;
            _scrollConsumed = false;
            _unstickNow = false;
            _boughtScrolls = false;
            _boughtPotions = false;
            _boughtMana = false;
            _boughtTorch = false;
            _boughtReagents = false;
            _boughtGear = false;
            _postBankSaleNeeded = false;
            _postBankSaleActive = false;
            _boughtBook = false;
            _bookSeller = null;
            _repaired = false;
            _repairedSpecial = false;
            _repairPendingUntil = DateTime.MinValue;

            _bankSpot = Point.Empty;
            _bankWalkDeadline = DateTime.MinValue;
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

        public Decision Next(WorldModel world, Backpack items, bool itemUsePending)
        {
            // C.ItemUse has one delayed slot on the server. Do not let a town or unstick scroll
            // replace a potion/book that is still awaiting its slot-specific verdict.
            _scrollSlot = itemUsePending ? -1 : items.FindTownTeleportSlot();

            // Heavy is not the same as heavy WITH SOMETHING TO PUT DOWN.
            //
            // The old test read the scales alone, so a bag full of reserved potions, scrolls and
            // worn equipment counted as a reason to shop. It is not: the itinerary sells nothing,
            // the bot comes home exactly as heavy, and the cooldown starts the same lap again. Two
            // bots were found doing precisely that - a warrior at 242/242 twenty-four trips deep,
            // an assassin at 109/120 walking a ten-stop lap - both reporting "0 sellable" at every
            // single stop, both effectively unable to hunt at all.
            //
            // The condition that ends the loop is the same one that defines it: go only if selling
            // what we may sell would actually put us back under the line. Anything less repeats.
            // The other triggers below - slots, repair, supplies, the button - are untouched and
            // still bring the bot to town when a trip can genuinely help.
            bool heavy = world.MaxBagWeight > 0 &&
                         world.WeightPercent >= _config.TownAtWeightPercent;

            bool overweight = false;

            if (heavy)
            {
                int limit = world.MaxBagWeight * _config.TownAtWeightPercent / 100;
                int shed = world.BagWeight - limit + 1;

                int disposable = items.DisposableWeight(world.Class, world.Gender, world.Level,
                    world.PlayerStats, _config.HealthPotionTarget(world.MaxBagWeight),
                    _config.ManaPotionTarget(world.MaxBagWeight), _config.TownScrollReserve,
                    _books, world);

                overweight = disposable >= shed;

                WeightDiagnostic = overweight
                    ? $"{world.WeightPercent}% full, {disposable} weight to sell"
                    : $"{world.WeightPercent}% full but only {disposable} of {shed} weight is " +
                      "sellable - a trip could not lighten us, so not going";
            }
            else WeightDiagnostic = "";

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
                // Weight against weight. HealthPotionTarget is a WEIGHT budget and
                // CountHealthPotions is a COUNT, and comparing them assumes a potion weighs one.
                // A warrior carrying ten Elixir Of Life (V) - nineteen weight apiece, 44 weight of
                // an 82 budget, comfortably stocked - counted as ten against a floor of twenty and
                // declared itself short, which is a town trip it did not need and then could not
                // satisfy, because buying more would blow the budget it was already inside.
                int budget = _config.HealthPotionTarget(world.MaxBagWeight);
                int floor = Math.Max(1, budget * _config.RestockAtPotionPercent / 100);

                shortOfPotions = items.HealthPotionLoad() < floor;
            }

            // MANA potions count too, for anyone who spends mana.
            //
            // The trip triggers watched health potions, bag weight, free slots and broken gear -
            // and nothing at all watched mana. So a wizard that ran its pool dry had no reason to
            // go anywhere: forty-two healing potions in the bag, weight under the threshold, gear
            // fine. It meleed at 0 of 371 mana indefinitely, which is most of a wizard's damage
            // simply switched off, and every "the caster is out of mana again" observation this
            // session traces back here. Raising the mana budget let it CARRY more; only this
            // makes it go and buy more.
            //
            // Gated on actually spending mana, so a character with no costed magic never takes a
            // trip for potions it will not drink. That includes warriors: Half Moon costs mana
            // too, and a warrior with none is a warrior missing its attack skills.
            bool shortOfMana = false;

            if (_config.RestockAtPotionPercent > 0 && world.MaxBagWeight > 0 &&
                _config.ManaPotionWeightPercent > 0 && SpendsMana(world))
            {
                int manaBudget = _config.ManaPotionTarget(world.MaxBagWeight);
                int manaFloor = Math.Max(1, manaBudget * _config.RestockAtPotionPercent / 100);

                shortOfMana = items.ManaPotionLoad() < manaFloor;

                // A TRIP WE CANNOT PAY FOR IS NOT A TRIP, IT IS A WALK.
                //
                // Being short is only half a reason to go shopping; the other half is being able
                // to do something when we arrive. A level 24 wizard down to 75 gold bought three
                // Mana Potions - all its money would stretch to - against a budget of forty-one,
                // walked a hundred and eighty tiles back to its hunting ground, ran dry, and
                // triggered the identical trip again. It spent its afternoon walking.
                //
                // The same shape as the repair deadlock above, which already says it plainly: a
                // repair we cannot pay for does not fix the break. So the trigger now asks
                // whether the shop can actually help, and when it cannot the bot stays out and
                // fights - Kite already drops a caster to melee when it has no mana, so a wizard
                // with an empty purse is a poor melee character rather than a stranded one, and
                // killing things is how it earns the gold that makes the trip worth taking.
                //
                // Deliberately scoped to AFFORDABILITY alone. A caster with money in a cave is
                // untouched: it is short, it can pay, it goes. Only the broke case is suppressed.
                if (shortOfMana && !CanAffordAnyMana(world))
                {
                    shortOfMana = false;

                    SupplyDiagnostic =
                        $"short of mana potions but only {world.Gold:N0} gold - staying out to " +
                        $"earn rather than walking to a shop we cannot buy from";
                }
            }

            shortOfPotions = shortOfPotions || shortOfMana;

            // Published so the TRAVEL layer can see it. The trip itself can only walk to vendors
            // on the map it is standing on; choosing a different map is the journey's job, and
            // until now the journey had no idea the bot was out of supplies.
            ShortOfSupplies = shortOfPotions;

            bool bankedReady = items.StorageReclaims(world.Class, world.Gender, world.Level,
                world.PlayerStats, _books, world).Count > 0 || items.HasCompletableParts();
            NeedsStorage = bankedReady;

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
                // Out of SLOTS rather than out of carrying capacity. These are separate limits
                // and only the weight one was ever checked: a bag of rings and crafting drops
                // reaches 48 of 48 slots at a third of the weight cap, and a bot in that state
                // silently stops looting - every drop it walks over is refused by the server and
                // nothing in the bot notices, because the scales still say there is room.
                //
                // Deliberately NOT urgent. A bag full of things no vendor here buys is a problem
                // the trip cannot fix, and letting it bypass the cooldown would be the repair-loop
                // bug again in a different costume.
                bool outOfSlots = items.FreeSlotCount <= Math.Max(0, _config.TownAtFreeSlots);

                // Levelling can turn a correctly banked book into an immediately useful one, and
                // enough item parts can accumulate while the bot is out hunting. Neither used to
                // count as a reason to visit storage, so both waited for an unrelated town trip.
                bool reason = overweight || outOfSlots || repairable || shortOfPotions ||
                              bankedReady || _forced;

                // SAY WHICH ONE, and say it here where the answer is actually known.
                //
                // _tripReason used to be composed further down from only three cases, with
                // "bag N% full" as the CATCH-ALL - so a trip taken because a caster was out of
                // mana reported "bag 48% full", on a bag nowhere near any limit. The comment
                // beside that line warns against exactly this kind of misleading log and then
                // commits it. Whoever reads the log next should not have to cross-reference the
                // travel line to find out why the bot went to town.
                _startedBecause =
                    _forced ? "asked to"
                    : bankedReady ? "banked item ready to use"
                    : repairable ? "gear needs repair"
                    : shortOfPotions ? "short of potions"
                    : outOfSlots ? $"{items.InventoryCount}/{Globals.InventorySize} slots used"
                    : overweight ? $"bag {world.WeightPercent}% full"
                    : "";
                bool urgent = brokenUrgent || supplyUrgent || _forced;

                if (!reason) return null;
                if (OnCooldown && !urgent) return null;

                bool forced = _forced;
                _forced = false;

                return Begin(world, items, brokenUrgent || forced, supplyUrgent, bankedReady);
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

                    // Remember a scroll that did nothing, so the next trip from this map does
                    // not spend another one on the same silence.
                    //
                    // ONLY when the server actually ACCEPTED the scroll. A refused use means "not
                    // now" - the one-second item cooldown, most often - and treating that as "town
                    // scrolls do not work on this map" is a permanent sentence passed on a
                    // temporary condition. It locked a level 25 wizard holding TWELVE scrolls out
                    // of ever leaving Bichon Cave: every later trip reported "no town scroll to
                    // reach one" while the scrolls sat in its bag.
                    //
                    // ScrollWasConsumed is the server's own answer, via S.ItemChanged.
                    if (!landed && _scrollConsumed) _scrollFailedHere = world.MapIndex;
                    else if (!landed)
                        SupplyDiagnostic = "the scroll was refused rather than wasted - this map " +
                                           "is not being marked, we will simply try again";

                    Abort(landed
                        ? $"landed on {world.MapName} with nothing to do there"
                        : "the scroll did not move us - walking out instead");
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

                        // Now that the flag only clears on confirmation, an unlock the server
                        // declines leaves the slot locked and it would be asked about again on the
                        // very next tick, for ever. A refusal must cost something, so three goes
                        // and the slot is left alone for a while.
                        locked.RemoveAll(slot => UnlockGivenUp(slot));

                        if (locked.Count > 0)
                        {
                            int slot = locked[0];

                            _unlockTries.TryGetValue(slot, out int tries);
                            _unlockTries[slot] = tries + 1;

                            if (tries + 1 >= UnlockAttemptLimit)
                            {
                                _sellRefused[slot] = DateTime.UtcNow + TimeSpan.FromMinutes(10);

                                SellDiagnostic =
                                    $"unlock refused for {items.InSlot(slot)?.Info?.ItemName} " +
                                    $"after {UnlockAttemptLimit} tries - the server is not " +
                                    "answering, so it stays locked and is not offered for sale";
                            }

                            _sold = false;      // the sell list changes once these are free
                            return new Decision
                            {
                                Action = BotAction.Unlock,
                                Reason = $"unlocking {items.InSlot(slot)?.Info?.ItemName}",
                                Subject = "unlocking",
                                FromSlot = slot
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
                                _config.TownScrollReserve, _books, world,
                                out Dictionary<int, long> partial)
                            .Where(slot => Backpack.Sellable(items.InSlot(slot), accepted))
                            .Where(slot => !SellRefusedRecently(slot))
                            .ToList();

                        // One at a time once the server has objected to anything this trip.
                        if (_sellOneAtATime && slots.Count > 1) slots = slots.Take(1).ToList();

                        // Say what is actually being compared, in one unit.
                        //
                        // This used to read "(64 health potions, 3 scrolls held)", where "held"
                        // sounded like the reserve refusing to release them and the number was
                        // simply how many were carried. It sent two separate investigations after
                        // the Locked flag - which has a whole working unlock path and was innocent
                        // both times - when the answer was that the potion budget was half the bag
                        // and nothing was over it. The budget is a WEIGHT, so show a weight.
                        // MANA IS REPORTED TOO.
                        //
                        // This line named health and scrolls and said nothing whatever about mana,
                        // so a wizard that had quietly stopped restocking looked healthy in every
                        // log line it produced: "44 health potions (load 44 of budget 44); 3
                        // scrolls" while it sat on four mana potions against a budget of thirty-six
                        // and cast nothing. The whole reason mana problems keep having to be
                        // reconstructed from inventory dumps is that no diagnostic ever mentions it.
                        SellDiagnostic = $"{slots.Count} sellable of {items.InventoryCount} slots " +
                                         $"(carrying {items.CountHealthPotions()} health potions " +
                                         $"(load {items.HealthPotionLoad()} of budget " +
                                         $"{_config.HealthPotionTarget(world.MaxBagWeight)}); " +
                                         $"{items.CountManaPotions()} mana potions " +
                                         $"(load {items.ManaPotionLoad()} of budget " +
                                         $"{_config.ManaPotionTarget(world.MaxBagWeight)}); " +
                                         $"{items.CountTownScrolls()} scrolls)";

                        // Re-ask next tick while singles are still being worked through, or the
                        // trip would offer one slot per VENDOR instead of one per tick.
                        if (_sellOneAtATime && slots.Count > 0) _sold = false;

                        if (slots.Count > 0)
                            return new Decision
                            {
                                Action = BotAction.NPCSell,
                                Reason = $"{slots.Count} slots to {_target.NPC.NPCName}",
                                SellSlots = slots,
                                SellCounts = partial
                            };
                    }

                    if (_postBankSaleActive)
                    {
                        // This second circuit exists solely to sell what banking withdrew.
                        // Buying again here would turn cleanup into another full shopping trip.
                        if (_itinerary.Count > 0)
                        {
                            _target = _itinerary.Dequeue();
                            Enter(TownPhase.Travelling, $"next reclaimed-item buyer: {_target.NPC.NPCName}");
                            return new Decision { Action = BotAction.NPCClose,
                                Reason = $"sold reclaimed items, on to {_target.NPC.NPCName}" };
                        }

                        _postBankSaleActive = false;
                        Enter(TownPhase.Banking, "reclaimed-item sale complete");
                        return new Decision { Action = BotAction.NPCClose,
                            Reason = "reclaimed-item sale complete" };
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
                        world.Level >= _config.SpecialRepairMinimumLevel &&
                        _currentPage?.DialogType == NPCDialogType.Repair)
                    {
                        _repairedSpecial = true;

                        List<int> special = items.AffordableRepairSlots(
                            items.SpecialRepairSlots(AcceptedTypes()), world.Gold, true,
                            out long specialCost, out int specialSkipped);

                        // SPECIAL REPAIR MUST NOT CREATE POVERTY.
                        //
                        // It costs twice as much as ordinary repair. Jill reached Dakota with
                        // 10,587 gold and special-repaired three accessories for 8,602, crossing
                        // both the 10,000 PoorGold line and the 5,000 RecoveryGold line in one
                        // request. The recovery override then prevented the very book hunt she
                        // needed. Gear shopping already protects GoldReserve; maintenance needs a
                        // hard floor too, and RecoveryGold is the honest one because crossing it
                        // changes the bot's entire hunting policy.
                        //
                        // Skip the WHOLE special batch when it would cross that floor. Do not take
                        // a smaller special subset: the ordinary pass immediately below is the
                        // requested fallback and may be able to repair every item for less than the
                        // proposed special subset.
                        long specialFloor = Math.Max(0, _config.RecoveryGold);
                        bool specialWouldCrossRecovery = special.Count > 0 &&
                            specialCost > Math.Max(0, world.Gold - specialFloor);

                        if (specialWouldCrossRecovery)
                        {
                            RepairDiagnostic = $"skipping {special.Count} special repair(s) " +
                                $"costing {specialCost:N0}: {world.Gold:N0} gold must not fall " +
                                $"below recovery floor {specialFloor:N0}; trying ordinary repair";
                            special.Clear();
                        }

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
                        int needed = RoomFor(world, items, good, true);

                        int wanted = Affordable(good, needed, PotionGold(world));

                        if (good != null && needed > 0 && wanted <= 0)
                            SupplyRefused($"cannot afford {good.Item.ItemName} at " +
                                          $"{CostOf(good)} each with {world.Gold} gold");

                        if (good != null && wanted > 0)
                        {
                            NotePotionSpend(good, wanted);

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

                        // Affordable is not the same as worth affording. A torch must leave
                        // enough behind to still buy a potion, or the light is being paid for
                        // with the bot's life - see TorchGoldFloor.
                        if (good != null &&
                            world.Gold - CostOf(good) >= _config.TorchGoldFloor)
                            return new Decision
                            {
                                Action = BotAction.NPCBuy,
                                Reason = $"1 x {good.Item.ItemName}",
                                BuyIndex = good.Index,
                                BuyAmount = 1
                            };

                        if (good != null)
                            SupplyDiagnostic = $"not replacing the torch at {CostOf(good)} " +
                                               $"with {world.Gold:N0} gold - keeping " +
                                               $"{_config.TorchGoldFloor:N0} back for potions";
                    }

                    if (!_boughtMana && _config.ManaPotionWeightPercent > 0)
                    {
                        _boughtMana = true;

                        NPCGood good = BestPotion(world, items, false);

                        int room = good == null ? 0 : RoomFor(world, items, good, false);
                        int wanted = good == null ? 0 : Affordable(good, room, PotionGold(world));

                        // EVERY other supply branch explains itself; this one returned silently.
                        //
                        // Three separate investigations into "the caster has no mana" had to be
                        // done by reading inventory dumps and re-deriving the budget arithmetic by
                        // hand, because the one place that knows the answer threw it away. There
                        // are three distinct ways to buy nothing here and they need telling apart:
                        // the shop has none, the bag has no room, or the purse cannot reach it.
                        if (wanted <= 0)
                            SupplyDiagnostic = good == null
                                ? $"no mana potion on this page ({_currentPage?.Goods?.Count ?? 0} goods)"
                                : $"not buying {good.Item.ItemName}: room {room} " +
                                  $"(load {items.ManaPotionLoad()} of budget " +
                                  $"{_config.ManaPotionTarget(world.MaxBagWeight)}), " +
                                  $"budget {PotionGold(world):N0} gold at {CostOf(good)} each";

                        if (good != null && wanted > 0)
                        {
                            NotePotionSpend(good, wanted);

                            return new Decision
                            {
                                Action = BotAction.NPCBuy,
                                Reason = $"{wanted} x {good.Item.ItemName}",
                                BuyIndex = good.Index,
                                BuyAmount = wanted
                            };
                        }
                    }

                    // Reagents: ammunition for the spells this character actually knows.
                    //
                    // Bought before books, because a spell we already own and cannot fire is worth
                    // more than one we do not own yet. Only for characters whose magics consume
                    // them, so a warrior never carries any.
                    if (!_boughtReagents && _config.BuyReagents)
                    {
                        _boughtReagents = true;

                        ItemType needed = ReagentNeeded(world, items);

                        if (needed != ItemType.Nothing)
                        {
                            NPCGood good = CheapestOfType(needed);

                            // Budgeted, not merely affordable. Buying every reagent the purse
                            // can stretch to is how a bot arrives at the stock target in one
                            // purchase and leaves itself unable to buy a potion - see
                            // ReagentGoldPercent. The target is still the target; this only
                            // paces the approach to it.
                            long reagentBudget = _config.ReagentGoldPercent > 0
                                ? world.Gold * _config.ReagentGoldPercent / 100
                                : world.Gold;

                            int wanted = good == null ? 0
                                : Affordable(good, _config.ReagentReserve - items.CountReagent(needed),
                                      reagentBudget);

                            if (good != null && wanted > 0)
                            {
                                ReagentDiagnostic = $"{wanted} x {good.Item.ItemName} " +
                                                    $"at {_target?.NPC?.NPCName}";

                                return new Decision
                                {
                                    Action = BotAction.NPCBuy,
                                    Reason = $"{wanted} x {good.Item.ItemName}",
                                    BuyIndex = good.Index,
                                    BuyAmount = wanted
                                };
                            }

                            ReagentDiagnostic = good == null
                                ? $"no {needed} on sale at {_target?.NPC?.NPCName}"
                                : $"cannot afford {needed} at {_target?.NPC?.NPCName}";
                        }
                    }

                    // Books only once potions AND scrolls are at their reserves - evaluated NOW,
                    // after the restock steps above have run, not from the values at Begin.
                    //
                    // Every branch here records why, because the whole step is silent otherwise:
                    // the bot walks to the book seller, opens the page, buys nothing and closes,
                    // which is indistinguishable in the log from the step never running at all.
                    // We are standing at the book stop, so it is no longer "still to come"
                    // whatever happens next. Cleared here rather than on a purchase so that a
                    // seller with nothing affordable cannot keep gear locked out for ever.
                    if (_bookSeller != null && _target == _bookSeller) _bookSeller = null;

                    if (!_boughtBook && _config.BuyBooks)
                    {
                        int potions = items.CountHealthPotions();
                        int scrolls = items.CountTownScrolls();

                        // Judged on whether we are SAFE, not on whether we are perfectly stocked.
                        //
                        // Demanding the full reserve looked equivalent and was not. The bot buys up
                        // to the reserve early in the trip and then drinks on the way round, so it
                        // arrives at the book seller one short of its own target and books are
                        // refused - every trip, for ever. A level 14 Taoist sat at
                        // "restock first (9/10 potions, 3/3 scrolls)" moments after buying ten.
                        //
                        // The gate is meant to stop books eating the survival budget, so it asks
                        // the survival question: are we above the floor that would send us back to
                        // town anyway? That floor is computed here exactly as the trip trigger
                        // computes it (see shortOfPotions above), so the two can never disagree
                        // about what "short of potions" means. Scrolls stay absolute - one is the
                        // way home - so those still need the full reserve.
                        // Weight, for the same reason as shortOfPotions above - and measured the
                        // same way, so the two can never disagree about "short of potions".
                        int potionFloor = _config.RestockAtPotionPercent > 0 && world.MaxBagWeight > 0
                            ? Math.Max(1, _config.HealthPotionTarget(world.MaxBagWeight) *
                                          _config.RestockAtPotionPercent / 100)
                            : _config.HealthPotionReserve;

                        int potionWeight = items.HealthPotionLoad();

                        // ONE scroll, not the full reserve.
                        //
                        // The potion half of this gate was already fixed to ask the survival
                        // question rather than the target question. The scroll half was left
                        // absolute and has the identical fault: scrolls are bought partway round
                        // the route, so at every book seller visited BEFORE the scroll vendor the
                        // reserve is short and books are refused - every trip, for ever. Sindo was
                        // refused Full Bloom at Joeban and David on "1/3 scrolls" at 09:49, then
                        // bought two scrolls at 09:50 with both sellers already behind it.
                        //
                        // What this gate protects is the way home, and one scroll is a way home -
                        // as the comment above says in as many words.
                        if (potionWeight < potionFloor || scrolls < 1)
                        {
                            BookDiagnostic =
                                $"not buying at {_target?.NPC?.NPCName}: restock first " +
                                $"({potions} potions, load {potionWeight}, " +
                                $"need {potionFloor}; " +
                                $"{scrolls}/{_config.TownScrollReserve} scrolls)";
                        }
                        else
                        {
                            NPCGood good = FindGood(info => WantedBook(info, world));

                            // Priced before it is asked for. An unaffordable C.NPCBuy is refused
                            // whole, with nothing but a chat line the bot does not read - a level
                            // 14 Taoist asked for a Heal book with 1,114 gold against a 1,710 price
                            // and the only trace was the server saying "You need another -596 Gold"
                            // into a channel nothing consumes. The bot has the price in NPCGood, so
                            // there is no reason to find out from the server.
                            //
                            // Checked against raw gold rather than GoldReserve on purpose: the
                            // restock gate above has already secured potions and scrolls, so the
                            // survival spend is done, and a caster's first spell book is worth more
                            // than the reserve it would otherwise sit behind.
                            if (good != null && Affordable(good, 1, world.Gold) < 1)
                            {
                                BookDiagnostic =
                                    $"{good.Item.ItemName} at {_target?.NPC?.NPCName} costs " +
                                    $"{CostOf(good):N0} and we have {world.Gold:N0}";
                            }
                            else if (good != null)
                            {
                                // Latched only on a purchase we can actually pay for. Setting it
                                // merely because the step RAN meant the first vendor past the
                                // restock gate consumed the trip's one book - and on a
                                // Joeban/Lennard/Isaac itinerary that was Lennard, who sells no
                                // books, so the block was already marked done by the time the bot
                                // reached the real seller. Latching on an UNAFFORDABLE one has the
                                // same effect, which is why the affordability test sits above this.
                                _boughtBook = true;

                                return new Decision
                                {
                                    Action = BotAction.NPCBuy,
                                    Reason = $"1 x {good.Item.ItemName}",
                                    BuyIndex = good.Index,
                                    BuyAmount = 1,    // one per trip: a bought book is Locked and
                                    CapitalPurchase = true,
                                    BuyItemIndex = good.Item.Index
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
                    // Gear waits while a book stop is still ahead of us.
                    //
                    // Books are checked before gear at each vendor, but that is per-vendor and the
                    // damage is done across the itinerary: a level 14 Taoist with NO SPELLS AT ALL
                    // walked Hardy -> Haylee -> Flora -> Melisa -> ... -> Gresham, bought potions
                    // and a Scimitar on the way, and reached the one NPC selling skill books with
                    // 333 gold against a 5,000 reserve. She had had 10,554 when the trip began.
                    //
                    // A spell is worth more than a marginal weapon to a character that cannot cast,
                    // and the book seller is the stop most likely to be last, so the purse is held
                    // until that stop has had its turn.
                    // RESERVE THE BOOK'S PRICE, NOT THE WHOLE PURSE.
                    //
                    // Holding everything back until the book stop "has had its turn" became
                    // holding everything back FOR EVER, because the itinerary deliberately queues
                    // the book seller LAST - for the same spending-priority reason. Two individually
                    // correct rules deadlocked: a level 16 warrior with 46,000 gold walked past the
                    // armour seller on every trip, reached the book seller at the end, and the trip
                    // finished. It wore its level-1 starter outfit the whole time, and said nothing,
                    // because the diagnostic is identical at every stop and the log only writes on
                    // change.
                    //
                    // Reserving the BOOK'S cost instead protects exactly what the rule was written
                    // to protect, and leaves everything above it free to spend.
                    //
                    // AND ONLY WHEN THE BOOK IS ACTUALLY REACHABLE. Potion Mastery costs 2,500,000
                    // at the vendor; reserving that would re-create the deadlock in a form no
                    // low-level character could ever escape. A book we cannot afford on this trip
                    // is not a claim on this trip's gold.
                    long bookHold = _bookSeller != null && _bookReserve > 0 &&
                                    world.Gold - _config.GoldReserve >= _bookReserve
                        ? _bookReserve
                        : 0;

                    if (!_boughtGear && _config.BuyGear && bookHold > 0 &&
                        world.Gold - _config.GoldReserve - bookHold <= 0)
                    {
                        GearDiagnostic =
                            $"holding {bookHold:N0} for {_bookSeller.NPC?.NPCName}, who sells a " +
                            $"book we want, and {world.Gold:N0} leaves nothing over";
                    }
                    else if (!_boughtGear && _config.BuyGear)
                    {
                        _boughtGear = true;

                        NPCGood good = BestUpgradeOnPage(world, items, bookHold, out string why);

                        if (good != null)
                        {
                            GearDiagnostic = $"buying {good.Item.ItemName} at {_target?.NPC?.NPCName} - {why}";

                            return new Decision
                            {
                                Action = BotAction.NPCBuy,
                                Reason = $"1 x {good.Item.ItemName}",
                                BuyIndex = good.Index,
                                BuyAmount = 1,
                                CapitalPurchase = true,
                                BuyItemIndex = good.Item.Index
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
                    // Outside the safe zone, so walk into it rather than giving up.
                    //
                    // This used to skip banking entirely on the assumption that vendors stand in
                    // safe zones. They do not: a trip ending at Henry in Banya Village finishes
                    // outside the zone, and across an entire log the bots had made ZERO Deposit
                    // and ZERO Withdraw decisions. Because DisposableSlots deliberately refuses to
                    // sell anything WorthStoring - on the promise it will be banked instead - the
                    // unkept promise meant that gear accumulated in the bag permanently.
                    if (!world.InSafeZone)
                    {
                        if (_bankWalkDeadline == DateTime.MinValue)
                        {
                            _bankSpot = _safeZones?.Nearest(world.MapIndex, world.Location)
                                        ?? Point.Empty;
                            _bankWalkDeadline = DateTime.UtcNow.AddSeconds(
                                Math.Max(5, _config.BankWalkSeconds));
                        }

                        if (_bankSpot == Point.Empty)
                        {
                            BankDiagnostic = "no safe zone on this map to bank in";
                            Enter(TownPhase.Returning, "nowhere to bank");
                            return null;
                        }

                        if (DateTime.UtcNow > _bankWalkDeadline)
                        {
                            BankDiagnostic = $"could not reach the safe zone at " +
                                             $"{_bankSpot.X},{_bankSpot.Y} in time";
                            Enter(TownPhase.Returning, "gave up walking to the bank");
                            return null;
                        }

                        int away = WorldModel.Distance(world.Location, _bankSpot);

                        BankDiagnostic = $"walking {away} tiles to the safe zone to bank";

                        return new Decision
                        {
                            Action = BotAction.WalkTo,
                            Reason = $"safe zone to bank ({away} tiles)",
                            Subject = "walking to the bank",
                            Destination = _bankSpot
                        };
                    }

                    // Arrived. Restart the phase clock, or the walk we just made would count
                    // against the twenty seconds banking itself is allowed.
                    if (_bankWalkDeadline != DateTime.MinValue)
                    {
                        _bankWalkDeadline = DateTime.MinValue;
                        _bankSpot = Point.Empty;
                        Enter(TownPhase.Banking, "reached the safe zone");
                    }

                    if (StuckIn(TimeSpan.FromSeconds(20)))
                    {
                        if (_postBankSaleNeeded && PlanPostBankSale(world, items)) return null;
                        Enter(TownPhase.Returning, "banking done");
                        return null;
                    }

                    // A complete stack already in the bag came from PartsStorage on the preceding
                    // tick. Use it now; the finished item is gained into the bag and the normal
                    // equip scorer handles it.
                    KeyValuePair<int, ClientUserItem>? carriedPart = items.CompletableCarriedPart();
                    if (carriedPart != null)
                    {
                        ItemInfo target = Backpack.PartTarget(carriedPart.Value.Value);
                        BankDiagnostic = $"assembling {target?.ItemName ?? "item"} from parts";
                        return new Decision
                        {
                            Action = BotAction.AssemblePart,
                            Reason = $"{target?.ItemName ?? "item"}: complete part stack",
                            Subject = target?.ItemName ?? "item parts",
                            PotionSlot = carriedPart.Value.Key
                        };
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

                            if (take.Why.Contains("selling", StringComparison.OrdinalIgnoreCase))
                                _postBankSaleNeeded = true;

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

                    // Withdraw a completed part stack. Assembly happens on the next tick through
                    // the existing inventory ItemUse path, whose server verdict updates counts.
                    KeyValuePair<int, ClientUserItem>? completePart = items.CompletableStoredPart();
                    if (completePart != null)
                    {
                        int free = items.FirstFreeInventorySlot();
                        ItemInfo target = Backpack.PartTarget(completePart.Value.Value);

                        if (free < 0)
                        {
                            BankDiagnostic = $"{target?.ItemName ?? "part item"} is complete but " +
                                             "the bag is full";
                        }
                        else
                        {
                            BankDiagnostic = $"withdrawing complete {target?.ItemName ?? "part item"} stack";
                            return new Decision
                            {
                                Action = BotAction.Withdraw,
                                Reason = $"assemble {target?.ItemName ?? "part item"}",
                                Subject = target?.ItemName ?? "item parts",
                                FromSlot = completePart.Value.Key,
                                ToSlot = free,
                                PartsGrid = true
                            };
                        }
                    }

                    // A total spread over several slots cannot be assembled. Consolidate one
                    // compatible pair per tick, applying it locally only after S.ItemMove succeeds.
                    (int From, int To)? partMerge = items.PendingPartMerge();
                    if (partMerge != null)
                    {
                        BankDiagnostic = $"merging parts slots {partMerge.Value.From} -> " +
                                         $"{partMerge.Value.To}";
                        return new Decision
                        {
                            Action = BotAction.MergeParts,
                            Reason = "consolidating item parts for assembly",
                            Subject = "item parts",
                            FromSlot = partMerge.Value.From,
                            ToSlot = partMerge.Value.To,
                            PartsGrid = true,
                            MergeItem = true
                        };
                    }

                    // Stow what we cannot use yet but will.
                    foreach (KeyValuePair<int, ClientUserItem> carried in items.Carried.ToList())
                    {
                        if (!items.ShouldBank(carried.Value, world.Class, world.Gender,
                                world.Level, world.PlayerStats, _books, world)) continue;

                        // Parts go to their own grid. The server keeps PartsStorage separate from
                        // Storage and refuses a mismatch without a word, which is why every part
                        // deposit had silently failed while the bot recorded it as done.
                        bool parts = Backpack.StoresInPartsGrid(carried.Value);

                        int merge = parts
                            ? items.MergeablePartsStorageSlot(carried.Value)
                            : -1;
                        int free = parts && merge >= 0
                            ? merge
                            : parts
                            ? items.FirstFreePartsStorageSlot(_config.StorageSize)
                            : items.FirstFreeStorageSlot(_config.StorageSize);

                        if (free < 0)
                        {
                            BankDiagnostic = (parts ? "parts storage" : "storage") +
                                             $" is full at {_config.StorageSize} slots";
                            break;
                        }

                        BankDiagnostic = $"storing {carried.Value.Info.ItemName} for later" +
                                         (parts ? " (parts grid)" : "");

                        return new Decision
                        {
                            Action = BotAction.Deposit,
                            Reason = $"{carried.Value.Info.ItemName} for later",
                            Subject = carried.Value.Info.ItemName,
                            FromSlot = carried.Key,
                            ToSlot = free,
                            PartsGrid = parts,
                            MergeItem = parts && merge >= 0
                        };
                    }

                    if (string.IsNullOrEmpty(BankDiagnostic))
                        BankDiagnostic = $"nothing to move: {items.StoredCount} stored, " +
                                         "none usable yet and nothing worth storing";

                    if (_postBankSaleNeeded && PlanPostBankSale(world, items)) return null;

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
        /// Banking follows shopping, so obsolete withdrawals need one last seller visit. This is
        /// sell-only: do not re-run the full shopping plan and buy another round of supplies.
        /// </summary>
        private bool PlanPostBankSale(WorldModel world, Backpack items)
        {
            _postBankSaleNeeded = false;
            _itinerary.Clear();

            List<ItemType> uncovered = items
                .DisposableSlots(world.Class, world.Gender, world.Level, world.PlayerStats,
                    _config.HealthPotionTarget(world.MaxBagWeight),
                    _config.ManaPotionTarget(world.MaxBagWeight),
                    _config.TownScrollReserve, _books, world)
                .Select(slot => items.InSlot(slot)?.Info)
                .Where(info => info != null)
                .Select(info => info.ItemType)
                .Distinct()
                .ToList();

            while (uncovered.Count > 0)
            {
                VendorEntry buyer = _directory.BestBuyerFor(uncovered, world.MapIndex,
                    world.MapIndex);
                if (buyer == null) break;
                int before = uncovered.Count;
                uncovered.RemoveAll(buyer.Buys.Contains);
                if (uncovered.Count == before) break;
                if (!_itinerary.Contains(buyer)) _itinerary.Enqueue(buyer);
            }

            if (_itinerary.Count == 0)
            {
                BankDiagnostic = "reclaimed items need selling but no buyer exists on this map";
                return false;
            }

            _target = _itinerary.Dequeue();
            _postBankSaleActive = true;
            _currentPage = null;
            _buttonPath.Clear();
            _sold = false;
            Enter(TownPhase.Travelling, "selling reclaimed gear at " + _target.NPC.NPCName);
            BankDiagnostic = "returning to a vendor to sell reclaimed obsolete gear";
            return true;
        }

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
            // LOAD, not count. The target is a weight budget, so comparing it with a count is
            // only ever right when potions weigh one apiece - true of Healing Potion (II) and of
            // nothing above it. The same confusion has now been fixed in seven other places.
            bool needPotions = items.HealthPotionLoad() <
                               _config.HealthPotionTarget(world.MaxBagWeight);

            // MANA GETS A STOP OF ITS OWN.
            //
            // needPotions asked about health and nothing else, so a character full of healing
            // potions and empty of mana had no reason to route anywhere that sells mana - and the
            // buy step only ever sees the page that happens to be open. Wizzler toured Linda, Amy
            // and Mr. Kang with four mana potions against a budget of thirty-six and bought none,
            // because none of them stock any and nothing had asked for one that does.
            //
            // Gated on SpendsMana so a warrior with no costed magic never adds the stop.
            bool needMana = _config.ManaPotionWeightPercent > 0 && SpendsMana(world) &&
                            items.ManaPotionLoad() <
                            _config.ManaPotionTarget(world.MaxBagWeight);

            List<VendorEntry> restockers = _directory.BestRestockersFor(needScrolls, needPotions,
                mapIndex, _itinerary, world.Location, true, needMana);

            // Selling comes first in the itinerary so the gold is in hand before anything is
            // bought, and the extra buyers follow the primary for the same reason.
            if (buyer != null) _itinerary.Enqueue(buyer);

            foreach (VendorEntry extra in extraBuyers)
                if (!_itinerary.Contains(extra)) _itinerary.Enqueue(extra);

            // A reagent seller, if this character's spells eat reagents and we are short.
            //
            // Routed rather than merely bought-if-present. The buy step only ever sees the page
            // that happens to be open, so without a stop of its own a Taoist can walk an entire
            // itinerary past no poison vendor and carry on casting Poison Dust into an empty
            // equipment slot - the server consumes nothing, reports nothing, and the spell simply
            // does not happen.
            ItemType reagent = ReagentNeeded(world, items);

            if (_config.BuyReagents && reagent != ItemType.Nothing)
            {
                VendorEntry seller = _directory.BestSellerOf(reagent, mapIndex);

                if (seller != null && !_itinerary.Contains(seller)) _itinerary.Enqueue(seller);

                ReagentDiagnostic = seller == null
                    ? $"short of {reagent} and nobody here sells it"
                    : $"short of {reagent}, calling at {seller.NPC?.NPCName}";
            }

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
                    // ONE REPAIRER PER KIND OF GEAR, not one repairer overall - exactly the fix
                    // the gear shopping below already carries, and for exactly the same reason.
                    //
                    // BestRepairerFor returns the single NPC covering the MOST damaged types and
                    // says nothing about the rest, so a town that splits repair between several
                    // NPCs left everything outside that one NPC's trade permanently unrepaired.
                    // Joeban came home from a trip with its accessories repaired and its helmet
                    // still at 1 of 8, trip after trip, because whoever mends accessories in
                    // Bichon does not mend helmets.
                    //
                    // The per-STOP latch for this was fixed once already, which is what makes the
                    // gap easy to miss: the bot was perfectly willing to repair at a second NPC,
                    // it was simply never routed to one.
                    VendorEntry repairer = _directory.BestRepairerFor(damaged, mapIndex, mapIndex);

                    if (repairer != null)
                    {
                        List<ItemType> stillDamaged = new List<ItemType>(damaged);

                        while (stillDamaged.Count > 0)
                        {
                            VendorEntry next = _directory.BestRepairerFor(stillDamaged, mapIndex,
                                mapIndex);

                            if (next == null) break;

                            if (!_itinerary.Contains(next)) _itinerary.Enqueue(next);

                            // Whatever this one mends is dealt with; loop for the remainder. The
                            // selector always covers at least one damaged type or returns null,
                            // so this terminates.
                            stillDamaged.RemoveAll(next.Repairs.Contains);
                        }
                    }
                    else if (items.AnyBrokenEquipment() &&
                             _directory.BestRepairerFor(damaged, mapIndex) == null)
                    {
                        // Nothing ANYWHERE can repair it, which is what makes this permanent latch
                        // safe. The selector above is pinned to this map, so its miss only means
                        // "not in this town" - latching on that would write off repair for the rest
                        // of the session because one town lacked the right NPC.
                        _noRepairerKnown = true;
                        Status = "no repair NPC found for the damaged gear";

                        // A forced re-scan that comes back empty is worth saying out loud. The
                        // operator pressed a button expecting a repair; silence would read as the
                        // button not working, when in fact the answer is a definite no.
                        if (_repairRescan)
                        {
                            _repairRescan = false;
                            RepairDiagnostic = "looked again on request - still no repairer " +
                                               "anywhere for this gear";
                        }
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

                _bookSeller = bookSeller;

                // What the pending book actually costs, so gear can reserve THAT rather than the
                // entire purse. Cheapest wanted book on the seller's pages: if we can afford any
                // of them the trip is worth protecting, and the cheapest is the one we would buy
                // first.
                _bookReserve = bookSeller == null ? 0 : CheapestWantedBook(bookSeller, wanted);

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

                bool partWork = items.HasCompletableParts();

                if ((waiting.Count > 0 || partWork) && world.InSafeZone)
                {
                    BankDiagnostic = partWork
                        ? "item parts ready to consolidate and assemble"
                        : $"{waiting.Count} banked item(s) usable now";
                    return PlanOutcome.Banking;
                }

                if (waiting.Count > 0 || partWork)
                    BankDiagnostic = (partWork
                        ? "item parts ready to assemble"
                        : $"{waiting.Count} banked item(s) usable") +
                                     ", but we are not in a safe zone to collect them";

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
            bool outOfSupplies = false, bool bankedReady = false)
        {
            ResetTripState();

            // Captured ONCE, before any transport, and never by the planner - which can run again
            // after landing and would otherwise record the town as the hunting ground.
            _huntingMap = world.MapIndex;
            _huntingSpot = world.Location;

            // Say which limit sent us. Weight and slots are separate and either can be the cause,
            // and a trip that reports "bag 73% full" while the real reason was 48 of 48 slots is
            // a log line that actively misleads whoever reads it next.
            _tripReason = !string.IsNullOrEmpty(_startedBecause)
                ? _startedBecause
                : bankedReady
                ? "banked item ready to use"
                : items.FreeSlotCount <= Math.Max(0, _config.TownAtFreeSlots)
                ? $"{items.InventoryCount}/{Globals.InventorySize} slots used"
                : $"bag {world.WeightPercent}% full";

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
                    return WalkStep(world, _tripReason);
            }

            // Nothing to do where we stand. A scroll is now the only way to reach a town that can
            // serve us - and it is spent WITHOUT a plan, because where it lands decides the plan.
            int scroll = _scrollFailedHere == world.MapIndex ? -1 : items.FindTownTeleportSlot();

            // A scroll is only worth spending if it lands somewhere that can serve the trip.
            //
            // Two separate ways it cannot. The first is the map forbidding town teleport outright
            // (PlayerObject.cs:5662 checks CurrentMap.Info.AllowTT and answers with a chat line);
            // four Phantom Ship maps do that.
            //
            // The second is the one that actually bit us. A scroll goes to the character's BIND
            // POINT, and the server re-binds to whatever safe zone the character last stood in.
            // Bot 1 walked through Sabuk Keep's safe zone, bound there, and then scrolled itself
            // back to Sabuk Keep with a completely full bag - a map with a safe zone and not one
            // vendor. The scroll was gone, the trip aborted, and only the travel system getting it
            // to Bichon on foot saved it. Letting the journey do the work from the start is both
            // cheaper and correct.
            if (scroll >= 0 && !AllowsTownScroll(world.MapIndex))
            {
                Abort($"{world.MapName} does not allow town teleport - walking out instead");
                return null;
            }

            if (scroll >= 0 && !bankedReady && world.BindMapIndex > 0 &&
                !_directory.HasVendorsOn(world.BindMapIndex))
            {
                Abort($"a scroll would only take us back to {MapNameOf(world.BindMapIndex)}, " +
                      "which has no vendors - walking instead");
                return null;
            }

            if (scroll >= 0)
            {
                // Town teleport is legal during combat. PlayerObject.ItemUse checks AllowTeleport
                // and AllowTT for this item, but unlike logout it does not inspect CombatTime. The
                // panic-flee path relies on the same rule and scrolls immediately while fighting.
                _mapBeforeTeleport = world.MapIndex;
                _scrollConsumed = false;
                _teleportScrollSlot = scroll;
                Enter(TownPhase.Teleporting, "scrolling to town to shop");

                return new Decision
                {
                    Action = BotAction.TownTeleport,
                    Reason = _tripReason,
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
        private static string MapNameOf(int mapIndex) =>
            Globals.MapInfoList?.Binding?.FirstOrDefault(x => x.Index == mapIndex)?.Description
            ?? $"map {mapIndex}";

        /// <summary>Does this map permit a Scroll Of Town Portal at all?</summary>
        private static bool AllowsTownScroll(int mapIndex)
        {
            MapInfo info = Globals.MapInfoList?.Binding?
                .FirstOrDefault(x => x.Index == mapIndex);

            // Unknown map: assume it works. Refusing on missing data would ground the bot
            // permanently, and the cost of being wrong is one wasted packet.
            return info == null || info.AllowTT;
        }

        /// <summary>One scroll per trip may be spent purely to get unstuck.</summary>
        private bool _scrolledToUnstick;

        /// <summary>
        /// Where a town scroll is, refreshed each tick by Next.
        ///
        /// WalkStep has no Backpack of its own - it is handed a WorldModel and nothing else - so
        /// the slot is carried here rather than changing the signature of a method called from
        /// half a dozen places.
        /// </summary>
        private int _scrollSlot = -1;

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
            else if (_unstickNow ||
                     (_lastProgress != DateTime.MinValue &&
                      DateTime.Now - _lastProgress > TimeSpan.FromSeconds(25)))
            {
                // Stuck. Before giving up, try the thing a player would do: scroll out.
                //
                // Abandoning the trip is right when the destination is unreachable. It is the
                // wrong answer when the bot is simply BOXED IN - a wizard surrounded in Bichon
                // Town, seventy-three tiles from its vendor, holding three town scrolls, gave up
                // on the trip and went back to being chewed on. The operator pressed "Town trip"
                // and reasonably expected a scroll to be spent; it was not, because the bot was
                // already in a town and the scroll is normally only for reaching one.
                //
                // A scroll goes to the bind point, which breaks the encirclement whether or not it
                // changes map, and the itinerary is replanned on landing as a scrolled trip always
                // is. Once per trip only: if it is still stuck afterwards, something else is wrong
                // and the abort is the honest answer.
                bool asked = _unstickNow;
                _unstickNow = false;

                if (!_scrolledToUnstick && _scrollSlot >= 0 && AllowsTownScroll(world.MapIndex))
                {
                    _scrolledToUnstick = true;
                    _mapBeforeTeleport = world.MapIndex;
                    _teleportScrollSlot = _scrollSlot;
                    _scrollConsumed = false;
                    _lastProgress = DateTime.Now;
                    _lastRemaining = int.MaxValue;

                    Enter(TownPhase.Teleporting,
                        $"{(asked ? "asked to unstick" : "stuck")} {remaining} tiles out - " +
                        "scrolling clear");

                    return new Decision
                    {
                        Action = BotAction.TownTeleport,
                        Reason = asked
                            ? $"asked to unstick, {remaining} tiles from the vendor"
                            : $"stuck {remaining} tiles from the vendor",
                        Subject = "unsticking",
                        PotionSlot = _scrollSlot
                    };
                }

                // Asked to unstick but there is no scroll to do it with: say so and carry on
                // walking rather than throwing the trip away on the operator's behalf.
                if (asked)
                {
                    Status = $"asked to unstick {remaining} tiles out, but no town scroll";
                }
                else
                {
                    Abort($"stopped making progress {remaining} tiles out");
                    return null;
                }
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
                _boughtReagents = false;
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
        private NPCGood BestUpgradeOnPage(WorldModel world, Backpack items, long bookHold,
            out string why)
        {
            why = "";

            if (_currentPage?.Goods == null) { why = "page sells nothing"; return null; }

            long spendable = world.Gold - _config.GoldReserve - Math.Max(0, bookHold);

            if (spendable <= 0)
            {
                why = $"only {world.Gold:N0} gold, reserve is {_config.GoldReserve:N0}";
                return null;
            }

            NPCGood best = null;
            int bestGain = 0;
            int considered = 0;
            int skippedDuplicate = 0;

            foreach (NPCGood good in _currentPage.Goods)
            {
                ItemInfo info = good.Item;

                if (info == null) continue;
                if (_target?.NPC != null && good.GoodsIndex != _target.NPC.GoodsIndex) continue;
                long cost = CostOf(good);

                if (cost <= 0 || cost > spendable) continue;

                int gain = items.UpgradeGain(info, world.Class, world.Gender, world.Level,
                    world.PlayerStats);

                if (gain <= 0) continue;

                // ALREADY HAVE ONE IN THE BAG.
                //
                // The gain is measured against what is WORN, so an identical item sitting
                // unequipped in the bag is invisible to it and gets bought again every trip. It
                // cannot be equipped either - PendingEquips needs a STRICTLY higher score, and a
                // duplicate ties - so it becomes surplus, gets offered back to the vendor that
                // sold it, and the cycle repeats. Four Necklace Of Lantern purchases at 4,000
                // gold each inside an hour, every one of them refused on the way back out.
                if (items.HasUnequippedCopy(info))
                {
                    skippedDuplicate++;
                    continue;
                }

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
                // SAY WHAT THE GAIN IS MEASURED AGAINST.
                //
                // "+10 for 4,000 gold" reads as a bargain and gives no way to check the claim. The
                // suspicion in the duplicate case above is that the slot was read as EMPTY - an
                // empty slot makes anything wearable a gain of its whole score - and the log could
                // neither confirm nor rule that out. It can now.
                why = $"+{bestGain} for {CostOf(best):N0} gold, {world.Gold:N0} held " +
                      $"(vs {items.DescribeWornFor(best.Item.ItemType, world.Class)})";
                return best;
            }

            why = considered > 0
                ? $"{considered} wearable, none worth {_config.MinimumUpgradePercent}% more"
                : skippedDuplicate > 0
                    ? $"nothing worth buying - {skippedDuplicate} already in the bag"
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
        /// <summary>
        /// The reagent this character is short of, or Nothing.
        ///
        /// Driven by what it KNOWS, not by its class: a Taoist who has not learnt a summon or a
        /// poison yet has no use for either, and buying speculatively wastes gold a low-level
        /// caster does not have. Amulets come first because far more spells consume them.
        /// </summary>
        private ItemType ReagentNeeded(WorldModel world, Backpack items)
        {
            if (NeedsAmulet(world) && items.CountReagent(ItemType.Amulet) < _config.ReagentReserve)
                return ItemType.Amulet;

            if (NeedsPoison(world) && items.CountReagent(ItemType.Poison) < _config.ReagentReserve)
                return ItemType.Poison;

            return ItemType.Nothing;
        }

        /// <summary>
        /// Magics that consume an amulet inside MagicCast, per the server's Taoist sources. The
        /// consumption happens BEFORE most other validation, so a missing amulet is a silently
        /// failed cast rather than an error - which is exactly why these are bought rather than
        /// discovered.
        /// </summary>
        private static bool NeedsAmulet(WorldModel world) =>
            world.CanUseMagic(MagicType.SummonSkeleton) ||
            world.CanUseMagic(MagicType.MagicResistance) ||
            world.CanUseMagic(MagicType.Resilience) ||
            world.CanUseMagic(MagicType.StrengthOfFaith) ||
            world.CanUseMagic(MagicType.Invisibility);

        /// <summary>PoisonDust reads the equipped POISON item and takes its Shape to decide green or
        /// red (PoisonDust.cs:64) - the amulet does not choose.</summary>
        private static bool NeedsPoison(WorldModel world) =>
            world.CanUseMagic(MagicType.PoisonDust);

        /// <summary>
        /// The cheapest thing of this type on the open page. Reagents are interchangeable as far as
        /// the bot is concerned, so price is the only sensible tie-break.
        /// </summary>
        private NPCGood CheapestOfType(ItemType type)
        {
            if (_currentPage?.Goods == null) return null;

            NPCGood best = null;

            foreach (NPCGood good in _currentPage.Goods)
            {
                if (good?.Item == null || good.Item.ItemType != type) continue;
                if (best == null || CostOf(good) < CostOf(best)) best = good;
            }

            return best;
        }

        /// <summary>
        /// How many of THIS good the potion budget still has room for.
        ///
        /// The last place the weight budget was read as a count. Every caller computed
        ///     HealthPotionTarget(bag, thisTiersWeight) - CountHealthPotions()
        /// which subtracts a COUNT of every potion carried, of every tier, from a count of how
        /// many of ONE tier would fill the budget. The two only agree when the bag holds nothing
        /// else.
        ///
        /// A level 26 warrior showed the consequence: 86 light potions weighing 68 against an 82
        /// budget - fourteen weight of genuine headroom - and a tier-four target of 27. 27 minus 86
        /// is negative, so it bought nothing, and would go on buying nothing for as long as the
        /// weak potions lasted. A bot that can never top up with the good tier drifts into carrying
        /// only the bad one, which is the behaviour this whole evening started with.
        ///
        /// Weight left in the budget, divided by what one of these weighs. Both sides in weight,
        /// converted to a count exactly once, at the end.
        /// </summary>
        /// <summary>
        /// Gold a potion restock may spend. ONE definition, used to choose the tier and to choose
        /// the quantity - they were two expressions over the same question, and the quantity one
        /// used the raw purse, so capping only the tier choice would have changed nothing.
        ///
        /// Capped as a share so a single restock cannot empty the purse, never capped below
        /// PoorGold, and not capped at all beneath it: a bot that is already broke needs something
        /// to drink more than it needs a balance.
        /// </summary>
        /// <summary>
        /// Slots the server has refused to buy, and when to try them again.
        ///
        /// Ten minutes rather than for ever, because the cause is usually a stale model rather
        /// than a permanent property of the item, and a fresh S.ItemsChanged or a relog fixes it.
        /// Keyed by slot for the same reason the drop memory is: what we got wrong is our record
        /// of that slot, not the item's identity.
        /// </summary>
        private readonly Dictionary<int, DateTime> _sellRefused = new Dictionary<int, DateTime>();

        /// <summary>How many unanswered unlock requests a slot gets before we stop asking.</summary>
        private const int UnlockAttemptLimit = 3;

        private readonly Dictionary<int, int> _unlockTries = new Dictionary<int, int>();

        private bool UnlockGivenUp(int slot) =>
            _unlockTries.TryGetValue(slot, out int tries) && tries >= UnlockAttemptLimit;

        /// <summary>
        /// After a refusal, offer ONE SLOT AT A TIME until the trip ends.
        ///
        /// NPCSell is all-or-nothing and the server names no culprit, so a batch of thirty tells
        /// us only that at least one of them is unacceptable - and re-offering the same thirty at
        /// the next vendor fails identically. That is how a taoist spent thirty-three minutes at
        /// 90% bag selling nothing.
        ///
        /// Singles turn an unanswerable question into an answerable one. Each refusal now names
        /// exactly one slot, which NoteSellRefused parks for ten minutes, and everything else in
        /// the bag goes through. It costs a packet per slot on a trip that already walks between
        /// half a dozen vendors, and it works no matter WHY the server objects - a flag we cannot
        /// see, a count we have wrong, a slot that is not there. Six theories have been argued
        /// about that question and five were wrong; this needs none of them to be right.
        /// </summary>
        private bool _sellOneAtATime;

        public void NoteSellOrderRefused() => _sellOneAtATime = true;

        public void NoteSellRefused(int slot) =>
            _sellRefused[slot] = DateTime.UtcNow + TimeSpan.FromMinutes(10);

        private bool SellRefusedRecently(int slot) =>
            _sellRefused.TryGetValue(slot, out DateTime until) && DateTime.UtcNow < until;

        /// <summary>Potion gold committed on this trip. Reset with the rest of the trip state.</summary>
        private long _potionGoldSpent;

        /// <summary>
        /// Does this character spend mana on anything it knows? Spells for a caster, attack
        /// skills for a warrior - both are reasons to keep mana potions in the bag.
        /// </summary>
        private static bool SpendsMana(WorldModel world)
        {
            foreach (ClientUserMagic known in world.Magics)
            {
                if (known?.Info == null) continue;
                if (world.Level < known.Info.NeedLevel1) continue;

                if (known.Cost > 0) return true;
            }

            return false;
        }

        /// <summary>
        /// The map where a scroll was spent and nothing happened.
        ///
        /// The server can ignore a town teleport without a word - no movement, no chat, and not
        /// even the scroll consumed - and the trip's own six-second check then reports "the scroll
        /// did not move us". Asking again on the same map gets the same silence, so we stop asking
        /// and let the journey walk us out instead.
        /// </summary>
        private int _scrollFailedHere = -1;

        /// <summary>What the pending book stop is expected to cost. See the gear branch.</summary>
        private long _bookReserve;

        /// <summary>
        /// The cheapest wanted book this seller stocks, priced as the server prices it.
        ///
        /// Returns 0 when nothing can be found, which makes the gear branch stop holding - an
        /// unknown cost is not a reason to freeze spending indefinitely.
        /// </summary>
        private long CheapestWantedBook(VendorEntry seller, List<int> wanted)
        {
            if (seller?.Page?.Goods == null || _books == null) return 0;

            long cheapest = 0;

            foreach (NPCGood good in seller.Page.Goods)
            {
                if (good?.Item == null) continue;
                if (good.Item.ItemType != ItemType.Book) continue;

                MagicInfo magic = _books.For(good.Item);

                if (magic == null || !wanted.Contains(magic.Index)) continue;

                long cost = CostOf(good);

                if (cost > 0 && (cheapest == 0 || cost < cheapest)) cheapest = cost;
            }

            return cheapest;
        }

        /// <summary>Set when the server confirms the town scroll was actually consumed.</summary>
        private bool _scrollConsumed;

        /// <summary>The inventory slot used for the current town teleport attempt.</summary>
        private int _teleportScrollSlot = -1;

        /// <summary>
        /// Told by the connection when an item use is accepted or refused. Only the verdict for
        /// this teleport's slot counts: a potion or an earlier late reply must never make a failed
        /// scroll look consumed and blacklist an otherwise valid map.
        /// </summary>
        public void NoteScrollOutcome(int slot, bool consumed)
        {
            if (Phase != TownPhase.Teleporting || slot != _teleportScrollSlot) return;
            _scrollConsumed = consumed;
        }

        private long PotionGold(WorldModel world)
        {
            long gold = Math.Max(0, world.Gold);

            if (_config.PotionMaxGoldPercent <= 0) return gold;

            // THE CAP IS PER TRIP, NOT PER PURCHASE.
            //
            // Capping each purchase on its own looks equivalent and is not, because they run in
            // sequence and each one re-reads the purse the last one just emptied. A wizard with
            // 14,875 gold bought 44 healing potions against a 10,000 cap, then reached the mana
            // step holding 4,875 - below PoorGold, where the cap deliberately does NOT apply,
            // because a bot that is already broke needs a drink more than a balance. It spent the
            // lot on 29 mana potions, left the shop with FORTY-NINE gold, and died.
            //
            // The exemption is right. What was wrong is letting a purchase we just made qualify us
            // for it. Adding back what this trip has already committed measures the purse as it
            // was on arrival, so the share is taken once and divided between the steps.
            long atArrival = gold + _potionGoldSpent;

            if (atArrival <= _config.PoorGold) return gold;

            long allowance = Math.Max(_config.PoorGold,
                                      atArrival * _config.PotionMaxGoldPercent / 100);

            return Math.Max(0, Math.Min(gold, allowance - _potionGoldSpent));
        }

        /// <summary>Called as a potion order goes out, so later steps see the budget shrink.</summary>
        private void NotePotionSpend(NPCGood good, int amount)
        {
            if (good?.Item == null || amount <= 0) return;

            _potionGoldSpent += CostOf(good) * amount;
        }

        /// <summary>
        /// Cheapest mana potion the game defines, or 0 if the database has not loaded.
        ///
        /// Read from ItemInfo rather than from a vendor page, because the question is asked at
        /// the hunting ground where there is no page open. The cheapest tier anywhere is the
        /// right bound: if we cannot afford THAT, no shop on the server can help us.
        /// </summary>
        private static int _cheapestMana = -1;

        private static int CheapestManaPotionPrice()
        {
            if (_cheapestMana >= 0) return _cheapestMana;

            int cheapest = int.MaxValue;

            try
            {
                foreach (ItemInfo info in Globals.ItemInfoList?.Binding
                                          ?? Enumerable.Empty<ItemInfo>())
                {
                    if (info == null) continue;
                    if (info.ItemType != ItemType.Consumable) continue;
                    if (info.Stats[Stat.Health] > 0 || info.Stats[Stat.Mana] <= 0) continue;
                    if (info.Price <= 0) continue;

                    if (info.Price < cheapest) cheapest = info.Price;
                }
            }
            catch
            {
                // Database not loaded. 0 disables the gate, so behaviour is exactly as before.
            }

            _cheapestMana = cheapest == int.MaxValue ? 0 : cheapest;
            return _cheapestMana;
        }

        /// <summary>
        /// Enough gold to make a mana trip worth walking?
        ///
        /// Measured as MinUsefulPotionBuy of the cheapest tier - the same bar BestPotion already
        /// applies when deciding whether a purchase is worth making at all, so the trigger and the
        /// buyer agree about what "enough" means instead of the trigger sending the bot to a shop
        /// the buyer will then refuse to use.
        /// </summary>
        private bool CanAffordAnyMana(WorldModel world)
        {
            int price = CheapestManaPotionPrice();

            if (price <= 0) return true;   // unknown - never suppress on a guess

            long need = (long)price * Math.Max(1, _config.MinUsefulPotionBuy);

            return world.Gold >= need;
        }

        private int RoomFor(WorldModel world, Backpack items, NPCGood good, bool healing)
        {
            if (good?.Item == null) return 0;

            int unit = Math.Max(1, good.Item.Weight);

            int budget = healing
                ? _config.HealthPotionTarget(world.MaxBagWeight)
                : _config.ManaPotionTarget(world.MaxBagWeight);

            int carried = healing ? items.HealthPotionLoad() : items.ManaPotionLoad();

            int room = Math.Max(0, (budget - carried) / unit);

            // A weight budget cannot bound something that weighs nothing.
            //
            // max(1, weight) costs a weightless pill as though it weighed one, so the whole budget
            // converts straight into units - 87 of them for a 293-weight bag - and the only real
            // limit left is gold. That is how a single restock cost 266,000. Total healing is the
            // bound that still means something when weight does not.
            int restores = healing ? good.Item.Stats[Stat.Health] : good.Item.Stats[Stat.Mana];
            int pool = healing ? world.MaxHealth : world.MaxMana;

            if (restores > 0 && pool > 0 && _config.PotionMaxPoolMultiple > 0)
            {
                long bars = (long)pool * _config.PotionMaxPoolMultiple / restores;

                room = (int)Math.Min(room, Math.Max(1, bars));
            }

            return room;
        }

        private static int Affordable(NPCGood good, int wanted, long gold)
        {
            if (good?.Item == null || wanted <= 0) return 0;

            long price = Math.Max(1, CostOf(good));

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

            // Biggest FIRST, but capped to what the character can actually absorb.
            //
            // Buying the largest potion on the shelf looks obviously right and is not. Drinking
            // starts at HealAtPercent, so the gap a potion has to fill is at most
            // MaxHealth * (100 - HealAtPercent)%: for a 216 HP wizard at 60% that is 86, and a
            // Healing Potion (V) restores 250. Most of it hits the health cap and is thrown away,
            // and with the no-overheal rule in the brain the bot would rather not drink it at all -
            // so it would carry expensive potions it never used and still run dry.
            //
            // Tiers on this server heal 30 / 70 / 110 / 170 / 250, so there is almost always one
            // that fits. If none does - a very low-level character where even the smallest
            // overshoots - take the smallest and accept the waste.
            //
            // THE POOL AND THE THRESHOLD MUST MATCH THE POTION. This used to compute the gap from
            // MaxHealth and HealAtPercent whichever kind it was buying, so a wizard shopping for
            // mana was sized against its health bar: 216 HP at a 60% threshold gave a gap of 86,
            // and every mana potion restoring more than 86 was treated as waste. It came home with
            // fifteen tier-one potions worth 40 mana each for a 319 mana pool, drank four of them
            // in town to no visible effect, and left with eleven.
            //
            // Mana also has its own drinking threshold - DrinkManaAtPercent, typically lower than
            // HealAtPercent - so the gap is genuinely larger for the same size of pool.
            int pool = healing ? world.MaxHealth : world.MaxMana;
            int threshold = healing ? _config.HealAtPercent : _config.DrinkManaAtPercent;

            int gap = pool > 0
                ? pool * Math.Max(1, 100 - threshold) / 100
                : int.MaxValue;

            // A PRICE TERM, before any of the weight ranking runs.
            //
            // See PotionGoldPerHealFactor. Healing per weight alone reliably picks the most
            // expensive thing on the shelf, because the cheapest way to save weight is to pay for
            // it. Weight and gold are both scarce and the ranking knew about one of them.
            //
            // Judged against the cheapest option THAT FITS THE GAP where any does, so a tier we
            // would never buy cannot set the bar. Never strikes out everything: if the whole page
            // is expensive then expensive is what we buy, because not drinking is worse.
            double GoldPerHeal(NPCGood g) =>
                Restores(g) <= 0 ? double.MaxValue
                                 : Math.Max(0, (double)CostOf(g)) / Restores(g);

            // max(1, weight) so a weightless item cannot set the bar at zero and strike out
            // everything else - the same trap the ranking fell into.
            double WeightPerHeal(NPCGood g) =>
                Restores(g) <= 0 ? double.MaxValue
                                 : Math.Max(1, g.Item.Weight) / (double)Restores(g);

            if (_config.PotionGoldPerHealFactor > 0)
            {
                bool anyFits = candidates.Any(g => Restores(g) <= gap);

                double bestValue = double.MaxValue;

                foreach (NPCGood g in candidates)
                {
                    if (anyFits && Restores(g) > gap) continue;

                    bestValue = Math.Min(bestValue, GoldPerHeal(g));
                }

                if (bestValue < double.MaxValue)
                {
                    double bar = bestValue * _config.PotionGoldPerHealFactor;

                    List<NPCGood> sane = candidates.Where(g => GoldPerHeal(g) <= bar).ToList();

                    if (sane.Count > 0) candidates = sane;
                }
            }

            // The same bound again on WEIGHT, so that both scarce resources are fenced off before
            // anything is ranked. See PotionWeightPerHealFactor.
            if (_config.PotionWeightPerHealFactor > 0)
            {
                bool anyFitsW = candidates.Any(g => Restores(g) <= gap);

                double lightest = double.MaxValue;

                foreach (NPCGood g in candidates)
                {
                    if (anyFitsW && Restores(g) > gap) continue;

                    lightest = Math.Min(lightest, WeightPerHeal(g));
                }

                if (lightest < double.MaxValue)
                {
                    double bar = lightest * _config.PotionWeightPerHealFactor;

                    List<NPCGood> light = candidates.Where(g => WeightPerHeal(g) <= bar).ToList();

                    if (light.Count > 0) candidates = light;
                }
            }

            // AND NOW: THE BIGGEST ONE THAT STILL FITS. Drinks per fight is the thing that matters.
            //
            // Ranking on healing per weight was my mistake and it is worth naming, because it looks
            // reasonable and is not. Weight is an integer, so the low tiers win it almost by
            // construction: Healing Potion (II) heals 70 for 1 weight and scores 70, while a (III)
            // heals 110 for 2 and scores 55. It duly bought 70-point potions for a taoist with a
            // 330 health bar and a 132-point gap that a (III) fitted comfortably.
            //
            // That is the SAME complaint that started all of this - a wizard with a 319 mana pool
            // buying 40-point mana potions - reached from the opposite direction. A potion too
            // small for the bar is useless however efficient it looks, because there is a cooldown
            // between drinks: what keeps a character alive is closing the gap in one or two, not
            // the ratio of healing to anything.
            //
            // So gold and weight are BOUNDS, checked above, and the ranking is simply the largest
            // restore that does not overshoot the gap. Ties, which are real - Life Pill (IV) and
            // Healing Potion (IV) both heal 170 - go to the lighter one.
            candidates.Sort((a, b) =>
            {
                bool aFits = Restores(a) <= gap, bFits = Restores(b) <= gap;

                if (aFits != bFits) return aFits ? -1 : 1;            // anything that fits wins

                if (aFits)
                {
                    int bySize = Restores(b).CompareTo(Restores(a));   // biggest that fits

                    return bySize != 0 ? bySize : WeightPerHeal(a).CompareTo(WeightPerHeal(b));
                }

                return Restores(a).CompareTo(Restores(b));            // none fit: least waste
            });

            // Everything the character has, NOT gold minus the reserve.
            //
            // The reserve exists to stop optional gear shopping eating the survival budget - the
            // gear step checks it separately, which is the right place. Applying it here made the
            // reserve block the one purchase it is being held for: a wizard with 914 gold and a
            // 5,000 reserve had nothing "spendable" at all, so it could never buy a healing potion
            // while broke, which is exactly when it needs one.
            long spendable = PotionGold(world);

            // GOLD DECIDES HOW MANY, NEVER WHICH.
            //
            // The candidates are already in preference order - biggest that fits the gap first -
            // so this only has to find the best one we can actually pay for. The bug was in what
            // "pay for" meant. It used to size ONE target for all tiers, from HealthPotionTarget
            // with the weight argument defaulted to 1 - so the weight budget was read as a count,
            // sixty-nine for a 138-weight bag - and then asked each tier "can I afford all
            // sixty-nine of these?". A poor bot can never afford sixty-nine of anything good, so
            // it fell through every candidate and hit the cheapest-on-the-page fallback below.
            //
            // That is how a level 18 assassin with a 310 health bar came home with fifty-two
            // Healing Potions restoring 30 apiece. Not because the sort preferred them - the sort
            // was right, its gap was 124 and tier three at 110 fitted perfectly - but because it
            // had 900 gold and the test asked for a full stack.
            //
            // The consequences were not subtle. Thirty a drink against a 124 gap is four drinks to
            // top up, taking damage throughout; fifty-two of them is half the bag, so it filled up
            // after two minutes of hunting and went home. Five town trips in nineteen minutes, and
            // it eventually died in Flea Cave drinking tier ones.
            //
            // So each tier is asked its OWN question now, sized by what IT weighs, and accepted if
            // we can afford a useful number. Being short of gold is temporary and self-correcting -
            // the next trip tops up - whereas a bag full of the wrong tier persists until something
            // sells it.
            //
            // There used to be a second rule here, a FLOOR: skip anything restoring less than
            // PotionHealPercentTarget of the pool. That was written before the sort understood
            // tiers, and once the sort gained a CEILING the two could disagree - a floor wanting a
            // bigger potion and a ceiling wanting a smaller one, with the outcome decided by which
            // happened to run last. One rule about potion size is enough, and the ceiling is the
            // one that keeps the no-overheal promise.
            foreach (NPCGood good in candidates)
            {
                int want = RoomFor(world, items, good, healing);

                if (want <= 0) want = 1;

                long cost = CostOf(good);
                long canBuy = cost > 0 ? spendable / cost : want;

                // Enough to be worth the walk, or the whole remaining target if that is smaller.
                if (canBuy >= Math.Min(want, Math.Max(1, _config.MinUsefulPotionBuy))) return good;
            }

            // Genuinely broke - cannot afford even a handful of the weakest thing that fits. Take
            // the cheapest on the page and let Affordable() trim the count; something to drink
            // beats nothing to drink.
            NPCGood cheapest = candidates[0];

            foreach (NPCGood good in candidates)
                if (CostOf(good) < CostOf(cheapest)) cheapest = good;

            return cheapest;
        }

        /// <summary>
        /// What this vendor ACTUALLY charges, which is not ItemInfo.Price.
        ///
        /// The server bills NPCGood.Cost (PlayerObject.cs:9440,
        /// `price = Math.Max(1, good.Cost * currency.ExchangeRate)`). ItemInfo.Price is the item's
        /// nominal value and the two routinely disagree - Potion Mastery is listed at 1,000 and
        /// sold at 2,500,000 - and Cost varies BY VENDOR for the same item, which a single field
        /// on ItemInfo structurally cannot express: Healing Potion (IV) is 1,250 at one shop and
        /// 2,500 at another.
        ///
        /// Every purchase decision in this file priced from ItemInfo.Price, so affordability
        /// checks, the potion gold budget and the upgrade bar were all computed against a number
        /// the server never uses. A level 31 warrior with a million gold ordered Potion Mastery
        /// twice believing it cost a thousand; both orders were silently refused, and because
        /// nothing learned the real price it would have kept trying indefinitely.
        ///
        /// Falls back to ItemInfo.Price only when Cost is unset, so a good with no explicit price
        /// still prices sensibly rather than as free.
        /// </summary>
        private static long CostOf(NPCGood good)
        {
            if (good?.Item == null) return 0;

            return good.Cost > 0 ? good.Cost : good.Item.Price;
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
