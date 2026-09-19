using System;
using System.Collections.Generic;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    /// <summary>
    /// What every NPC buys and sells, derived from System.db at startup.
    ///
    /// An NPCPage carries two lists: Types is what that page will BUY from the player, and Goods is
    /// what it SELLS. Both are already in the database the bot loads, so there is no need to name a
    /// vendor in config and no risk of that name going stale when the NPCs are edited.
    ///
    /// The route to a page matters as much as the page: the button presses that reach it are
    /// recorded here so the trip can walk the dialogue without guessing.
    /// </summary>
    public sealed class VendorEntry
    {
        public NPCInfo NPC;
        public NPCPage Page;
        public List<int> ButtonPath = new List<int>();

        /// <summary>Item types this page buys from the player.</summary>
        public HashSet<ItemType> Buys = new HashSet<ItemType>();

        /// <summary>True when this page stocks a town teleport scroll.</summary>
        public bool SellsTownScroll;

        /// <summary>MagicInfo indexes of the skill books this page stocks.</summary>
        public HashSet<int> SellsBooksFor = new HashSet<int>();

        /// <summary>True when this page stocks a healing potion.</summary>
        public bool SellsHealthPotion;

        /// <summary>Item types this page will REPAIR (DialogType.Repair pages only).</summary>
        public HashSet<ItemType> Repairs = new HashSet<ItemType>();

        public int MapIndex => NPC?.Region?.Map?.Index ?? -1;
        public string MapName => NPC?.Region?.Map?.Description ?? "?";

        public System.Drawing.Point Point =>
            NPC?.Region?.PointRegion != null && NPC.Region.PointRegion.Length > 0
                ? NPC.Region.PointRegion[0]
                : System.Drawing.Point.Empty;

        public override string ToString() =>
            $"{NPC?.NPCName} on {MapName} at {Point.X},{Point.Y} " +
            $"[buys {(Buys.Count == 0 ? "nothing" : string.Join("/", Buys))}" +
            $"{(SellsTownScroll ? ", sells scrolls" : "")}" +
            $"{(SellsHealthPotion ? ", sells potions" : "")}" +
            $"{(Repairs.Count == 0 ? "" : ", repairs " + string.Join("/", Repairs))}" +
            $"{(SellsBooksFor.Count == 0 ? "" : $", sells {SellsBooksFor.Count} books")}]";
    }

    public sealed class VendorDirectory
    {
        private readonly List<VendorEntry> _entries = new List<VendorEntry>();

        public int Count => _entries.Count;
        public IEnumerable<VendorEntry> Entries => _entries;

        /// <summary>Walk every NPC's dialogue tree and record what each reachable page trades.</summary>
        public void Build(MagicBooks books)
        {
            _entries.Clear();

            IEnumerable<NPCInfo> npcs;
            try
            {
                npcs = Globals.NPCInfoList?.Binding?.ToList() ?? new List<NPCInfo>();
            }
            catch
            {
                return;
            }

            foreach (NPCInfo npc in npcs)
            {
                if (npc?.EntryPage == null || npc.Region?.Map == null) continue;

                foreach (VendorEntry entry in Explore(npc, books))
                    _entries.Add(entry);
            }
        }

        private static IEnumerable<VendorEntry> Explore(NPCInfo npc, MagicBooks books)
        {
            Queue<(NPCPage Page, List<int> Path)> queue = new Queue<(NPCPage, List<int>)>();
            HashSet<NPCPage> seen = new HashSet<NPCPage>();

            queue.Enqueue((npc.EntryPage, new List<int>()));
            seen.Add(npc.EntryPage);

            while (queue.Count > 0)
            {
                (NPCPage page, List<int> path) = queue.Dequeue();

                // A page only BUYS if it is a BuySell page. Repair pages also carry a Types list
                // (the item types they can repair), so testing Types alone routed the bot to a
                // repair NPC to sell junk - C.NPCSell was then silently dropped by the server
                // (PlayerObject.cs:10270 requires DialogType == BuySell) and the whole trip wasted.
                bool buys = page.DialogType == NPCDialogType.BuySell &&
                            page.Types != null && page.Types.Count > 0;
                bool sellsScroll = page.Goods != null && page.Goods.Any(
                    g => g.Item != null &&
                         g.Item.ItemType == ItemType.Consumable &&
                         g.Item.Shape == Backpack.TownTeleportShape &&
                         g.GoodsIndex == npc.GoodsIndex);

                bool sellsPotion = page.Goods != null && page.Goods.Any(
                    g => g.Item != null &&
                         g.Item.ItemType == ItemType.Consumable &&
                         g.Item.Stats[Stat.Health] > 0 &&
                         g.GoodsIndex == npc.GoodsIndex);

                List<int> bookMagics = new List<int>();

                if (books != null && page.Goods != null)
                    foreach (NPCGood good in page.Goods)
                    {
                        if (good.Item == null || good.GoodsIndex != npc.GoodsIndex) continue;

                        MagicInfo magic = books.For(good.Item);
                        if (magic != null) bookMagics.Add(magic.Index);
                    }

                bool repairs = page.DialogType == NPCDialogType.Repair &&
                               page.Types != null && page.Types.Count > 0;

                if (buys || sellsScroll || sellsPotion || repairs || bookMagics.Count > 0)
                {
                    VendorEntry entry = new VendorEntry
                    {
                        NPC = npc,
                        Page = page,
                        ButtonPath = new List<int>(path),
                        SellsTownScroll = sellsScroll,
                        SellsHealthPotion = sellsPotion
                    };

                    foreach (int index in bookMagics) entry.SellsBooksFor.Add(index);

                    if (page.Types != null)
                        foreach (NPCType type in page.Types)
                        {
                            if (buys) entry.Buys.Add(type.ItemType);
                            if (repairs) entry.Repairs.Add(type.ItemType);
                        }

                    yield return entry;
                }

                if (page.Buttons == null) continue;

                foreach (NPCButton button in page.Buttons)
                {
                    NPCPage next = button.DestinationPage;
                    if (next == null || !seen.Add(next)) continue;

                    queue.Enqueue((next, new List<int>(path) { button.ButtonID }));
                }
            }
        }

        /// <summary>
        /// The best page for unloading this bag: whichever buys the most of what we are carrying.
        /// Same map wins ties, since the bot can only walk within a map.
        /// </summary>
        public VendorEntry BestBuyerFor(IEnumerable<ItemType> carrying, int currentMapIndex)
        {
            List<ItemType> wanted = carrying?.ToList() ?? new List<ItemType>();
            if (wanted.Count == 0) return null;

            VendorEntry best = null;
            int bestScore = 0;

            foreach (VendorEntry entry in _entries)
            {
                if (entry.Buys.Count == 0) continue;

                int score = wanted.Count(entry.Buys.Contains);
                if (score == 0) continue;

                // Prefer a vendor we can actually reach on foot.
                if (entry.MapIndex == currentMapIndex) score += 100;

                if (score <= bestScore) continue;

                best = entry;
                bestScore = score;
            }

            return best;
        }

        /// <summary>A vendor that restocks both if possible, so the trip needs fewer stops.</summary>
        public VendorEntry BestRestockerFor(bool needScrolls, bool needPotions, int currentMapIndex)
        {
            if (!needScrolls && !needPotions) return null;

            return _entries
                .Where(x => (needScrolls && x.SellsTownScroll) || (needPotions && x.SellsHealthPotion))
                .OrderByDescending(x => x.MapIndex == currentMapIndex)
                .ThenByDescending(x => (needScrolls && x.SellsTownScroll ? 1 : 0) +
                                       (needPotions && x.SellsHealthPotion ? 1 : 0))
                .FirstOrDefault();
        }

        /// <summary>Whoever repairs the most of what is damaged. Mirrors BestBuyerFor.</summary>
        /// <summary>Whoever stocks the most of the skill books we want.</summary>
        public VendorEntry BestBookSellerFor(IEnumerable<int> wantedMagicIndexes, int currentMapIndex)
        {
            List<int> wanted = wantedMagicIndexes?.ToList() ?? new List<int>();
            if (wanted.Count == 0) return null;

            VendorEntry best = null;
            int bestScore = 0;

            foreach (VendorEntry entry in _entries)
            {
                if (entry.SellsBooksFor.Count == 0) continue;

                int score = wanted.Count(entry.SellsBooksFor.Contains);
                if (score == 0) continue;

                if (entry.MapIndex == currentMapIndex) score += 100;
                if (score <= bestScore) continue;

                best = entry;
                bestScore = score;
            }

            return best;
        }

        public VendorEntry BestRepairerFor(IEnumerable<ItemType> damaged, int currentMapIndex)
        {
            List<ItemType> wanted = damaged?.ToList() ?? new List<ItemType>();
            if (wanted.Count == 0) return null;

            VendorEntry best = null;
            int bestScore = 0;

            foreach (VendorEntry entry in _entries)
            {
                if (entry.Repairs.Count == 0) continue;

                int score = wanted.Count(entry.Repairs.Contains);
                if (score == 0) continue;

                if (entry.MapIndex == currentMapIndex) score += 100;
                if (score <= bestScore) continue;

                best = entry;
                bestScore = score;
            }

            return best;
        }

        public VendorEntry BestScrollSellerFor(int currentMapIndex)
        {
            return _entries
                .Where(x => x.SellsTownScroll)
                .OrderByDescending(x => x.MapIndex == currentMapIndex)
                .FirstOrDefault();
        }

        public string Describe(int limit = 6)
        {
            if (_entries.Count == 0) return "no trading NPCs found";

            IEnumerable<string> lines = _entries
                .Where(x => x.Buys.Count > 0 || x.SellsTownScroll)
                .Take(limit)
                .Select(x => "  " + x);

            return $"{_entries.Count} trading pages:" + Environment.NewLine +
                   string.Join(Environment.NewLine, lines);
        }
    }
}
