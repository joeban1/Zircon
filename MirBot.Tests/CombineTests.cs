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
    /// Payton's combinations: recipes read from the page tree, pieces banked (never sold or worn)
    /// until a set is complete, then withdrawn, carried and combined - judged by the bag.
    /// Also Toby's second summon.
    /// </summary>
    [Collection("CombineBook")]
    public sealed class CombineTests : IDisposable
    {
        private readonly ItemInfo _rusty = Item(868, "Rusty Seal Of Overlord", ItemType.Ring);
        private readonly ItemInfo _cracked = Item(869, "Cracked Seal Of Overlord", ItemType.Ring);
        private readonly ItemInfo _worn = Item(870, "Worn Seal Of Overlord", ItemType.Ring);
        private readonly ItemInfo _seal = Item(923, "Seal Of Overlord", ItemType.Ring);
        private readonly MapInfo _numa = Map(12, "Numa Village");
        private readonly NPCInfo _payton;
        private readonly CombineBook _book = new CombineBook();

        public CombineTests()
        {
            _payton = Payton();
            _book.Build("Payton", new[] { _payton });
            Backpack.Combines = _book;
        }

        public void Dispose() => Backpack.Combines = null;

        [Fact]
        public void TheRecipeIsReadFromThePageTree()
        {
            CombineRecipe recipe = Assert.Single(_book.Recipes);
            Assert.Equal(_seal, recipe.Output);
            Assert.Equal(new[] { 868, 869, 870 }, recipe.Inputs.Select(i => i.Item.Index));
            Assert.Equal(2000000, recipe.Gold);
            Assert.Equal(10, recipe.ChanceOneIn);
            Assert.Equal(new[] { 2, 1 }, recipe.ButtonPath);
            Assert.Equal(new[] { 239, 246 }, recipe.PagePath);
        }

        [Fact]
        public void PiecesAreNeverSoldOrWornAndAlwaysBanked()
        {
            ClientUserItem cracked = new ClientUserItem { Info = _cracked, Count = 1, Slot = 3 };
            Backpack bag = new Backpack();
            bag.Reset(new[] { cracked });

            Assert.False(Backpack.Sellable(cracked, null));
            Assert.Empty(bag.PendingEquips(MirClass.Warrior, MirGender.Male));
            Assert.True(bag.ShouldBank(cracked, MirClass.Warrior, MirGender.Male, 40, new Stats(), null, null));
            Assert.True(bag.WorthLootingOnJourney(_cracked, null, MirClass.Warrior, 1500));
        }

        [Fact]
        public void ASetSplitAcrossBagAndBankIsReadyAndItsPiecesComeOut()
        {
            Backpack bag = new Backpack();
            bag.Reset(new[] { new ClientUserItem { Info = _rusty, Count = 1, Slot = 3 } });
            bag.ResetStorage(new[]
            {
                new ClientUserItem { Info = _cracked, Count = 1, Slot = 1 },
                new ClientUserItem { Info = _worn, Count = 1, Slot = 2 }
            });

            Assert.Null(_book.Ready(bag, 1000000, 50000, bagOnly: false));      // gold short
            CombineRecipe ready = _book.Ready(bag, 5000000, 50000, bagOnly: false);
            Assert.NotNull(ready);
            Assert.Null(_book.Ready(bag, 5000000, 50000, bagOnly: true));       // not all in the bag

            // Not ready: the bank keeps them. Ready: out they come, and they are not re-banked.
            Assert.Empty(bag.StorageReclaims(MirClass.Warrior, MirGender.Male, 40, new Stats(), null, null));
            bag.CombineReady = ready;
            var reclaims = bag.StorageReclaims(MirClass.Warrior, MirGender.Male, 40, new Stats(), null, null);
            Assert.Equal(new[] { 869, 870 }, reclaims.Select(r => r.Item.Info.Index).OrderBy(x => x));
            Assert.False(bag.ShouldBank(bag.InSlot(3), MirClass.Warrior, MirGender.Male, 40, new Stats(), null, null));
        }

        [Fact]
        public void TheErrandPressesThePathOnceAndJudgesSuccessByTheBag()
        {
            (WorldModel world, Backpack bag, CombineErrand errand, List<bool> results) = Visit();

            Decision call = errand.Next(world, bag);
            Assert.Equal(BotAction.NPCCall, call.Action);

            errand.PageChanged(_payton.EntryPage);
            Decision first = errand.Next(world, bag);
            Assert.Equal(BotAction.NPCButton, first.Action);
            Assert.Equal(2, first.ButtonID);

            errand.PageChanged(Page(239));
            Decision last = errand.Next(world, bag);
            Assert.Equal(BotAction.NPCButton, last.Action);
            Assert.Equal(1, last.ButtonID);

            // The server takes the pieces, then gives the Seal.
            bag.Reset(new[] { new ClientUserItem { Info = _seal, Count = 1, Slot = 3 } });
            Assert.Null(errand.Next(world, bag));
            Assert.Equal(new[] { true }, results);
            Assert.False(errand.Active);
        }

        [Fact]
        public void PiecesGoneAndNoSealIsAFailureAndIsNeverPressedAgain()
        {
            (WorldModel world, Backpack bag, CombineErrand errand, List<bool> results) = Visit();
            DateTime t = DateTime.UtcNow;

            errand.Next(world, bag, t);
            errand.PageChanged(_payton.EntryPage);
            errand.Next(world, bag, t);
            errand.PageChanged(Page(239));
            errand.Next(world, bag, t);

            bag.Reset(Array.Empty<ClientUserItem>());
            Decision waiting = errand.Next(world, bag, t.AddSeconds(1));
            Assert.Equal(BotAction.Idle, waiting.Action);                       // committed, not pressing

            Assert.Null(errand.Next(world, bag, t.AddSeconds(7)));
            Assert.Equal(new[] { false }, results);
        }

        [Fact]
        public void NothingChangingIsARefusalWithACooldown()
        {
            (WorldModel world, Backpack bag, CombineErrand errand, List<bool> results) = Visit();
            DateTime t = DateTime.UtcNow;

            errand.Next(world, bag, t);
            errand.PageChanged(_payton.EntryPage);
            errand.Next(world, bag, t);
            errand.PageChanged(Page(239));
            errand.Next(world, bag, t);

            Assert.Null(errand.Next(world, bag, t.AddSeconds(6)));
            Assert.Empty(results);
            Assert.False(errand.HasWork(world, bag));                          // cooling down
        }

        // ---- Toby's second summon ---------------------------------------------------------------

        [Theory]
        [InlineData(false, 50, MagicType.SummonSkeleton)]    // Toby: Jin + Skeleton -> Skeleton joins
        [InlineData(true, 50, MagicType.SummonShinsu)]       // Jill: Shinsu before the Skeleton
        [InlineData(true, 4, MagicType.None)]                // Shinsu short of amulets holds the slot
        public void TheSecondPetIsTheBestOneNotOut(bool knowsShinsu, int amulets, MagicType expected)
        {
            MonsterIndex previous = BotConnection.Monsters;
            try
            {
                MonsterInfo jin = new MonsterInfo();
                typeof(MonsterInfo).GetProperty("Index").SetValue(jin, 501);
                Set(jin, "_Flag", MonsterFlag.JinSkeleton);
                MonsterIndex index = new MonsterIndex();
                ((Dictionary<int, MonsterInfo>)typeof(MonsterIndex)
                    .GetField("_byIndex", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(index))[501] = jin;
                BotConnection.Monsters = index;

                WorldModel world = new WorldModel
                {
                    Name = "Toby", Class = MirClass.Taoist, Level = 41, Mana = 500, MaxMana = 500,
                    Health = 900, MaxHealth = 900, Location = new Point(10, 10), SelfID = 1
                };
                world.ApplyMagic(Summon(1, MagicType.SummonJinSkeleton));
                world.ApplyMagic(Summon(2, MagicType.SummonSkeleton));
                if (knowsShinsu) world.ApplyMagic(Summon(3, MagicType.SummonShinsu));
                world.AddMonster(40, "Jin Skeleton", 0, new Point(11, 10), MirDirection.Up, false, "Toby", 501);

                ItemInfo amulet = Item(700, "Amulet", ItemType.Amulet);
                Set(amulet, "_StackSize", 1000);
                Backpack bag = new Backpack();
                bag.Reset(new[]
                {
                    new ClientUserItem { Info = amulet, Count = amulets,
                        Slot = Globals.EquipmentOffSet + (int)EquipmentSlot.Amulet }
                });

                ScriptedBrain brain = new ScriptedBrain(new BotConfig());
                Decision summon = (Decision)typeof(ScriptedBrain)
                    .GetMethod("KeepSummon", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(brain, new object[] { world, bag });

                Assert.Equal(expected, summon?.Magic ?? MagicType.None);
            }
            finally
            {
                BotConnection.Monsters = previous;
            }
        }

        private static ClientUserMagic Summon(int index, MagicType type)
        {
            MagicInfo info = new MagicInfo();
            typeof(MagicInfo).GetProperty("Index").SetValue(info, index);
            Set(info, "_Name", type.ToString());
            Set(info, "_Magic", type);
            Set(info, "_NeedLevel1", 1);
            Set(info, "_BaseCost", 10);
            return new ClientUserMagic { InfoIndex = index, Info = info, Level = 3 };
        }

        // ---- helpers ---------------------------------------------------------------------------

        private (WorldModel, Backpack, CombineErrand, List<bool>) Visit()
        {
            WorldModel world = new WorldModel
            {
                MapIndex = _numa.Index, Location = new Point(145, 148), Gold = 5000000, SelfID = 1
            };
            world.AddNPC(99, new Point(145, 147), MirDirection.Down);

            Backpack bag = new Backpack();
            bag.Reset(new[]
            {
                new ClientUserItem { Info = _rusty, Count = 1, Slot = 3 },
                new ClientUserItem { Info = _cracked, Count = 1, Slot = 4 },
                new ClientUserItem { Info = _worn, Count = 1, Slot = 5 }
            });

            List<bool> results = new List<bool>();
            CombineErrand errand = new CombineErrand(new BotConfig(), _book, null)
            {
                OnCombined = (_, ok) => results.Add(ok)
            };
            Assert.True(errand.HasWork(world, bag));
            return (world, bag, errand, results);
        }

        private readonly Dictionary<int, NPCPage> _pages = new Dictionary<int, NPCPage>();

        private NPCPage Page(int index)
        {
            if (_pages.TryGetValue(index, out NPCPage page)) return page;

            page = new NPCPage();
            typeof(NPCPage).GetProperty("Index").SetValue(page, index);
            Set(page, "_Description", $"page {index}");
            page.Buttons = List<NPCButton>();
            page.Actions = List<NPCAction>();
            page.Checks = List<NPCCheck>();
            _pages[index] = page;
            return page;
        }

        private NPCInfo Payton()
        {
            NPCPage main = Page(198), lair = Page(239), request = Page(246), end = Page(247);

            Button(main, 2, lair);
            Button(lair, 1, request);

            Check(request, NPCCheckType.HasItem, 1, 0, _rusty);
            Action(request, NPCActionType.TakeItem, 1, 0, _rusty);
            Action(request, NPCActionType.TakeItem, 1, 0, _cracked);
            Action(request, NPCActionType.TakeItem, 1, 0, _worn);
            Action(request, NPCActionType.TakeGold, 2000000, 0, null);
            Set(request, "_SuccessPage", end);

            Check(end, NPCCheckType.Random, 10, 0, null);
            Action(end, NPCActionType.GiveItemExperience, 1, 455, _seal);

            MapRegion region = new MapRegion();
            Set(region, "_Map", _numa);
            Set(region, "_PointRegion", new[] { new Point(145, 147) });

            NPCInfo npc = new NPCInfo();
            typeof(NPCInfo).GetProperty("Index").SetValue(npc, 136);
            Set(npc, "_NPCName", "Payton");
            Set(npc, "_Region", region);
            Set(npc, "_EntryPage", main);
            return npc;
        }

        private static void Button(NPCPage page, int id, NPCPage to)
        {
            NPCButton button = new NPCButton();
            Set(button, "_ButtonID", id);
            Set(button, "_DestinationPage", to);
            page.Buttons.Add(button);
        }

        private static void Action(NPCPage page, NPCActionType type, int p1, int p2, ItemInfo item)
        {
            NPCAction action = new NPCAction();
            Set(action, "_ActionType", type);
            Set(action, "_IntParameter1", p1);
            Set(action, "_IntParameter2", p2);
            Set(action, "_ItemParameter1", item);
            page.Actions.Add(action);
        }

        private static void Check(NPCPage page, NPCCheckType type, int p1, int p2, ItemInfo item)
        {
            NPCCheck check = new NPCCheck();
            Set(check, "_CheckType", type);
            Set(check, "_Operator", Operator.Equal);
            Set(check, "_IntParameter1", p1);
            Set(check, "_IntParameter2", p2);
            Set(check, "_ItemParameter1", item);
            page.Checks.Add(check);
        }

        private static ItemInfo Item(int index, string name, ItemType type)
        {
            ItemInfo info = new ItemInfo();
            typeof(ItemInfo).GetProperty("Index").SetValue(info, index);
            Set(info, "_ItemName", name);
            Set(info, "_ItemType", type);
            Set(info, "_RequiredClass", RequiredClass.All);
            Set(info, "_RequiredGender", RequiredGender.None);
            Set(info, "_RequiredType", RequiredType.Level);
            Set(info, "_RequiredAmount", 20);
            Set(info, "_StackSize", 1);
            Set(info, "_CanSell", true);
            info.Stats = new Stats { [Stat.MinDC] = 2, [Stat.MaxDC] = 2 };
            return info;
        }

        private static MapInfo Map(int index, string name)
        {
            MapInfo map = new MapInfo();
            typeof(MapInfo).GetProperty("Index").SetValue(map, index);
            Set(map, "_Description", name);
            return map;
        }

        private static DBBindingList<T> List<T>() where T : DBObject, new()
        {
            DBBindingList<T> list = (DBBindingList<T>)RuntimeHelpers.GetUninitializedObject(typeof(DBBindingList<T>));
            typeof(Collection<T>).GetField("items", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(list, new List<T>());
            return list;
        }

        private static void Set<T>(object target, string field, T value) =>
            target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(target, value);
    }
}
