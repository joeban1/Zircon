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
    /// Quests: acceptance rules, the quest log's transitions, reward bag space, tracker-object
    /// visibility and buff timing. DB objects are built by reflection (no Session exists in tests).
    /// </summary>
    public sealed class QuestTests
    {
        // ---- acceptance ----------------------------------------------------------------------

        [Theory]
        [InlineData(40, true)]
        [InlineData(41, false)]
        public void TheChickenDailyStopsAtForty(int level, bool expected)
        {
            QuestInfo daily = Quest(1, "Do your dailies", QuestType.Daily, Monster(10, "Chicken", false));
            AddRequirement(daily, QuestRequirementType.MaxLevel, 40);

            Assert.Equal(expected, new QuestBook().CanAccept(daily, World(level), 45));
        }

        [Theory]
        [InlineData(44, false)]
        [InlineData(45, true)]
        public void ABossQuestWaitsForTheBotRuleNotJustTheServers(int level, bool expected)
        {
            QuestInfo boss = Quest(2, "Level 40 - Well done", QuestType.General,
                Monster(11, "Crazed Warrior", true), amount: 1);
            AddRequirement(boss, QuestRequirementType.MinLevel, 40);

            Assert.Equal(expected, new QuestBook().CanAccept(boss, World(level), 45));
        }

        [Fact]
        public void AQuestAlreadyInTheLogOrForAnotherClassIsNotAcceptable()
        {
            QuestInfo daily = Quest(3, "Do your dailies 2", QuestType.Daily, Monster(12, "Pig", false));
            WorldModel world = World(45);
            Assert.True(new QuestBook().CanAccept(daily, world, 45));

            world.ResetQuests(new[] { UserQuest(1, daily, 0) });
            Assert.False(new QuestBook().CanAccept(daily, world, 45));

            QuestInfo taoist = Quest(4, "Taoist only", QuestType.General, Monster(13, "Hen", false));
            AddRequirement(taoist, QuestRequirementType.Class, 0, RequiredClass.Taoist);
            Assert.False(new QuestBook().CanAccept(taoist, World(45), 45));
        }

        [Fact]
        public void HaveNotCompletedBlocksARepeatOnlyOnceCompleted()
        {
            QuestInfo once = Quest(5, "Getting Started", QuestType.General, Monster(14, "Forest Yeti", false));
            AddRequirement(once, QuestRequirementType.HaveNotCompleted, 0, questParameter: once);
            QuestInfo other = Quest(6, "Other", QuestType.General, Monster(15, "Deer", false));
            AddRequirement(other, QuestRequirementType.HaveNotCompleted, 0, questParameter: once);

            WorldModel world = World(20);
            Assert.True(new QuestBook().CanAccept(other, world, 45));

            ClientUserQuest done = UserQuest(1, once, 5);
            done.Completed = true;
            world.ResetQuests(new[] { done });
            Assert.False(new QuestBook().CanAccept(other, world, 45));
        }

        // ---- the quest log -------------------------------------------------------------------

        [Fact]
        public void TheLogSeparatesAnAcceptFromKillProgressFromAHandIn()
        {
            QuestInfo daily = Quest(7, "Do your dailies 2", QuestType.Daily, Monster(16, "Pig", false));
            WorldModel world = World(45);
            world.ResetQuests(null);

            QuestTransition accept = world.ApplyQuestChanged(UserQuest(9, daily, 0));
            Assert.True(accept.Accepted);
            Assert.False(accept.Completed);

            QuestTransition kill = world.ApplyQuestChanged(UserQuest(9, daily, 3));
            Assert.False(kill.Accepted);
            Assert.True(kill.Progressed);
            Assert.Equal("3/10 Pig", QuestBook.ProgressText(world.FindQuest(7)));

            ClientUserQuest ready = UserQuest(9, daily, 10);
            Assert.True(QuestBook.AllTasksDone(ready));
            ready.Completed = true;
            QuestTransition handIn = world.ApplyQuestChanged(ready);
            Assert.True(handIn.Completed);
            Assert.False(handIn.Accepted);

            // The daily reset names the USER quest index.
            Assert.NotNull(world.RemoveQuest(9));
            Assert.Null(world.FindQuest(7));
        }

        [Fact]
        public void KillTargetsOnlyListWhatIsStillNeededAndWhereItSpawns()
        {
            MonsterInfo pig = Monster(17, "Pig", false);
            QuestInfo daily = Quest(8, "Do your dailies 2", QuestType.Daily, pig);
            QuestBook book = new QuestBook();
            ((List<QuestInfo>)Field(book, "_quests")).Add(daily);
            ((Dictionary<int, HashSet<int>>)Field(book, "_spawnMaps"))[17] = new HashSet<int> { 1, 6 };

            WorldModel world = World(45);
            world.ResetQuests(new[] { UserQuest(1, daily, 4) });

            QuestKillTarget target = Assert.Single(book.KillTargets(world));
            Assert.Equal(17, target.MonsterIndex);
            Assert.Contains(1, target.Maps);
            Assert.False(target.IsBoss);

            world.ResetQuests(new[] { UserQuest(1, daily, 10) });
            Assert.Empty(book.KillTargets(world));
            Assert.Single(book.ReadyToHandIn(world));
        }

        // ---- reward bag space ----------------------------------------------------------------

        [Fact]
        public void RewardsNeedASlotEachUnlessTheyStack()
        {
            Backpack bag = FullBag(freeSlots: 1);
            ItemInfo buff = Item(100, stack: 1);
            ItemInfo scroll = Item(101, stack: 1);

            Assert.True(bag.HasRoomForRewards(new[] { (buff, 1L, false, false) }));
            Assert.False(bag.HasRoomForRewards(new[] { (buff, 1L, false, false), (scroll, 2L, false, false) }));

            ItemInfo stacking = Item(102, stack: 10);
            Assert.True(bag.HasRoomForRewards(new[] { (stacking, 5L, false, false) }));
        }

        [Fact]
        public void AnExperienceRewardNeedsNoSlot()
        {
            Backpack bag = FullBag(freeSlots: 0);
            ItemInfo exp = Item(103, stack: 1);
            Set(exp, "_ItemEffect", ItemEffect.Experience);

            Assert.True(bag.HasRoomForRewards(new[] { (exp, 500L, false, false) }));
            Assert.False(bag.HasRoomForRewards(new[] { (Item(104, 1), 1L, false, false) }));
        }

        // ---- tracked-boss visibility -----------------------------------------------------------

        [Fact]
        public void ABossStaysUntilBothChannelsDropIt()
        {
            WorldModel world = World(45);
            world.AddMonster(50, "Skeleton Lord", 0, new Point(10, 10), MirDirection.Up, false, monsterIndex: 18);
            world.MarkSeen(50, asData: false);
            world.MarkSeen(50, asData: true);

            Assert.False(world.ApplyRemove(50));               // still shown as data
            Assert.Contains(world.Objects, o => o.ObjectID == 50);

            Assert.NotNull(world.ApplyDataRemove(50));          // now gone for good
            Assert.DoesNotContain(world.Objects, o => o.ObjectID == 50);
        }

        [Fact]
        public void ADataOnlyBossDisappearsOnDataObjectRemove()
        {
            WorldModel world = World(45);
            world.AddMonster(51, "Ghoul Champion", 0, new Point(90, 90), MirDirection.Up, false, monsterIndex: 19);
            world.MarkSeen(51, asData: true);

            WorldObject gone = world.ApplyDataRemove(51);
            Assert.NotNull(gone);
            Assert.Equal(new Point(90, 90), gone.Location);
        }

        // ---- buff timing ---------------------------------------------------------------------

        [Fact]
        public void APermanentBuffHasNoTimeAndAPausedOneDoesNotCountDown()
        {
            WorldModel world = World(45);
            ClientBuffInfo permanent = new ClientBuffInfo { Index = 1, Type = BuffType.ItemBuff, ItemIndex = 200,
                RemainingTime = TimeSpan.MaxValue };
            ClientBuffInfo paused = new ClientBuffInfo { Index = 2, Type = BuffType.ItemBuff, ItemIndex = 201,
                RemainingTime = TimeSpan.FromMinutes(90), Pause = true };
            world.AddBuff(permanent);
            world.AddBuff(paused);

            Assert.Null(world.BuffRemaining(permanent));
            Assert.Equal(TimeSpan.FromMinutes(90), world.BuffRemaining(paused));
            Assert.True(world.HasItemBuff(201));
            Assert.False(world.HasItemBuff(202));
        }

        // ---- helpers -------------------------------------------------------------------------

        private static WorldModel World(int level) => new WorldModel
        {
            Class = MirClass.Warrior, Level = level, Location = new Point(10, 10)
        };

        private static MonsterInfo Monster(int index, string name, bool boss)
        {
            MonsterInfo info = new MonsterInfo();
            typeof(MonsterInfo).GetProperty("Index").SetValue(info, index);
            Set(info, "_MonsterName", name);
            Set(info, "_IsBoss", boss);
            return info;
        }

        private static ItemInfo Item(int index, int stack)
        {
            ItemInfo info = new ItemInfo();
            typeof(ItemInfo).GetProperty("Index").SetValue(info, index);
            Set(info, "_ItemType", ItemType.Consumable);
            Set(info, "_StackSize", stack);
            return info;
        }

        private static Backpack FullBag(int freeSlots)
        {
            Backpack bag = new Backpack();
            ItemInfo filler = Item(999, 1);
            for (int slot = 0; slot < Globals.InventorySize - freeSlots; slot++)
                bag.Set(new ClientUserItem { Info = filler, Count = 1, Slot = slot });
            return bag;
        }

        private static QuestInfo Quest(int index, string name, QuestType type, MonsterInfo monster,
            int amount = 10)
        {
            QuestInfo quest = new QuestInfo();
            typeof(QuestInfo).GetProperty("Index").SetValue(quest, index);
            Set(quest, "_QuestName", name);
            Set(quest, "_QuestType", type);
            quest.Requirements = List<QuestRequirement>();
            quest.Rewards = List<QuestReward>();
            quest.Tasks = List<QuestTask>();

            QuestTask task = new QuestTask();
            typeof(QuestTask).GetProperty("Index").SetValue(task, index * 10);
            Set(task, "_Task", QuestTaskType.KillMonster);
            Set(task, "_Amount", amount);
            task.MonsterDetails = List<QuestTaskMonsterDetails>();
            QuestTaskMonsterDetails detail = new QuestTaskMonsterDetails();
            Set(detail, "_Monster", monster);
            Set(detail, "_Chance", 1);
            Set(detail, "_Amount", 1);
            task.MonsterDetails.Add(detail);
            quest.Tasks.Add(task);
            return quest;
        }

        private static void AddRequirement(QuestInfo quest, QuestRequirementType type, int value,
            RequiredClass cls = RequiredClass.None, QuestInfo questParameter = null)
        {
            QuestRequirement req = new QuestRequirement();
            Set(req, "_Requirement", type);
            Set(req, "_IntParameter1", value);
            Set(req, "_Class", cls);
            Set(req, "_QuestParameter", questParameter);
            quest.Requirements.Add(req);
        }

        private static ClientUserQuest UserQuest(int userIndex, QuestInfo quest, long progress)
        {
            QuestTask task = quest.Tasks[0];
            return new ClientUserQuest
            {
                Index = userIndex, QuestIndex = quest.Index, Quest = quest,
                Tasks = new List<ClientUserQuestTask>
                {
                    new ClientUserQuestTask { TaskIndex = task.Index, Task = task, Amount = progress }
                }
            };
        }

        /// <summary>A DBBindingList without a Session: skip its constructor, give it a backing list.</summary>
        private static DBBindingList<T> List<T>() where T : DBObject, new()
        {
            DBBindingList<T> list = (DBBindingList<T>)RuntimeHelpers.GetUninitializedObject(typeof(DBBindingList<T>));
            typeof(Collection<T>).GetField("items", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(list, new List<T>());
            return list;
        }

        private static object Field(object target, string name) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

        private static void Set<T>(object target, string field, T value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(target, value);
    }
}
