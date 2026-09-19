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
            _repairedSpecial = false;
            _boughtGear = false;
            _boughtMana = false;
            _boughtTorch = false;
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
                    if (!_repairedSpecial && _config.RepairEnabled && _config.PreferSpecialRepair &&
                        _currentPage?.DialogType == NPCDialogType.Repair)
                    {
                        _repairedSpecial = true;

                        List<int> special = items.SpecialRepairSlots(AcceptedTypes());

                        if (special.Count > 0)
                            return new Decision
                            {
                                Action = BotAction.NPCRepair,
                                Reason = $"{special.Count} items (special) at {_target.NPC.NPCName}",
                                RepairSlots = special,
                                Special = true
                            };
                    }

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
                        NPCGood good = BestPotion(world, items, true);

                        // Sized once the tier is known: a heavier potion earns a shorter stack.
                        int wanted = good == null ? 0
                            : _config.HealthPotionTarget(world.MaxBagWeight,
                                  Math.Max(1, good.Item.Weight)) - items.CountHealthPotions();

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
                            : _config.ManaPotionTarget(world.MaxBagWeight,
                                  Math.Max(1, good.Item.Weight)) - items.CountManaPotions();

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

        /// <summary>
        /// Will this trip start with a town scroll? Mirrors the test made once the first stop is
        /// known, so the itinerary can be planned against the scroll count we will actually have.
        /// </summary>
        private bool WillTeleport(WorldModel world, Backpack items)
        {
            if (items.FindTownTeleportSlot() < 0) return false;

            // Cheapest safe assumption: any trip long enough to be worth a scroll will use one.
            // Being wrong costs one unnecessary restock stop, never a missed one.
            return true;
        }

        private Decision Begin(WorldModel world, Backpack items, bool broken)
        {
            // Who actually buys what we are carrying?
            List<ItemType> junk = items
                .DisposableSlots(world.Class, world.Gender, world.Level, world.PlayerStats,
                    _config.HealthPotionTarget(world.MaxBagWeight),
                    _config.ManaPotionTarget(world.MaxBagWeight),
                    _config.TownScrollReserve, _books, world)
                .Select(slot => items.InSlot(slot)?.Info?.ItemType ?? ItemType.Nothing)
                .Where(t => t != ItemType.Nothing)
                .Distinct()
                .ToList();

            _itinerary.Clear();

            VendorEntry buyer = _directory.BestBuyerFor(junk, world.MapIndex);

            // Restock only when we are actually short, and only from someone who sells scrolls.
            // The best buyer usually does not - Joeban buys every item type but sells nothing.
            // One scroll is about to be spent getting here, so the count that matters is the one
            // we will have ON ARRIVAL. Planning against the pre-teleport number left the bot one
            // scroll short for the whole trip: with 3 in the bag nothing looked needed, the scroll
            // seller was left out of the itinerary, the teleport took it to 2, and every book stop
            // then refused with "restock first (2/3 scrolls)" - which is why Slaying was never
            // bought despite the gold, the level and the bag space all being there.
            int scrollsOnArrival = items.CountTownScrolls() - (WillTeleport(world, items) ? 1 : 0);

            bool needScrolls = scrollsOnArrival < _config.TownScrollReserve;
            bool needPotions = items.CountHealthPotions() <
                               _config.HealthPotionTarget(world.MaxBagWeight);

            // With no scroll in the bag there is no way off this map: a town trip walks, and it
            // walks on one map only. An off-map vendor is not a worse choice then, it is an
            // impossible one.
            bool stuckOnThisMap = items.CountTownScrolls() == 0;

            List<VendorEntry> restockers = _directory.BestRestockersFor(needScrolls, needPotions,
                world.MapIndex, _itinerary, world.Location, stuckOnThisMap);

            if (buyer != null) _itinerary.Enqueue(buyer);

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

            // Somewhere that sells torches, when the light has gone out. Cheap, and the bot is
            // otherwise blind in caves - which is where the good hunting is.
            if (_config.KeepTorchLit && !items.HasTorchEquipped && !items.CarryingTorch)
            {
                VendorEntry torchSeller = _directory.BestGearSellerFor(
                    new List<ItemType> { ItemType.Torch }, world.MapIndex, _itinerary, world.Location,
                    _config.MaxShoppingDistance);

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

                    VendorEntry seller = _directory.BestGearSellerFor(one, world.MapIndex,
                        _itinerary, world.Location, _config.MaxShoppingDistance);

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
                _sold = _boughtScrolls = _boughtPotions = _boughtGear = _boughtMana = false;
            _boughtTorch = false;
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

            long spendable = Math.Max(0, world.Gold - _config.GoldReserve);

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
