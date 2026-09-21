using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Library;
using Library.SystemModels;
using MirDB;

namespace SpawnDump
{
    /// <summary>
    /// Copy content from a DONOR System.db into ours: item definitions and stats, drop rows,
    /// quests and quest rewards.
    ///
    /// Two passes over two separate sessions rather than one merged session, because MirDB binds
    /// its collections to static Globals lists - opening two databases at once would have the
    /// second quietly overwrite the first's bindings. So the donor is EXPORTED to a plain in-memory
    /// model first, the donor session is closed, and only then is ours opened for writing.
    ///
    /// Reflection rather than a hand-written field list. ItemInfo alone has dozens of properties
    /// and a hand-list is a silent data-loss bug waiting to happen: the field nobody remembered is
    /// the one that matters. Anything that is a value type, string or enum is copied by name;
    /// object references (ItemInfo, NPCInfo, QuestInfo, MapRegion) are resolved by identity in the
    /// destination, and a reference that cannot be resolved makes the whole row fail loudly rather
    /// than silently landing as null.
    ///
    ///     MERGE_FROM=&lt;folder with donor System.db&gt;  [MERGE_COMMIT=1]  &lt;our folder&gt;
    ///
    /// Dry run unless MERGE_COMMIT=1. The destination MUST be opened in SessionMode.System or
    /// SaveSystem writes nothing while still reporting success.
    /// </summary>
    internal static class Merge
    {
        internal sealed class Row
        {
            public string Key = "";
            public Dictionary<string, string> Values = new Dictionary<string, string>();
            public Dictionary<Stat, int> Stats = new Dictionary<Stat, int>();
            public List<Dictionary<string, string>> Children = new List<Dictionary<string, string>>();
            public List<string> ChildKinds = new List<string>();
        }

        private static bool Simple(Type t) =>
            t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) ||
            t == typeof(DateTime) || t == typeof(TimeSpan) ||
            (Nullable.GetUnderlyingType(t) != null && Simple(Nullable.GetUnderlyingType(t)));

        private static IEnumerable<PropertyInfo> Copyable(Type t) =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
             .Where(p => p.CanRead && p.CanWrite && Simple(p.PropertyType) &&
                         p.Name != "Index" &&
                         p.GetCustomAttribute<IgnorePropertyAttribute>() == null);

        private static string Write(object v) =>
            v == null ? "\u0000null"
                      : Convert.ToString(v, CultureInfo.InvariantCulture);

        private static object Read(string s, Type t)
        {
            if (s == "\u0000null") return null;

            Type u = Nullable.GetUnderlyingType(t) ?? t;

            if (u.IsEnum) return Enum.Parse(u, s, true);
            if (u == typeof(string)) return s;

