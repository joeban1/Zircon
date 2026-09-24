using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Library;
using Library.SystemModels;
using MirDB;
using Xunit;

namespace MirBot.Tests
{
    /// <summary>
    /// Every quest giver (scoping, boss levels, the level-aware map rule, quest items, talk-only
    /// hand-offs) and the fame ranks (order, and the press/commit/refuse protocol).
    /// </summary>
    public sealed class QuestFameTests
    {
        // ---- quests ------------------------------------------------------------------------

        [Theory]
        [InlineData(39, false)]
        [InlineData(40, true)]
        public void AMiniBossQuestWaitsForForty(int level, bool expected)
        {
            MonsterInfo lord = Monster(1, "Skeleton Lord", boss: true, health: 850);
            QuestInfo quest = Quest(1, "Crushing the Remains Pt. 2", Npc(1, "Lennard"), Npc(1, "Lennard"), lord);

            Assert.Equal(expected, new QuestBook().CanAccept(quest, World(level), Rules()));
        }

        [Theory]
        [InlineData(49, false)]
        [InlineData(50, true)]
        public void CrazedWarriorStillWaitsForFifty(int level, bool expected)
        {
            MonsterInfo crazed = Monster(2, "Crazed Warrior", boss: true, health: 3300);
            QuestInfo quest = Quest(2, "Level 40 - Well done", Npc(2, "Joeban"), Npc(2, "Joeban"), crazed, amount: 1);

            Assert.Equal(expected, new QuestBook().CanAccept(quest, World(level), Rules()));
        }

        [Fact]
        public void AQuestWhoseMonstersLiveOnlyWhereWeMayNotHuntIsNotTaken()
        {
            MapInfo forest = Map(14, "Lost Paradise Forest");
            MonsterInfo elephant = Monster(3, "Evil Elephant", boss: false, health: 1100);
            QuestInfo quest = Quest(3, "Elephants on Parade", Npc(3, "Hardy"), Npc(3, "Hardy"), elephant,
                amount: 50, map: forest);
            QuestBook book = Book(quest);
            SpawnOn(book, elephant, 14);

            QuestRules tooHard = Rules(huntable: map => false);
            QuestRules fine = Rules(huntable: map => map == 14);

            Assert.False(book.CanAccept(quest, World(38), tooHard));
            Assert.True(book.CanAccept(quest, World(48), fine));
        }

        [Fact]
        public void AnNpcIsOnlyAskedAboutItsOwnQuests()
        {
            NPCInfo kang = Npc(13, "Mr. Kang"), david = Npc(15, "David");
            QuestInfo pt4 = Quest(4, "Curing the Poison Pt. 4", kang, david, null);   // talk-only hand-off
            QuestInfo pt5 = Quest(5, "Curing the Poison Pt. 5", david, kang, null);
            QuestBook book = Book(pt4, pt5);
            WorldModel world = World(30);

            var kangWork = book.WorkFor(kang, world, Rules());
            Assert.Single(kangWork.Accepts);
            Assert.Equal("Curing the Poison Pt. 4", kangWork.Accepts[0].QuestName);

            // Accepted at Mr. Kang, a talk-only quest is ready at once - and ready at DAVID.
            world.ResetQuests(new[] { UserQuest(1, pt4, 0) });
            Assert.Empty(book.WorkFor(kang, world, Rules()).HandIns);
            Assert.Single(book.WorkFor(david, world, Rules()).HandIns);
            Assert.Equal("talk to David", QuestBook.ProgressText(world.Quests.First()));
        }

        [Fact]
        public void TheNpcFilterStillWorksPerBot()
        {
            QuestInfo daily = Quest(6, "Do your dailies 2", Npc(141, "Joeban"), Npc(141, "Joeban"),
                Monster(4, "Pig", false, 50));
            QuestRules onlyLinda = Rules();
            onlyLinda.NpcFilter = new HashSet<string>(new[] { "Linda" }, StringComparer.OrdinalIgnoreCase);

            Assert.True(new QuestBook().CanAccept(daily, World(45), Rules()));
            Assert.False(new QuestBook().CanAccept(daily, World(45), onlyLinda));
        }

