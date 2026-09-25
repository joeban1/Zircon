using System;
using System.Drawing;
using System.Reflection;
using Library;
using Library.SystemModels;
using Xunit;

namespace MirBot.Tests
{
    public sealed class AoeGeometryTests
    {
        private static readonly Point Caster = new Point(10, 10);

        [Fact]
        public void RegistryCoversVerifiedPlayerSpellsButNotMonsterThunderStorm()
        {
            Assert.Equal(14, AoeGeometry.Shapes.Count);
            Assert.Equal(AoeShape.Square1, AoeGeometry.Shapes[MagicType.DragonTornado]);
            Assert.Equal(AoeShape.Square1, AoeGeometry.Shapes[MagicType.LightningWave]);
            Assert.True(SpellBook.IsSupported(MagicType.DragonTornado, out _));
            Assert.True(SpellBook.IsSupported(MagicType.LightningWave, out _));
            Assert.False(AoeGeometry.Shapes.ContainsKey(MagicType.MonsterThunderStorm));
            Assert.Equal(AoeShape.Plus, AoeGeometry.Shapes[MagicType.FireWall]);
            Assert.Equal(AoeShape.Meteor, AoeGeometry.Shapes[MagicType.MeteorShower]);
        }

        [Theory]
        [InlineData(AoeShape.Square1, 9)]
        [InlineData(AoeShape.Square3, 49)]
        [InlineData(AoeShape.Plus, 5)]
        [InlineData(AoeShape.Meteor, 49)]
        public void GroundShapesMatchServerCells(AoeShape shape, int count)
        {
            var cells = AoeGeometry.Footprint(shape, Caster, new Point(12, 10),
                MirDirection.Right, 30, 30);
            Assert.Equal(count, cells.Count);
            Assert.Contains(new Point(12, 10), cells);
        }

        [Fact]
        public void RayUsesCardinalAndDiagonalFlanksAndContinuesPastMissingCell()
        {
            var cardinal = AoeGeometry.Footprint(AoeShape.Ray, Caster, Point.Empty,
                MirDirection.Right, 30, 30, p => p != new Point(11, 10));
            Assert.DoesNotContain(new Point(11, 10), cardinal);
            Assert.Contains(new Point(12, 10), cardinal);
            Assert.Contains(new Point(12, 9), cardinal);
            Assert.Contains(new Point(12, 11), cardinal);

            var diagonal = AoeGeometry.Footprint(AoeShape.Ray, Caster, Point.Empty,
                MirDirection.UpRight, 30, 30);
            Assert.Contains(new Point(11, 9), diagonal);
            Assert.Contains(new Point(11, 8), diagonal);
            Assert.Contains(new Point(12, 9), diagonal);
        }

        [Fact]
        public void ConeFansThreeRayDirectionsAndClipsAtMapEdge()
        {
            var cells = AoeGeometry.Footprint(AoeShape.Cone, new Point(1, 1), Point.Empty,
                MirDirection.Up, 4, 4);
            Assert.Contains(new Point(1, 0), cells);
            Assert.Contains(new Point(2, 0), cells);
            Assert.Contains(new Point(0, 0), cells);
            Assert.All(cells, p => Assert.InRange(p.X, 0, 3));
            Assert.All(cells, p => Assert.InRange(p.Y, 0, 3));
        }

        [Fact]
        public void PersistentOverlapBlocksReplacementButNotDistantCast()
        {
            var fire = AoeGeometry.Footprint(AoeShape.Plus, Caster, new Point(10, 10),
                MirDirection.Up, 30, 30);
            var tempest = AoeGeometry.Footprint(AoeShape.Square1, Caster, new Point(10, 10),
                MirDirection.Up, 30, 30);
            var distant = AoeGeometry.Footprint(AoeShape.Plus, Caster, new Point(20, 20),
                MirDirection.Up, 30, 30);
            Assert.True(AoeGeometry.SubstantiallyOverlaps(fire, tempest));
            Assert.False(AoeGeometry.SubstantiallyOverlaps(fire, distant));
        }

