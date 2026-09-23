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

        /// <summary>Topping up the stack already worn rather than replacing it.</summary>
        public bool Merge;

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

        /// <summary>
        /// Slots still free. Globals.InventorySize is a flat 48 on this server - PlayerObject
        /// allocates `new UserItem[Globals.InventorySize]` with no expansion - so this is the whole
        /// story, and weight is a SEPARATE limit that can be nowhere near its own cap while every
        /// slot is taken. Light junk fills slots without moving the scales.
        /// </summary>
        public int FreeSlotCount => Math.Max(0, Globals.InventorySize - _inventory.Count);

        /// <summary>
        /// Would the server let us pick this up? Mirrors PlayerObject.CanGainItems with
        /// checkWeight false, which is how ItemObject.PickUpItem calls it - weight is enforced
        /// elsewhere, slots are enforced here.
        ///
        /// This matters because a refusal is SILENT. ItemObject.PickUpItem simply returns false:
        /// no packet, no message, the item stays on the ground. A bot that does not model this
        /// walks onto the item, sends C.PickUp, sees the item still there, and sends it again -
        /// for ever. Bot 1 was found standing still doing exactly that with 48 of 48 slots used
        /// and the bag only three-quarters of its weight limit.
        /// </summary>
        public bool HasRoomFor(ItemInfo info, ClientUserItem instance)
        {
            if (info == null) return false;

            // Things that never occupy a slot. Currency is the important one: gold drops as an
            // ordinary ground item, and refusing to collect it because the bag is full would be
            // exactly backwards - gold is what pays for the trip that empties the bag.
            if (info.ItemEffect == ItemEffect.Experience) return true;
            if (IsCurrency(info)) return true;

            UserItemFlags flags = instance?.Flags ?? UserItemFlags.None;

            if ((flags & UserItemFlags.QuestItem) == UserItemFlags.QuestItem) return true;

            if (FreeSlotCount > 0) return true;

            // No free slot, so the only way in is merging onto a stack that has room. An expirable
            // item never merges (the server refuses to pool different expiry times).
            if (info.StackSize <= 1) return false;
            if ((flags & UserItemFlags.Expirable) == UserItemFlags.Expirable) return false;

            foreach (ClientUserItem existing in _inventory.Values)
            {
                if (existing?.Info != info) continue;
                if (existing.Count >= info.StackSize) continue;
                if ((existing.Flags & UserItemFlags.Expirable) == UserItemFlags.Expirable) continue;
                if ((existing.Flags & UserItemFlags.Bound) != (flags & UserItemFlags.Bound)) continue;
                if ((existing.Flags & UserItemFlags.Worthless) != (flags & UserItemFlags.Worthless)) continue;
                if ((existing.Flags & UserItemFlags.NonRefinable) != (flags & UserItemFlags.NonRefinable)) continue;

                // ADDED STATS, the sixth condition - the one Place() applies and this did not.
                //
                // The server requires oldItem.Stats.Compare(item.Stats) before merging
                // (PlayerObject.cs:5498). Without it this predicts a merge that Place() will then
                // refuse, so the bot walks to a drop it has no room for, fails to pick it up, and
                // tries again - and its idea of remaining capacity is wrong the whole time.
                if (!SameStats(existing.AddedStats, instance?.AddedStats)) continue;

                return true;
            }

            return false;
        }

        private static bool IsCurrency(ItemInfo info) =>
            Globals.CurrencyInfoList?.Binding != null &&
            Globals.CurrencyInfoList.Binding.Any(x => x.DropItem == info);
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

        /// <summary>
        /// Does this arrive in the BAG at all?
        ///
        /// S.ItemsGained is sent BEFORE the server decides what to do with each item
        /// (PlayerObject.cs:5441), and three kinds never reach the inventory: experience orbs are
        /// converted, quest items are credited to the task and deleted, and currency is added to
        /// a counter. The real client skips exactly these three in GameScene.AddItems
        /// (GameScene.cs:3584-3597).
        ///
        /// Modelling them as bag items is the mirror image of the optimistic-use bug just fixed:
        /// an entry we hold that the server does not, occupying a slot number and pushing every
        /// later placement out of step. HasRoomFor has always known these do not take a slot
        /// (see its currency comment); Set did not.
        /// </summary>
        public static bool OccupiesASlot(ClientUserItem item)
        {
            if (item?.Info == null) return false;
            if (item.Info.ItemEffect == ItemEffect.Experience) return false;
            if ((item.Flags & UserItemFlags.QuestItem) == UserItemFlags.QuestItem) return false;
            if (IsCurrency(item.Info)) return false;

            return true;
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
        /// <summary>
        /// Two stacks the SERVER would merge. All six of its conditions, in its order.
        ///
        /// GainItem (PlayerObject.cs:5489-5513) refuses to merge unless the Expirable, Bound,
        /// Worthless and NonRefinable flags all match AND the added stats compare equal. This copy
        /// tested two of the six - same ItemInfo, room in the stack - and merged everything else.
        ///
        /// That is not a cosmetic difference, because nothing ever corrects it. The server does not
        /// send the slot it chose: items arrive from S.ItemsGained with Slot = -1 and both sides
        /// place them by "the same rule", so the rule being the same IS the synchronisation. Loot a
        /// Bound potion onto an unbound stack and we merge it into slot 3 while the server opens
        /// slot 30; from that moment our slot map is wrong, in both directions at once - a count
        /// too high here, an item we cannot see there.
        ///
        /// Every sell order touching a wrong slot is then voided in silence, all-or-nothing, and
        /// only a relog repairs it. Measured on a level 22 wizard: our model held slots 0-29 with a
        /// Candle at 29, the server held 0-9 and 11-32 with a Necklace at 29. It ran 6,757 times
        /// in one session, which is why drift appeared within the hour and why the busiest bot
        /// drifted fastest.
        /// </summary>
        private static bool WouldMerge(ClientUserItem existing, ClientUserItem item)
        {
            if (existing?.Info == null) return false;
            if (existing.Info != item.Info) return false;
            if (existing.Count >= existing.Info.StackSize) return false;

            if ((existing.Flags & UserItemFlags.Expirable) == UserItemFlags.Expirable) return false;

            if ((existing.Flags & UserItemFlags.Bound) != (item.Flags & UserItemFlags.Bound))
                return false;
            if ((existing.Flags & UserItemFlags.Worthless) != (item.Flags & UserItemFlags.Worthless))
                return false;
            if ((existing.Flags & UserItemFlags.NonRefinable) != (item.Flags & UserItemFlags.NonRefinable))
                return false;

// Added stats too - the sixth and last of the server's conditions.
            //
            // I removed this once, on five refusals in five minutes that looked like a regression,
            // and that was the wrong call on a sample too small to read. The decisive evidence came
            // from outside the bot: the server's own GUI showed slot 1 holding NINETEEN potions
            // while the game client showed TWENTY-ONE. Our count runs HIGH, which is precisely what
            // over-merging produces - we fold two into an existing stack that the server keeps
            // separate - and it is fatal to selling, because NPCSell refuses the whole order when
            // link.Count exceeds the server's count (PlayerObject.cs:9565).
            //
            // Matching the server exactly is correct by construction; the A/B that argued against
            // it was noise.
            return SameStats(existing.AddedStats, item.AddedStats);
        }

        /// <summary>Stats.Compare, tolerating the nulls a client item may carry.</summary>
        private static bool SameStats(Stats a, Stats b)
        {
            if (ReferenceEquals(a, b)) return true;

            return (a ?? new Stats()).Compare(b ?? new Stats());
        }

        private bool Place(ClientUserItem item)
        {
            // Expirable items never stack at all, on either side.
            if (item.Info.StackSize > 1 &&
                (item.Flags & UserItemFlags.Expirable) != UserItemFlags.Expirable)
            {
                // Slot order, because the server walks its Inventory array in slot order and the
                // first stack with room wins. Iterating any other way picks a different stack.
                foreach (KeyValuePair<int, ClientUserItem> pair in _inventory.OrderBy(x => x.Key))
                {
                    ClientUserItem existing = pair.Value;

                    if (!WouldMerge(existing, item)) continue;

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
            _partsStorage.Clear();
            if (items == null) return;

            // Two grids, not one. The server keeps item parts in a SEPARATE PartsStorage array and
            // distinguishes them by slot: anything at or above Globals.PartsStorageOffset (2000)
            // belongs to it (PlayerObject.cs:194). Treating them as one grid is what made every
            // part deposit fail - the bot asked to put a part into GridType.Storage, the server
            // declined without a word, and the bot's own model happily recorded it as banked.
            foreach (ClientUserItem item in items)
            {
                if (item?.Info == null) continue;

                if (item.Slot >= Globals.PartsStorageOffset)
                    _partsStorage[item.Slot - Globals.PartsStorageOffset] = item;
                else
                    _storage[item.Slot] = item;
            }
        }

        private readonly Dictionary<int, ClientUserItem> _partsStorage =
            new Dictionary<int, ClientUserItem>();

        public int PartsStoredCount => _partsStorage.Count;

        public IEnumerable<KeyValuePair<int, ClientUserItem>> PartsStored => _partsStorage;

        public IEnumerable<KeyValuePair<int, ClientUserItem>> Stored => _storage;

        /// <summary>Which grid this item belongs in. Parts have their own.</summary>
        public static bool StoresInPartsGrid(ClientUserItem item) => IsItemPart(item);

        public int FirstFreePartsStorageSlot(int storageSize)
        {
            for (int i = 0; i < storageSize; i++)
                if (!_partsStorage.ContainsKey(i)) return i;

            return -1;
        }

        /// <summary>Equipped items keyed by EquipmentSlot index. Read-only use only - this is
        /// the live dictionary, so never hand it to another thread.</summary>
        public IEnumerable<KeyValuePair<int, ClientUserItem>> Worn => _equipment;
        public IEnumerable<KeyValuePair<int, ClientUserItem>> Carried => _inventory;

        public void NoteDeposited(int fromSlot, int toSlot, bool parts, bool merge)
        {
            if (!_inventory.TryGetValue(fromSlot, out ClientUserItem item)) return;

            Dictionary<int, ClientUserItem> destination = parts ? _partsStorage : _storage;

            if (merge && destination.TryGetValue(toSlot, out ClientUserItem existing))
            {
                long moved = Math.Min(item.Count, existing.Info.StackSize - existing.Count);
                if (moved <= 0) return;

                existing.Count += moved;
                item.Count -= moved;

                if (item.Count <= 0) _inventory.Remove(fromSlot);
                return;
            }

            _inventory.Remove(fromSlot);
            item.Slot = toSlot;

            destination[toSlot] = item;
        }

        public void NoteWithdrawn(int fromSlot, int toSlot, bool parts)
        {
            Dictionary<int, ClientUserItem> source = parts ? _partsStorage : _storage;
            if (!source.TryGetValue(fromSlot, out ClientUserItem item)) return;

            source.Remove(fromSlot);
            item.Slot = toSlot;
            _inventory[toSlot] = item;
        }

        /// <summary>Apply a server-confirmed merge inside PartsStorage.</summary>
        public void NotePartsMerged(int fromSlot, int toSlot)
        {
            if (!_partsStorage.TryGetValue(fromSlot, out ClientUserItem from)) return;
            if (!_partsStorage.TryGetValue(toSlot, out ClientUserItem to)) return;

            long moved = Math.Min(from.Count, to.Info.StackSize - to.Count);
            if (moved <= 0) return;

            to.Count += moved;
            from.Count -= moved;

            if (from.Count <= 0) _partsStorage.Remove(fromSlot);
        }

        /// <summary>An occupied parts-storage slot this stack can merge into, or -1.</summary>
        public int MergeablePartsStorageSlot(ClientUserItem item) => _partsStorage
            .OrderByDescending(x => x.Value.Count)
            .Where(x => WouldMerge(x.Value, item))
            .Select(x => x.Key)
            .DefaultIfEmpty(-1)
            .First();

        /// <summary>Two existing PartsStorage slots that can be consolidated, or null.</summary>
        public (int From, int To)? PendingPartMerge()
        {
            foreach (KeyValuePair<int, ClientUserItem> to in _partsStorage
                         .OrderByDescending(x => x.Value.Count).ThenBy(x => x.Key))
            {
                foreach (KeyValuePair<int, ClientUserItem> from in _partsStorage
                             .Where(x => x.Key != to.Key).OrderBy(x => x.Key))
                {
                    if (WouldMerge(to.Value, from.Value)) return (from.Key, to.Key);
                }
            }

            return null;
        }

        public KeyValuePair<int, ClientUserItem>? CompletableStoredPart()
        {
            foreach (KeyValuePair<int, ClientUserItem> pair in _partsStorage.OrderBy(x => x.Key))
            {
                ItemInfo target = PartTarget(pair.Value);
                if (target != null && target.PartCount > 0 && pair.Value.Count >= target.PartCount)
                    return pair;
            }

            return null;
        }

        public KeyValuePair<int, ClientUserItem>? CompletableCarriedPart()
        {
            foreach (KeyValuePair<int, ClientUserItem> pair in _inventory.OrderBy(x => x.Key))
            {
                ItemInfo target = PartTarget(pair.Value);
                if (target != null && target.PartCount > 0 && pair.Value.Count >= target.PartCount)
                    return pair;
            }

            return null;
        }

        /// <summary>Enough fragments exist to finish at least one item, even across slots.</summary>
        public bool HasCompletableParts()
        {
            return _partsStorage.Values.Concat(_inventory.Values)
                .Where(IsItemPart)
                .Select(x => new { Item = x, Target = PartTarget(x) })
                .Where(x => x.Target != null && x.Target.PartCount > 0)
                .GroupBy(x => x.Target.Index)
                .Any(g => g.Sum(x => x.Item.Count) >= g.First().Target.PartCount);
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
        /// Could this character ever meet the item's requirement?
        ///
        /// Only the stats a class actually builds count. The three offensive stats are each one
        /// class's primary and near-zero for the others, so a requirement on the wrong one is
        /// permanent rather than temporary. AC, MR, Health, Accuracy and Agility come from gear
        /// and levels for everybody, so those stay eligible.
        /// </summary>
        private static bool RequirementCanGrow(ItemInfo info, MirClass mirClass, int level)
        {
            switch (info.RequiredType)
            {
                // Already past the cap and levels only go up, so this can never come back.
                case RequiredType.MaxLevel: return level <= info.RequiredAmount;

                case RequiredType.DC: return mirClass == MirClass.Warrior ||
                                             mirClass == MirClass.Assassin;

                case RequiredType.MC: return mirClass == MirClass.Wizard;

                case RequiredType.SC: return mirClass == MirClass.Taoist;

                default: return true;
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

            // Supplies are for the bag, not future equipment. This also guards against a
            // consumable with an unusual level requirement entering the storage path.
            if (item.Info.ItemType == ItemType.Consumable) return false;

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

            // The whole premise of storing something is that we will GROW into it. That holds for
            // a level requirement and it does not hold for a requirement on a stat this class does
            // not build: a warrior's MaxMC stays near zero for ever, so a necklace gated on MC is
            // not a future upgrade, it is a vendor item that happens to be unequippable today.
            //
            // CanEquip does not catch this because such items are usually RequiredClass.All - they
            // are not forbidden to a warrior, merely pointless - so the only thing standing between
            // them and the bank was a requirement that would never be met. A warrior banked a
            // Platinum Necklace this way.
            if (!RequirementCanGrow(item.Info, mirClass, level)) return false;

            // WOULD WE EVER WANT IT, not merely could we one day wear it.
            //
            // Every test above asks about ELIGIBILITY - right class, right gender, a requirement
            // that will be met eventually. None of them asks whether the item is any use, and for
            // a stat this class does not fight with the answer is no at every level. A Taoist
            // banked a DC sword and an MC necklace: the sword's requirement is on DC, which a
            // Taoist does grow, just uselessly, so RequirementCanGrow waved it through - the guard
            // that catches the mirror case of a warrior banking an MC-gated necklace.
            //
            // ScoreInfo is the same class-aware valuation the gear buyer uses, so this agrees with
            // the rest of the bot by construction: a pure-DC weapon scores 0 for a Taoist because
            // the weapon branch reads MaxSC, and a necklace carrying only MC scores 0 because the
            // accessory branch adds MaxSC and counts AC, MR, HP and MP - which any class wants -
            // separately. Anything with real armour or resistance on it therefore still stores.
            if (ScoreInfo(item.Info, mirClass) <= 0) return false;

            return SlotFor(item.Info.ItemType) != null;
        }

        /// <summary>
        /// A future item must be useful for this class AND beat an actual worn slot. A little AC
        /// on an MC ring can make its score positive for a Taoist without making it an upgrade.
        /// Books and parts are intentionally exempt from the gear comparison.
        /// </summary>
        public bool ShouldBank(ClientUserItem item, MirClass mirClass, MirGender gender,
            int level, Stats stats, MagicBooks books, WorldModel world)
        {
            if (!WorthStoring(item, mirClass, gender, level, stats, books, world)) return false;
            if (item.Info.ItemType == ItemType.Book || IsItemPart(item)) return true;
            return BeatsWeakestSlot(item.Info.ItemType, Score(item, mirClass), mirClass);
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
                    BookVerdict verdict = books?.Judge(item, mirClass, level, stats, world)
                                          ?? BookVerdict.Junk;
                    if (verdict == BookVerdict.Wanted)
                        found.Add(new Reclaim(pair.Key, item, "learnable now"));
                    else if (!RetainUnlearnedBook(verdict))
                        found.Add(new Reclaim(pair.Key, item, "unwanted book - selling"));

                    continue;
                }

                // Recover legacy deposits. An already-usable potion has no equipment slot, so
                // the generic gear reclaim path below would leave it in storage forever.
                if (item.Info.ItemType == ItemType.Consumable &&
                    (IsHealthPotion(item) || IsManaPotion(item)))
                {
                    found.Add(new Reclaim(pair.Key, item, "potion belongs in the bag"));
                    continue;
                }

                // Anything the bank should never have taken comes back out to be sold. Storing is
                // a bet that levelling will unlock the item, and a bet on a stat this class does
                // not build never comes in - a warrior banked a Platinum Necklace on exactly that
                // mistake and, once the rule was fixed, it would have sat there for ever, because
                // the only way out of storage used to be becoming USABLE.
                if (IsItemPart(item)) continue;

                if (!CanEquip(item, mirClass, gender))
                {
                    found.Add(new Reclaim(pair.Key, item, "should not have been banked - selling"));
                    continue;
                }

                if (!MeetsRequirement(item.Info, level, stats))
                {
                    if (!ShouldBank(item, mirClass, gender, level, stats, books, world))
                        found.Add(new Reclaim(pair.Key, item, "not a future upgrade - selling"));
                    continue;
                }

                found.Add(new Reclaim(pair.Key, item,
                    BeatsWeakestSlot(item.Info.ItemType, Score(item, mirClass), mirClass)
                        ? "beats what we are wearing"
                        : "obsolete stored gear - selling"));
            }

            return found;
        }

        /// <summary>
        /// Both grids. This line is logged at login straight from the server's own S.Login.Items,
        /// which makes it the ONE authoritative view of storage the bot has - everything else is
        /// its optimistic local model, which records a refused deposit as a success. Reporting only
        /// the ordinary grid made it say "empty" while parts storage held items, which is precisely
        /// the kind of confident wrong answer that hid the parts bug in the first place.
        /// </summary>
        public string DescribeStorage()
        {
            if (_storage.Count == 0 && _partsStorage.Count == 0) return "empty";

            string main = string.Join(", ", _storage
                .OrderBy(x => x.Key)
                .Select(x => $"{x.Key}:{x.Value.Info.ItemName}"));

            string parts = string.Join(", ", _partsStorage
                .OrderBy(x => x.Key)
                .Select(x => $"{Globals.PartsStorageOffset + x.Key}:{x.Value.Info.ItemName}"));

            if (main.Length == 0) return $"(parts) {parts}";
            if (parts.Length == 0) return main;

            return $"{main} | (parts) {parts}";
        }

        /// <summary>The first carried book this character can learn right now, or -1.</summary>
        /// <summary>
        /// Drop an item the server has destroyed or removed. See Process(S.ItemDelete).
        /// </summary>
        public void NoteDeleted(GridType grid, int slot)
        {
            if (grid == GridType.Equipment) _equipment.Remove(slot);
            else if (grid == GridType.Inventory) _inventory.Remove(slot);
        }

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
        /// Durability as the GAME shows it. Raw values are scaled by a thousand, and the client
        /// rounds the display up, so anything still hanging on shows as 1 rather than 0.
        ///
        /// Integer division truncates, which is a different number and a worse one. An operator
        /// reading the status page saw a helmet at "0/8" that the game showed as 1/8, and a weapon
        /// at 12/22 that the game showed as 13/22 - every item reading one low, and the helmet
        /// looking broken when it was not. Truncation also disagrees with IsBroken, which tests
        /// CurrentDurability == 0 exactly because that is what the server tests, so an item could
        /// be displayed as broken and correctly reported as not broken on the same line.
        /// </summary>
        public static int Displayed(int raw) =>
            raw <= 0 ? 0 : (raw + DurabilityScale - 1) / DurabilityScale;

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
                             $"{Displayed(x.Value.CurrentDurability)}/" +
                             $"{Displayed(x.Value.MaxDurability)}" +
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
        /// <summary>
        /// Below this much restored, a consumable is loot rather than medicine. Set from
        /// BotConfig.MinPotionRestore; 0 disables the test.
        ///
        /// Chicken Blood is the case that needed it: ItemType.Consumable with Health 5 and Mana
        /// 5, so every potion test in the bot said yes. It counted towards the health potion
        /// budget, displacing real potions that heal a hundred and ten, and the reserve then
        /// protected it from being sold - a permanent slot occupied by ten gold of vendor trash.
        /// Its own stats give the game away: SaleBonus5, SaleBonus10, SaleBonus15 and SaleBonus20
        /// mean it is designed to be sold in bulk, not drunk.
        ///
        /// Tier one heals thirty and restores forty mana, so a floor of twenty keeps every real
        /// potion on the server and excludes the trash.
        /// </summary>
        public static int MinPotionRestore = 20;

        private static bool RestoresEnough(ClientUserItem item, Stat stat) =>
            MinPotionRestore <= 0 || item.Info.Stats[stat] >= MinPotionRestore;

        /// <summary>
        /// A consumable that restores something, but too little to be worth carrying.
        ///
        /// The distinction matters because the consumable planner keeps anything it cannot
        /// classify - "food and oddities: not ours to judge here" - so merely excluding Chicken
        /// Blood from the potion buckets moved it from one protected list to another and it still
        /// never sold. This says positively that we DO know what it is and it is not worth a slot.
        ///
        /// Restoring nothing at all still falls through to the keep list: that is genuinely
        /// unknown territory (food, quest oddities) and not ours to sell on a guess.
        /// </summary>
        public static bool IsWeakRestorative(ClientUserItem item) =>
            item?.Info != null &&
            item.Info.ItemType == ItemType.Consumable &&
            item.Info.Shape != TownTeleportShape &&
            (item.Info.Stats[Stat.Health] > 0 || item.Info.Stats[Stat.Mana] > 0) &&
            !IsHealthPotion(item) && !IsManaPotion(item);

        public static bool IsHealthPotion(ClientUserItem item) =>
            item?.Info != null &&
            item.Info.ItemType == ItemType.Consumable &&
            item.Info.Stats[Stat.Health] > 0 &&
            RestoresEnough(item, Stat.Health);

        /// <summary>
        /// Amulets and poison: ammunition for spells, not jewellery.
        ///
        /// Zircon consumes these from the EQUIPMENT slot and matches them by exact Shape -
        /// UseAmulet reads Equipment[EquipmentSlot.Amulet] (PlayerObject.cs:7963) and UsePoison the
        /// Poison slot (:7933). A Taoist with no amulet equipped cannot summon, cannot shield and
        /// cannot poison, and the server refuses each attempt in silence.
        ///
        /// They must never be scored as gear. A nine-stack and a one-stack of the same amulet have
        /// identical stats, so any upgrade comparison calls them equal and the spare is sold as
        /// junk - which is how a caster ends up unable to cast while standing next to its own
        /// reagents in a vendor window.
        /// </summary>
        public static bool IsReagent(ClientUserItem item) =>
            item?.Info != null &&
            (item.Info.ItemType == ItemType.Amulet || item.Info.ItemType == ItemType.Poison);

        /// <summary>How many of this reagent type we hold, carried and equipped together.</summary>
        public int CountReagent(ItemType type)
        {
            long total = 0;

            foreach (ClientUserItem item in _inventory.Values)
                if (item?.Info != null && item.Info.ItemType == type) total += item.Count;

            foreach (ClientUserItem item in _equipment.Values)
                if (item?.Info != null && item.Info.ItemType == type) total += item.Count;

            return (int)Math.Min(int.MaxValue, total);
        }

        /// <summary>
        /// Reagent count the server can actually consume. Zircon's UsePoison and UseAmulet read
        /// only their equipment slots; a stack in the bag is stock, not usable ammunition.
        /// </summary>
        public int EquippedReagentCount(ItemType type)
        {
            EquipmentSlot? slot = SlotFor(type);
            if (slot == null) return 0;

            return _equipment.TryGetValue((int)slot.Value, out ClientUserItem item) &&
                   item?.Info?.ItemType == type
                ? (int)Math.Min(int.MaxValue, item.Count)
                : 0;
        }

        public bool HasHealthPotion() => FindHealthPotionSlot(int.MaxValue) >= 0;

        /// <summary>
        /// The best health potion to drink right now, or -1 when we carry none.
        ///
        /// This used to return the LOWEST SLOT holding any health potion, which is an arbitrary
        /// choice dressed up as a decision. A wizard carrying tier 2 at slot 16 and thirteen tier 1
        /// at slot 38 drank slot 16 every single time, and slot 38 was unreachable for as long as
        /// any tier 2 remained - so the weak stack was never touched, and buying stronger potions
        /// in town made it worse by filling a lower slot.
        ///
        /// Ranked instead: the smallest potion that still covers what we are missing, because that
        /// heals fully without throwing the surplus away, and the LARGEST available when nothing
        /// covers it, because at that point the bot is in trouble and wants the biggest drink it
        /// has. Either way it never refuses to drink while it is holding something.
        ///
        /// Pass the deficit rather than the world: Backpack must not hold a reference to mutable
        /// player health.
        /// </summary>
        public int FindHealthPotionSlot(int missingHealth) =>
            BestPotionSlot(missingHealth, IsHealthPotion, Stat.Health);

        /// <summary>The biggest health potion carried, for when survival beats economy.</summary>
        public int FindBiggestHealthPotionSlot() =>
            BestPotionSlot(int.MaxValue, IsHealthPotion, Stat.Health);

        /// <summary>The same ranking on the other pool.</summary>
        public int FindManaPotionSlot(int missingMana) =>
            BestPotionSlot(missingMana, IsManaPotion, Stat.Mana);

        public bool HasManaPotion() => FindManaPotionSlot(int.MaxValue) >= 0;

        /// <summary>
        /// The heal a given slot would deliver, or 0. Lets the caller decide whether drinking it
        /// now would waste most of it.
        /// </summary>
        public int RestoreAmount(int slot, bool health)
        {
            if (!_inventory.TryGetValue(slot, out ClientUserItem item) || item?.Info == null) return 0;

            return item.Info.Stats[health ? Stat.Health : Stat.Mana];
        }

        private int BestPotionSlot(int missing, Func<ClientUserItem, bool> predicate, Stat restores)
        {
            int covering = -1, coveringSize = int.MaxValue;
            int largest = -1, largestSize = -1;
            int smallest = -1, smallestSize = int.MaxValue;

            // Ordered so ties resolve to the lowest slot, which keeps the choice deterministic
            // and therefore reproducible when reading a log back.
            foreach (KeyValuePair<int, ClientUserItem> pair in _inventory.OrderBy(x => x.Key))
            {
                if (pair.Key < 0 || pair.Value.Count <= 0) continue;
                if (!predicate(pair.Value)) continue;

                int heals = pair.Value.Info.Stats[restores];
                if (heals <= 0) continue;

                if (heals >= missing && heals < coveringSize)
                {
                    coveringSize = heals;
                    covering = pair.Key;
                }

                if (heals > largestSize)
                {
                    largestSize = heals;
                    largest = pair.Key;
                }

                if (heals < smallestSize)
                {
                    smallestSize = heals;
                    smallest = pair.Key;
                }
            }

            // Smallest that covers the deficit: heals fully, wastes nothing.
            if (covering >= 0) return covering;

            // Nothing covers it. Prefer the SMALLEST rather than the largest - it is the closest
            // fit to what is actually missing, and the surplus of a big potion is thrown away
            // against the health cap. The caller decides whether the situation is desperate enough
            // to want the biggest thing available regardless.
            return smallest >= 0 ? smallest : largest;
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
            item.Info.Stats[Stat.Mana] > 0 &&
            RestoresEnough(item, Stat.Mana);


        /// <summary>
        /// Drop what the server CONFIRMED it took, and nothing else.
        ///
        /// The earlier version of this ran the moment the packet was sent, on the reading that
        /// S.ItemsChanged is echoed before the sale is validated and so cannot be trusted. The echo
        /// part is true; the conclusion was wrong. The server enqueues the packet OBJECT and
        /// serialises it later (BaseConnection.cs:156, SendList.Enqueue), then sets Success on that
        /// same object once the sale goes through - so what reaches us does carry the verdict.
        ///
        /// Trusting the send instead cost two hours of bot time. NPCSell on this server is
        /// all-or-nothing: its validation loop RETURNS on the first bad link rather than skipping
        /// it, so one locked, worthless, zero-count or already-gone slot voids the entire order,
        /// silently. Cross thirty-five slots off on send, have the server take none, and the bag
        /// still weighs what it weighed while the model believes it is nearly empty. A level 24
        /// warrior spent twenty-four consecutive town trips reporting "0 sellable of 7 slots" while
        /// actually carrying forty-two, because it could no longer see its own inventory. Nothing
        /// recovered it short of a relog, which rebuilds the model from StartInformation.Items.
        ///
        /// Erring the other way is cheap: an item we wrongly believe we still hold is re-offered on
        /// the next trip and sold then. An item we wrongly believe is gone is invisible for ever.
        /// </summary>
        public void NoteSoldConfirmed(IEnumerable<CellLinkInfo> links)
        {
            if (links == null) return;

            foreach (CellLinkInfo link in links)
            {
                if (link == null || link.GridType != GridType.Inventory) continue;
                if (!_inventory.TryGetValue(link.Slot, out ClientUserItem item)) continue;

                // Partial sales are possible - ParseLinks merges duplicate slots and the server
                // decrements rather than removing when it takes less than the stack.
                item.Count -= link.Count;

                if (item.Count <= 0) _inventory.Remove(link.Slot);
            }
        }

        /// <summary>
        /// Set a slot to what the server says is LEFT in it after a drop.
        ///
        /// Remaining, not removed. S.ItemChanged comes back with Link.Count set to what stays
        /// behind - and to ZERO when the whole stack went, which is the opposite of how it reads.
        /// Treating it as the amount taken subtracted nothing, left the item in the model, and the
        /// bot asked to drop an empty slot on the next tick.
        /// </summary>
        public void NoteDropped(int slot, long remaining) => NoteSlotCount(slot, remaining);

        /// <summary>
        /// Set a slot to the count the SERVER says is left in it.
        ///
        /// The one way an item use or a drop may change the model. S.ItemChanged carries
        /// Link.Count as what REMAINS - zero meaning the whole stack went - for both paths
        /// (PlayerObject.cs:6486-6496), so one setter serves both.
        /// </summary>
        public void NoteSlotCount(int slot, long remaining) =>
            NoteSlotCount(GridType.Inventory, slot, remaining);

        /// <summary>Apply an accepted server count to either modeled grid.</summary>
        public void NoteSlotCount(GridType grid, int slot, long remaining)
        {
            Dictionary<int, ClientUserItem> slots = grid == GridType.Inventory ? _inventory :
                grid == GridType.Equipment ? _equipment : null;
            if (slots == null || !slots.TryGetValue(slot, out ClientUserItem item)) return;

            if (remaining <= 0)
            {
                slots.Remove(slot);
                if (grid == GridType.Equipment) ClearEquipRefusals();
                return;
            }

            item.Count = remaining;
        }

        /// <summary>
        /// DELIBERATELY UNUSED - kept so this comment has somewhere to live.
        ///
        /// This was called the moment a C.ItemUse was sent, and it was the fourth and last
        /// instance of the optimistic-update bug in this bot, after NoteSold, NoteEquipped and
        /// NoteUnlocked. The server enqueues S.ItemChanged BEFORE it validates a use
        /// (PlayerObject.cs:5580) and only sets Success after every check passes, so a use can be
        /// refused for the one-second UseItemTime cooldown, CanUseItem, being dead, Fishing,
        /// DragonRepulse, or a book restriction - each a bare return leaving Success false.
        ///
        /// Every one of those refusals deleted the item from this model while the server kept it.
        /// The bot then believed the slot was free, Place() put the next pickup there, the server
        /// put it somewhere else, and from that moment the two slot maps disagreed - which is why
        /// slot-addressed sell orders were voided wholesale. The give-away in the logs was that
        /// the exact slots failing to sell one at a time (24 Slaying, 29 Slaying, 34 Poison Dust)
        /// were precisely the items the server's own GUI still showed the character holding.
        ///
        /// Use NoteSlotCount from Process(S.ItemChanged) instead.
        /// </summary>
        [Obsolete("Optimistic. Apply S.ItemChanged via NoteSlotCount instead.")]
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

            // No slot for it, so wanting it is beside the point - the server will refuse without
            // saying so and the bot will stand on the item asking for ever. Checked first because
            // every rule below this decides whether we WANT the item, and this decides whether we
            // can have it at all.
            if (!HasRoomFor(info, instance)) return false;

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
                // Same predicate as everywhere else, so the loot rule cannot decide something is
                // a potion that the budget and the sell planner both say is not.
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

        /// <summary>
        /// The ONE scoring formula. Both Score (a carried instance) and ScoreInfo (a shop
        /// definition) route through it, differing only in where the numbers come from.
        ///
        /// THEY USED TO BE SEPARATE FORMULAS AND THAT COST REAL MONEY. ScoreInfo weighted armour
        /// as MaxAC * 4 while Score weighted the same item as (MaxAC + MinAC) * 2, and carried
        /// neither MinAC, the minimum of the class stat, nor Utility. For any item with MinAC 0 -
        /// most of them - the copy on the shelf therefore scored DOUBLE the identical copy already
        /// being worn, so it read as a permanent upgrade and was bought again on every single trip.
        ///
        /// A level 24 wizard re-bought its own Flame Robe, Magic Bronze Helmet and Necklace Of
        /// Lantern once per town trip - seventeen thousand gold a lap, roughly every twenty
        /// minutes - sold them straight back at vendor rates, and could not work out why it was
        /// the only character on the server going backwards while the warrior passed half a
        /// million. A Taoist had just started down the same slope with a Faith Robe.
        ///
        /// Keeping the two in step by hand was never going to work; this makes it structural.
        /// </summary>
        private static int ScoreFrom(Func<Stat, int> stat, ItemType type, MirClass mirClass)
        {
            if (type == ItemType.Weapon)
            {
                int weapon;

                switch (mirClass)
                {
                    case MirClass.Wizard:
                        weapon = stat(Stat.MaxMC) * 10 + stat(Stat.MinMC); break;
                    case MirClass.Taoist:
                        weapon = stat(Stat.MaxSC) * 10 + stat(Stat.MinSC); break;
                    default:
                        weapon = stat(Stat.MaxDC) * 10 + stat(Stat.MinDC); break;
                }

                return weapon + UtilityFrom(stat);
            }

            int score = (stat(Stat.MaxAC) + stat(Stat.MinAC)) * 2 +
                        (stat(Stat.MaxMR) + stat(Stat.MinMR)) * 2 +
                        stat(Stat.Health) + stat(Stat.Mana);

            switch (mirClass)
            {
                case MirClass.Wizard:
                    score += stat(Stat.MaxMC) * 8 + stat(Stat.MinMC) * 4; break;
                case MirClass.Taoist:
                    score += stat(Stat.MaxSC) * 8 + stat(Stat.MinSC) * 4; break;
                default:
                    score += stat(Stat.MaxDC) * 8 + stat(Stat.MinDC) * 4; break;
            }

            return score + UtilityFrom(stat);
        }

        /// <summary>Utility, from whichever stat source the caller supplies. See Utility.</summary>
        private static int UtilityFrom(Func<Stat, int> stat) =>
            stat(Stat.AttackSpeed) * 6 +
            stat(Stat.Accuracy) * 2 +
            stat(Stat.Agility) * 2 +
            stat(Stat.CriticalChance) * 2 +
            stat(Stat.CriticalDamage) +
            stat(Stat.WearWeight) + stat(Stat.HandWeight) + stat(Stat.BagWeight) +
            stat(Stat.Comfort);

        public static int ScoreInfo(ItemInfo info, MirClass mirClass) =>
            info == null ? 0 : ScoreFrom(s => info.Stats[s], info.ItemType, mirClass);

        public const int TownTeleportShape = 2;

        /// <summary>
        /// How many are in this slot, or 0 when we do not believe anything is.
        ///
        /// It used to answer 1 for an unknown slot. That guess is not harmless here: the server
        /// rejects a whole sell order if any link has Count &lt;= 0 or names an empty slot
        /// (ParseLinks, and the null check in NPCSell), so one invented count loses the sale for
        /// every other item in the same order. Callers skip a zero instead.
        /// </summary>
        public long CountInSlot(int slot) =>
            _inventory.TryGetValue(slot, out ClientUserItem item) ? item.Count : 0;

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
                if (IsManaPotion(item) && item.Info.Shape != TownTeleportShape)
                    total += item.Count;

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
        /// <summary>
        /// Already carrying one of these, unequipped?
        ///
        /// The gear buyer compared a shop item against what is WORN and never against what is
        /// already in the bag, so an item bought and then not equipped was bought again on the
        /// next trip, and the next. A level 22 Taoist bought a Necklace Of Lantern at Amy four
        /// times in under an hour at 4,000 gold each, tried to sell each one straight back to the
        /// same NPC, and was refused every time.
        ///
        /// Compared by ItemInfo identity rather than by name: two items can share a name and
        /// differ in added stats, and the one in the bag is the one that would be equipped.
        /// </summary>
        public bool HasUnequippedCopy(ItemInfo info)
        {
            if (info == null) return false;

            foreach (ClientUserItem item in _inventory.Values)
                if (item?.Info != null && item.Info.Index == info.Index)
                    return true;

            return false;
        }

        /// <summary>What is worn in the slot this item would go to, for diagnostics.</summary>
        public string DescribeWornFor(ItemType type, MirClass mirClass)
        {
            List<string> worn = new List<string>();

            foreach (EquipmentSlot option in SlotsFor(type))
                worn.Add(_equipment.TryGetValue((int)option, out ClientUserItem item) &&
                         item?.Info != null
                    ? $"{option}={item.Info.ItemName} ({Score(item, mirClass)})"
                    : $"{option}=EMPTY");

            return worn.Count == 0 ? "no slot" : string.Join(", ", worn);
        }

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

                // THE SAME ITEM IS NEVER AN UPGRADE ON ITSELF.
                //
                // Belt and braces beside the shared formula above: identical definitions now
                // score identically, so this cannot trigger on arithmetic alone - but a future
                // divergence would be silent and expensive, and the answer here is knowable
                // without any arithmetic at all. A per-instance roll can still make a fresh copy
                // genuinely better; that is what AddedStats is for, and it shows up as the worn
                // item scoring lower on the shared formula rather than as a definition mismatch.
                if (worn.Info != null && worn.Info.Index == info.Index) return 0;

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

        /// <summary>
        /// Starter kit that has been replaced and can never be sold - the one case worth throwing
        /// away rather than carrying.
        ///
        /// The character's first weapon and armour are given at creation flagged Bound and
        /// Worthless on the INSTANCE. The database item is perfectly sellable - the shop version of
        /// a Commoner Outfit sells for 500 - so nothing in ItemInfo says anything is wrong; it is
        /// the copy in the bag that no vendor will take. The server refuses Worthless in NPCSell
        /// and so do we, correctly, which leaves the item as permanent bag weight: five for a
        /// Commoner Outfit, eleven for a Trainee's Armour, on bots that spend their lives near the
        /// weight cap.
        ///
        /// Dropping is allowed where selling is not: ItemDrop checks CanDrop, Locked and Marriage,
        /// and none of those apply here.
        ///
        /// Deliberately narrow. Four conditions, all required:
        ///   - Info.StartItem - the database says this is starting kit;
        ///   - Worthless on the instance - it genuinely cannot be sold, so nothing is being
        ///     thrown away that a vendor would have paid for;
        ///   - Weapon or Armour only - the two slots a character is always given and always
        ///     replaces early. Not rings, not potions, not quest items;
        ///   - something is already equipped in that slot - "replaced", not merely "carried".
        /// A starter weapon is still the only weapon until it is not.
        /// </summary>
        public bool IsReplacedStarterKit(ClientUserItem item)
        {
            if (item?.Info == null) return false;
            if (!item.Info.StartItem) return false;

            if ((item.Flags & UserItemFlags.Worthless) != UserItemFlags.Worthless) return false;

            if (!item.Info.CanDrop) return false;
            if ((item.Flags & UserItemFlags.Locked) == UserItemFlags.Locked) return false;

            EquipmentSlot slot;

            switch (item.Info.ItemType)
            {
                case ItemType.Weapon: slot = EquipmentSlot.Weapon; break;
                case ItemType.Armour: slot = EquipmentSlot.Armour; break;
                default: return false;
            }

            // Replaced means something else is in the slot. Worn gear lives in _equipment, so an
            // item that IS the equipped one cannot match - we are looking at the bag.
            return _equipment.TryGetValue((int)slot, out ClientUserItem worn) &&
                   worn?.Info != null && worn.Index != item.Index;
        }

        /// <summary>The first bag slot holding replaced starter kit, or -1.</summary>
        public int FirstReplacedStarterSlot()
        {
            foreach (KeyValuePair<int, ClientUserItem> pair in _inventory.OrderBy(x => x.Key))
                if (IsReplacedStarterKit(pair.Value)) return pair.Key;

            return -1;
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

        /// <summary>Record a lock the server has confirmed.</summary>
        public void NoteLocked(int slot)
        {
            ClientUserItem item = InSlot(slot);

            if (item == null) return;

            item.Flags |= UserItemFlags.Locked;
        }

        /// <summary>
        /// Clear the flag because the server SAID SO - called only from Process(S.ItemLock).
        ///
        /// It used to be called straight after sending the request, which meant the model recorded
        /// a change the server might never have made. See the comment on BotAction.Unlock.
        /// </summary>
        public void NoteUnlocked(int slot)
        {
            ClientUserItem item = InSlot(slot);

            if (item == null) return;

            item.Flags &= ~UserItemFlags.Locked;

            // Unlocking changes the answer to "can this be equipped", so a refusal recorded while
            // it was locked is meaningless now. Without this a transient refusal is permanent.
            ClearEquipRefusals();
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

                bool healing = IsHealthPotion(item);
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

        /// <summary>
        /// Bag weight currently spent on healing potions, for the sell diagnostic.
        ///
        /// The budget is a weight and the potions are counted, so "67 potions" against "budget 41"
        /// is two different units and tells the operator nothing. This is the number that is
        /// actually compared.
        /// </summary>
        public int HealthPotionWeight()
        {
            int weight = 0;

            foreach (ClientUserItem item in _inventory.Values)
                if (IsHealthPotion(item) && item.Info.Shape != TownTeleportShape)
                    weight += item.Weight;

            return weight;
        }

        /// <summary>
        /// How stocked we are, in units comparable to the potion budget.
        ///
        /// The budget is a weight, so weight is the right measure - until a potion weighs NOTHING.
        /// Life Pill (IV) does. A warrior holding 103 of them reported "0 weight" against a floor
        /// of 20, declared itself short of potions on every single check, and would have gone to
        /// town for ever - which is precisely the trip churn the weight comparison was introduced
        /// to fix, arriving from the opposite direction.
        ///
        /// The larger of weight and count. For weight-one potions the two are equal and nothing
        /// changes. For heavy ones weight dominates, which is correct - ten 19-weight elixirs
        /// really do fill the budget. For weightless ones the count takes over, which is also
        /// correct, because a potion that costs no bag cannot be limited by a bag budget.
        /// </summary>
        public int HealthPotionLoad() => Math.Max(HealthPotionWeight(), CountHealthPotions());

        public int ManaPotionLoad() => Math.Max(ManaPotionWeight(), CountManaPotions());

        /// <summary>Bag weight currently spent on mana potions. See HealthPotionWeight.</summary>
        public int ManaPotionWeight()
        {
            int weight = 0;

            foreach (ClientUserItem item in _inventory.Values)
                if (IsManaPotion(item) && item.Info.Shape != TownTeleportShape)
                    weight += item.Weight;

            return weight;
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
        private Dictionary<int, long> PlanConsumableKeeps(int healthReserve, int manaReserve,
            int scrollReserve)
        {
            Dictionary<int, long> keep = new Dictionary<int, long>();

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
                else if (IsHealthPotion(item)) health.Add(pair);
                else if (IsManaPotion(item)) mana.Add(pair);

                // Deliberately NOT added to keep: a weak restorative is known trash, and the
                // whole point of classifying it is that it becomes sellable. Chicken Blood heals
                // five, weighs one, sells for ten, and carries SaleBonus5/10/15/20 - the data
                // saying outright that it is meant to be sold in bulk.
                else if (IsWeakRestorative(item)) continue;

                else keep[pair.Key] = item.Count;   // food and oddities: not ours to judge here
            }

            // Best value per unit of weight first, so the budget is spent on the potions worth
            // carrying - and ranked the SAME WAY the buyer ranks them in TownTrip.BestPotion.
            //
            // "Strongest first" was the rule, and it is the wrong one for a budget denominated in
            // weight. Restore per weight is not monotonic in tier: the ordinary tiers climb
            // (30/1, 70/1, 110/2, 170/3) and then Elixir Of Life (V) arrives at nineteen weight.
            // Sorting by raw heal puts the elixir first, so an 82-weight budget is spent on four
            // of them - and the efficient tiers, which deliver several times the healing per unit
            // of bag, get sold as surplus.
            //
            // It also has to match the buyer or the two fight: the buyer would purchase the
            // efficient tier on one trip and the keeper would sell it on the next. Two orderings
            // over the same items is the same mistake as two parsers over the same config keys.
            int Restore(KeyValuePair<int, ClientUserItem> p, Stat stat) => p.Value.Info.Stats[stat];

            double PerWeight(KeyValuePair<int, ClientUserItem> p, Stat stat) =>
                Restore(p, stat) / (double)Math.Max(1, p.Value.Info.Weight);

            Comparison<KeyValuePair<int, ClientUserItem>> ByValue(Stat stat) => (a, b) =>
            {
                int byWeight = PerWeight(b, stat).CompareTo(PerWeight(a, stat));

                return byWeight != 0 ? byWeight : Restore(b, stat).CompareTo(Restore(a, stat));
            };

            health.Sort(ByValue(Stat.Health));
            mana.Sort(ByValue(Stat.Mana));

            // Scrolls are the one genuine COUNT. TownScrollReserve means "three scrolls", not
            // "three units of bag weight" - one scroll is one trip home - so it must not go through
            // the weight budget, which would keep 3/weight of them if a scroll ever weighed more
            // than one.
            FillByCount(keep, scrolls, scrollReserve);
            Fill(keep, health, healthReserve);
            Fill(keep, mana, manaReserve);

            return keep;
        }

        /// <summary>Keep whole stacks in the given order until the budget is spent.</summary>
        /// <summary>
        /// Keep the strongest stacks that fit the WEIGHT budget; everything after is surplus.
        ///
        /// The budget arrives as a weight - a share of the bag - and this used to convert it into a
        /// COUNT by dividing by whatever the single best tier we held weighed, then count units
        /// against that. The conversion is exact only when every stack is that tier. A bag holding
        /// twelve tier-two potions and fifty-two tier-one divided by the tier-two weight and then
        /// counted all sixty-four as though they cost the same - which happens to be true at weight
        /// one, and is wrong in either direction as soon as a heavier tier is in the bag.
        ///
        /// Spending each stack's real weight removes the approximation rather than improving it.
        /// ClientUserItem.Weight already accounts for Count, so it is the stack's true cost.
        ///
        /// The budget is checked BEFORE adding, never after, so the best stack is always kept even
        /// when it alone exceeds the allowance. Selling the only thing we can drink to satisfy an
        /// arithmetic bound would be a strictly worse bot.
        /// </summary>
        /// <summary>Keep this many UNITS, strongest stack first. For reserves that are counts.</summary>
        private static void FillByCount(Dictionary<int, long> keep,
            List<KeyValuePair<int, ClientUserItem>> stacks, int reserve)
        {
            long budget = Math.Max(1, reserve);
            long held = 0;

            foreach (KeyValuePair<int, ClientUserItem> pair in stacks)
            {
                if (held >= budget) return;

                long take = Math.Min(pair.Value.Count, budget - held);

                keep[pair.Key] = take;
                held += take;
            }
        }

        private static void Fill(Dictionary<int, long> keep,
            List<KeyValuePair<int, ClientUserItem>> stacks, int reserve)
        {
            int budget = Math.Max(1, reserve);
            int spent = 0;

            foreach (KeyValuePair<int, ClientUserItem> pair in stacks)
            {
                if (spent >= budget) return;      // everything after this is surplus

                ClientUserItem item = pair.Value;

                int stackWeight = Math.Max(1, item.Weight);
                int unitWeight = Math.Max(1, item.Info.Weight);

                if (stackWeight <= budget - spent)
                {
                    keep[pair.Key] = item.Count;
                    spent += stackWeight;
                    continue;
                }

                // PART of this stack, because a stack is not an indivisible thing.
                //
                // Keeping or dropping whole slots was the last thing standing between a bot and its
                // own potion budget. Potions arrive from the shop as ONE stack - fifty-five in a
                // single slot - so "keep it or sell it" against a budget of forty-one could only
                // ever answer "keep it", and an assassin sat on sixty-seven tier-one potions across
                // two slots with nothing surplus for the vendor, trip after trip.
                //
                // The server has never had a problem with this: NPCSell refuses link.Count GREATER
                // than the stack and handles a smaller one explicitly (PlayerObject.cs:9565,9604),
                // which is how a player sells half a stack. Only our side insisted on all or none.
                long units = (budget - spent) / unitWeight;

                // Never end up with nothing to drink to satisfy an arithmetic bound. If the best
                // stack alone overshoots the whole budget, the budget is wrong, not the potions.
                if (units <= 0 && spent == 0) units = item.Count;

                if (units > 0) keep[pair.Key] = Math.Min(units, item.Count);

                return;
            }
        }

        /// <summary>
        /// How much bag weight a town trip could actually shed.
        ///
        /// The weight trigger used to read the scales alone, and that is only half a question. A
        /// bag can be at 100% and hold nothing a vendor wants: potions, scrolls and worn equipment
        /// are all reserved, all heavy, and none of them disposable. The trip then fires, walks the
        /// whole itinerary, sells nothing, comes home exactly as heavy, and fires again after the
        /// cooldown - for ever.
        ///
        /// Observed running: a level 24 warrior at 242/242 weight reporting "0 sellable of 7 slots"
        /// on every stop, 24 trips deep, and a level 16 assassin at 109/120 doing the same with a
        /// ten-stop lap. Between them they spent two hours shopping and managed 223 combat actions.
        ///
        /// So the trigger asks both halves now: are we heavy, AND is there something to put down.
        /// </summary>
        public int DisposableWeight(MirClass mirClass, MirGender gender, int level,
            Stats stats, int healthReserve, int manaReserve, int scrollReserve,
            MagicBooks books, WorldModel world)
        {
            int weight = 0;

            foreach (int slot in DisposableSlots(mirClass, gender, level, stats,
                         healthReserve, manaReserve, scrollReserve, books, world,
                         out Dictionary<int, long> partial))
            {
                ClientUserItem item = InSlot(slot);

                if (item?.Info == null) continue;

                // The stack's weight, not the unit's - InSlot returns the instance, whose Weight
                // already accounts for Count - except where only part of the stack is going, in
                // which case price exactly the part that is going. Overstating this number is how
                // a trip fires expecting to shed weight it cannot actually put down.
                weight += partial.TryGetValue(slot, out long units)
                    ? (int)Math.Min(int.MaxValue, units * Math.Max(1, item.Info.Weight))
                    : item.Weight;
            }

            return weight;
        }

        public List<int> DisposableSlots(MirClass mirClass, MirGender gender, int level,
            Stats stats, int healthReserve, int manaReserve, int scrollReserve,
            MagicBooks books, WorldModel world) =>
            DisposableSlots(mirClass, gender, level, stats, healthReserve, manaReserve,
                scrollReserve, books, world, out _);

        /// <param name="partialCounts">
        /// Slot -> how many UNITS to sell, for slots where only part of the stack is surplus.
        /// Slots absent from this map are sold whole. Nothing else in the bag is ever partial -
        /// gear does not stack - so this is empty except for consumables over their reserve.
        /// </param>
        public List<int> DisposableSlots(MirClass mirClass, MirGender gender, int level,
            Stats stats, int healthReserve, int manaReserve, int scrollReserve,
            MagicBooks books, WorldModel world, out Dictionary<int, long> partialCounts)
        {
            List<int> slots = new List<int>();

            partialCounts = new Dictionary<int, long>();

            Dictionary<int, long> keepConsumables = PlanConsumableKeeps(healthReserve, manaReserve,
                scrollReserve);

            foreach (KeyValuePair<int, ClientUserItem> pair in _inventory.OrderBy(x => x.Key))
            {
                ClientUserItem item = pair.Value;

                if (item.Info.ItemType == ItemType.Consumable)
                {
                    keepConsumables.TryGetValue(pair.Key, out long kept);

                    if (kept >= item.Count) continue;          // wholly within the reserve

                    slots.Add(pair.Key);

                    if (kept > 0) partialCounts[pair.Key] = item.Count - kept;
                    continue;
                }

                if (item.Info.ItemType == ItemType.Book)
                {
                    BookVerdict verdict = books?.Judge(item, mirClass, level, stats, world)
                                          ?? BookVerdict.Junk;

                    // Learning can fail and consumes the book. Every duplicate is another
                    // chance, so bank ALL too-early copies and try ALL wanted copies until
                    // the server confirms the skill was learned.
                    if (RetainUnlearnedBook(verdict)) continue;

                    slots.Add(pair.Key);   // Junk / WrongClass / AlreadyKnown
                    continue;
                }

                // Reagents are ammunition. Selling them is selling the ability to cast, and
                // because they carry no stats every gear comparison rates them worthless, so
                // without this they fall straight through to the junk pile.
                if (IsReagent(item)) continue;

                // An item part is never junk. Its own ItemInfo is a generic placeholder - the
                // real item is named by AddedStats[Stat.ItemIndex] - so it looks like a nameless
                // trinket to every rule here and used to be sold. Parts accumulate towards
                // Info.PartCount and are how the good gear is assembled, so they are always kept.
                if (IsItemPart(item)) continue;

                // Keep anything the bank should hold for later.
                if (ShouldBank(item, mirClass, gender, level, stats, books, world)) continue;

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

        public static bool RetainUnlearnedBook(BookVerdict verdict) =>
            verdict == BookVerdict.TooEarly || verdict == BookVerdict.Wanted;

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
                case ItemType.Shield: return EquipmentSlot.Shield;
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

        // Utility now lives in UtilityFrom, shared by Score and ScoreInfo.

        public static int Score(ClientUserItem item, MirClass mirClass) =>
            item?.Info == null
                ? 0
                : ScoreFrom(stat => Total(item, stat), item.Info.ItemType, mirClass);

        /// <summary>
        /// Items worth equipping: empty slots first, then straight upgrades. A candidate only
        /// replaces a worn item when it scores strictly higher for this class, so the bot can never
        /// downgrade itself. Rings and bracelets are paired: an empty side is preferred, otherwise
        /// the weaker side is displaced.
        ///
        /// The server validates every move, and a refused request is REMEMBERED - see
        /// NoteEquipRefused. Without that this method is a loop: it re-proposes the same refused
        /// item every tick, for ever.
        /// </summary>
        /// <summary>
        /// A locked item which would be equipped immediately if unlocked. Vendor purchases arrive
        /// locked, and the old code skipped them here while only the sell path knew how to unlock;
        /// protected poison therefore stayed in the bag forever while PoisonDust silently failed.
        /// </summary>
        public EquipRequest PendingEquipUnlock(MirClass mirClass, MirGender gender) =>
            EquipRequests(mirClass, gender, true).FirstOrDefault();

        public List<EquipRequest> PendingEquips(MirClass mirClass, MirGender gender) =>
            EquipRequests(mirClass, gender, false);

        private List<EquipRequest> EquipRequests(MirClass mirClass, MirGender gender,
            bool lockedOnly)
        {
            List<EquipRequest> requests = new List<EquipRequest>();
            HashSet<int> claimed = new HashSet<int>();

            foreach (KeyValuePair<int, ClientUserItem> pair in _inventory.OrderBy(x => x.Key))
            {
                ClientUserItem item = pair.Value;

                if (!CanEquip(item, mirClass, gender)) continue;

                bool locked = (item.Flags & UserItemFlags.Locked) == UserItemFlags.Locked;
                if (locked != lockedOnly) continue;

                int candidateScore = Score(item, mirClass);

                // Rings and bracelets have a left and a right slot. Prefer an empty one; otherwise
                // displace whichever side is currently weaker.
                EquipmentSlot? chosen = null;
                string reason = null;
                bool merge = false;
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

                    // SAME ITEM, AND THE WORN STACK HAS ROOM: top it up.
                    //
                    // Poison and Amulet hold stackable reagents that are consumed by casting, so
                    // "already wearing one" is not a reason to leave 58 more in the bag. The score
                    // comparison below can never allow this - an identical item ties, and the test
                    // is strictly greater - so without this the bot buys reagents it can never
                    // equip and quietly runs out mid-fight.
                    if (worn.Info == item.Info && worn.Count < worn.Info.StackSize &&
                        (worn.Flags & UserItemFlags.Expirable) != UserItemFlags.Expirable &&
                        (item.Flags & UserItemFlags.Expirable) != UserItemFlags.Expirable)
                    {
                        merge = true;
                        chosen = option;
                        reason = $"topping up {worn.Info.ItemName} " +
                                 $"({worn.Count} + {item.Count} of {worn.Info.StackSize})";
                        break;
                    }

                    int current = Score(worn, mirClass);
                    if (current >= candidateScore || current >= weakest) continue;

                    weakest = current;
                    chosen = option;
                    reason = $"upgrade over {worn.Info.ItemName} ({current} -> {candidateScore})";
                }

                if (chosen == null) continue;

                // Already refused for this slot, and nothing has changed since that could make the
                // answer different. Proposing it again just burns a packet and a tick.
                if (_refusedEquips.Contains(RefusalKey(item.Info.ItemName, (int)chosen.Value)))
                    continue;

                claimed.Add((int)chosen.Value);
                requests.Add(new EquipRequest
                {
                    FromSlot = pair.Key,
                    ToSlot = (int)chosen.Value,
                    ItemName = item.Info.ItemName,
                    Slot = chosen.Value,
                    Reason = reason,
                    Merge = merge
                });
            }

            return requests;
        }

        /// <summary>
        /// Equips the server has thrown out, as "item name -> slot index".
        ///
        /// The bot had no such memory, so a refused equip was re-proposed on the very next tick and
        /// refused again. Two level-22 casters carrying robes they were four weight over the limit
        /// for did this for four solid minutes at roughly 140 attempts each per minute - 1,328
        /// refusals in the log - and would have continued indefinitely. The equip decision sits at
        /// the very top of Decide(), so every one of those ticks was a tick NOT spent fighting.
        ///
        /// Keyed by name and destination rather than by inventory slot, because slots shift under
        /// us whenever anything is bought, sold or looted, and a stale slot number would silence
        /// the wrong item.
        /// </summary>
        private readonly HashSet<string> _refusedEquips = new HashSet<string>();

        private static string RefusalKey(string itemName, int slot) => itemName + " -> " + slot;

        /// <summary>The server refused this equip. Stop proposing it until something changes.</summary>
        public void NoteEquipRefused(EquipRequest request)
        {
            if (request?.ItemName == null) return;

            _refusedEquips.Add(RefusalKey(request.ItemName, request.ToSlot));
        }

        /// <summary>
        /// Forget every refusal, because the thing that caused them may no longer be true.
        ///
        /// Called on a level up and on any successful equip. Those are the two events that move
        /// the wear allowance - the level raises the budget (BaseStat.WearWeight is per class and
        /// level), and a successful equip changes what is already being worn against it. A refusal
        /// for any other reason - wrong class, too low a level for the item - will simply be
        /// recorded again on the single retry this costs.
        /// </summary>
        public void ClearEquipRefusals() => _refusedEquips.Clear();

        /// <summary>Apply an equip the server has CONFIRMED. See Process(S.ItemMove).</summary>
        public void NoteEquipped(EquipRequest request)
        {
            // Worn weight has changed, so previously refused items may fit now.
            ClearEquipRefusals();

            if (!_inventory.TryGetValue(request.FromSlot, out ClientUserItem item)) return;

            // A MERGE MOVES COUNTS, NOT ITEMS. The server adds what it can to the worn stack and
            // leaves any remainder behind (PlayerObject.cs ItemMove, the MergeItem branch), so
            // modelling it as a swap would put the worn stack in the bag and lose the total.
            if (request.Merge &&
                _equipment.TryGetValue(request.ToSlot, out ClientUserItem into) &&
                into?.Info != null && into.Info == item.Info)
            {
                long room = into.Info.StackSize - into.Count;
                long moved = Math.Min(room, item.Count);

                into.Count += moved;
                item.Count -= moved;

                if (item.Count <= 0) _inventory.Remove(request.FromSlot);
                return;
            }

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
                             // ALWAYS the count, even when it is 1 - or 0.
                             //
                             // Hiding "x1" also hid "x0", and a zero-count stack is exactly the
                             // shape of bug worth seeing: it occupies a slot in our model that the
                             // server does not have, which shifts every slot after it and is a
                             // strong candidate for the wholesale sell refusals. Two mana potions
                             // were listed here while CountManaPotions reported none, and the log
                             // could not say which of the two was wrong.
                             $" x{x.Value.Count}"));
        }
    }
}