        [Fact]
        public void AFloorItemIsWantedOnlyWhileItsGatherTaskIsOpen()
        {
            ItemInfo spine = Item(500, "Skeletal Spine");
            QuestInfo gather = Quest(7, "Crushing the Remains Pt. 1", Npc(16, "Lennard"), Npc(16, "Lennard"),
                Monster(5, "Skeleton", false, 100), amount: 50, gatherItem: spine);
            QuestBook book = Book(gather);
            WorldModel world = World(30);

            Assert.False(book.QuestItemWanted(spine, world));           // not accepted yet

            world.ResetQuests(new[] { UserQuest(1, gather, 12) });
            Assert.True(book.QuestItemWanted(spine, world));
            Assert.False(book.QuestItemWanted(Item(501, "Zombie Flesh"), world));

            world.ResetQuests(new[] { UserQuest(1, gather, 50) });      // done: no more pickups
            Assert.False(book.QuestItemWanted(spine, world));
            Assert.Equal("50/50 Skeletal Spine", QuestBook.ProgressText(world.Quests.First()));
        }

        [Fact]
        public void TheMiniBossRuleNeedsTwoSpawnsReturningWithinTheHour()
        {
            MonsterInfo lord = Monster(8, "Skeleton Lord", true, 850);
            AddRespawn(lord, Map(30, "Bichon Cave Lv 3"), count: 2, delay: 30);
            MonsterInfo lich = Monster(9, "Arch Lich Taedu", true, 15000);
            AddRespawn(lich, Map(31, "Banya Stone Cave Lv 5"), count: 1, delay: 300);

            Assert.True(QuestBook.IsMiniBoss(lord));
            Assert.False(QuestBook.IsMiniBoss(lich));
        }

        // ---- fame --------------------------------------------------------------------------

        [Fact]
        public void TheNextRankFollowsOrderNotIndex()
        {
            FameBook book = Fame(Rank(7, order: 1, cost: 500, "Village Explorer"),
                                 Rank(3, order: 0, cost: 1000, "Unknown Novice"),
                                 Rank(9, order: 2, cost: 2000, "Regional Apprentice"));

            Assert.Equal("Unknown Novice", book.Next(0).Name);
            Assert.Equal("Village Explorer", book.Next(3).Name);
            Assert.Equal("Regional Apprentice", book.Next(7).Name);
            Assert.Null(book.Next(9));
        }

        [Fact]
        public void AFameRankIsBoughtOncePerPressAndNeverPressedAgainAfterFpFalls()
        {
            FameBook book = Fame(Rank(1, 0, 1000, "Unknown Novice"), Rank(2, 1, 500, "Village Explorer"));
            WorldModel world = FameWorld(fp: 1600, fameIndex: 0);
            FameErrand errand = new FameErrand(new BotConfig(), book, _ => { });
            List<FameInfo> reached = new List<FameInfo>();
            errand.OnPromoted = (rank, left) => reached.Add(rank);
            Backpack bag = new Backpack();
            DateTime t = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

            Assert.Equal(BotAction.NPCCall, errand.Next(world, bag, t).Action);
            errand.PageChanged(null);
            Decision press = errand.Next(world, bag, t.AddSeconds(1));
            Assert.Equal(BotAction.NPCButton, press.Action);
            Assert.Equal(1, press.ButtonID);

            // FP falls first; the rank shows later. Meanwhile: wait, never press again.
            world.ApplyCurrency(4, 600);
            Assert.Equal(BotAction.Idle, errand.Next(world, bag, t.AddSeconds(2)).Action);
            Assert.Equal(BotAction.Idle, errand.Next(world, bag, t.AddSeconds(3)).Action);

            world.ApplyStats(new Stats { [Stat.Fame] = 1 });
            Assert.Equal(BotAction.NPCCall, errand.Next(world, bag, t.AddSeconds(4)).Action);   // re-open for rank 2
            Assert.Single(reached);
            Assert.Equal("Unknown Novice", reached[0].Name);

            errand.PageChanged(null);
            Assert.Equal(BotAction.NPCButton, errand.Next(world, bag, t.AddSeconds(5)).Action);
            world.ApplyCurrency(4, 100);
            world.ApplyStats(new Stats { [Stat.Fame] = 2 });
            Assert.Null(errand.Next(world, bag, t.AddSeconds(6)));                               // nothing more affordable
            Assert.Equal(2, reached.Count);
            Assert.False(errand.Active);
        }