        private static WorldModel ClusterWorld(MagicType type, int cost = 10)
        {
            // MagicInfo normally belongs to MirDB. Populate its backing fields for this
            // isolated decision test so DBObject.OnChanged does not require a database.
            var info = new MagicInfo();
            void Set(string field, object value) => typeof(MagicInfo)
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(info, value);
            Set("_Name", type.ToString());
            Set("_Magic", type);
            Set("_NeedLevel1", 1);
            Set("_BaseCost", cost);
            var world = new WorldModel
            {
                Location = Caster, MapIndex = 1, Level = 40,
                Mana = 200, MaxMana = 200
            };
            world.ApplyMagic(new ClientUserMagic
            {
                InfoIndex = 1, Level = 0,
                Info = info
            });
            world.AddMonster(1, "one", 0, new Point(14, 14), MirDirection.Up, false);
            world.AddMonster(2, "two", 0, new Point(14, 16), MirDirection.Up, false);
            world.AddMonster(3, "three", 0, new Point(16, 14), MirDirection.Up, false);
            world.AddMonster(4, "four", 0, new Point(16, 16), MirDirection.Up, false);
            world.AddMonster(5, "pet", 0, new Point(15, 15), MirDirection.Up, false, "Jill");
            world.AddMonster(6, "guard", -1, new Point(15, 15), MirDirection.Up, false);
            return world;
        }

        [Fact]
        public void EmptyCentreWinsAndCountsOnlyValidHostiles()
        {
            var world = ClusterWorld(MagicType.IceStorm);
            var book = new SpellBook(new BotConfig());
            var aim = book.ChooseArea(world, world.Find(1), MapGrid.ForTests(30, 30, (_, _) => true));
            Assert.NotNull(aim);
            Assert.Equal(new Point(15, 15), aim.Value.Point);
            Assert.Equal(4, aim.Value.Covered);
        }

        [Fact]
        public void AreaRespectsManaFloorAndConfigDisable()
        {
            var world = ClusterWorld(MagicType.IceStorm, 100);
            var config = new BotConfig { SpellManaFloorPercent = 60 };
            var book = new SpellBook(config);
            var grid = MapGrid.ForTests(30, 30, (_, _) => true);
            Assert.Null(book.ChooseArea(world, world.Find(1), grid));
            Assert.False(book.CanCastNow(world));
            world.Mana = 200;
            config.SpellManaFloorPercent = 10;
            Assert.NotNull(book.ChooseArea(world, world.Find(1), grid));
            config.AoeEnabled = false;
            Assert.Null(book.ChooseArea(world, world.Find(1), grid));
            Assert.False(book.CanCastNow(world));
        }

        [Fact]
        public void PersistentCastReservesAcrossFireWallAndTempest()
        {
            var world = ClusterWorld(MagicType.FireWall);
            world.AddMonster(1, "one", 0, new Point(14, 15), MirDirection.Up, false);
            world.AddMonster(2, "two", 0, new Point(15, 14), MirDirection.Up, false);
            world.AddMonster(3, "three", 0, new Point(15, 16), MirDirection.Up, false);
            world.AddMonster(4, "four", 0, new Point(16, 15), MirDirection.Up, false);
            var book = new SpellBook(new BotConfig());
            var grid = MapGrid.ForTests(30, 30, (_, _) => true);
            var first = book.ChooseArea(world, world.Find(1), grid);
            Assert.NotNull(first);
            Assert.Equal(new Point(15, 15), first.Value.Point);
            book.ReserveIssued(MagicType.FireWall, world, first.Value.Point,
                first.Value.Direction, grid);
            Assert.Null(book.ChooseArea(world, world.Find(1), grid));

            var tempest = new MagicInfo();
            typeof(MagicInfo).GetField("_Magic", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(tempest, MagicType.Tempest);
            typeof(MagicInfo).GetField("_NeedLevel1", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(tempest, 1);
            world.ApplyMagic(new ClientUserMagic { InfoIndex = 2, Info = tempest });
            Assert.Null(book.ChooseArea(world, world.Find(1), grid));
        }

        [Fact]
        public void IceRainRetainsSingleTargetFallback()
        {
            var world = ClusterWorld(MagicType.IceRain);
            var book = new SpellBook(new BotConfig { AoeMinimumTargets = 10 });
            Assert.Null(book.ChooseArea(world, world.Find(1), MapGrid.ForTests(30, 30, (_, _) => true)));
            Assert.Equal(MagicType.IceRain,
                book.Choose(world, world.Find(1), WorldModel.Distance(Caster, world.Find(1).Location))!
                    .Info.Magic);
        }
    }
}
