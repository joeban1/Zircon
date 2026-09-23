using System;
using System.Collections.Generic;
using System.Drawing;
using Xunit;

namespace MirBot.Tests
{
    public sealed class ExplorationCoverageTests
    {
        private static readonly DateTime Now =
            new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

        private static MapGrid OpenGrid(int width, int height) =>
            MapGrid.ForTests(width, height, (_, _) => true);

        [Fact]
        public void UnseenSectorWinsBeforeVisitedSectors()
        {
            MapGrid grid = OpenGrid(48, 16);
            ExplorationCoverage coverage = new ExplorationCoverage();

            coverage.Observe(1, new Point(4, 8), Now.AddMinutes(-2));
            coverage.Observe(1, new Point(20, 8), Now.AddMinutes(-1));

            ExplorationChoice choice = coverage.Choose(1, grid, new Point(4, 8), 25,
                true, null, new Random(1), Now);

            Assert.NotNull(choice);
            Assert.Equal(2, choice.SectorX);
            Assert.Null(choice.LastVisitedUtc);
        }

        [Fact]
        public void FullyCoveredMapReturnsLeastRecentlyVisitedSector()
        {
            MapGrid grid = OpenGrid(48, 16);
            ExplorationCoverage coverage = new ExplorationCoverage();

            coverage.Observe(1, new Point(4, 8), Now.AddMinutes(-2));
            coverage.Observe(1, new Point(20, 8), Now.AddMinutes(-8));
            coverage.Observe(1, new Point(36, 8), Now.AddMinutes(-1));

            ExplorationChoice choice = coverage.Choose(1, grid, new Point(4, 8), 25,
                true, null, new Random(2), Now);

            Assert.NotNull(choice);
            Assert.Equal(1, choice.SectorX);
            Assert.Equal(Now.AddMinutes(-8), choice.LastVisitedUtc);
        }

        [Fact]
        public void LocalChoiceStaysInsideRadiusWhileMapWideReachesUnseenFarSector()
        {
            MapGrid grid = OpenGrid(64, 16);
            ExplorationCoverage coverage = new ExplorationCoverage();
            Point origin = new Point(4, 8);

            coverage.Observe(1, origin, Now.AddMinutes(-1));
            coverage.Observe(1, new Point(20, 8), Now.AddMinutes(-2));
            coverage.Observe(1, new Point(36, 8), Now.AddMinutes(-1));

            ExplorationChoice local = coverage.Choose(1, grid, origin, 20,
                false, null, new Random(3), Now);
            ExplorationChoice global = coverage.Choose(1, grid, origin, 20,
                true, null, new Random(3), Now);

            Assert.NotNull(local);
            Assert.True(Distance(origin, local.Target) <= 20);
            Assert.False(local.MapWide);
            Assert.NotNull(global);
            Assert.Equal(3, global.SectorX);
            Assert.True(global.MapWide);
        }

        [Fact]
        public void ExcludedUnseenSectorFallsBackToAnotherUsableSector()
        {
            MapGrid grid = OpenGrid(32, 16);
            ExplorationCoverage coverage = new ExplorationCoverage();
            HashSet<Point> excluded = new HashSet<Point>();

            for (int x = 16; x < 32; x++)
                for (int y = 0; y < 16; y++)
                    excluded.Add(new Point(x, y));

            coverage.Observe(1, new Point(4, 8), Now.AddMinutes(-1));

            ExplorationChoice choice = coverage.Choose(1, grid, new Point(4, 8), 25,
                true, excluded, new Random(4), Now);

            Assert.NotNull(choice);
            Assert.Equal(0, choice.SectorX);
            Assert.DoesNotContain(choice.Target, excluded);
        }

        [Fact]
        public void BarredSectorIsNotImmediatelyReselected()
        {
            MapGrid grid = OpenGrid(48, 16);
            ExplorationCoverage coverage = new ExplorationCoverage();
            Point origin = new Point(4, 8);

            coverage.Observe(1, origin, Now);
            ExplorationChoice first = coverage.Choose(1, grid, origin, 25,
                true, null, new Random(5), Now);

            Assert.NotNull(first);
            coverage.Bar(1, first.Target, Now.AddMinutes(10));

            ExplorationChoice second = coverage.Choose(1, grid, origin, 25,
                true, null, new Random(5), Now.AddSeconds(1));

            Assert.NotNull(second);
            Assert.NotEqual(first.SectorX, second.SectorX);
        }

        [Fact]
        public void CoverageIsKeptSeparatelyPerMap()
        {
            MapGrid grid = OpenGrid(32, 16);
            ExplorationCoverage coverage = new ExplorationCoverage();

            coverage.Observe(1, new Point(4, 8), Now);
            coverage.Observe(2, new Point(20, 8), Now);

            ExplorationSnapshot first = coverage.Snapshot(1, grid);
            ExplorationSnapshot second = coverage.Snapshot(2, grid);

            Assert.Equal(1, first.VisitedSectors);
            Assert.Equal(1, second.VisitedSectors);
            Assert.Equal(2, first.TotalSectors);
            Assert.Equal(2, second.TotalSectors);
        }

        [Fact]
        public void LongTargetDoesNotExpireOnWallClock()
        {
            DateTime until = Now.AddSeconds(20);

            Assert.True(ScriptedBrain.ShouldExpireRoamTarget(
                Now.AddMinutes(2), until, false));
            Assert.False(ScriptedBrain.ShouldExpireRoamTarget(
                Now.AddMinutes(2), until, true));
        }

        [Fact]
        public void MissingGridProducesNoChoiceOrCoverage()
        {
            ExplorationCoverage coverage = new ExplorationCoverage();

            Assert.Null(coverage.Choose(1, null, new Point(4, 8), 20,
                false, null, new Random(1), Now));

            ExplorationSnapshot snapshot = coverage.Snapshot(1, null);
            Assert.Equal(0, snapshot.VisitedSectors);
            Assert.Equal(0, snapshot.TotalSectors);
        }

        private static int Distance(Point a, Point b) =>
            Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }
}
