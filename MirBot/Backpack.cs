using System;
using System.Collections.Generic;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    public sealed class EquipRequest
    {
        public int FromSlot;
        public int ToSlot;
        public string ItemName;
        public EquipmentSlot Slot;
        public string Reason;

        public override string ToString() =>
            $"{ItemName} -> {Slot}" + (string.IsNullOrEmpty(Reason) ? "" : $" ({Reason})");
    }

    /// <summary>
    /// The character's items, split the same way the server splits them
    /// (ServerLibrary/Models/PlayerObject.cs:210-219): a slot at or above Globals.EquipmentOffSet is
    /// equipped, at that offset; anything below is an inventory slot.
    ///
    /// Source of truth is StartInformation.Items. NOTE: S.Login.Items is a different list - it is
    /// account storage, not the character's inventory.
    /// </summary>
    public sealed class Backpack
    {
        private readonly Dictionary<int, ClientUserItem> _inventory = new Dictionary<int, ClientUserItem>();
        private readonly Dictionary<int, ClientUserItem> _equipment = new Dictionary<int, ClientUserItem>();

        public int InventoryCount => _inventory.Count;
        public int EquippedCount => _equipment.Count;

        /// <summary>Gained items the bag had no room for. The server did not place them either.</summary>
        public int UntrackedGains;

        public void Reset(IEnumerable<ClientUserItem> items)
        {
            _inventory.Clear();
            _equipment.Clear();
            UntrackedGains = 0;

            if (items == null) return;
            foreach (ClientUserItem item in items) Set(item);
        }

        public void Set(ClientUserItem item)
        {
            if (item?.Info == null) return;

            // Items from S.ItemsGained arrive with Slot = -1: the server snapshots them before
            // assigning a slot (PlayerObject.cs:6081, then :6275). Server and client then place the
            // item by the same rule - merge into a matching stack, else lowest free slot - and stay
            // in agreement without any packet carrying the result. The bot applies the same rule so
            // its slot numbers match the server's; otherwise every later ItemUse/ItemMove would
            // address the wrong item.
            if (item.Slot < 0)
            {
                if (!Place(item)) UntrackedGains++;
                return;
            }

            if (item.Slot >= Globals.EquipmentOffSet)
                _equipment[item.Slot - Globals.EquipmentOffSet] = item;
            else
                _inventory[item.Slot] = item;
        }

        /// <summary>Replicates the server's placement rule (PlayerObject.cs:6240-6277).</summary>
        private bool Place(ClientUserItem item)
        {
            if (item.Info.StackSize > 1)
            {
                foreach (KeyValuePair<int, ClientUserItem> pair in _inventory.OrderBy(x => x.Key))
                {
                    ClientUserItem existing = pair.Value;
                    if (existing.Info != item.Info) continue;
                    if (existing.Count >= existing.Info.StackSize) continue;

                    long room = existing.Info.StackSize - existing.Count;

                    if (item.Count <= room)
                    {
                        existing.Count += item.Count;
                        return true;
                    }

                    item.Count -= room;
                    existing.Count = existing.Info.StackSize;
                }
            }

            for (int i = 0; i < Globals.InventorySize; i++)
            {
                if (_inventory.ContainsKey(i)) continue;

                item.Slot = i;
                _inventory[i] = item;
                return true;
            }

            return false;
        }

        private readonly Dictionary<int, ClientUserItem> _storage = new Dictionary<int, ClientUserItem>();

        public int StoredCount => _storage.Count;

        /// <summary>
        /// Account storage, which arrives in S.Login.Items - a different list from the character's
        /// own inventory in StartInformation.Items.
        /// </summary>
        public void ResetStorage(IEnumerable<ClientUserItem> items)
        {
            _storage.Clear();
            if (items == null) return;

            foreach (ClientUserItem item in items)
                if (item?.Info != null)
                    _storage[item.Slot] = item;
        }

        public IEnumerable<KeyValuePair<int, ClientUserItem>> Stored => _storage;

        /// <summary>Equipped items keyed by EquipmentSlot index. Read-only use only - this is
        /// the live dictionary, so never hand it to another thread.</summary>
        public IEnumerable<KeyValuePair<int, ClientUserItem>> Worn => _equipment;
        public IEnumerable<KeyValuePair<int, ClientUserItem>> Carried => _inventory;

        public void NoteDeposited(int fromSlot, int toSlot)
        {
            if (!_inventory.TryGetValue(fromSlot, out ClientUserItem item)) return;

            _inventory.Remove(fromSlot);
            item.Slot = toSlot;
            _storage[toSlot] = item;
        }

        public void NoteWithdrawn(int fromSlot, int toSlot)
        {
            if (!_storage.TryGetValue(fromSlot, out ClientUserItem item)) return;

            _storage.Remove(fromSlot);
            item.Slot = toSlot;
            _inventory[toSlot] = item;
        }

        public int FirstFreeStorageSlot(int storageSize)
        {
            for (int i = 0; i < storageSize; i++)
                if (!_storage.ContainsKey(i)) return i;

            return -1;
        }

        public int FirstFreeInventorySlot()
        {
            for (int i = 0; i < Globals.InventorySize; i++)
                if (!_inventory.ContainsKey(i)) return i;

            return -1;
        }

        /// <summary>
        /// Does the character currently satisfy the item's RequiredType/RequiredAmount gate?
        /// Mirrors the server's own check (PlayerObject.cs:7313+). Requirement types the bot cannot
        /// evaluate are treated as unmet, so the item is stored rather than wrongly worn or sold.
        /// </summary>
        public static bool MeetsRequirement(ItemInfo info, int level, Stats stats)
        {
            if (info == null) return false;
            if (stats == null) stats = new Stats();

            switch (info.RequiredType)
            {
                case RequiredType.Level: return level >= info.RequiredAmount;
                case RequiredType.MaxLevel: return level <= info.RequiredAmount;
                case RequiredType.AC: return stats[Stat.MaxAC] >= info.RequiredAmount;
                case RequiredType.MR: return stats[Stat.MaxMR] >= info.RequiredAmount;
                case RequiredType.DC: return stats[Stat.MaxDC] >= info.RequiredAmount;
                case RequiredType.MC: return stats[Stat.MaxMC] >= info.RequiredAmount;
                case RequiredType.SC: return stats[Stat.MaxSC] >= info.RequiredAmount;
                case RequiredType.Health: return stats[Stat.Health] >= info.RequiredAmount;
                case RequiredType.Mana: return stats[Stat.Mana] >= info.RequiredAmount;
                case RequiredType.Accuracy: return stats[Stat.Accuracy] >= info.RequiredAmount;
                case RequiredType.Agility: return stats[Stat.Agility] >= info.RequiredAmount;
                default: return false;
            }
        }

        /// <summary>
        /// Worth keeping for later: the right class and gender, but a requirement we do not meet
        /// yet. Levelling will unlock it, so it goes to the bank instead of the vendor. Skill books
        /// for our class count too - they are useless now and valuable later.
        /// </summary>
        public static bool WorthStoring(ClientUserItem item, MirClass mirClass, MirGender gender,
            int level, Stats stats, MagicBooks books, WorldModel world)
        {
            if (item?.Info == null) return false;

            // Item parts belong in the bank.
            //
            // They were falling through every rule and staying in the bag for ever: never sold
            // (DisposableSlots and Sellable both refuse them, deliberately - a part's own ItemInfo
            // is a nameless placeholder and it used to be sold for scrap), never equipped, and
            // never stored either, because SlotFor(ItemType.ItemPart) is null so CanEquip below
            // says no and WorthStoring returned false. Two of them sat in a warrior's bag through
            // a dozen town trips.
            //
            // They are worth keeping - parts accumulate towards Info.PartCount and are how the
            // good gear is assembled - so the bank is where they go rather than the bag.
            if (IsItemPart(item)) return true;

            // Books first: CanEquip reads ItemInfo.RequiredClass, which books normally leave at
            // All, so it would wave every class's books through. MagicBooks does the real test.
            if (item.Info.ItemType == ItemType.Book)
                return books != null &&
                       books.Judge(item, mirClass, level, stats, world) == BookVerdict.TooEarly;

            // Wrong class or gender is never going to change. Sell it.
            if (!CanEquip(item, mirClass, gender)) return false;

            // Already usable - it belongs in the bag, not the bank.
            if (MeetsRequirement(item.Info, level, stats)) return false;

            return SlotFor(item.Info.ItemType) != null;
        }

        /// <summary>One banked item that is worth pulling back out, and why.</summary>
        public readonly struct Reclaim
        {
            public readonly int Slot;
            public readonly ClientUserItem Item;
            public readonly string Why;

            public Reclaim(int slot, ClientUserItem item, string why)
            {
                Slot = slot;
                Item = item;
                Why = why;
            }
        }

        /// <summary>
        /// What in storage has become worth having since it was put there.
        ///
        /// Storage exists because of levelling: WorthStoring banks exactly the things this
        /// character cannot use YET, so the question at every town trip is which of them it can use
        /// now. That question was being asked, but only half of it - anything equippable and
        /// level-met was pulled out, whether or not it beat what the character had since found.
        /// A helmet banked at level 1 and reclaimed at level 20 is bag weight that gets carried to
        /// a vendor and sold, having displaced something worth looting on the way.
        ///
        /// So the bar is the same one the loot rule uses: it has to beat the WEAKEST slot it could
        /// displace. Books are judged by MagicBooks instead, because a book is not scored against
        /// anything - either the character can learn it now or it stays banked.
        /// </summary>
        public List<Reclaim> StorageReclaims(MirClass mirClass, MirGender gender, int level,
            Stats stats, MagicBooks books, WorldModel world)
        {
            List<Reclaim> found = new List<Reclaim>();

            foreach (KeyValuePair<int, ClientUserItem> pair in _storage.OrderBy(x => x.Key))
            {
                ClientUserItem item = pair.Value;
                if (item?.Info == null) continue;

                if (item.Info.ItemType == ItemType.Book)
                {
                    if (books?.Judge(item, mirClass, level, stats, world) == BookVerdict.Wanted)
                        found.Add(new Reclaim(pair.Key, item, "learnable now"));

                    continue;
                }

                if (!CanEquip(item, mirClass, gender)) continue;
                if (!MeetsRequirement(item.Info, level, stats)) continue;

                if (!BeatsWeakestSlot(item.Info.ItemType, Score(item, mirClass), mirClass)) continue;

                found.Add(new Reclaim(pair.Key, item, "beats what we are wearing"));
            }

            return found;
        }

        /// <summary>How many of this exact item are already banked - for "one of each" rules.</summary>
        public int StoredCountOf(ItemInfo info)
        {
            long total = 0;

            foreach (ClientUserItem item in _storage.Values)
                if (item.Info == info) total += item.Count;

            return (int)Math.Min(int.MaxValue, total);
        }

        public string DescribeStorage()
        {
            if (_storage.Count == 0) return "empty";

            return string.Join(", ", _storage
                .OrderBy(x => x.Key)
                .Select(x => $"{x.Key}:{x.Value.Info.ItemName}"));
        }

        /// <summary>The first carried book this character can learn right now, or -1.</summary>
        public int FindLearnableBookSlot(MagicBooks books, WorldModel world)
        {
            if (books == null || world == null) return -1;

            foreach (KeyValuePair<int, ClientUserItem> pair in _inventory.OrderBy(x => x.Key))
            {
                if (pair.Value.Info.ItemType != ItemType.Book) continue;

                if (books.Judge(pair.Value, world.Class, world.Level, world.PlayerStats, world)
                    == BookVerdict.Wanted)
                    return pair.Key;
            }

            return -1;
        }

        #region Durability

        /// <summary>
        /// Durability is stored at 1000x the displayed value for equipment: the client shows
        /// Math.Round(raw / 1000M) (GameScene.cs:2515). Raw 3 therefore DISPLAYS as 0 while not
        /// being broken - the server tests CurrentDurability == 0 exactly.
        /// </summary>
        public const int DurabilityScale = 1000;

        /// <summary>
        /// Does this item have durability at all? Amulets, torches, costumes and most cosmetic gear
        /// have Info.Durability == 0 and sit at CurrentDurability == 0 permanently - the server
        /// refuses to damage them (PlayerObject.cs:8611). Without this guard a "something is broken"
        /// test fires on the first tick after login and never stops.
        /// </summary>
        public static bool HasDurability(ClientUserItem item) =>
            item?.Info != null && item.Info.Durability != 0;

        public static bool IsBroken(ClientUserItem item) =>
            HasDurability(item) && item.CurrentDurability == 0;

        public static bool IsWorn(ClientUserItem item, int atOrBelowDisplayed) =>
            HasDurability(item) &&
            item.CurrentDurability < item.MaxDurability &&
            item.CurrentDurability <= atOrBelowDisplayed * DurabilityScale;

        /// <summary>Item types the server will repair (PlayerObject.NPCRepair).</summary>
        private static bool RepairableType(ItemType type)
        {
            switch (type)
            {
                case ItemType.Weapon:
                case ItemType.Armour:
                case ItemType.Helmet:
                case ItemType.Necklace:
                case ItemType.Bracelet:
                case ItemType.Ring:
                case ItemType.Shoes:
                case ItemType.Shield:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Will this NPC page repair this item? The server aborts the WHOLE batch on the first item
        /// that fails, exactly as NPCSell does, so the filtering has to happen here.
        /// </summary>
        public static bool Repairable(ClientUserItem item, IEnumerable<ItemType> accepted)
        {
            if (!HasDurability(item)) return false;
            if (!item.Info.CanRepair) return false;
            if (item.CurrentDurability >= item.MaxDurability) return false;
            if ((item.Flags & UserItemFlags.Marriage) == UserItemFlags.Marriage) return false;
            if (!RepairableType(item.Info.ItemType)) return false;

            if (accepted == null) return true;

            foreach (ItemType type in accepted)
                if (type == item.Info.ItemType) return true;

            return false;
        }

        public void NoteDurability(GridType grid, int slot, int current)
        {
            Dictionary<int, ClientUserItem> target =
                grid == GridType.Equipment ? _equipment :
                grid == GridType.Inventory ? _inventory : null;

            if (target == null) return;
            if (!target.TryGetValue(slot, out ClientUserItem item)) return;

            item.CurrentDurability = current;
        }

        public bool AnyBrokenEquipment() => _equipment.Values.Any(IsBroken);

        public IEnumerable<ItemType> DamagedTypes(int atOrBelowDisplayed) =>
            _equipment.Values
                .Where(x => IsWorn(x, atOrBelowDisplayed))
                .Select(x => x.Info.ItemType)
                .Distinct();

        /// <summary>Equipment slots this page will actually repair.</summary>
        /// <summary>
        /// Trim a repair request to what the purse will actually cover, and say what it costs.
        ///
        /// The server prices the WHOLE request and refuses all of it if the total exceeds our gold
        /// (PlayerObject.NPCRepair): it sums RepairCost over every link and returns without
        /// repairing anything. So asking to repair five items with the money for one does not get
        /// one repaired - it gets none, silently. A level 15 wizard with two broken items, three
        /// worn ones and 22 gold sat in exactly that state, unable to fix the weapon that was the
        /// reason it could not earn.
        ///
        /// The cost is computable here rather than guessed: ClientUserItem.RepairCost is the same
        /// arithmetic the server runs, over fields the bot already holds.
        ///
        /// Priority is BROKEN FIRST, then cheapest. That is lexicographic, not "as many items as
        /// possible" - one expensive broken item will displace several cheap worn ones, and that is
        /// deliberate. A broken item contributes no stats whatsoever, while a worn one still works;
        /// restoring the zero is worth more than tidying several near-misses.
        /// </summary>
        public List<int> AffordableRepairSlots(IEnumerable<int> slots, long gold, bool special,
            out long spend, out int skipped)
        {
            spend = 0;
            skipped = 0;

            List<(int Slot, long Cost, bool Broken)> priced =
                new List<(int, long, bool)>();

            foreach (int slot in slots ?? Enumerable.Empty<int>())
            {
                // A slot whose item or definition is missing would throw inside RepairCost, which
                // dereferences Info and AddedStats without checking.
                if (!_equipment.TryGetValue(slot, out ClientUserItem item)) continue;
                if (item?.Info == null || item.AddedStats == null) continue;

                long cost = item.RepairCost(special);
                if (cost <= 0) continue;

                priced.Add((slot, cost, IsBroken(item)));
            }

            // Broken before worn; cheapest first inside each group.
            priced.Sort((a, b) => a.Broken != b.Broken
                ? (a.Broken ? -1 : 1)
                : a.Cost.CompareTo(b.Cost));

            List<int> chosen = new List<int>();
            long running = 0;      // long: a batch of good gear can exceed an int comfortably

            foreach ((int slot, long cost, bool _) in priced)
            {
                if (running + cost > gold) { skipped++; continue; }

                running += cost;
                chosen.Add(slot);
            }

            spend = running;
            chosen.Sort();
            return chosen;
        }

        /// <summary>What one repair would cost, for reporting. 0 when the slot is not repairable.</summary>
        public long RepairCostOf(int slot, bool special) =>
            _equipment.TryGetValue(slot, out ClientUserItem item) &&
            item?.Info != null && item.AddedStats != null
                ? item.RepairCost(special)
                : 0;

        /// <summary>The worn name in a slot, for diagnostics.</summary>
        public string WornName(int slot) =>
            _equipment.TryGetValue(slot, out ClientUserItem item) ? item?.Info?.ItemName ?? "?" : "?";

        public List<int> DamagedEquipmentSlots(int atOrBelowDisplayed, IEnumerable<ItemType> accepted)
        {
            List<ItemType> list = accepted?.ToList();

            return _equipment
                .Where(x => IsWorn(x.Value, atOrBelowDisplayed) && Repairable(x.Value, list))
                .OrderBy(x => x.Key)
                .Select(x => x.Key)
                .ToList();
        }

        /// <summary>
        /// Apply the server's repair formula locally. Nothing reports a completed repair - no
        /// S.ItemDurability follows S.NPCRepair - so without this the bot still sees the old value,
        /// re-requests the repair, and the server answers RepairFailRepaired, which ABORTS THE WHOLE
        /// BATCH and silently kills the repair of every other item too.
        /// Normal repair burns max durability: Max -= (Max - Cur) / Globals.DuraLossRate (15).
        /// </summary>
        public void NoteRepaired(IEnumerable<int> equipmentSlots, bool special = false)
        {
            if (equipmentSlots == null) return;

            foreach (int slot in equipmentSlots.ToList())
            {
                if (!_equipment.TryGetValue(slot, out ClientUserItem item)) continue;

                // A special repair costs no maximum durability, which is the whole point of it.
                // Applying the ordinary formula here would shrink our local copy while the server
                // left the item alone, and every later repair decision would work off that.
                if (!special)
                    item.MaxDurability = Math.Max(0,
                        item.MaxDurability - (item.MaxDurability - item.CurrentDurability) / 15);

                item.CurrentDurability = item.MaxDurability;

                if (special) item.NextSpecialRepair = Time.Now + item.SpecialRepairCoolDown;
            }
        }

        public string DescribeDurability(int atOrBelowDisplayed)
        {
            List<string> parts = _equipment
                .Where(x => HasDurability(x.Value))
                .OrderBy(x => x.Key)
                .Select(x => $"{(EquipmentSlot)x.Key}:" +
                             $"{x.Value.CurrentDurability / DurabilityScale}/" +
                             $"{x.Value.MaxDurability / DurabilityScale}" +
                             (IsBroken(x.Value) ? " BROKEN" :
                              IsWorn(x.Value, atOrBelowDisplayed) ? " worn" : ""))
                .ToList();

            return parts.Count == 0 ? "no durability items" : string.Join(", ", parts);
        }

        #endregion

        #region Potions

        /// <summary>
        /// The server's own auto-potion predicate (PlayerObject.cs:544): an item heals if
        /// Info.Stats[Stat.Health] > 0. Nothing is inferred from the item's name.
        /// </summary>
        public static bool IsHealthPotion(ClientUserItem item) =>
            item?.Info != null &&
            item.Info.ItemType == ItemType.Consumable &&
            item.Info.Stats[Stat.Health] > 0;

        public int FindHealthPotionSlot()
        {
            foreach (KeyValuePair<int, ClientUserItem> pair in _inventory.OrderBy(x => x.Key))
                if (pair.Key >= 0 && IsHealthPotion(pair.Value) && pair.Value.Count > 0)
                    return pair.Key;

            return -1;
        }

        /// <summary>
        /// Same test on the other pool. A mana potion restores mana and not health - the ordering
        /// matters, because some consumables restore both and those are health potions as far as
        /// the bot is concerned.
        /// </summary>
        public static bool IsManaPotion(ClientUserItem item) =>
            item?.Info != null &&
            item.Info.ItemType == ItemType.Consumable &&
            item.Info.Stats[Stat.Health] <= 0 &&
            item.Info.Stats[Stat.Mana] > 0;

        public int FindManaPotionSlot()
        {
            foreach (KeyValuePair<int, ClientUserItem> pair in _inventory.OrderBy(x => x.Key))
                if (pair.Key >= 0 && IsManaPotion(pair.Value) && pair.Value.Count > 0)
                    return pair.Key;

            return -1;
        }

        /// <summary>
        /// Drop slots we just sold. S.ItemsChanged is echoed before the server validates the sale
        /// (PlayerObject.cs:10267), so it is not a reliable success signal; the local model is
        /// updated optimistically instead and corrected by the next authoritative update. Without
        /// this the bot re-offers the same item on every subsequent trip.
        /// </summary>
        public void NoteSold(IEnumerable<int> slots)
        {
            if (slots == null) return;

            foreach (int slot in slots.ToList())
                _inventory.Remove(slot);
        }

        public void NoteUsed(int slot)
        {
            if (!_inventory.TryGetValue(slot, out ClientUserItem item)) return;

            item.Count--;
            if (item.Count <= 0) _inventory.Remove(slot);
        }

        #endregion

        /// <summary>
        /// Consumable with Shape 2 is a Town Teleport scroll; Shape 0 is a potion
        /// (PlayerObject.ItemUse, the switch on Info.Shape).
        /// </summary>
        public int FindTownTeleportSlot()
        {
            foreach (KeyValuePair<int, ClientUserItem> pair in _inventory.OrderBy(x => x.Key))
            {
                ClientUserItem item = pair.Value;
                if (item.Info.ItemType != ItemType.Consumable) continue;
                if (item.Info.Shape != 2 || item.Count <= 0) continue;

                return pair.Key;
            }

            return -1;
        }

        /// <summary>
        /// Is this item on the ground worth the weight? This is what stops the bag filling with the
        /// Commoner Outfits and Wood Swords that every low-level mob drops.
        /// </summary>
        /// <summary>
        /// Is this item on the ground worth the weight? This is what stops the bag filling with the
        /// Commoner Outfits and Wood Swords that every low-level mob drops.
        ///
        /// When the full instance is known (S.ObjectItem carries one) its added stats are scored
        /// too, so a special roll is not mistaken for a plain drop. S.DataObjectItem gives only the
        /// definition; there the score is a floor and a good roll on a poor base can be passed over.
        /// </summary>
        public bool WorthLooting(ItemInfo info, ClientUserItem instance, MirClass mirClass,
            MirGender gender, bool heavy, bool full, int healthReserve, int manaReserve,
            long gold, LootValueRule value)
        {
            if (info == null) return !heavy;

            // A town scroll is the only way home, so it is worth its weight even at capacity.
            bool isConsumable = info.ItemType == ItemType.Consumable;
            if (isConsumable && info.Shape == TownTeleportShape) return true;

            // Gold and anything else weightless is free to carry - the bag never notices it, so
            // there is nothing to weigh the value against and no reason ever to refuse it. Gold in
            // particular drops as an ordinary ground item, and is the whole point of the exercise.
            if (WeightOf(info, instance) <= 0) return true;

            // Over capacity, take nothing else at all. Exempting consumables from this - as an
            // earlier version did - let the bag climb past 110% on looted potions alone, and being
            // overweight blocks running, so the bot ends up slow AND unable to carry anything.
            if (full) return false;

            if (isConsumable)
            {
                // Potions are the difference between fighting safely and dying, but they are not
                // weightless: past a reserve they are just cargo.
                //
                // Counted by ROLE, across every tier. Counting this exact ItemInfo - as this did
                // originally - means "Healing Potion" and "Healing Potion (II)" each get their own
                // reserve, so a bot with ten of each keeps looting both. A level 10 wizard hoarded
                // seventeen and spent its life at 105% weight, unable to run, while the restock
                // step (which counts them together) saw no shortage at all.
                bool healing = info.Stats[Stat.Health] > 0;

                int held = healing ? CountHealthPotions() : CountManaPotions();
                int reserve = healing ? healthReserve : manaReserve;

                return held < reserve;
            }

            RequiredGender genderFlag = gender == MirGender.Female
                ? RequiredGender.Female
                : RequiredGender.Male;

            // Gear for the other gender can never be worn, but it still sells.
            if (!info.RequiredGender.HasFlag(genderFlag))
                return WorthSelling(info, instance, heavy, gold, value);

            // Books are never cargo: a book is a skill we want now, one to bank until we are high
            // enough, or a sale. That judgement is made in the bag, so always pick one up.
            //
            // The code used to say !heavy, which contradicted the comment directly above it and
            // meant the bot stopped taking books at seventy percent weight - on Deserted Mine,
            // where skill books are the whole reason to be there. A book is light and is either a
            // skill, a banked skill, or the best-paying sale a monster drops; none of those stop
            // being true because the bag is filling. Capacity is still respected: the `full` test
            // above has already refused everything at a hundred percent.
            if (info.ItemType == ItemType.Book) return true;

            if (SlotFor(info.ItemType) != null)
            {
                int candidate = instance != null
                    ? Score(instance, mirClass)
                    : ScoreInfo(info, mirClass);

                // An upgrade is taken whatever it is worth. Anything else is judged as cargo:
                // gear we cannot use is still the most valuable thing most monsters drop.
                if (BeatsWeakestSlot(info.ItemType, candidate, mirClass)) return true;
            }

            // Everything else - ore, meat, gear we already beat - has to pay for its weight.
            return WorthSelling(info, instance, heavy, gold, value);
        }

        /// <summary>Weight this drop would actually add, which for a stack means the whole stack.</summary>
        private static int WeightOf(ItemInfo info, ClientUserItem instance)
        {
            if (instance?.Info != null) return instance.Weight;

            return info?.Weight ?? 0;
        }

        /// <summary>
        /// Worth carrying purely to sell it?
        ///
        /// Before this existed the bot took non-equipment drops whenever the bag was not yet heavy
        /// and refused them once it was - it had no idea what anything was worth, so a gemstone and
        /// a rotten carcass were the same decision. Now the sale value the vendor would actually
        /// pay is weighed against the space it costs, on the sliding bar in LootValueRule.
        ///
        /// When only the definition is known (S.DataObjectItem carries no instance) the value is
        /// estimated from the base price, which is a floor: a good roll is worth more than this.
        /// </summary>
        private static bool WorthSelling(ItemInfo info, ClientUserItem instance, bool heavy,
            long gold, LootValueRule value)
        {
            // No rule configured - fall back to the old behaviour rather than looting nothing.
            if (value == null) return !heavy;

            long price;

            if (instance?.Info != null)
                price = instance.Price(Math.Max(1L, instance.Count));
            else
                price = (long)(info.Price * info.SellRate);

            // Quest items and the like are flagged Worthless and sell for nothing.
            if (price <= 0) return false;

            int weight = Math.Max(1, WeightOf(info, instance));

            return price / weight >= value.MinimumPerWeight(gold, heavy);
        }

        public static int ScoreInfo(ItemInfo info, MirClass mirClass)
        {
            if (info == null) return 0;

            Stats stats = info.Stats;

            if (info.ItemType == ItemType.Weapon)
            {
                switch (mirClass)
                {
                    case MirClass.Wizard: return stats[Stat.MaxMC] * 10 + stats[Stat.MinMC];
                    case MirClass.Taoist: return stats[Stat.MaxSC] * 10 + stats[Stat.MinSC];
                    default: return stats[Stat.MaxDC] * 10 + stats[Stat.MinDC];
                }
            }

            int score = stats[Stat.MaxAC] * 4 + stats[Stat.MaxMR] * 4 +
                        stats[Stat.Health] + stats[Stat.Mana];

            switch (mirClass)
            {
                case MirClass.Wizard: score += stats[Stat.MaxMC] * 8; break;
                case MirClass.Taoist: score += stats[Stat.MaxSC] * 8; break;
                default: score += stats[Stat.MaxDC] * 8; break;
            }

            return score;
        }

        public const int TownTeleportShape = 2;

        public long CountInSlot(int slot) =>
            _inventory.TryGetValue(slot, out ClientUserItem item) ? item.Count : 1;

        /// <summary>Total healing potions carried, across stacks and tiers.</summary>
        public int CountHealthPotions()
        {
            long total = 0;

            foreach (ClientUserItem item in _inventory.Values)
                if (IsHealthPotion(item)) total += item.Count;

            return (int)Math.Min(int.MaxValue, total);
        }

        /// <summary>Mana potions carried, across every tier - the mirror of CountHealthPotions.</summary>
        public int CountManaPotions()
        {
            long total = 0;

            foreach (ClientUserItem item in _inventory.Values)
                if (item?.Info != null &&
                    item.Info.ItemType == ItemType.Consumable &&
                    item.Info.Shape != TownTeleportShape &&
                    item.Info.Stats[Stat.Health] <= 0 &&
                    item.Info.Stats[Stat.Mana] > 0) total += item.Count;

            return (int)Math.Min(int.MaxValue, total);
        }

        public int CountTownScrolls()
        {
            long total = 0;

            foreach (ClientUserItem item in _inventory.Values)
                if (item.Info.ItemType == ItemType.Consumable &&
                    item.Info.Shape == TownTeleportShape) total += item.Count;

            return (int)Math.Min(int.MaxValue, total);
        }

        /// <summary>How many of this exact item are in the bag, counting stacks.</summary>
        public int CountOf(ItemInfo info)
        {
            long total = 0;

            foreach (ClientUserItem item in _inventory.Values)
                if (item.Info == info) total += item.Count;

            return (int)Math.Min(int.MaxValue, total);
        }

        /// <summary>
        /// Inventory slots holding nothing the bot wants to keep - the sell list for when a
        /// vendor trip is implemented. Potions up to the reserve, town scrolls and anything
        /// currently worn are never included.
        /// </summary>
        /// <summary>
        /// Can this NPC page actually buy this item? The server checks Info.CanSell and requires the
        /// item's type to be in the page's Types list, and it aborts the entire sale on the first
        /// failure rather than skipping that one item - so the filtering has to happen here.
        /// </summary>
        /// <summary>
        /// Is this a piece of something bigger?
        ///
        /// A part carries a generic ItemInfo; the item it builds towards is ItemIndex on the
        /// instance's AddedStats, exactly as the client resolves it for drawing. The bot sold an
        /// Iron Shield part because nothing here knew that.
        /// </summary>
        /// <summary>
        /// Worn slots eligible for a SPECIAL repair right now.
        ///
        /// Special repair restores durability without touching the maximum. Ordinary repair sets
        /// MaxDurability -= (Max - Current) / DuraLossRate first, so every normal repair
        /// permanently shrinks the item - repeat it enough and good gear becomes worthless.
        /// Special costs twice as much and has a per-item cooldown, which the client can see as
        /// NextSpecialRepair, so items still cooling down are left out rather than discovered by
        /// having the whole batch rejected.
        /// </summary>
        public List<int> SpecialRepairSlots(IEnumerable<ItemType> accepted)
        {
            List<int> slots = new List<int>();

            // Repairable already applies every gate the server does - durability, CanRepair, the
            // allowed item types and this page's own Types list.
            List<ItemType> types = accepted?.ToList();

            foreach (KeyValuePair<int, ClientUserItem> pair in _equipment.OrderBy(x => x.Key))
            {
                ClientUserItem item = pair.Value;

                if (!Repairable(item, types)) continue;

                // Still cooling down from its last special repair. One such item rejects the WHOLE
                // batch server-side, so they are filtered here rather than discovered.
                if (item.NextSpecialRepair > Time.Now) continue;

                slots.Add(pair.Key);
            }

            return slots;
        }

        /// <summary>
        /// How much better would this shop item be than the slot it would replace?
        ///
        /// Returns the score gain, or 0 when it is not an upgrade at all. Only the definition is
        /// known for something on a shelf - a shop item has no roll yet - so this is the same floor
        /// used when judging a drop we can only see from a distance.
        /// </summary>
        public int UpgradeGain(ItemInfo info, MirClass mirClass, MirGender gender, int level,
            Stats stats)
        {
            if (info == null) return 0;
            if (!CanEquipInfo(info, mirClass, gender)) return 0;
            if (!MeetsRequirement(info, level, stats)) return 0;

            int candidate = ScoreInfo(info, mirClass);
            if (candidate <= 0) return 0;

            int weakest = int.MaxValue;
            bool any = false;

            foreach (EquipmentSlot option in SlotsFor(info.ItemType))
            {
                any = true;

                // An empty slot is measured against nothing, so anything wearable is a gain.
                if (!_equipment.TryGetValue((int)option, out ClientUserItem worn)) return candidate;

                int current = Score(worn, mirClass);
                if (current < weakest) weakest = current;
            }

            if (!any || weakest == int.MaxValue) return 0;

            return candidate > weakest ? candidate - weakest : 0;
        }

        /// <summary>The worn score this item type would have to beat, for sizing an upgrade.</summary>
        public int WeakestWornScore(ItemType type, MirClass mirClass)
        {
            int weakest = int.MaxValue;

            foreach (EquipmentSlot option in SlotsFor(type))
            {
                if (!_equipment.TryGetValue((int)option, out ClientUserItem worn)) return 0;

                int current = Score(worn, mirClass);
                if (current < weakest) weakest = current;
            }

            return weakest == int.MaxValue ? 0 : weakest;
        }

        /// <summary>Class and gender check against a definition rather than an instance.</summary>
        public static bool CanEquipInfo(ItemInfo info, MirClass mirClass, MirGender gender)
        {
            if (info == null) return false;
            if (SlotFor(info.ItemType) == null) return false;

            RequiredGender genderFlag = gender == MirGender.Female
                ? RequiredGender.Female
                : RequiredGender.Male;

            if (!info.RequiredGender.HasFlag(genderFlag)) return false;

            return CanClassUseInfo(info, mirClass);
        }

        /// <summary>
        /// Is there a torch burning?
        ///
        /// Torches carry durability on this server and are consumed as they burn, so an empty slot
        /// is the normal end state rather than an error. A torch at zero is treated as no torch:
        /// it is about to go out and is worth replacing on this trip rather than the next one.
        /// </summary>
        public bool HasTorchEquipped
        {
            get
            {
                if (!_equipment.TryGetValue((int)EquipmentSlot.Torch, out ClientUserItem torch))
                    return false;

                if (torch?.Info == null) return false;

                return torch.Info.Durability == 0 || torch.CurrentDurability > 0;
            }
        }

        /// <summary>A torch already in the bag, waiting to be equipped.</summary>
        public bool CarryingTorch
        {
            get
            {
                foreach (ClientUserItem item in _inventory.Values)
                    if (item?.Info != null && item.Info.ItemType == ItemType.Torch) return true;

                return false;
            }
        }

        public static bool IsItemPart(ClientUserItem item) =>
            item?.Info != null && item.Info.ItemEffect == ItemEffect.ItemPart;

        /// <summary>The item a part builds towards, or null when it cannot be resolved.</summary>
        public static ItemInfo PartTarget(ClientUserItem item)
        {
            if (!IsItemPart(item)) return null;

            int index = item.AddedStats?[Stat.ItemIndex] ?? 0;
            if (index <= 0) return null;

            return Globals.ItemInfoList?.Binding?.FirstOrDefault(x => x.Index == index);
        }

        /// <summary>"Iron Shield (part 1/5)" rather than the useless "[Part]".</summary>
        public static string Describe(ClientUserItem item)
        {
            if (item?.Info == null) return "nothing";

            ItemInfo target = PartTarget(item);

            if (target == null) return item.Info.ItemName;

            return $"{target.ItemName} (part {item.Count}/{target.PartCount})";
        }

        public static bool Sellable(ClientUserItem item, IEnumerable<ItemType> accepted)
        {
            if (item?.Info == null) return false;

            // Belt and braces: even if a part reached a sell batch some other way, refuse it here.
            if (IsItemPart(item)) return false;

            if (!item.Info.CanSell) return false;
            if ((item.Flags & UserItemFlags.Locked) == UserItemFlags.Locked) return false;
            if ((item.Flags & UserItemFlags.Marriage) == UserItemFlags.Marriage) return false;
            if ((item.Flags & UserItemFlags.Worthless) == UserItemFlags.Worthless) return false;

            if (accepted == null) return true;

            foreach (ItemType type in accepted)
                if (type == item.Info.ItemType) return true;

            return false;
        }

        /// <summary>
        /// Slots holding something we would sell if it were not locked.
        ///
        /// Locking is a player convenience - it stops an item being sold by accident - and the
        /// server unlocks on request with no cost and no checks beyond the slot being real. A bot
        /// that respects the flag but can never clear it just accumulates: sixteen tier one potions
        /// sat locked and unsellable in a bag that had no room for loot.
        /// </summary>
        public List<int> LockedSlots(IEnumerable<int> amongSlots)
        {
            List<int> locked = new List<int>();

            if (amongSlots == null) return locked;

            foreach (int slot in amongSlots)
            {
                ClientUserItem item = InSlot(slot);

                if (item?.Info == null) continue;
                if ((item.Flags & UserItemFlags.Locked) != UserItemFlags.Locked) continue;

                locked.Add(slot);
            }

            return locked;
        }

        /// <summary>Apply an unlock locally; the server echoes S.ItemLock to confirm.</summary>
        public void NoteUnlocked(int slot)
        {
            ClientUserItem item = InSlot(slot);

            if (item == null) return;

            item.Flags &= ~UserItemFlags.Locked;
        }

        /// <summary>
        /// Every slot we intend to dispose of, locked or not. DisposableSlots filters locked items
        /// out via Sellable, so this is the wider list used to decide what to unlock first.
        /// </summary>
        public List<int> DisposableIncludingLocked(MirClass mirClass, MirGender gender, int level,
            Stats stats, int healthReserve, int manaReserve, int scrollReserve,
            MagicBooks books, WorldModel world)
        {
            List<int> all = DisposableSlots(mirClass, gender, level, stats,
                healthReserve, manaReserve, scrollReserve, books, world);

            // DisposableSlots already walks everything; locked items reach it and are kept only
            // because Sellable refuses them later. Re-scan for those.
            foreach (KeyValuePair<int, ClientUserItem> pair in _inventory)
            {
                ClientUserItem item = pair.Value;

                if (item?.Info == null) continue;
                if (all.Contains(pair.Key)) continue;
                if ((item.Flags & UserItemFlags.Locked) != UserItemFlags.Locked) continue;

                // Only consumables past their reserve - never gear we might still want.
                if (item.Info.ItemType != ItemType.Consumable) continue;

                bool healing = item.Info.Stats[Stat.Health] > 0;
                bool scroll = item.Info.Shape == TownTeleportShape;

                int unit = Math.Max(1, item.Info.Weight);
                int reserve = scroll ? scrollReserve
                    : (healing ? healthReserve : manaReserve) / unit;

                int held = scroll ? CountTownScrolls()
                    : healing ? CountHealthPotions() : CountManaPotions();

                if (held > Math.Max(1, reserve)) all.Add(pair.Key);
            }

            return all;
        }

        public ClientUserItem InSlot(int slot) =>
            _inventory.TryGetValue(slot, out ClientUserItem item) ? item : null;

        /// <summary>
        /// Which consumable stacks to keep, deciding across ALL tiers at once and keeping the best.
        ///
        /// The reserve used to be tracked per ItemInfo, so every tier got its own full allowance:
        /// a bot holding nineteen tier four potions and three tier one still counted the tier ones
        /// as "under reserve" and never sold them. They were not locked out by the Locked flag at
        /// all - that was a red herring - they simply never appeared in the disposal list.
        ///
        /// Now the allowance is a single budget for healing, one for mana and one for scrolls. The
        /// strongest potion is kept first, and whatever the budget will not stretch to is surplus,
        /// which is exactly the weak tier we want to be rid of.
        /// </summary>
        private HashSet<int> PlanConsumableKeeps(int healthReserve, int manaReserve,
            int scrollReserve)
        {
            HashSet<int> keep = new HashSet<int>();

            List<KeyValuePair<int, ClientUserItem>> scrolls =
                new List<KeyValuePair<int, ClientUserItem>>();
            List<KeyValuePair<int, ClientUserItem>> health =
                new List<KeyValuePair<int, ClientUserItem>>();
            List<KeyValuePair<int, ClientUserItem>> mana =
                new List<KeyValuePair<int, ClientUserItem>>();

            foreach (KeyValuePair<int, ClientUserItem> pair in _inventory)
            {
                ClientUserItem item = pair.Value;

                if (item?.Info == null || item.Info.ItemType != ItemType.Consumable) continue;

                if (item.Info.Shape == TownTeleportShape) scrolls.Add(pair);
                else if (item.Info.Stats[Stat.Health] > 0) health.Add(pair);
                else if (item.Info.Stats[Stat.Mana] > 0) mana.Add(pair);
                else keep.Add(pair.Key);      // food and oddities: not ours to judge here
            }

            // Strongest first, so the budget is spent on the potions worth carrying.
            health.Sort((a, b) => b.Value.Info.Stats[Stat.Health]
                                   .CompareTo(a.Value.Info.Stats[Stat.Health]));
            mana.Sort((a, b) => b.Value.Info.Stats[Stat.Mana]
                                 .CompareTo(a.Value.Info.Stats[Stat.Mana]));

            Fill(keep, scrolls, scrollReserve, 1);

            // Reserves arrive sized for a weight-one potion. Scale by what the BEST tier we hold
            // weighs, so the budget stays a share of the bag rather than a share per tier.
            Fill(keep, health, healthReserve, health.Count == 0
                ? 1 : Math.Max(1, health[0].Value.Info.Weight));

            Fill(keep, mana, manaReserve, mana.Count == 0
                ? 1 : Math.Max(1, mana[0].Value.Info.Weight));

            return keep;
        }

        /// <summary>Keep whole stacks in the given order until the budget is spent.</summary>
        private static void Fill(HashSet<int> keep,
            List<KeyValuePair<int, ClientUserItem>> stacks, int reserve, int unitWeight)
        {
            int target = Math.Max(1, reserve / Math.Max(1, unitWeight));
            int held = 0;

            foreach (KeyValuePair<int, ClientUserItem> pair in stacks)
            {
                if (held >= target) return;      // everything after this is surplus

                keep.Add(pair.Key);
                held += (int)pair.Value.Count;
            }
        }

        public List<int> DisposableSlots(MirClass mirClass, MirGender gender, int level,
            Stats stats, int healthReserve, int manaReserve, int scrollReserve,
            MagicBooks books, WorldModel world)
        {
            List<int> slots = new List<int>();
            HashSet<ItemInfo> keptBooks = new HashSet<ItemInfo>();

            HashSet<int> keepConsumables = PlanConsumableKeeps(healthReserve, manaReserve,
                scrollReserve);

            foreach (KeyValuePair<int, ClientUserItem> pair in _inventory.OrderBy(x => x.Key))
            {
                ClientUserItem item = pair.Value;

                if (item.Info.ItemType == ItemType.Consumable)
                {
                    if (!keepConsumables.Contains(pair.Key)) slots.Add(pair.Key);
                    continue;
                }

                if (item.Info.ItemType == ItemType.Book)
                {
                    BookVerdict verdict = books?.Judge(item, mirClass, level, stats, world)
                                          ?? BookVerdict.Junk;

                    if (verdict == BookVerdict.Wanted) continue;   // about to be learnt

                    if (verdict == BookVerdict.TooEarly)
                    {
                        // One of each only: a copy already banked, or an earlier slot already
                        // claimed the keep, makes this one a duplicate to sell.
                        if (StoredCountOf(item.Info) == 0 && keptBooks.Add(item.Info)) continue;
                    }

                    slots.Add(pair.Key);   // Junk / WrongClass / AlreadyKnown / duplicate
                    continue;
                }

                // An item part is never junk. Its own ItemInfo is a generic placeholder - the
                // real item is named by AddedStats[Stat.ItemIndex] - so it looks like a nameless
                // trinket to every rule here and used to be sold. Parts accumulate towards
                // Info.PartCount and are how the good gear is assembled, so they are always kept.
                if (IsItemPart(item)) continue;

                // Keep anything the bank should hold for later.
                if (WorthStoring(item, mirClass, gender, level, stats, books, world)) continue;

                EquipmentSlot? slot = SlotFor(item.Info.ItemType);

                if (slot != null &&
                    CanEquip(item, mirClass, gender) &&
                    _equipment.TryGetValue((int)slot.Value, out ClientUserItem worn) &&
                    Score(item, mirClass) > Score(worn, mirClass))
                    continue;   // a genuine upgrade we have not equipped yet

                slots.Add(pair.Key);
            }

            return slots;
        }

        #region Equipping

        private static EquipmentSlot? SlotFor(ItemType type)
        {
            switch (type)
            {
                case ItemType.Weapon: return EquipmentSlot.Weapon;
                case ItemType.Armour: return EquipmentSlot.Armour;
                case ItemType.Helmet: return EquipmentSlot.Helmet;
                case ItemType.Torch: return EquipmentSlot.Torch;
                case ItemType.Necklace: return EquipmentSlot.Necklace;
                case ItemType.Bracelet: return EquipmentSlot.BraceletL;
                case ItemType.Ring: return EquipmentSlot.RingL;
                case ItemType.Shoes: return EquipmentSlot.Shoes;
                case ItemType.Poison: return EquipmentSlot.Poison;
                case ItemType.Amulet: return EquipmentSlot.Amulet;
                default: return null;
            }
        }

        /// <summary>
        /// Every equipment slot an item type can occupy. Rings and bracelets come in pairs, so
        /// returning only the left one leaves half the character's accessory slots permanently empty.
        /// </summary>
        private static IEnumerable<EquipmentSlot> SlotsFor(ItemType type)
        {
            switch (type)
            {
                case ItemType.Ring:
                    yield return EquipmentSlot.RingL;
                    yield return EquipmentSlot.RingR;
                    break;
                case ItemType.Bracelet:
                    yield return EquipmentSlot.BraceletL;
                    yield return EquipmentSlot.BraceletR;
                    break;
                default:
                    EquipmentSlot? single = SlotFor(type);
                    if (single != null) yield return single.Value;
                    break;
            }
        }

        /// <summary>
        /// Would this beat something we are wearing, across every slot the type can occupy?
        ///
        /// Rings and bracelets have a left and a right slot, so asking only about the left one - as
        /// the loot check originally did - walks the bot past a real upgrade whenever the left slot
        /// happens to be good. A Horned Ring on the ground was refused because an identical Horned
        /// Ring was worn on the left, while the right slot held a Taoist ring worth nothing to a
        /// warrior. The bar is therefore the WEAKEST slot the item could displace, matching what
        /// PendingEquips would actually do with it once carried.
        /// </summary>
        private bool BeatsWeakestSlot(ItemType type, int candidate, MirClass mirClass)
        {
            int weakest = int.MaxValue;

            foreach (EquipmentSlot option in SlotsFor(type))
            {
                // An empty slot is always worth filling, whatever it scores.
                if (!_equipment.TryGetValue((int)option, out ClientUserItem worn))
                    return true;

                int current = Score(worn, mirClass);
                if (current < weakest) weakest = current;
            }

            // No slot at all - not equipment as far as we are concerned.
            if (weakest == int.MaxValue) return false;

            return candidate > weakest;
        }

        /// <summary>
        /// Can this character wear it at all? Class AND gender: opposite-gender gear can never be
        /// equipped, so keeping it as a "pending upgrade" just wastes bag weight forever.
        /// </summary>
        public static bool CanEquip(ClientUserItem item, MirClass mirClass, MirGender gender)
        {
            if (item?.Info == null) return false;

            RequiredGender genderFlag = gender == MirGender.Female
                ? RequiredGender.Female
                : RequiredGender.Male;

            if (!item.Info.RequiredGender.HasFlag(genderFlag)) return false;

            return CanClassUse(item, mirClass);
        }

        /// <summary>The server's own RequiredClass gate, usable on a bare ItemInfo.</summary>
        public static bool CanClassUseInfo(ItemInfo info, MirClass mirClass)
        {
            if (info == null) return false;
            if (info.RequiredClass == RequiredClass.All) return true;

            RequiredClass flag;
            switch (mirClass)
            {
                case MirClass.Warrior: flag = RequiredClass.Warrior; break;
                case MirClass.Wizard: flag = RequiredClass.Wizard; break;
                case MirClass.Taoist: flag = RequiredClass.Taoist; break;
                case MirClass.Assassin: flag = RequiredClass.Assassin; break;
                default: return false;
            }

            return info.RequiredClass.HasFlag(flag);
        }

        private static bool CanClassUse(ClientUserItem item, MirClass mirClass)
        {
            if (item.Info.RequiredClass == RequiredClass.All) return true;

            RequiredClass flag;
            switch (mirClass)
            {
                case MirClass.Warrior: flag = RequiredClass.Warrior; break;
                case MirClass.Wizard: flag = RequiredClass.Wizard; break;
                case MirClass.Taoist: flag = RequiredClass.Taoist; break;
                case MirClass.Assassin: flag = RequiredClass.Assassin; break;
                default: return false;
            }

            return item.Info.RequiredClass.HasFlag(flag);
        }

        /// <summary>
        /// How good an item is for a class, in one number. Weapons are judged on the class's primary
        /// damage stat; everything else on its defensive contribution. Crude on purpose - it only
        /// ever orders two items competing for the same slot, never across slots.
        /// </summary>
        /// <summary>
        /// A stat's real value on a specific item: the base definition plus whatever this instance
        /// rolled. Items can carry per-instance bonuses (a base DC 0-1 sword rolling +1 special is
        /// really DC 0-2), and those live in ClientUserItem.AddedStats, NOT in Info.Stats. Scoring
        /// the definition alone makes every special drop look identical to a plain one.
        /// </summary>
        private static int Total(ClientUserItem item, Stat stat) =>
            item.Info.Stats[stat] + (item.AddedStats?[stat] ?? 0);

        public static int Score(ClientUserItem item, MirClass mirClass)
        {
            if (item?.Info == null) return 0;

            if (item.Info.ItemType == ItemType.Weapon)
            {
                switch (mirClass)
                {
                    case MirClass.Wizard:
                        return Total(item, Stat.MaxMC) * 10 + Total(item, Stat.MinMC);
                    case MirClass.Taoist:
                        return Total(item, Stat.MaxSC) * 10 + Total(item, Stat.MinSC);
                    default:
                        return Total(item, Stat.MaxDC) * 10 + Total(item, Stat.MinDC);
                }
            }

            int score = Total(item, Stat.MaxAC) * 4 + Total(item, Stat.MaxMR) * 4 +
                        Total(item, Stat.Health) + Total(item, Stat.Mana);

            switch (mirClass)
            {
                case MirClass.Wizard: score += Total(item, Stat.MaxMC) * 8; break;
                case MirClass.Taoist: score += Total(item, Stat.MaxSC) * 8; break;
                default: score += Total(item, Stat.MaxDC) * 8; break;
            }

            return score;
        }

        /// <summary>
        /// Items worth equipping: empty slots first, then straight upgrades. A candidate only
        /// replaces a worn item when it scores strictly higher for this class, so the bot can never
        /// downgrade itself. Rings and bracelets are paired: an empty side is preferred, otherwise
        /// the weaker side is displaced.
        ///
        /// The server validates every move, so a request it refuses simply does nothing.
        /// </summary>
        public List<EquipRequest> PendingEquips(MirClass mirClass, MirGender gender)
        {
            List<EquipRequest> requests = new List<EquipRequest>();
            HashSet<int> claimed = new HashSet<int>();

            foreach (KeyValuePair<int, ClientUserItem> pair in _inventory.OrderBy(x => x.Key))
            {
                ClientUserItem item = pair.Value;

                if (!CanEquip(item, mirClass, gender)) continue;

                int candidateScore = Score(item, mirClass);

                // Rings and bracelets have a left and a right slot. Prefer an empty one; otherwise
                // displace whichever side is currently weaker.
                EquipmentSlot? chosen = null;
                string reason = null;
                int weakest = int.MaxValue;

                foreach (EquipmentSlot option in SlotsFor(item.Info.ItemType))
                {
                    int index = (int)option;
                    if (claimed.Contains(index)) continue;

                    if (!_equipment.TryGetValue(index, out ClientUserItem worn))
                    {
                        chosen = option;
                        reason = "empty slot";
                        break;
                    }

                    int current = Score(worn, mirClass);
                    if (current >= candidateScore || current >= weakest) continue;

                    weakest = current;
                    chosen = option;
                    reason = $"upgrade over {worn.Info.ItemName} ({current} -> {candidateScore})";
                }

                if (chosen == null) continue;

                claimed.Add((int)chosen.Value);
                requests.Add(new EquipRequest
                {
                    FromSlot = pair.Key,
                    ToSlot = (int)chosen.Value,
                    ItemName = item.Info.ItemName,
                    Slot = chosen.Value,
                    Reason = reason
                });
            }

            return requests;
        }

        /// <summary>Optimistically apply an equip locally; the server's next update corrects us.</summary>
        public void NoteEquipped(EquipRequest request)
        {
            if (!_inventory.TryGetValue(request.FromSlot, out ClientUserItem item)) return;

            _inventory.Remove(request.FromSlot);

            // A swap sends the displaced item back to the slot we just vacated, which is what the
            // server's ItemMove does too.
            if (_equipment.TryGetValue(request.ToSlot, out ClientUserItem worn))
            {
                worn.Slot = request.FromSlot;
                _inventory[request.FromSlot] = worn;
            }

            _equipment[request.ToSlot] = item;
        }

        #endregion

        public string DescribeEquipment()
        {
            if (_equipment.Count == 0) return "nothing equipped";

            return string.Join(", ", _equipment
                .OrderBy(x => x.Key)
                .Select(x => $"{(EquipmentSlot)x.Key}: {x.Value.Info.ItemName}"));
        }

        public string DescribeInventory()
        {
            if (_inventory.Count == 0) return "empty";

            return string.Join(", ", _inventory
                .OrderBy(x => x.Key)
                .Select(x => $"{x.Key}:{x.Value.Info.ItemName}" +
                             (x.Value.Count > 1 ? $" x{x.Value.Count}" : "")));
        }
    }
}
