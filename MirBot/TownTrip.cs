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
        private bool _repaired;
        private bool _boughtBook;

        /// <summary>Latched when no NPC in System.db repairs what we carry, so a broken item
        /// does not loop begin -> abort -> cooldown forever.</summary>
        private bool _noRepairerKnown;

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

        private Point Destination =>
            Phase == TownPhase.Returning ? _huntingSpot : _target?.Point ?? Point.Empty;

        public void Abort(string why)
        {
            // Returning is best-effort; failing to get back out should not lock out the next trip.
            if (Phase != TownPhase.None && Phase != TownPhase.Returning)
                _retryAfter = DateTime.Now + RetryDelay;

            Phase = TownPhase.None;
            Status = why;
            _forced = false;        // a failed forced trip must not repeat on the next Start
            _currentPage = null;
            _buttonPath.Clear();
            _itinerary.Clear();
            _sold = _boughtScrolls = _boughtPotions = _repaired = _boughtBook = false;
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
            // it fights with a dead weapon. Guarded by _noRepairerKnown so it cannot spin.
            bool broken = _config.RepairEnabled && !_noRepairerKnown && items.AnyBrokenEquipment();

            if (!Active)
            {
                if (!overweight && !broken && !_forced) return null;
                if (OnCooldown && !broken && !_forced) return null;

                bool forced = _forced;
                _forced = false;

                return Begin(world, items, broken || forced);
            }

            switch (Phase)
            {
                case TownPhase.Teleporting:
                    // The scroll drops us at the bind point; from there we walk the last stretch.
                    if (StuckIn(TimeSpan.FromSeconds(6)))
                    {
                        Enter(TownPhase.Travelling, "landed, walking to vendor");
                        return WalkStep(world, "walking to vendor");
                    }
                    return null;

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
                    if (!_sold)
                    {
                        _sold = true;

                        List<ItemType> accepted = AcceptedTypes();

                        List<int> slots = items
                            .DisposableSlots(world.Class, world.Gender, world.Level, world.PlayerStats,
                                _config.HealthPotionReserve, _config.ManaPotionReserve,
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
                    if (!_repaired && _config.RepairEnabled &&
                        _currentPage?.DialogType == NPCDialogType.Repair)
                    {
                        _repaired = true;

                        List<int> slots = items.DamagedEquipmentSlots(
                            _config.RepairAtDurability, AcceptedTypes());

                        if (slots.Count > 0)
                            return new Decision
                            {
                                Action = BotAction.NPCRepair,
                                Reason = $"{slots.Count} items at {_target.NPC.NPCName}",
                                RepairSlots = slots
                            };
                    }

                    if (!_boughtScrolls)
                    {
                        _boughtScrolls = true;

                        int wanted = _config.TownScrollReserve - items.CountTownScrolls();
                        NPCGood good = wanted > 0 ? FindGood(IsTownScroll) : null;

                        if (good != null)
                            return new Decision
                            {
                                Action = BotAction.NPCBuy,
                                Reason = $"{wanted} x {good.Item.ItemName}",
                                BuyIndex = good.Index,
                                BuyAmount = wanted
                            };
                    }

                    if (!_boughtPotions)
                    {
                        _boughtPotions = true;

                        // Topping up healing potions matters more than the scrolls: without them the
                        // heal branch has nothing to drink and the bot fights until it dies.
                        int wanted = _config.HealthPotionReserve - items.CountHealthPotions();
                        NPCGood good = wanted > 0 ? FindGood(IsHealthPotion) : null;

                        if (good != null)
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
                    if (!_boughtBook && _config.BuyBooks &&
                        items.CountHealthPotions() >= _config.HealthPotionReserve &&
                        items.CountTownScrolls() >= _config.TownScrollReserve)
                    {
                        _boughtBook = true;

                        NPCGood good = FindGood(info => WantedBook(info, world));

                        if (good != null)
                            return new Decision
                            {
                                Action = BotAction.NPCBuy,
                                Reason = $"1 x {good.Item.ItemName}",
                                BuyIndex = good.Index,
                                BuyAmount = 1     // one per trip: a bought book is Locked and
                                                  // NonRefinable, so a wrong one is dead weight
                            };
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

                    // Reclaim anything levelling has unlocked since it was stored.
                    foreach (KeyValuePair<int, ClientUserItem> stored in items.Stored.ToList())
                    {
                        if (stored.Value.Info.ItemType == ItemType.Book)
                        {
                            // Books are judged by MagicBooks: MeetsRequirement returns false for
                            // RequiredType.None, which some books carry, so using it here would
                            // leave them banked forever.
                            if (_books?.Judge(stored.Value, world.Class, world.Level,
                                    world.PlayerStats, world) != BookVerdict.Wanted) continue;
                        }
                        else
                        {
                            if (!Backpack.CanEquip(stored.Value, world.Class, world.Gender)) continue;
                            if (!Backpack.MeetsRequirement(stored.Value.Info, world.Level, world.PlayerStats))
                                continue;
                        }

                        int free = items.FirstFreeInventorySlot();
                        if (free < 0) break;

                        return new Decision
                        {
                            Action = BotAction.Withdraw,
                            Reason = $"{stored.Value.Info.ItemName} is usable now",
                            FromSlot = stored.Key,
                            ToSlot = free
                        };
                    }

                    // Stow what we cannot use yet but will.
                    foreach (KeyValuePair<int, ClientUserItem> carried in items.Carried.ToList())
                    {
                        if (!Backpack.WorthStoring(carried.Value, world.Class, world.Gender,
                                world.Level, world.PlayerStats, _books, world)) continue;

                        int free = items.FirstFreeStorageSlot(_config.StorageSize);
                        if (free < 0) break;

                        return new Decision
                        {
                            Action = BotAction.Deposit,
                            Reason = $"{carried.Value.Info.ItemName} for later",
                            FromSlot = carried.Key,
                            ToSlot = free
                        };
                    }

                    Enter(TownPhase.Returning, "banking done");
                    return null;
                }

                case TownPhase.Returning:
                {
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

        private Decision Begin(WorldModel world, Backpack items, bool broken)
        {
            // Who actually buys what we are carrying?
            List<ItemType> junk = items
                .DisposableSlots(world.Class, world.Gender, world.Level, world.PlayerStats,
                    _config.HealthPotionReserve, _config.ManaPotionReserve,
                    _config.TownScrollReserve, _books, world)
                .Select(slot => items.InSlot(slot)?.Info?.ItemType ?? ItemType.Nothing)
                .Where(t => t != ItemType.Nothing)
                .Distinct()
                .ToList();

            _itinerary.Clear();

            VendorEntry buyer = _directory.BestBuyerFor(junk, world.MapIndex);

            // Restock only when we are actually short, and only from someone who sells scrolls.
            // The best buyer usually does not - Joeban buys every item type but sells nothing.
            bool needScrolls = items.CountTownScrolls() < _config.TownScrollReserve;
            bool needPotions = items.CountHealthPotions() < _config.HealthPotionReserve;

            VendorEntry scrollSeller = _directory.BestRestockerFor(needScrolls, needPotions,
                world.MapIndex);

            if (buyer != null) _itinerary.Enqueue(buyer);

            if (scrollSeller != null &&
                (buyer == null || scrollSeller.Page != buyer.Page))
                _itinerary.Enqueue(scrollSeller);

            // Repair, when anything is broken or worn past the threshold.
            if (_config.RepairEnabled)
            {
                List<ItemType> damaged = items.DamagedTypes(_config.RepairAtDurability).ToList();

                if (damaged.Count > 0)
                {
                    VendorEntry repairer = _directory.BestRepairerFor(damaged, world.MapIndex);

                    if (repairer != null) _itinerary.Enqueue(repairer);
                    else if (broken)
                    {
                        // Nothing can repair it. Say so once and stop triggering on it.
                        _noRepairerKnown = true;
                        Status = "no repair NPC found for the damaged gear";
                    }
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
                    ? _directory.BestBookSellerFor(wanted, world.MapIndex)
                    : null;

                if (bookSeller != null) _itinerary.Enqueue(bookSeller);

                BookDiagnostic = $"{wanted.Count} skills wanted at level {world.Level}, " +
                                 $"seller: {bookSeller?.NPC.NPCName ?? "none"}";
            }

            if (_itinerary.Count == 0)
            {
                Abort("nothing to do in town for what we are carrying");
                return null;
            }

            _target = _itinerary.Dequeue();

            _huntingSpot = world.Location;

            int distance = WorldModel.Distance(world.Location, _target.Point);
            bool sameMap = _target.MapIndex == world.MapIndex;

            // Prefer a scroll when there is real ground to cover: it is instant, and walking home
            // can take minutes. Close by on the same map, walking beats teleporting to the bind
            // point and walking back.
            int scroll = items.FindTownTeleportSlot();

            if (scroll >= 0 && (!sameMap || distance > _config.ScrollIfFurtherThan))
            {
                Enter(TownPhase.Teleporting, $"scroll home, then {_target.NPC.NPCName}"
                    + (_itinerary.Count > 0 ? $" (+{_itinerary.Count} more)" : ""));
                return new Decision
                {
                    Action = BotAction.TownTeleport,
                    Reason = $"bag {world.WeightPercent}% full",
                    PotionSlot = scroll
                };
            }

            if (!sameMap)
            {
                Abort($"{_target.NPC.NPCName} is on another map and we have no town scroll");
                return null;
            }

            string plan = _itinerary.Count > 0
                ? $"{_target.NPC.NPCName}, then {string.Join(", ", _itinerary.Select(x => x.NPC.NPCName))}"
                : _target.NPC.NPCName;

            Enter(TownPhase.Travelling, $"walking to {plan}");
            return WalkStep(world, $"bag {world.WeightPercent}% full");
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
                _sold = _boughtScrolls = _boughtPotions = false;
            }
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
        private bool WantedBook(ItemInfo info, WorldModel world)
        {
            if (info.ItemType != ItemType.Book || _books == null) return false;

            MagicInfo magic = _books.For(info);

            if (magic == null) return false;
            if (magic.School == MagicSchool.None || magic.School == MagicSchool.Discipline) return false;
            if (!magic.MatchesClass(world.Class)) return false;
            if (magic.NeedLevel1 > world.Level) return false;
            if (world.Knows(magic.Index)) return false;

            return Backpack.MeetsRequirement(info, world.Level, world.PlayerStats);
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
