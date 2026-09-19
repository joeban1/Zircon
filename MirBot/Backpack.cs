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
        public void NoteRepaired(IEnumerable<int> equipmentSlots)
        {
            if (equipmentSlots == null) return;

            foreach (int slot in equipmentSlots.ToList())
            {
                if (!_equipment.TryGetValue(slot, out ClientUserItem item)) continue;

                item.MaxDurability = Math.Max(0,
                    item.MaxDurability - (item.MaxDurability - item.CurrentDurability) / 15);
                item.CurrentDurability = item.MaxDurability;
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
            MirGender gender, bool heavy, bool full, int healthReserve, int manaReserve)
        {
            if (info == null) return !heavy;

            // A town scroll is the only way home, so it is worth its weight even at capacity.
            bool isConsumable = info.ItemType == ItemType.Consumable;
            if (isConsumable && info.Shape == TownTeleportShape) return true;

            // Over capacity, take nothing else at all. Exempting consumables from this - as an
            // earlier version did - let the bag climb past 110% on looted potions alone, and being
            // overweight blocks running, so the bot ends up slow AND unable to carry anything.
            if (full) return false;

            if (isConsumable)
            {
                // Potions are the difference between fighting safely and dying, but they are not
                // weightless: past a reserve they are just cargo.
                int held = CountOf(info);
                int reserve = info.Stats[Stat.Health] > 0 ? healthReserve : manaReserve;

                return held < reserve;
            }

            RequiredGender genderFlag = gender == MirGender.Female
                ? RequiredGender.Female
                : RequiredGender.Male;

            if (!info.RequiredGender.HasFlag(genderFlag)) return !heavy;

            EquipmentSlot? slot = SlotFor(info.ItemType);

            if (slot != null)
            {
                int index = (int)slot.Value;

                if (!_equipment.TryGetValue(index, out ClientUserItem worn))
                    return true;

                int candidate = instance != null
                    ? Score(instance, mirClass)
                    : ScoreInfo(info, mirClass);

                return candidate > Score(worn, mirClass);
            }

            // Everything else (ore, meat, junk) only while there is room to spare.
            return !heavy;
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
        public static bool Sellable(ClientUserItem item, IEnumerable<ItemType> accepted)
        {
            if (item?.Info == null) return false;
            if (!item.Info.CanSell) return false;
            if ((item.Flags & UserItemFlags.Locked) == UserItemFlags.Locked) return false;
            if ((item.Flags & UserItemFlags.Marriage) == UserItemFlags.Marriage) return false;
            if ((item.Flags & UserItemFlags.Worthless) == UserItemFlags.Worthless) return false;

            if (accepted == null) return true;

            foreach (ItemType type in accepted)
                if (type == item.Info.ItemType) return true;

            return false;
        }

        public ClientUserItem InSlot(int slot) =>
            _inventory.TryGetValue(slot, out ClientUserItem item) ? item : null;

        public List<int> DisposableSlots(MirClass mirClass, MirGender gender, int level,
            Stats stats, int healthReserve, int manaReserve, int scrollReserve,
            MagicBooks books, WorldModel world)
        {
            List<int> slots = new List<int>();
            Dictionary<ItemInfo, int> kept = new Dictionary<ItemInfo, int>();
            HashSet<ItemInfo> keptBooks = new HashSet<ItemInfo>();

            foreach (KeyValuePair<int, ClientUserItem> pair in _inventory.OrderBy(x => x.Key))
            {
                ClientUserItem item = pair.Value;

                if (item.Info.ItemType == ItemType.Consumable)
                {
                    // Every consumable has a reserve now, scrolls included: below it we buy, above
                    // it we sell. Keeping scrolls unconditionally meant a stack could only ever grow.
                    int reserve = item.Info.Shape == TownTeleportShape
                        ? scrollReserve
                        : item.Info.Stats[Stat.Health] > 0 ? healthReserve : manaReserve;

                    kept.TryGetValue(item.Info, out int already);

                    if (already < reserve)
                    {
                        kept[item.Info] = already + (int)item.Count;
                        continue;
                    }

                    slots.Add(pair.Key);
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
        /// downgrade itself. Bracelets and rings only fill the left slot - pairing is not worth it.
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