        [Fact]
        public void AFamePressThatChangesNothingIsRefusedAndBackedOff()
        {
            FameBook book = Fame(Rank(1, 0, 1000, "Unknown Novice"));
            WorldModel world = FameWorld(fp: 1000, fameIndex: 0);
            List<string> log = new List<string>();
            FameErrand errand = new FameErrand(new BotConfig(), book, log.Add);
            Backpack bag = new Backpack();
            DateTime t = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

            errand.Next(world, bag, t);
            errand.PageChanged(null);
            Assert.Equal(BotAction.NPCButton, errand.Next(world, bag, t.AddSeconds(1)).Action);
            errand.NoteServerLine("You do not have enough space.", t.AddSeconds(2));

            Assert.Null(errand.Next(world, bag, t.AddSeconds(8)));
            Assert.False(errand.Active);
            Assert.Contains(log, l => l.Contains("refused") && l.Contains("enough space"));
        }

        // ---- helpers -------------------------------------------------------------------------

        private static QuestRules Rules(Func<int, bool> huntable = null) =>
            new QuestRules { MiniBossMinLevel = 40, BossMinLevel = 50, Huntable = huntable };

        private static WorldModel World(int level) => new WorldModel
        {
            Class = MirClass.Warrior, Level = level, Location = new Point(10, 10)
        };

        private static WorldModel FameWorld(long fp, int fameIndex)
        {
            WorldModel world = new WorldModel { Class = MirClass.Warrior, Level = 45, Location = new Point(256, 133) };
            world.ApplyMapChanged(241);
            CurrencyInfo fame = new CurrencyInfo();
            typeof(CurrencyInfo).GetProperty("Index").SetValue(fame, 4);
            Set(fame, "_Type", CurrencyType.FP);
            world.ApplyCurrencies(new[] { new ClientUserCurrency { Info = fame, Amount = fp } });
            world.ApplyStats(new Stats { [Stat.Fame] = fameIndex });
            world.AddNPC(900, new Point(256, 132), MirDirection.Down);
            return world;
        }

