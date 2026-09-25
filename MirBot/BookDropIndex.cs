using System;
using System.Collections.Generic;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    /// <summary>
    /// Which skill books can only be got by killing things, and where those things live.
    ///
    /// Some of the best skills on this server are never sold. Summon Skeleton is the obvious one -
    /// it roughly doubles what a Taoist can do - and no NPC anywhere stocks it; it drops from the
    /// undead in the cave tiers at about one in 450 from a Ghost Mage. A bot that only ever chooses
    /// hunting grounds by experience per hour has no reason to go anywhere that has it, so it can
    /// stay permanently weak while standing next to the answer.
    ///
    /// The concrete case: a level 22 Taoist farming Ant Cave North, which drops NOT ONE skill book
    /// for ANY class. No amount of time there could ever have advanced her skills.
    ///
    /// Deliberately derived from the database rather than configured. The first instinct was a rule
    /// naming Deserted Mine, and the data says that would have been wrong: Summon Skeleton drops on
    /// TWELVE maps - Banya Cave 1-3, Bichon Cave 1-3, Deserted Mine 1-3 and Lost Paradise Cave 1-3 -
    /// one of which, Bichon Cave Lv 1, was already in that character's own preferred list and is far
    /// gentler than the mine. Hardcoding the destination would have sent a weak character past the
    /// easy source to a hard one.
    /// </summary>
    public sealed class BookDropIndex
    {
        /// <summary>MagicInfo.Index -> the book item that teaches it, for drop-only books.</summary>
        private readonly Dictionary<int, ItemInfo> _dropOnly = new Dictionary<int, ItemInfo>();

        /// <summary>
        /// Map index -> every magic whose book DROPS there, sold or not. Unlearned skills are only
        /// ever wanted from drop-only books (a sold one is bought), but a level 4 training read
        /// needs a dropped copy of ANY book - a bought one is refused - so both live here and
        /// Wanted decides which count.
        /// </summary>
        private readonly Dictionary<int, HashSet<int>> _byMap = new Dictionary<int, HashSet<int>>();

        /// <summary>MagicInfo.Index -> a book item for it, for names in the log.</summary>
        private readonly Dictionary<int, ItemInfo> _anyBook = new Dictionary<int, ItemInfo>();

        /// <summary>Boss monster index -> the magics its books teach (boss lairs).</summary>
        private readonly Dictionary<int, HashSet<int>> _bossBooks = new Dictionary<int, HashSet<int>>();

        /// <summary>
        /// Magics whose book drops from at least one ORDINARY monster. The rest come only from
        /// bosses and mini-bosses (Flaming Sword, Blade Storm, Dragon Rise, the Taoist summons).
        /// </summary>
        private readonly HashSet<int> _fromOrdinary = new HashSet<int>();

        public int BookCount => _dropOnly.Count;
        public int BossCount => _bossBooks.Count;
        public int MapCount => _byMap.Count;

        public void Build(MagicBooks books)
        {
            _dropOnly.Clear();
            _byMap.Clear();
            _anyBook.Clear();
            _bossBooks.Clear();
            _fromOrdinary.Clear();

            if (books == null) return;

            try
            {
                // A book counts as SOLD only when its goods index belongs to a real NPC. The goods
                // tables carry entries no vendor is attached to, and treating those as "on sale"
                // would hide genuinely drop-only skills.
                HashSet<int> npcGoods = new HashSet<int>();

                foreach (NPCInfo npc in Globals.NPCInfoList?.Binding ?? Enumerable.Empty<NPCInfo>())
                    npcGoods.Add(npc.GoodsIndex);

                HashSet<int> sold = new HashSet<int>();

                foreach (NPCPage page in Globals.NPCPageList?.Binding ?? Enumerable.Empty<NPCPage>())
                {
                    if (page?.Goods == null) continue;

                    foreach (NPCGood good in page.Goods)
                    {
                        if (good?.Item == null || good.Item.ItemType != ItemType.Book) continue;
                        if (!npcGoods.Contains(good.GoodsIndex)) continue;

                        sold.Add(good.Item.Index);
                    }
                }

                // Every drop-only book, keyed by the skill it teaches.
                foreach (ItemInfo info in Globals.ItemInfoList?.Binding ?? Enumerable.Empty<ItemInfo>())
                {
                    if (info == null || info.ItemType != ItemType.Book) continue;
                    if (sold.Contains(info.Index)) continue;

                    MagicInfo magic = books.For(info);
                    if (magic == null) continue;

                    _dropOnly[magic.Index] = info;
                }

                // Where they fall: monster -> its drops, monster -> the maps it spawns on.
                foreach (MonsterInfo monster in Globals.MonsterInfoList?.Binding
                                                ?? Enumerable.Empty<MonsterInfo>())
                {
                    if (monster?.Drops == null || monster.Respawns == null) continue;

                    List<int> magics = new List<int>();

                    foreach (DropInfo drop in monster.Drops)
                    {
                        if (drop?.Item == null || drop.Item.ItemType != ItemType.Book) continue;

                        MagicInfo magic = books.For(drop.Item);
                        if (magic == null) continue;

                        magics.Add(magic.Index);
                        if (!_anyBook.ContainsKey(magic.Index)) _anyBook[magic.Index] = drop.Item;
                    }

                    if (magics.Count == 0) continue;

                    if (monster.IsBoss)
                    {
                        if (!_bossBooks.TryGetValue(monster.Index, out HashSet<int> taught))
                            _bossBooks[monster.Index] = taught = new HashSet<int>();
                        taught.UnionWith(magics);
                    }
                    else
                        _fromOrdinary.UnionWith(magics);

                    foreach (RespawnInfo respawn in monster.Respawns)
                    {
                        int mapIndex = respawn?.Region?.Map?.Index ?? -1;
                        if (mapIndex < 0) continue;

                        if (!_byMap.TryGetValue(mapIndex, out HashSet<int> set))
                            _byMap[mapIndex] = set = new HashSet<int>();

                        foreach (int index in magics) set.Add(index);
                    }
                }
            }
            catch
            {
                // Pre-login or a database that did not load. Everything below then returns empty,
                // and travel behaves exactly as it did before this existed.
                _dropOnly.Clear();
                _byMap.Clear();
                _anyBook.Clear();
                _bossBooks.Clear();
                _fromOrdinary.Clear();
            }
        }

        /// <summary>
        /// Skills never worth a hunting trip (NoHuntSkills), by MagicInfo.Index. Potion Mastery is
        /// sold by vendors, and its level 4 training copies are too rare a drop to chase - bots
        /// were picking maps for them. A copy that drops anyway is still looted and read.
        /// </summary>
        private readonly HashSet<int> _noHunt = new HashSet<int>();

        /// <summary>Resolve NoHuntSkills names ("Potion Mastery, ...") to magic indexes.</summary>
        public void SetNoHunt(string names)
        {
            _noHunt.Clear();

            HashSet<string> wanted = new HashSet<string>(
                (names ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            foreach (MagicInfo magic in Globals.MagicInfoList?.Binding ?? Enumerable.Empty<MagicInfo>())
                if (magic != null && wanted.Contains(magic.Name ?? ""))
                    _noHunt.Add(magic.Index);
        }

        public bool NoHunt(int magic) => _noHunt.Contains(magic);

        /// <summary>A book for this magic drops only from bosses and mini-bosses.</summary>
        public bool BossOnly(int magic) => _anyBook.ContainsKey(magic) && !_fromOrdinary.Contains(magic);

        /// <summary>
        /// Drop-only skills this character could learn TODAY and does not have.
        ///
        /// Gated on the book's own requirement rather than the skill's cast level, because that is
        /// what the server enforces when the book is used - the same distinction that decides
        /// whether the town trip buys one.
        /// </summary>
        public HashSet<int> Wanted(MirClass mirClass, int level, Stats stats, WorldModel world)
        {
            HashSet<int> wanted = new HashSet<int>();

            foreach (KeyValuePair<int, ItemInfo> pair in _dropOnly)
            {
                if (world != null && world.Knows(pair.Key)) continue;
                if (!Backpack.CanClassUseInfo(pair.Value, mirClass)) continue;
                if (!Backpack.MeetsRequirement(pair.Value, level, stats)) continue;

                if (_noHunt.Contains(pair.Key)) continue;
                wanted.Add(pair.Key);
            }

            // Level 4 training: a known level 3 skill wants any dropped copy of its book - unless
            // only bosses and mini-bosses drop it. Level 4 takes many copies (500 pages, each read
            // a roll), and a mini-boss respawns on the order of half an hour, so hunting one for
            // training copies of Flaming Sword is days of lair walks for one level. Operator's
            // call: not sought. A copy that drops anyway is still looted and read (MagicBooks.Judge
            // is separate); learning such a skill the FIRST time is still sought above.
            if (world != null)
                foreach (KeyValuePair<int, ItemInfo> pair in _anyBook)
                    if (world.Trainable(pair.Key) && !BossOnly(pair.Key) && !_noHunt.Contains(pair.Key) &&
                        Backpack.MeetsRequirement(pair.Value, level, stats))
                        wanted.Add(pair.Key);

            return wanted;
        }

        /// <summary>The wanted magics a boss monster's books teach, or none.</summary>
        public IEnumerable<int> BossTeaches(int monsterIndex, HashSet<int> wanted)
        {
            if (wanted == null || !_bossBooks.TryGetValue(monsterIndex, out HashSet<int> taught))
                yield break;
            foreach (int magic in taught)
                if (wanted.Contains(magic)) yield return magic;
        }

        /// <summary>A book's name for a magic, for the log.</summary>
        public string BookName(int magic) =>
            _anyBook.TryGetValue(magic, out ItemInfo info) ? info.ItemName : $"magic {magic}";

        /// <summary>How many of the wanted skills this map can supply.</summary>
        public int Supplies(int mapIndex, HashSet<int> wanted, Func<int, bool> counts = null)
        {
            if (wanted == null || wanted.Count == 0) return 0;
            if (!_byMap.TryGetValue(mapIndex, out HashSet<int> here)) return 0;

            int count = 0;
            foreach (int magic in here)
                if (wanted.Contains(magic) && (counts == null || counts(magic))) count++;
            return count;
        }

        /// <summary>The book names a map supplies, for the travel log.</summary>
        public string Names(int mapIndex, HashSet<int> wanted, Func<int, bool> counts = null)
        {
            if (!_byMap.TryGetValue(mapIndex, out HashSet<int> here)) return "";

            List<string> names = new List<string>();

            foreach (int magic in here)
                if (wanted.Contains(magic) && (counts == null || counts(magic)) &&
                    _anyBook.TryGetValue(magic, out ItemInfo info))
                    names.Add(info.ItemName);

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(", ", names.Take(4)) + (names.Count > 4 ? ", ..." : "");
        }
    }
}