            return Convert.ChangeType(s, u, CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ export

        public static (List<Row> items, List<Row> drops, List<Row> quests) Export(string folder)
        {
            Session donor = new Session(SessionMode.Users, folder) { BackUp = false };
            donor.Initialize(Assembly.GetAssembly(typeof(ItemInfo)));

            List<Row> items = new List<Row>();
            List<Row> drops = new List<Row>();
            List<Row> quests = new List<Row>();

            foreach (ItemInfo ii in donor.GetCollection<ItemInfo>().Binding)
            {
                if (ii?.ItemName == null) continue;

                Row r = new Row { Key = ii.ItemName };
                foreach (PropertyInfo p in Copyable(typeof(ItemInfo)))
                    r.Values[p.Name] = Write(p.GetValue(ii));

                if (ii.Stats != null)
                    foreach (var kv in ii.Stats.Values)
                        if (kv.Value != 0) r.Stats[kv.Key] = kv.Value;

                items.Add(r);
            }

            foreach (DropInfo d in donor.GetCollection<DropInfo>().Binding)
            {
                if (d?.Monster?.MonsterName == null || d.Item?.ItemName == null) continue;

                Row r = new Row { Key = d.Monster.MonsterName + "\u0001" + d.Item.ItemName };
                foreach (PropertyInfo p in Copyable(typeof(DropInfo)))
                    r.Values[p.Name] = Write(p.GetValue(d));

                drops.Add(r);
            }

            foreach (QuestInfo q in donor.GetCollection<QuestInfo>().Binding)
            {
                if (q?.QuestName == null) continue;

                Row r = new Row { Key = q.QuestName };
                foreach (PropertyInfo p in Copyable(typeof(QuestInfo)))
                    r.Values[p.Name] = Write(p.GetValue(q));

                r.Values["@StartNPC"] = q.StartNPC?.NPCName ?? "\u0000null";
                r.Values["@FinishNPC"] = q.FinishNPC?.NPCName ?? "\u0000null";

                AddChildren(r, "REQ", q.Requirements, x => new Dictionary<string, string>
                {
                    ["@QuestParameter"] = x.QuestParameter?.QuestName ?? "\u0000null"
                });
                AddChildren(r, "RWD", q.Rewards, x => new Dictionary<string, string>
                {
                    ["@Item"] = x.Item?.ItemName ?? "\u0000null"
                });
                AddChildren(r, "TSK", q.Tasks, x => new Dictionary<string, string>
                {
                    ["@ItemParameter"] = x.ItemParameter?.ItemName ?? "\u0000null"
                });

                quests.Add(r);
            }

            return (items, drops, quests);
        }

        private static void AddChildren<T>(Row r, string kind, IEnumerable<T> list,
            Func<T, Dictionary<string, string>> refs) where T : DBObject
        {
            if (list == null) return;

            foreach (T child in list)
            {
                Dictionary<string, string> d = new Dictionary<string, string>();

                foreach (PropertyInfo p in Copyable(typeof(T)))
                    d[p.Name] = Write(p.GetValue(child));

                foreach (var kv in refs(child)) d[kv.Key] = kv.Value;

                r.Children.Add(d);
                r.ChildKinds.Add(kind);
            }
        }

        // ------------------------------------------------------------------ apply

        public static int Apply(string folder, List<Row> items, List<Row> drops, List<Row> quests,
            bool commit)
        {
            Session ours = new Session(SessionMode.System, folder) { BackUp = false };
            ours.Initialize(Assembly.GetAssembly(typeof(ItemInfo)));

            DBCollection<ItemInfo> itemCol = ours.GetCollection<ItemInfo>();
            DBCollection<MonsterInfo> monCol = ours.GetCollection<MonsterInfo>();
            DBCollection<DropInfo> dropCol = ours.GetCollection<DropInfo>();
            DBCollection<QuestInfo> questCol = ours.GetCollection<QuestInfo>();
            DBCollection<NPCInfo> npcCol = ours.GetCollection<NPCInfo>();
            DBCollection<ItemInfoStat> statCol = ours.GetCollection<ItemInfoStat>();

            Dictionary<string, ItemInfo> byItem = itemCol.Binding
                .Where(x => x.ItemName != null)
                .GroupBy(x => x.ItemName).ToDictionary(g => g.Key, g => g.First());
            Dictionary<string, MonsterInfo> byMonster = monCol.Binding
                .Where(x => x.MonsterName != null)
                .GroupBy(x => x.MonsterName).ToDictionary(g => g.Key, g => g.First());
            Dictionary<string, NPCInfo> byNpc = npcCol.Binding
                .Where(x => x.NPCName != null)
                .GroupBy(x => x.NPCName).ToDictionary(g => g.Key, g => g.First());

            int newItems = 0, changedItems = 0, changedStats = 0, newDrops = 0,
                newQuests = 0, newRewards = 0, failed = 0;

            // ---- items -------------------------------------------------------------
            foreach (Row r in items)
            {
                bool fresh = !byItem.TryGetValue(r.Key, out ItemInfo target);

                if (fresh)
                {
                    target = itemCol.CreateNewObject();
                    byItem[r.Key] = target;
                    newItems++;
                    Console.WriteLine($"   NEW ITEM   {r.Key}");
                }

                bool touched = false;

                foreach (PropertyInfo p in Copyable(typeof(ItemInfo)))
                {
                    if (!r.Values.TryGetValue(p.Name, out string raw)) continue;

                    object want = Read(raw, p.PropertyType);
                    object have = p.GetValue(target);

                    if (Equals(want, have)) continue;

                    if (!fresh)
                        Console.WriteLine($"   item  {r.Key,-34} {p.Name}: {have} -> {want}");

                    p.SetValue(target, want);
                    touched = true;
                }

                if (touched && !fresh) changedItems++;

                // STATS LIVE IN ItemStats, NOT IN Stats.
                //
                // ItemInfo.Stats is a plain runtime field rebuilt by StatsChanged() from the
                // ItemStats collection (ItemInfo.cs:485-490). Assigning to it changes nothing on
                // disk - the first version of this did exactly that, reported "79 restatted", and
                // a verification pass against the donor found 83 differences still standing.
                // Writing the ItemInfoStat rows is the only thing that persists.
                {
                    HashSet<Stat> all = new HashSet<Stat>(r.Stats.Keys);

                    foreach (ItemInfoStat existing in target.ItemStats) all.Add(existing.Stat);

                    bool statsTouched = false;

                    foreach (Stat s in all)
                    {
                        r.Stats.TryGetValue(s, out int want);

                        List<ItemInfoStat> rows = target.ItemStats.Where(x => x.Stat == s).ToList();
                        int have = rows.Sum(x => x.Amount);

                        if (want == have) continue;

                        if (!fresh)
                            Console.WriteLine($"   stat  {r.Key,-34} {s}: {have} -> {want}");

                        // Collapse any duplicates to a single row, then set or remove it.
                        for (int i = 1; i < rows.Count; i++) rows[i].Delete();

                        if (want == 0)
                        {
                            if (rows.Count > 0) rows[0].Delete();
                        }
                        else if (rows.Count > 0)
                        {
                            rows[0].Amount = want;
                        }
                        else
                        {
                            ItemInfoStat row = statCol.CreateNewObject();
                            row.Item = target;
                            row.Stat = s;
                            row.Amount = want;
                        }

                        statsTouched = true;
                    }

                    if (statsTouched)
                    {
                        target.StatsChanged();
                        if (!fresh) changedStats++;
                    }
                }
            }

            // ---- drops -------------------------------------------------------------
            HashSet<string> haveDrop = new HashSet<string>(
                dropCol.Binding.Where(x => x.Monster?.MonsterName != null && x.Item?.ItemName != null)
                    .Select(x => x.Monster.MonsterName + "\u0001" + x.Item.ItemName + "\u0001" +
                                 x.Chance + "\u0001" + x.Amount + "\u0001" + x.DropSet + "\u0001" +
                                 x.PartOnly));

            foreach (Row r in drops)
            {
                string[] parts = r.Key.Split('\u0001');

                string sig = r.Key + "\u0001" + r.Values["Chance"] + "\u0001" +
                             r.Values["Amount"] + "\u0001" + r.Values["DropSet"] + "\u0001" +
                             r.Values["PartOnly"];

                if (haveDrop.Contains(sig)) continue;

                if (!byMonster.TryGetValue(parts[0], out MonsterInfo mon) ||
                    !byItem.TryGetValue(parts[1], out ItemInfo item))
                {
                    Console.WriteLine($"   FAIL drop {parts[0]} / {parts[1]}: not in our database");
                    failed++;
                    continue;
                }

                DropInfo d = dropCol.CreateNewObject();
                d.Monster = mon;
                d.Item = item;

                foreach (PropertyInfo p in Copyable(typeof(DropInfo)))
                    if (r.Values.TryGetValue(p.Name, out string raw))
                        p.SetValue(d, Read(raw, p.PropertyType));

                haveDrop.Add(sig);
                newDrops++;
                Console.WriteLine($"   NEW DROP   {parts[0]} -> {parts[1]} " +
                                  $"(1 in {r.Values["Chance"]})");
            }

            // ---- quests ------------------------------------------------------------
            Dictionary<string, QuestInfo> byQuest = questCol.Binding
                .Where(x => x.QuestName != null)
                .GroupBy(x => x.QuestName).ToDictionary(g => g.Key, g => g.First());

            // Two passes: create every quest shell first, so a requirement pointing at another
            // new quest can be resolved regardless of the order they appear in.
            foreach (Row r in quests)
            {
                if (byQuest.ContainsKey(r.Key)) continue;

                QuestInfo q = questCol.CreateNewObject();
                q.QuestName = r.Key;
                byQuest[r.Key] = q;
                newQuests++;
                Console.WriteLine($"   NEW QUEST  {r.Key}");
            }

            foreach (Row r in quests)
            {
                QuestInfo q = byQuest[r.Key];

                foreach (PropertyInfo p in Copyable(typeof(QuestInfo)))
                    if (r.Values.TryGetValue(p.Name, out string raw))
                    {
                        object want = Read(raw, p.PropertyType);
                        if (!Equals(want, p.GetValue(q))) p.SetValue(q, want);
                    }

                if (!Link(r.Values["@StartNPC"], byNpc, x => q.StartNPC = x, "StartNPC", r.Key)) failed++;
                if (!Link(r.Values["@FinishNPC"], byNpc, x => q.FinishNPC = x, "FinishNPC", r.Key)) failed++;

                // Rewards only - requirements and tasks on EXISTING quests are already identical
                // (verified: the only difference across all 34 shared quests is the Fame Point
                // reward), and rewriting them would risk churning content that is already correct.
                for (int i = 0; i < r.Children.Count; i++)
                {
                    if (r.ChildKinds[i] != "RWD") continue;

                    Dictionary<string, string> c = r.Children[i];
                    string itemName = c["@Item"];

                    if (itemName == "\u0000null") continue;
                    if (!byItem.TryGetValue(itemName, out ItemInfo rewardItem))
                    {
                        Console.WriteLine($"   FAIL reward {r.Key}: no item '{itemName}'");
                        failed++;
                        continue;
                    }

                    int amount = int.Parse(c["Amount"], CultureInfo.InvariantCulture);

                    bool already = q.Rewards != null && q.Rewards.Any(
                        x => x.Item == rewardItem && x.Amount == amount);

                    if (already) continue;

                    QuestReward rw = ours.GetCollection<QuestReward>().CreateNewObject();
                    rw.Quest = q;
                    rw.Item = rewardItem;

                    foreach (PropertyInfo p in Copyable(typeof(QuestReward)))
                        if (c.TryGetValue(p.Name, out string raw))
                            p.SetValue(rw, Read(raw, p.PropertyType));

                    newRewards++;
                    Console.WriteLine($"   NEW REWARD {r.Key,-34} {itemName} x{amount}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"   items:   {newItems} new, {changedItems} changed, {changedStats} restatted");
            Console.WriteLine($"   drops:   {newDrops} new");
            Console.WriteLine($"   quests:  {newQuests} new, {newRewards} rewards added");
            Console.WriteLine($"   failures: {failed}");

            if (failed > 0)
            {
                Console.WriteLine("   REFUSING TO SAVE - unresolved references above.");
                return 1;
            }

            if (!commit)
            {
                Console.WriteLine("   dry run - nothing written.");
                return 0;
            }

            ours.Save(true);
            Console.WriteLine("   SAVED");
            return 0;
        }

        /// <summary>
        /// Donor name -> our name, from MERGE_NPC_ALIAS ("Joe=Joeban;Bob=Robert").
        ///
        /// The donor server has an NPC called "Joe" that simply does not exist here; five of its
        /// quests start and finish at him. Rather than let those quests land with null endpoints -
        /// a quest nobody can hand in, and invisible until someone tries - the operator names the
        /// stand-in explicitly.
        /// </summary>
        private static Dictionary<string, string> Aliases()
        {
            Dictionary<string, string> map =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string pair in (Environment.GetEnvironmentVariable("MERGE_NPC_ALIAS") ?? "")
                         .Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;

                map[pair.Substring(0, eq).Trim()] = pair.Substring(eq + 1).Trim();
            }

            return map;
        }

        private static readonly Dictionary<string, string> NpcAlias = Aliases();

        private static bool Link<T>(string name, Dictionary<string, T> lookup, Action<T> set,
            string what, string owner) where T : class
        {
            if (name == "\u0000null") { set(null); return true; }

            if (lookup.TryGetValue(name, out T found)) { set(found); return true; }

            if (NpcAlias.TryGetValue(name, out string alias) &&
                lookup.TryGetValue(alias, out T aliased))
            {
                Console.WriteLine($"   alias {what} on '{owner}': {name} -> {alias}");
                set(aliased);
                return true;
            }

            Console.WriteLine($"   FAIL {what} on '{owner}': no '{name}' in our database");
            return false;
        }
    }
}