        private static FameBook Fame(params FameInfo[] ranks)
        {
            FameBook book = new FameBook();
            var list = (List<FameInfo>)typeof(FameBook).GetField("_ranks", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(book);
            list.AddRange(ranks.OrderBy(r => r.Order));
            NPCInfo chief = Npc(140, "Chief Yonghyeon");
            MapRegion region = new MapRegion();
            Set(region, "_Map", Map(241, "Frost Village"));
            Set(region, "_PointRegion", new[] { new Point(256, 132) });
            Set(chief, "_Region", region);
            typeof(FameBook).GetField("<Npc>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(book, chief);
            typeof(FameBook).GetField("<ButtonId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(book, 1);
            return book;
        }

        private static FameInfo Rank(int index, int order, int cost, string name)
        {
            FameInfo rank = new FameInfo();
            typeof(FameInfo).GetProperty("Index").SetValue(rank, index);
            Set(rank, "_Order", order);
            Set(rank, "_Cost", cost);
            Set(rank, "_Name", name);
            rank.BuffStats = List<FameInfoStat>();
            rank.ItemRewards = List<FameInfoReward>();
            return rank;
        }

        private static QuestBook Book(params QuestInfo[] quests)
        {
            QuestBook book = new QuestBook();
            var list = (List<QuestInfo>)typeof(QuestBook).GetField("_quests", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(book);
            list.AddRange(quests);
            return book;
        }

        private static void SpawnOn(QuestBook book, MonsterInfo monster, int map)
        {
            var spawns = (Dictionary<int, HashSet<int>>)typeof(QuestBook)
                .GetField("_spawnMaps", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(book);
            spawns[monster.Index] = new HashSet<int> { map };
        }

        private static MapInfo Map(int index, string name)
        {
            MapInfo map = new MapInfo();
            typeof(MapInfo).GetProperty("Index").SetValue(map, index);
            Set(map, "_Description", name);
            return map;
        }

        private static NPCInfo Npc(int index, string name)
        {
            NPCInfo npc = new NPCInfo();
            typeof(NPCInfo).GetProperty("Index").SetValue(npc, index);
            Set(npc, "_NPCName", name);
            return npc;
        }

        private static MonsterInfo Monster(int index, string name, bool boss, int health)
        {
            MonsterInfo info = new MonsterInfo();
            typeof(MonsterInfo).GetProperty("Index").SetValue(info, index);
            Set(info, "_MonsterName", name);
            Set(info, "_IsBoss", boss);
            if (info.Stats == null) Set(info, "Stats", new Stats());
            info.Stats[Stat.Health] = health;
            return info;
        }

        private static void AddRespawn(MonsterInfo monster, MapInfo map, int count, int delay)
        {
            if (monster.Respawns == null) monster.Respawns = List<RespawnInfo>();
            MapRegion region = new MapRegion();
            Set(region, "_Map", map);
            RespawnInfo respawn = new RespawnInfo();
            Set(respawn, "_Region", region);
            Set(respawn, "_Count", count);
            Set(respawn, "_Delay", delay);
            monster.Respawns.Add(respawn);
        }

        private static ItemInfo Item(int index, string name)
        {
            ItemInfo info = new ItemInfo();
            typeof(ItemInfo).GetProperty("Index").SetValue(info, index);
            Set(info, "_ItemName", name);
            return info;
        }

        private static QuestInfo Quest(int index, string name, NPCInfo start, NPCInfo finish,
            MonsterInfo monster, int amount = 10, MapInfo map = null, ItemInfo gatherItem = null)
        {
            QuestInfo quest = new QuestInfo();
            typeof(QuestInfo).GetProperty("Index").SetValue(quest, index);
            Set(quest, "_QuestName", name);
            Set(quest, "_QuestType", QuestType.General);
            Set(quest, "_StartNPC", start);
            Set(quest, "_FinishNPC", finish);
            quest.Requirements = List<QuestRequirement>();
            quest.Rewards = List<QuestReward>();
            quest.Tasks = List<QuestTask>();
            if (monster == null) return quest;

            QuestTask task = new QuestTask();
            typeof(QuestTask).GetProperty("Index").SetValue(task, index * 10);
            Set(task, "_Task", gatherItem != null ? QuestTaskType.GainItem : QuestTaskType.KillMonster);
            Set(task, "_Amount", amount);
            if (gatherItem != null) Set(task, "_ItemParameter", gatherItem);
            task.MonsterDetails = List<QuestTaskMonsterDetails>();
            QuestTaskMonsterDetails detail = new QuestTaskMonsterDetails();
            Set(detail, "_Monster", monster);
            if (map != null) Set(detail, "_Map", map);
            Set(detail, "_Chance", 1);
            Set(detail, "_Amount", 1);
            task.MonsterDetails.Add(detail);
            quest.Tasks.Add(task);
            return quest;
        }

        private static ClientUserQuest UserQuest(int userIndex, QuestInfo quest, long progress) => new ClientUserQuest
        {
            Index = userIndex, QuestIndex = quest.Index, Quest = quest,
            Tasks = quest.Tasks.Select(t => new ClientUserQuestTask { TaskIndex = t.Index, Task = t, Amount = progress }).ToList()
        };

        private static DBBindingList<T> List<T>() where T : DBObject, new()
        {
            DBBindingList<T> list = (DBBindingList<T>)RuntimeHelpers.GetUninitializedObject(typeof(DBBindingList<T>));
            typeof(Collection<T>).GetField("items", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(list, new List<T>());
            return list;
        }

        private static void Set<T>(object target, string field, T value)
        {
            FieldInfo f = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null) throw new InvalidOperationException($"{target.GetType().Name}.{field} not found");
            f.SetValue(target, value);
        }
    }
}
