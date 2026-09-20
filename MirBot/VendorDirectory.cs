using System;
using System.Collections.Generic;
using System.Drawing;
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

        /// <summary>What this page actually stocks, by item type, with the goods-index split.</summary>
        public string DescribeStock()
        {
            if (Page?.Goods == null || Page.Goods.Count == 0) return "";

            Dictionary<string, int> matched = new Dictionary<string, int>();
            int wrongIndex = 0;

            foreach (NPCGood good in Page.Goods)
            {
                if (good.Item == null) continue;

                if (NPC != null && good.GoodsIndex != NPC.GoodsIndex) { wrongIndex++; continue; }

                string key = good.Item.ItemType.ToString();
                matched.TryGetValue(key, out int n);
                matched[key] = n + 1;
            }

            string sells = matched.Count == 0
                ? ""
                : ", sells " + string.Join("/", matched.Select(x => $"{x.Key} x{x.Value}"));

            if (wrongIndex > 0) sells += $", {wrongIndex} on another goods index";

            return sells;
        }

        public override string ToString() =>
            $"{NPC?.NPCName} on {MapName} at {Point.X},{Point.Y} " +
            $"[buys {(Buys.Count == 0 ? "nothing" : string.Join("/", Buys))}" +
            DescribeStock() +
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

        private readonly HashSet<int> _townMaps = new HashSet<int>();

        public IReadOnlyCollection<int> TownMaps => _townMaps;

        /// <summary>
        /// Restrict every shopping decision to these maps.
        ///
        /// This is a whitelist rather than a set of rules about distance or reachability, because
        /// the rules kept being defeated one at a time. The bot sent both characters to Lavar on
        /// Infernal Island - a long, expensive, high level trip with no route from Bichon - purely
        /// because he is the only NPC on the server who sells scrolls and potions from one page,
        /// and a "one stop is better than two" shortcut ran before any proximity test.
        ///
        /// Shopping happens in towns. Naming them is honest about what we actually want, and it
        /// cannot be short-circuited by the next clever optimisation.
        /// </summary>
        public void SetTownMaps(IEnumerable<string> mapNames)
        {
            _townMaps.Clear();

            if (mapNames == null) return;

            foreach (string name in mapNames)
            {
                string wanted = name?.Trim();
                if (string.IsNullOrEmpty(wanted)) continue;

                foreach (MapInfo info in Globals.MapInfoList?.Binding ?? Enumerable.Empty<MapInfo>())
                    if (string.Equals(info.Description, wanted, StringComparison.OrdinalIgnoreCase))
                        _townMaps.Add(info.Index);
            }
        }

        /// <summary>
        /// The only entries any shopping decision may consider. Everything that picks a vendor goes
        /// through here, so a new selection rule cannot accidentally reach outside the towns.
        /// </summary>
        private IEnumerable<VendorEntry> Shoppable =>
            _townMaps.Count == 0 ? _entries : _entries.Where(x => _townMaps.Contains(x.MapIndex));

        /// <summary>
        /// The pages a trip planned for ONE map may consider.
        ///
        /// Every selector below used to merely PREFER the current map - a +100 score, or a tie
        /// break - and fall back to another shoppable town when the current one had no match. That
        /// was right while a trip was planned where it would be walked. It is wrong now that a trip
        /// can be planned for the map it is standing on and then executed somewhere else, because a
        /// single off-map stop is enough to strand the whole itinerary: the bot walks to where the
        /// vendor would be, finds nobody, and aborts with "arrived but could not see the vendor".
        ///
        /// onlyMap of -1 keeps the old preference behaviour for callers that genuinely want it.
        /// </summary>
        private IEnumerable<VendorEntry> ShoppableOn(int onlyMap) =>
            onlyMap < 0 ? Shoppable : Shoppable.Where(x => x.MapIndex == onlyMap);

        public string DescribeTownMaps()
        {
            if (_townMaps.Count == 0) return "shopping anywhere (TownMaps is empty)";

            List<string> names = new List<string>();

            foreach (int index in _townMaps)
                names.Add(Globals.MapInfoList?.Binding?.FirstOrDefault(x => x.Index == index)?
                              .Description ?? index.ToString());

            return "shopping only in " + string.Join(", ", names) +
                   $" ({Shoppable.Count()} of {_entries.Count} pages)";
        }

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
        public VendorEntry BestBuyerFor(IEnumerable<ItemType> carrying, int currentMapIndex,
            int onlyMap = -1)
        {
            List<ItemType> wanted = carrying?.ToList() ?? new List<ItemType>();
            if (wanted.Count == 0) return null;

            VendorEntry best = null;
            int bestScore = 0;

            foreach (VendorEntry entry in ShoppableOn(onlyMap))
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
        /// <summary>
        /// Who to visit to restock. Up to two stops, because scrolls and potions are independent
        /// needs and one shop rarely covers both.
        ///
        /// This used to return a SINGLE vendor for both needs, ordered by "is it on this map"
        /// before "how many of the needs does it cover". So on a map with a potion seller and a
        /// scroll seller, the potion seller won on a tie and the scrolls were simply never bought -
        /// every stop of the trip reported "0/3 scrolls held" and the bot left town with none,
        /// which then blocked the book purchase as well.
        ///
        /// One stop is still preferred when a single shop genuinely sells both.
        /// </summary>
        /// <summary>
        /// Everything already on the itinerary, including what this call has just chosen. Nearest
        /// prefers an NPC that is already a stop, so telling it about the potion seller is what
        /// lets one shop cover both needs without a second walk.
        /// </summary>
        private static IEnumerable<VendorEntry> Combine(IEnumerable<VendorEntry> already,
            IEnumerable<VendorEntry> chosen)
        {
            if (already == null) return chosen;

            List<VendorEntry> all = new List<VendorEntry>(already);
            all.AddRange(chosen);
            return all;
        }

        public List<VendorEntry> BestRestockersFor(bool needScrolls, bool needPotions,
            int currentMapIndex, IEnumerable<VendorEntry> already = null, Point from = default,
            bool sameMapOnly = false)
        {
            List<VendorEntry> chosen = new List<VendorEntry>();

            if (!needScrolls && !needPotions) return chosen;

            if (needScrolls && needPotions)
            {
                VendorEntry both = Nearest(
                    x => x.SellsTownScroll && x.SellsHealthPotion, currentMapIndex, already, from,
                    sameMapOnly);

                // Collapsing two stops into one is only a saving if the one stop is HERE.
                //
                // Without this test the shortcut ran first and returned unconditionally, so the
                // single NPC on the whole server who sells scrolls and potions together - Lavar,
                // on Infernal Island - was chosen over the potion seller and scroll seller standing
                // twenty tiles away in the bot's own town. Infernal Island has no route from Bichon
                // at all, so the trip ended at "on another map - no path" every time, for both
                // characters, whenever they happened to need both things at once.
                if (both != null && both.MapIndex == currentMapIndex)
                {
                    chosen.Add(both);
                    return chosen;
                }
            }

            // Potions first, and the ORDER of these two blocks is the whole point.
            //
            // The itinerary is walked in order and the purse is spent as it goes, so whichever stop
            // comes first gets first call on the money. A wizard with 2,503 gold went to the scroll
            // seller, spent 1,500 of it, arrived at the potion seller with 1,003, left town with no
            // healing potions at all, and died ninety seconds later.
            //
            // A scroll is an escape. A potion is what stops you needing one. When there is not
            // enough for both, the thing that keeps the character alive has to win.
            if (needPotions)
            {
                VendorEntry potions = Nearest(x => x.SellsHealthPotion, currentMapIndex, already,
                    from, sameMapOnly);
                if (potions != null) chosen.Add(potions);
            }

            if (needScrolls)
            {
                VendorEntry scrolls = Nearest(x => x.SellsTownScroll, currentMapIndex,
                    Combine(already, chosen), from, sameMapOnly);
                if (scrolls != null && !chosen.Contains(scrolls)) chosen.Add(scrolls);
            }

            return chosen;
        }

        /// <summary>
        /// Pick a vendor, preferring one we are already going to see.
        ///
        /// Every need used to be resolved in isolation, so a trip could route to a distant NPC for
        /// a torch while an NPC already on the itinerary sold torches too - Bichon's Lennard sells
        /// both town scrolls and torches, and the bot still set off for Lavar on the far side of
        /// town. Reusing a stop we are making anyway is almost always better than a new one, and
        /// among new ones the closer is better.
        /// </summary>
        private VendorEntry Nearest(Func<VendorEntry, bool> match, int currentMapIndex,
            IEnumerable<VendorEntry> already = null, Point from = default, bool sameMapOnly = false)
        {
            HashSet<NPCInfo> queued = new HashSet<NPCInfo>();

            if (already != null)
                foreach (VendorEntry entry in already)
                    if (entry?.NPC != null) queued.Add(entry.NPC);

            return Shoppable
                .Where(match)
                .Where(x => !sameMapOnly || x.MapIndex == currentMapIndex)
                .OrderByDescending(x => queued.Contains(x.NPC))
                .ThenByDescending(x => x.MapIndex == currentMapIndex)
                .ThenBy(x => from == default || x.MapIndex != currentMapIndex
                    ? int.MaxValue
                    : Math.Max(Math.Abs(x.Point.X - from.X), Math.Abs(x.Point.Y - from.Y)))
                .FirstOrDefault();
        }

        /// <summary>
        /// Every page of the same NPC that sells any of these types.
        ///
        /// An NPC can split its stock across dialogue options - Bichon's Amy has one page for
        /// bracelets and another for necklaces - and each page is a separate entry with its own
        /// button path. Visiting only the page we happened to pick means half the stock is never
        /// looked at.
        /// </summary>
        public List<VendorEntry> PagesOf(VendorEntry entry, IEnumerable<ItemType> types)
        {
            List<VendorEntry> pages = new List<VendorEntry>();

            if (entry?.NPC == null) return pages;

            List<ItemType> wanted = types?.ToList();

            foreach (VendorEntry other in Shoppable)
            {
                if (other.NPC != entry.NPC) continue;
                if (other.Page?.Goods == null || other.Page.Goods.Count == 0) continue;

                if (wanted != null && !other.Page.Goods.Any(g =>
                        g.Item != null &&
                        g.GoodsIndex == other.NPC.GoodsIndex &&
                        wanted.Contains(g.Item.ItemType)))
                    continue;

                pages.Add(other);
            }

            return pages;
        }

        /// <summary>Whoever repairs the most of what is damaged. Mirrors BestBuyerFor.</summary>
        /// <summary>Whoever stocks the most of the skill books we want.</summary>
        public VendorEntry BestBookSellerFor(IEnumerable<int> wantedMagicIndexes, int currentMapIndex,
            int onlyMap = -1)
        {
            List<int> wanted = wantedMagicIndexes?.ToList() ?? new List<int>();
            if (wanted.Count == 0) return null;

            VendorEntry best = null;
            int bestScore = 0;

            foreach (VendorEntry entry in ShoppableOn(onlyMap))
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

        /// <summary>
        /// The shop most likely to sell us an upgrade: whoever stocks the most of the equipment
        /// types we are interested in. Scored the same way as the best buyer, with a bonus for
        /// being on this map so a shopping trip does not become a cross-map expedition.
        /// </summary>
        public VendorEntry BestGearSellerFor(IEnumerable<ItemType> wanted, int currentMapIndex,
            IEnumerable<VendorEntry> already = null, Point from = default, int maxDistance = 0,
            int onlyMap = -1)
        {
            List<ItemType> types = wanted?.ToList() ?? new List<ItemType>();
            if (types.Count == 0) return null;

            HashSet<NPCInfo> queued = new HashSet<NPCInfo>();

            if (already != null)
                foreach (VendorEntry entry in already)
                    if (entry?.NPC != null) queued.Add(entry.NPC);

            // Ranked in order of what actually costs us, not added together. Adding a stock
            // COUNT to a tile COUNT compares different units, and stock always won: a general
            // store with two hundred items outscored a shop ten tiles away by sheer inventory, so
            // the bot walked ninety tiles across Bichon to Lavar while Lennard stood next door.
            //
            // Distance is bucketed rather than exact, so a much richer shop a few tiles further on
            // can still win - but nothing beats being close.
            const int Bucket = 25;

            VendorEntry best = null;
            (int queuedRank, int mapRank, int band, int stock) bestKey =
                (int.MaxValue, int.MaxValue, int.MaxValue, int.MinValue);

            foreach (VendorEntry entry in ShoppableOn(onlyMap))
            {
                if (entry.Page?.Goods == null || entry.Page.Goods.Count == 0) continue;

                int stocked = 0;

                foreach (NPCGood good in entry.Page.Goods)
                {
                    if (good.Item == null) continue;
                    if (good.GoodsIndex != entry.NPC.GoodsIndex) continue;
                    if (types.Contains(good.Item.ItemType)) stocked++;
                }

                if (stocked == 0) continue;

                bool sameMap = entry.MapIndex == currentMapIndex;

                int distance = sameMap && from != default
                    ? Math.Max(Math.Abs(entry.Point.X - from.X), Math.Abs(entry.Point.Y - from.Y))
                    : int.MaxValue;

                // Too far to be worth a browse, unless we are going there anyway.
                if (maxDistance > 0 && distance > maxDistance && !queued.Contains(entry.NPC))
                    continue;

                var key = (
                    queuedRank: queued.Contains(entry.NPC) ? 0 : 1,
                    mapRank: sameMap ? 0 : 1,
                    band: distance == int.MaxValue ? int.MaxValue : distance / Bucket,
                    stock: stocked);

                if (key.queuedRank > bestKey.queuedRank) continue;
                if (key.queuedRank == bestKey.queuedRank)
                {
                    if (key.mapRank > bestKey.mapRank) continue;
                    if (key.mapRank == bestKey.mapRank)
                    {
                        if (key.band > bestKey.band) continue;
                        if (key.band == bestKey.band && key.stock <= bestKey.stock) continue;
                    }
                }

                best = entry;
                bestKey = key;
            }

            return best;
        }

        public VendorEntry BestRepairerFor(IEnumerable<ItemType> damaged, int currentMapIndex,
            int onlyMap = -1)
        {
            List<ItemType> wanted = damaged?.ToList() ?? new List<ItemType>();
            if (wanted.Count == 0) return null;

            VendorEntry best = null;
            int bestScore = 0;

            foreach (VendorEntry entry in ShoppableOn(onlyMap))
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
            return Shoppable
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
