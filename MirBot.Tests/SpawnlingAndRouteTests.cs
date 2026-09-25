using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using Library;
using Xunit;

namespace MirBot.Tests
{
    /// <summary>
    /// Lesser Wedge Moths are not targets unless they wall us in; a journey leg's route search
    /// gets a bigger budget, says when it ran out rather than calling that "no route", and is
    /// remembered for the rest of the leg.
    /// </summary>
    public sealed class SpawnlingAndRouteTests
    {
        private static readonly Point Me = new Point(10, 10);

        [Fact]
        public void ALesserWedgeMothIsNotATarget()
        {
            WorldModel world = new WorldModel { Location = Me, SelfID = 1 };
            world.AddMonster(2, "Lesser Wedge Moth", 0, new Point(11, 10), MirDirection.Left, false);
            world.AddMonster(3, "Wedge Moth Larva", 44, new Point(12, 10), MirDirection.Left, false);

            Assert.True(world.Find(2).IsSpawnling);
            Assert.False(world.Find(2).IsValidTarget);
            Assert.True(world.Find(3).IsValidTarget);
        }

        [Fact]
        public void BoxedInByMothsTheBotCutsItsWayOut()
        {
            WorldModel world = Surrounded(gap: false, realMonsterBeside: false);
            WorldObject pick = Boxed(world);
            Assert.NotNull(pick);
            Assert.True(pick.IsSpawnling);
        }

        [Fact]
        public void DecideSwingsAtAMothWhenBoxedIn()
        {
            WorldModel world = Surrounded(gap: false, realMonsterBeside: false);
            world.Health = world.MaxHealth = 1000;

            Decision decision = new ScriptedBrain(new BotConfig()).Decide(world, new Backpack(), false);

            Assert.NotNull(decision);
            Assert.Equal(BotAction.Attack, decision.Action);
            Assert.True(world.Find(decision.TargetID).IsSpawnling);
        }

        [Fact]
        public void AGapOrARealMonsterBesideUsMeansNoMothIsChosen()
        {
            Assert.Null(Boxed(Surrounded(gap: true, realMonsterBeside: false)));
            Assert.Null(Boxed(Surrounded(gap: false, realMonsterBeside: true)));
        }

        [Fact]
        public void RunningOutOfBudgetIsNotNoRoute()
        {
            MapGrid open = MapGrid.ForTests(200, 200, (_, _) => true);
            // A wall with one gap at the far end forces a long search.
            MapGrid walled = MapGrid.ForTests(200, 200, (x, y) => x != 100 || y == 199);

            List<Point> tight = PathFinder.Find(walled, new Point(10, 10), new Point(190, 10), 0, null,
                50, out bool exhausted);
            Assert.Null(tight);
            Assert.True(exhausted);

            List<Point> roomy = PathFinder.Find(walled, new Point(10, 10), new Point(190, 10), 0, null,
                PathFinder.JourneyNodeBudget, out exhausted);
            Assert.NotNull(roomy);
            Assert.False(exhausted);

            MapGrid sealedOff = MapGrid.ForTests(50, 50, (x, _) => x != 25);
            Assert.Null(PathFinder.Find(sealedOff, new Point(5, 5), new Point(45, 5), 0, null,
                PathFinder.JourneyNodeBudget, out exhausted));
            Assert.False(exhausted);
            Assert.NotNull(PathFinder.Find(open, new Point(1, 1), new Point(150, 150), 0, null));
        }

        [Fact]
        public void ALegRouteIsFollowedWhileWeStandOnIt()
        {
            ScriptedBrain brain = new ScriptedBrain(new BotConfig());
            WorldModel world = new WorldModel { Location = Me, MapIndex = 7 };
            Point exit = new Point(14, 10);
            var route = new List<Point> { new Point(11, 10), new Point(12, 10), new Point(13, 10), exit };

            Invoke(brain, "RememberLegRoute", world, exit, route);

            world.Location = new Point(12, 10);
            var rest = (List<Point>)Invoke(brain, "CachedLegRoute", world, exit);
            Assert.Equal(new[] { new Point(13, 10), exit }, rest);

            world.Location = new Point(12, 12);                  // pulled off the route
            Assert.Null(Invoke(brain, "CachedLegRoute", world, exit));

            world.Location = new Point(12, 10);
            world.MapIndex = 8;                                   // another map
            Assert.Null(Invoke(brain, "CachedLegRoute", world, exit));
        }

        private static WorldModel Surrounded(bool gap, bool realMonsterBeside)
        {
            WorldModel world = new WorldModel { Location = Me, SelfID = 1, MapIndex = 1 };
            uint id = 10;

            for (int d = 0; d < 8; d++)
            {
                if (gap && d == 3) continue;
                Point cell = WorldModel.Step(Me, (MirDirection)d);
                bool real = realMonsterBeside && d == 0;
                world.AddMonster(id++, real ? "Wedge Moth" : "Lesser Wedge Moth", real ? 8 : 0, cell,
                    MirDirection.Up, false);
            }

            return world;
        }

        private static WorldObject Boxed(WorldModel world) =>
            (WorldObject)Invoke(new ScriptedBrain(new BotConfig()), "BoxedInBySpawnling", world);

        private static object Invoke(object target, string method, params object[] args) =>
            target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(target, args);
    }
}
