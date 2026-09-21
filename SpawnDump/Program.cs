using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Library;
using Library.SystemModels;
using MirDB;

namespace SpawnDump
{
    /// <summary>
    /// Prints the monster spawns a System.db defines, optionally filtered to named maps.
    ///
    ///     SpawnDump &lt;folder containing System.db&gt; [map name fragment] ...
    ///
    /// Read-only by construction: the session is opened, collections are read, nothing is saved.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("usage: SpawnDump <folder with System.db> [map filter] ...");
                return 1;
            }

            // ---- MERGE runs before anything else: it opens TWO databases in sequence and must
            //      not share the read-only session this tool normally builds.
            string mergeFrom = Environment.GetEnvironmentVariable("MERGE_FROM");

            if (!string.IsNullOrEmpty(mergeFrom))
            {
                string dest = args[0];
                if (!dest.EndsWith("/") && !dest.EndsWith("\\")) dest += "/";
                if (!mergeFrom.EndsWith("/") && !mergeFrom.EndsWith("\\")) mergeFrom += "/";

                Console.WriteLine($"# donor {mergeFrom}");
                Console.WriteLine($"# ours  {dest}");

                var (mi, md, mq) = Merge.Export(mergeFrom);
                Console.WriteLine($"# donor has {mi.Count} items, {md.Count} drops, {mq.Count} quests");

                return Merge.Apply(dest, mi, md, mq,
                    Environment.GetEnvironmentVariable("MERGE_COMMIT") == "1");
            }

            string path = args[0];

            if (!path.EndsWith(Path.DirectorySeparatorChar.ToString()) && !path.EndsWith("/"))
                path += Path.DirectorySeparatorChar;

            if (!File.Exists(Path.Combine(path, "System.db")))
            {
                Console.WriteLine($"no System.db in {path}");
                return 1;
            }

            // System mode when patching: SaveSystem() returns without writing unless the session
            // was opened with SessionMode.System, and Save() still reports success - so a Users
            // session silently discards every change. Read-only dumps stay on Users.
            bool patching = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SPAWNPATCH_FROM"))
                            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEARCOPY_FROM"))
                            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEARSET_TSV"))
                            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBSYNC_REF"));

            Session session = new Session(patching ? SessionMode.System : SessionMode.Users, path)
                { BackUp = false };
            session.Initialize(Assembly.GetAssembly(typeof(ItemInfo)));

            if (!session.SystemDatabaseExists)
            {
                Console.WriteLine("System.db did not load.");
                return 1;
            }

            Console.WriteLine($"# {path}");
            Console.WriteLine($"# version {session.SystemDatabaseVersion}");

            // ---- machine-readable dump: map 	 region 	 monster 	 count
            if (Environment.GetEnvironmentVariable("SPAWNDUMP_BASESTATS") == "1")
            {
                foreach (BaseStat bs in session.GetCollection<BaseStat>().Binding
                             .OrderBy(x => x.Class).ThenBy(x => x.Level))
                {
                    Console.WriteLine(string.Join("	", bs.Class, bs.Level,
                        "HP=" + bs.Health, "MP=" + bs.Mana,
                        "Bag=" + bs.BagWeight, "Wear=" + bs.WearWeight, "Hand=" + bs.HandWeight));
                }
                return 0;
            }

            if (Environment.GetEnvironmentVariable("SPAWNDUMP_EQUIPSTATS") == "1")
            {
                Dictionary<string,int> freq = new Dictionary<string,int>();

                foreach (ItemInfo ii in session.GetCollection<ItemInfo>().Binding)
                {
                    if (ii?.Stats == null) continue;
                    if (ii.ItemType == ItemType.Consumable || ii.ItemType == ItemType.Nothing) continue;

                    foreach (var kv in ii.Stats.Values)
                    {
                        if (kv.Value == 0) continue;
                        freq.TryGetValue(kv.Key.ToString(), out int n);
                        freq[kv.Key.ToString()] = n + 1;
                    }
                }

                foreach (var kv in freq.OrderByDescending(x => x.Value))
                    Console.WriteLine($"   {kv.Key,-20} on {kv.Value} items");
                return 0;
            }

            if (Environment.GetEnvironmentVariable("SPAWNDUMP_MAGICS") == "1")
            {
                foreach (MagicInfo mi in session.GetCollection<MagicInfo>().Binding
                             .OrderBy(x => x.NeedLevel1))
                    Console.WriteLine($"   {mi.Name,-26} reqClass={mi.RequiredClass,-34} " +
                                      $"school={mi.School,-12} needLvl1={mi.NeedLevel1,-4} idx={mi.Index}");
                return 0;
            }

            // ---- copy PartCount and drop Chance/Amount from a REFERENCE database.
            //
            //     DBSYNC_REF=<folder with the reference System.db> DBSYNC_COMMIT=1
            //
            // Matched by NAME, never by index: the two databases were edited independently and
            // their internal ids have no reason to agree.
            //
            // Adds and removals are REPORTED, NOT APPLIED. Where one side has a drop line the
            // other does not, or an item the other lacks, copying it would mean creating rows and
            // wiring up foreign keys across two databases - a different and much riskier
            // operation than changing a number in place. Those are listed at the end so the
            // operator can decide.
            string syncRef = Environment.GetEnvironmentVariable("DBSYNC_REF");

            if (!string.IsNullOrEmpty(syncRef))
            {
                bool commit = Environment.GetEnvironmentVariable("DBSYNC_COMMIT") == "1";

                if (!syncRef.EndsWith("/") && !syncRef.EndsWith("\\"))
                    syncRef += Path.DirectorySeparatorChar;

                Session refSession = new Session(SessionMode.Users, syncRef) { BackUp = false };
                refSession.Initialize(Assembly.GetAssembly(typeof(ItemInfo)));

                Console.WriteLine($"# reference {syncRef} version {refSession.SystemDatabaseVersion}");

                // ---- part counts
                Dictionary<string,int> refParts = new Dictionary<string,int>(StringComparer.Ordinal);

                foreach (ItemInfo ri in refSession.GetCollection<ItemInfo>().Binding)
                    if (ri.ItemName != null) refParts[ri.ItemName] = ri.PartCount;

                int partsChanged = 0;

                foreach (ItemInfo ii in session.GetCollection<ItemInfo>().Binding
                             .OrderBy(x => x.ItemName, StringComparer.Ordinal))
                {
                    if (ii.ItemName == null) continue;
                    if (!refParts.TryGetValue(ii.ItemName, out int want)) continue;
                    if (ii.PartCount == want) continue;

                    Console.WriteLine($"   part  {ii.ItemName,-36} {ii.PartCount} -> {want}");
                    ii.PartCount = want;
                    partsChanged++;
                }

                // ---- drop chances, grouped so duplicate (monster,item) rows line up by rank
                Dictionary<string,List<DropInfo>> Group(Session sess)
                {
                    Dictionary<string,List<DropInfo>> g =
                        new Dictionary<string,List<DropInfo>>(StringComparer.Ordinal);

                    foreach (DropInfo d in sess.GetCollection<DropInfo>().Binding)
                    {
                        if (d?.Monster?.MonsterName == null || d.Item?.ItemName == null) continue;

                        string key = d.Monster.MonsterName + "" + d.Item.ItemName +
                                     "" + d.DropSet + "" + d.PartOnly;

                        if (!g.TryGetValue(key, out List<DropInfo> list))
                            g[key] = list = new List<DropInfo>();

                        list.Add(d);
                    }

                    foreach (List<DropInfo> list in g.Values)
                        list.Sort((a, b) => a.Chance.CompareTo(b.Chance));

                    return g;
                }

                Dictionary<string,List<DropInfo>> mine = Group(session);
                Dictionary<string,List<DropInfo>> theirs = Group(refSession);

                int dropsChanged = 0, mismatched = 0, onlyMine = 0, onlyTheirs = 0;

                foreach (KeyValuePair<string,List<DropInfo>> pair in mine.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    if (!theirs.TryGetValue(pair.Key, out List<DropInfo> other)) { onlyMine++; continue; }

                    // Different NUMBER of rows means an add or a removal, not a rate change.
                    if (other.Count != pair.Value.Count) { mismatched++; continue; }

                    for (int i = 0; i < pair.Value.Count; i++)
                    {
                        DropInfo a = pair.Value[i], b = other[i];
                        if (a.Chance == b.Chance && a.Amount == b.Amount) continue;

                        Console.WriteLine($"   drop  {a.Monster.MonsterName,-24} {a.Item.ItemName,-30} " +
                                          $"{a.Chance}/{a.Amount} -> {b.Chance}/{b.Amount}");
                        a.Chance = b.Chance;
                        a.Amount = b.Amount;
                        dropsChanged++;
                    }
                }

                foreach (string k in theirs.Keys) if (!mine.ContainsKey(k)) onlyTheirs++;

                Console.WriteLine($"   {partsChanged} part counts, {dropsChanged} drop rates changed");
                Console.WriteLine($"   NOT APPLIED: {onlyTheirs} drop group(s) only on the reference, " +
                                  $"{onlyMine} only here, {mismatched} with a different row count");
                Console.WriteLine($"   commit={commit}");

                if (commit) { session.Save(true); Console.WriteLine("   SAVED"); }
                else Console.WriteLine("   dry run - nothing written");

                return 0;
            }

            // Where do skill books come from? Sold, dropped, or nowhere.
            if (Environment.GetEnvironmentVariable("SPAWNDUMP_BOOKSOURCE") == "1")
            {
                // Books on sale anywhere, by goods index actually attached to an NPC.
                HashSet<int> npcGoods = new HashSet<int>();
                foreach (NPCInfo n in session.GetCollection<NPCInfo>().Binding) npcGoods.Add(n.GoodsIndex);

                HashSet<string> sold = new HashSet<string>(StringComparer.Ordinal);
                foreach (NPCGood g in session.GetCollection<NPCGood>().Binding)
                    if (g?.Item != null && g.Item.ItemType == ItemType.Book &&
                        npcGoods.Contains(g.GoodsIndex))
                        sold.Add(g.Item.ItemName);

                // Which monsters drop each book, and which maps those monsters live on.
                Dictionary<string,HashSet<string>> monsterMaps =
                    new Dictionary<string,HashSet<string>>(StringComparer.Ordinal);

                foreach (RespawnInfo r in session.GetCollection<RespawnInfo>().Binding)
                {
                    if (r?.Region?.Map == null || r.Monster?.MonsterName == null) continue;
                    if (!monsterMaps.TryGetValue(r.Monster.MonsterName, out HashSet<string> m))
                        monsterMaps[r.Monster.MonsterName] = m = new HashSet<string>(StringComparer.Ordinal);
                    m.Add(r.Region.Map.Description);
                }

                Dictionary<string,HashSet<string>> bookMaps =
                    new Dictionary<string,HashSet<string>>(StringComparer.Ordinal);

                foreach (DropInfo d in session.GetCollection<DropInfo>().Binding)
                {
                    if (d?.Item == null || d.Item.ItemType != ItemType.Book) continue;
                    if (d.Monster?.MonsterName == null) continue;
                    if (!monsterMaps.TryGetValue(d.Monster.MonsterName, out HashSet<string> maps)) continue;

                    if (!bookMaps.TryGetValue(d.Item.ItemName, out HashSet<string> b))
                        bookMaps[d.Item.ItemName] = b = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string m in maps) b.Add(m);
                }

                foreach (ItemInfo ii in session.GetCollection<ItemInfo>().Binding
                             .Where(x => x.ItemType == ItemType.Book && x.ItemName != null)
                             .OrderBy(x => x.RequiredClass.ToString(), StringComparer.Ordinal)
                             .ThenBy(x => x.RequiredAmount))
                {
                    bool isSold = sold.Contains(ii.ItemName);
                    bookMaps.TryGetValue(ii.ItemName, out HashSet<string> maps);

                    string where = maps == null || maps.Count == 0
                        ? "-"
                        : string.Join("|", maps.OrderBy(x => x, StringComparer.Ordinal).Take(40));

                    Console.WriteLine($"{ii.RequiredClass}	{ii.RequiredAmount}	{ii.ItemName}	" +
                                      $"{(isSold ? "SOLD" : "drop-only")}	{where}");
                }
                return 0;
            }

            // Read-only TSV of part counts, for comparing two databases.
            if (Environment.GetEnvironmentVariable("SPAWNDUMP_PARTS") == "1")
            {
                foreach (ItemInfo ii in session.GetCollection<ItemInfo>().Binding
                             .Where(x => x.ItemName != null)
                             .OrderBy(x => x.ItemName, StringComparer.Ordinal))
                    Console.WriteLine($"{ii.ItemName}	{ii.PartCount}");
                return 0;
            }

            // Read-only TSV of every drop entry.
            if (Environment.GetEnvironmentVariable("SPAWNDUMP_DROPS") == "1")
            {
                foreach (DropInfo d in session.GetCollection<DropInfo>().Binding
                             .Where(x => x.Monster != null && x.Item != null)
                             .OrderBy(x => x.Monster.MonsterName, StringComparer.Ordinal)
                             .ThenBy(x => x.Item.ItemName, StringComparer.Ordinal)
                             .ThenBy(x => x.Chance).ThenBy(x => x.Amount))
                    Console.WriteLine($"{d.Monster.MonsterName}	{d.Item.ItemName}	" +
                                      $"{d.Chance}	{d.Amount}	{d.DropSet}	{d.PartOnly}");
                return 0;
            }

            string itemQ = Environment.GetEnvironmentVariable("SPAWNDUMP_ITEMS");

            if (!string.IsNullOrEmpty(itemQ))
            {
                foreach (ItemInfo ii in session.GetCollection<ItemInfo>().Binding
                             .Where(x => x.ItemName != null &&
                                    x.ItemName.IndexOf(itemQ, StringComparison.OrdinalIgnoreCase) >= 0)
                             .OrderBy(x => x.ItemName))
                {
                    string st = ii.Stats == null ? "" : string.Join(" ",
                        ii.Stats.Values.Where(kv => kv.Value != 0)
                                       .Select(kv => kv.Key + "=" + kv.Value));
                    Console.WriteLine($"   {ii.ItemName,-26} type={ii.ItemType,-8} price={ii.Price,-7} weight={ii.Weight,-4} cansell={ii.CanSell} " +
                                      $"req={ii.RequiredType}:{ii.RequiredAmount,-4} " +
                                      $"durability={ii.Durability,-6} shape={ii.Shape,-5} {st}");
                }
                return 0;
            }

            string sellerQ = Environment.GetEnvironmentVariable("SPAWNDUMP_SELLERS");

            if (!string.IsNullOrEmpty(sellerQ))
            {
                // Goods are tied to an NPC by GoodsIndex, which is the same join the bot itself
                // makes in BestPotion (good.GoodsIndex != _target.NPC.GoodsIndex).
                HashSet<int> indexes = new HashSet<int>();

                foreach (NPCGood g in session.GetCollection<NPCGood>().Binding)
                    if (g?.Item?.ItemName != null &&
                        g.Item.ItemName.IndexOf(sellerQ, StringComparison.OrdinalIgnoreCase) >= 0)
                        indexes.Add(g.GoodsIndex);

                Console.WriteLine($"   goods indexes stocking \"{sellerQ}\": {string.Join(",", indexes.OrderBy(x => x))}");

                foreach (NPCInfo npc in session.GetCollection<NPCInfo>().Binding
                             .Where(n => indexes.Contains(n.GoodsIndex))
                             .OrderBy(n => n.Region?.Map?.Description))
                    Console.WriteLine($"   {npc.Region?.Map?.Description,-24} {npc.NPCName,-18} goodsIndex={npc.GoodsIndex}");

                return 0;
            }

            string monQ = Environment.GetEnvironmentVariable("SPAWNDUMP_MONSTERS");

            if (!string.IsNullOrEmpty(monQ))
            {
                foreach (MonsterInfo mi in session.GetCollection<MonsterInfo>().Binding
                             .Where(x => x.MonsterName != null &&
                                    (monQ == "*" ||
                                     x.MonsterName.IndexOf(monQ, StringComparison.OrdinalIgnoreCase) >= 0))
                             .OrderBy(x => x.AI).ThenBy(x => x.MonsterName))
                {
                    string drops = mi.Drops == null || mi.Drops.Count == 0
                        ? "-"
                        : string.Join(", ", mi.Drops.Select(d =>
                            (d.Item?.ItemName ?? "?") + (d.PartOnly ? "[part]" : "") +
                            "/" + d.Chance + (d.DropSet != 0 ? "/set" + d.DropSet : "")));

                    if (Environment.GetEnvironmentVariable("SPAWNDUMP_BRIEF") == "1")
                    {
                        Console.WriteLine($"   {mi.MonsterName,-24} AI={mi.AI,-4} L{mi.Level,-4} " +
                            $"flag={mi.Flag,-16} view={mi.ViewRange,-3} atkDelay={mi.AttackDelay} moveDelay={mi.MoveDelay}");
                        continue;
                    }

                    Console.WriteLine($"AI={mi.AI,-4} L{mi.Level,-4} flag={mi.Flag,-14} " +
                                      $"{mi.MonsterName,-26} drops: {drops}");
                }
                return 0;
            }

            if (Environment.GetEnvironmentVariable("SPAWNDUMP_BOOKDROPS") == "1")
            {
                int withBooks = 0;

                foreach (MonsterInfo mi in session.GetCollection<MonsterInfo>().Binding
                             .OrderBy(x => x.Level))
                {
                    if (mi?.Drops == null) continue;

                    var books = mi.Drops.Where(d => d?.Item != null &&
                                                    d.Item.ItemType == ItemType.Book).ToList();
                    if (books.Count == 0) continue;

                    withBooks++;
                    Console.WriteLine($"   L{mi.Level,-4} {mi.MonsterName,-26} " +
                        string.Join(", ", books.Select(b => b.Item.ItemName + "/" + b.Chance)));
                }

                Console.WriteLine($"   {withBooks} monsters drop books");
                return 0;
            }

            string npcQ = Environment.GetEnvironmentVariable("SPAWNDUMP_NPCBUYS");

            if (!string.IsNullOrEmpty(npcQ))
            {
                foreach (NPCInfo npc in session.GetCollection<NPCInfo>().Binding)
                {
                    if (npc?.NPCName == null) continue;
                    if (npc.NPCName.IndexOf(npcQ, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    Console.WriteLine($"   {npc.NPCName} on {npc.Region?.Map?.Description} goodsIndex={npc.GoodsIndex}");

                    foreach (NPCPage pg in session.GetCollection<NPCPage>().Binding)
                    {
                        if (pg.DialogType != NPCDialogType.BuySell) continue;
                        if (pg.Types == null || pg.Types.Count == 0) continue;

                        Console.WriteLine($"      page {pg.Index} buys: " +
                            string.Join(",", pg.Types.Select(t => t.ItemType)));
                    }
                    break;
                }
                return 0;
            }

            string moveQ = Environment.GetEnvironmentVariable("SPAWNDUMP_EXITS");

            if (!string.IsNullOrEmpty(moveQ))
            {
                foreach (MovementInfo mi in session.GetCollection<MovementInfo>().Binding)
                {
                    string from = mi.SourceRegion?.Map?.Description ?? "?";
                    if (from.IndexOf(moveQ, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Console.WriteLine($"   {from} -> {mi.DestinationRegion?.Map?.Description ?? "?"}");
                }
                return 0;
            }

            // Every non-zero stat on every item, one row per stat, for diffing two databases.
            if (Environment.GetEnvironmentVariable("SPAWNDUMP_ITEMSTATS") == "1")
            {
                foreach (ItemInfo ii in session.GetCollection<ItemInfo>().Binding
                             .Where(x => x.ItemName != null)
                             .OrderBy(x => x.ItemName, StringComparer.Ordinal))
                {
                    Console.WriteLine($"ITEM	{ii.ItemName}	{ii.ItemType}	{ii.RequiredType}	" +
                                      $"{ii.RequiredAmount}	{ii.Price}	{ii.Weight}	" +
                                      $"{ii.Durability}	{ii.StackSize}	{ii.Rarity}");

                    if (ii.Stats == null) continue;

                    foreach (var kv in ii.Stats.Values.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal))
                        if (kv.Value != 0)
                            Console.WriteLine($"STAT	{ii.ItemName}	{kv.Key}	{kv.Value}");
                }
                return 0;
            }

            // Quests: the quest row, then its requirements, rewards and tasks.
            if (Environment.GetEnvironmentVariable("SPAWNDUMP_QUESTS") == "1")
            {
                foreach (QuestInfo q in session.GetCollection<QuestInfo>().Binding
                             .Where(x => x.QuestName != null)
                             .OrderBy(x => x.QuestName, StringComparer.Ordinal))
                {
                    Console.WriteLine($"QUEST	{q.QuestName}	{q.QuestType}	" +
                                      $"{q.StartNPC?.NPCName ?? "-"}	{q.FinishNPC?.NPCName ?? "-"}");

                    if (q.Requirements != null)
                        foreach (var r in q.Requirements)
                            Console.WriteLine($"QREQ	{q.QuestName}	{r.Requirement}	" +
                                              $"{r.IntParameter1}	{r.Class}	{r.QuestParameter?.QuestName ?? "-"}");

                    if (q.Rewards != null)
                        foreach (var r in q.Rewards)
                            Console.WriteLine($"QRWD	{q.QuestName}	{r.Item?.ItemName ?? "-"}	" +
                                              $"{r.Amount}	{r.Choice}	{r.Bound}");

                    if (q.Tasks != null)
                        foreach (var t in q.Tasks)
                            Console.WriteLine($"QTSK	{q.QuestName}	{t.Task}	{t.Amount}	" +
                                              $"{t.ItemParameter?.ItemName ?? "-"}	{t.MobDescription ?? "-"}");
                }
                return 0;
            }

            if (Environment.GetEnvironmentVariable("SPAWNDUMP_MAPFLAGS") == "1")
            {
                foreach (MapInfo mi in session.GetCollection<MapInfo>().Binding)
                    Console.WriteLine($"   {mi.Index,4} {mi.Description,-24} AllowTT={mi.AllowTT} AllowRT={mi.AllowRT}");
                return 0;
            }

            if (Environment.GetEnvironmentVariable("SPAWNDUMP_TSV") == "1")
            {
                foreach (RespawnInfo r in session.GetCollection<RespawnInfo>().Binding)
                {
                    if (r?.Region?.Map == null || r.Monster == null) continue;

                    Console.WriteLine(string.Join("	",
                        r.Region.Map.Description, r.Region.Description,
                        r.Monster.MonsterName, r.Count));
                }

                return 0;
            }

            // ---- apply counts from a TSV onto this database.
            //
            // Matched on (map, region, monster) because a map can hold several regions and the
            // same monster can carry a different count in each. Dry-run unless SPAWNPATCH_COMMIT=1.
            string tsv = Environment.GetEnvironmentVariable("SPAWNPATCH_FROM");

            if (!string.IsNullOrEmpty(tsv))
            {
                bool commit = Environment.GetEnvironmentVariable("SPAWNPATCH_COMMIT") == "1";
                bool noDecrease = Environment.GetEnvironmentVariable("SPAWNPATCH_NODECREASE") == "1";

                Dictionary<string,int> want = new Dictionary<string,int>();

                foreach (string line in File.ReadAllLines(tsv))
                {
                    string[] f = line.Split('	');
                    if (f.Length != 4) continue;
                    want[f[0] + "" + f[1] + "" + f[2]] = int.Parse(f[3]);
                }

                // Explicit overrides: "Map|Region|Monster=Count", semicolon separated.
                Dictionary<string,int> over = new Dictionary<string,int>();
                string ov = Environment.GetEnvironmentVariable("SPAWNPATCH_SET") ?? "";

                foreach (string one in ov.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = one.LastIndexOf('=');
                    string[] k = one.Substring(0, eq).Split('|');
                    over[k[0] + "" + k[1] + "" + k[2]] = int.Parse(one.Substring(eq + 1));
                }

                int changed = 0, skipped = 0;

                foreach (RespawnInfo r in session.GetCollection<RespawnInfo>().Binding)
                {
                    if (r?.Region?.Map == null || r.Monster == null) continue;

                    string key = r.Region.Map.Description + "" + r.Region.Description +
                                 "" + r.Monster.MonsterName;

                    int target;

                    if (over.TryGetValue(key, out int o)) target = o;
                    else if (want.TryGetValue(key, out int w)) target = w;
                    else continue;

                    if (target == r.Count) continue;

                    if (noDecrease && target < r.Count && !over.ContainsKey(key))
                    {
                        Console.WriteLine($"   skip  {r.Region.Map.Description} / {r.Monster.MonsterName}: " +
                                          $"{r.Count} -> {target} (decrease)");
                        skipped++;
                        continue;
                    }

                    Console.WriteLine($"   set   {r.Region.Map.Description} / {r.Region.Description} / " +
                                      $"{r.Monster.MonsterName}: {r.Count} -> {target}");
                    r.Count = target;
                    changed++;
                }

                Console.WriteLine($"   {changed} changed, {skipped} skipped, commit={commit}");

                if (commit) { session.Save(true); Console.WriteLine("   SAVED"); }
                else Console.WriteLine("   dry run - nothing written");

                return 0;
            }

            // ---- set WearWeight explicitly from a "Class <tab> Level <tab> Wear" file.
            //
            //     WEARSET_TSV=curve.tsv WEARSET_COMMIT=1
            //
            // Used to continue a hand-edited curve past the level the editing stopped at. The
            // operator raised Wizard wear to level 61 and the original table resumed at 62, so a
            // wizard crossing that level lost 141 of its 195 allowance in one step and would have
            // shed most of its equipment. Only WearWeight is written.
            string wearTsv = Environment.GetEnvironmentVariable("WEARSET_TSV");

            if (!string.IsNullOrEmpty(wearTsv))
            {
                bool commit = Environment.GetEnvironmentVariable("WEARSET_COMMIT") == "1";
                Dictionary<string,int> want = new Dictionary<string,int>();

                foreach (string line in File.ReadAllLines(wearTsv))
                {
                    string[] f = line.Split('	');
                    if (f.Length != 3) continue;
                    want[f[0].Trim() + "|" + f[1].Trim()] = int.Parse(f[2]);
                }

                int changed = 0, same = 0;

                foreach (BaseStat bs in session.GetCollection<BaseStat>().Binding
                             .OrderBy(x => x.Class).ThenBy(x => x.Level))
                {
                    if (!want.TryGetValue(bs.Class + "|" + bs.Level, out int target)) continue;
                    if (bs.WearWeight == target) { same++; continue; }

                    Console.WriteLine($"   {bs.Class} L{bs.Level,-3} Wear {bs.WearWeight} -> {target}");
                    bs.WearWeight = target;
                    changed++;
                }

                Console.WriteLine($"   {changed} changed, {same} already correct, commit={commit}");

                if (commit) { session.Save(true); Console.WriteLine("   SAVED"); }
                else Console.WriteLine("   dry run - nothing written");

                return 0;
            }

            // ---- copy one class's WearWeight curve onto another, level by level.
            //
            //     WEARCOPY_FROM=Wizard WEARCOPY_TO=Taoist WEARCOPY_MAXLEVEL=60 WEARCOPY_COMMIT=1
            //
            // BaseStat is one row per (Class, Level). Only WearWeight is touched - Health, Mana,
            // BagWeight and HandWeight are each class's own balance and copying them across would
            // quietly rewrite far more than was asked for.
            string wearFrom = Environment.GetEnvironmentVariable("WEARCOPY_FROM");

            if (!string.IsNullOrEmpty(wearFrom))
            {
                string wearTo = Environment.GetEnvironmentVariable("WEARCOPY_TO");
                bool commit = Environment.GetEnvironmentVariable("WEARCOPY_COMMIT") == "1";
                int maxLevel = int.Parse(Environment.GetEnvironmentVariable("WEARCOPY_MAXLEVEL") ?? "60");

                MirClass src = Enum.Parse<MirClass>(wearFrom, true);
                MirClass dst = Enum.Parse<MirClass>(wearTo, true);

                Dictionary<int,int> curve = new Dictionary<int,int>();

                foreach (BaseStat bs in session.GetCollection<BaseStat>().Binding)
                    if (bs.Class == src) curve[bs.Level] = bs.WearWeight;

                int changed = 0;

                foreach (BaseStat bs in session.GetCollection<BaseStat>().Binding
                             .Where(x => x.Class == dst).OrderBy(x => x.Level))
                {
                    if (bs.Level > maxLevel) continue;
                    if (!curve.TryGetValue(bs.Level, out int want)) continue;
                    if (bs.WearWeight == want) continue;

                    Console.WriteLine($"   {dst} L{bs.Level,-3} Wear {bs.WearWeight} -> {want}");
                    bs.WearWeight = want;
                    changed++;
                }

                Console.WriteLine($"   {changed} changed, commit={commit}");

                if (commit) { session.Save(true); Console.WriteLine("   SAVED"); }
                else Console.WriteLine("   dry run - nothing written");

                return 0;
            }

            if (Environment.GetEnvironmentVariable("SPAWNDUMP_COUNTS") == "1")
            {
                Console.WriteLine($"   NPCInfo   {session.GetCollection<NPCInfo>().Count}");
                Console.WriteLine($"   NPCPage   {session.GetCollection<NPCPage>().Count}");
                Console.WriteLine($"   RespawnInfo {session.GetCollection<RespawnInfo>().Count}");
                Console.WriteLine($"   MapInfo   {session.GetCollection<MapInfo>().Count}");
                Console.WriteLine($"   MovementInfo {session.GetCollection<MovementInfo>().Count}");
                return 0;
            }

            if (Environment.GetEnvironmentVariable("SPAWNDUMP_NPCTYPES") == "1")
            {
                var pages = session.GetCollection<NPCPage>();
                int withNothing = 0, total = 0, buysell = 0;

                foreach (NPCPage pg in pages.Binding)
                {
                    total++;
                    if (pg.DialogType != NPCDialogType.BuySell) continue;
                    buysell++;

                    var types = pg.Types.Select(t => t.ItemType).ToList();

                    if (types.Contains(ItemType.Nothing))
                    {
                        withNothing++;
                        if (withNothing <= 6)
                            Console.WriteLine($"   page {pg.Index} accepts Nothing; types = {string.Join(",", types)}");
                    }
                }

                Console.WriteLine($"   {total} pages, {buysell} BuySell, {withNothing} of which list ItemType.Nothing");
                return 0;
            }

            DBCollection<RespawnInfo> respawns = session.GetCollection<RespawnInfo>();

            string[] filters = args.Skip(1).ToArray();

            // Map name -> (monster -> total count, and the spawn entries behind it).
            SortedDictionary<string, SortedDictionary<string, (int Count, int Entries, int MinDelay)>> byMap =
                new SortedDictionary<string, SortedDictionary<string, (int, int, int)>>();

            foreach (RespawnInfo r in respawns.Binding)
            {
                if (r?.Region?.Map == null || r.Monster == null) continue;

                string map = r.Region.Map.Description ?? r.Region.Map.FileName ?? "?";

                if (filters.Length > 0 &&
                    !filters.Any(f => map.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;

                if (!byMap.TryGetValue(map, out var monsters))
                    byMap[map] = monsters = new SortedDictionary<string, (int, int, int)>();

                string name = r.Monster.MonsterName ?? "?";

                monsters.TryGetValue(name, out var cur);

                monsters[name] = (cur.Count + r.Count,
                                  cur.Entries + 1,
                                  cur.Entries == 0 ? r.Delay : Math.Min(cur.MinDelay, r.Delay));
            }

            // Per-region detail, because a map can hold several regions and the same monster can
            // carry a different count in each - four Minotaur entries on one map, for example.
            // Any copy between databases has to match on (map, region, monster), not (map, monster).
            if (Environment.GetEnvironmentVariable("SPAWNDUMP_REGIONS") == "1")
            {
                foreach (RespawnInfo r in respawns.Binding
                             .Where(x => x?.Region?.Map != null && x.Monster != null)
                             .Where(x => filters.Length == 0 || filters.Any(f =>
                                 (x.Region.Map.Description ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0))
                             .OrderBy(x => x.Region.Map.Description)
                             .ThenBy(x => x.Region.Description)
                             .ThenBy(x => x.Monster.MonsterName))
                {
                    Console.WriteLine($"   {r.Count,6}  x {r.Monster.MonsterName,-26} " +
                                      $"region '{r.Region.Description}' " +
                                      $"({r.Region.PointRegion?.Length ?? 0} pts, size {r.Region.Size}) " +
                                      $"delay {r.Delay}  [{r.Region.Map.Description}]");
                }

                return 0;
            }

            foreach (var map in byMap)
            {
                int total = map.Value.Sum(x => x.Value.Count);

                Console.WriteLine();
                Console.WriteLine($"== {map.Key}  (total spawn count {total}, {map.Value.Count} monster types)");

                foreach (var m in map.Value.OrderByDescending(x => x.Value.Count))
                    Console.WriteLine($"   {m.Value.Count,6}  x {m.Key}   ({m.Value.Entries} entr(y/ies), min delay {m.Value.MinDelay})");
            }

            if (byMap.Count == 0) Console.WriteLine("(no spawns matched)");

            return 0;
        }
    }
}
