using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using Library;
using Xunit;

namespace MirBot.Tests
{
    /// <summary>
    /// Banya Village -> Zuma Temple Lv 1 in miniature: two routes of two map changes each, a long
    /// free walk through Phantom Forest and a short paid Hexa Stone hop via Sabuk Keep.
    /// </summary>
    public sealed class RoutePlanningTests
    {
        private const int Banya = 6, Phantom = 12, Sabuk = 7, Zuma = 20;

        private static WorldGraph Graph()
        {
            WorldGraph graph = new WorldGraph();
            Dictionary<int, List<MapExit>> exits = (Dictionary<int, List<MapExit>>)typeof(WorldGraph)
                .GetField("_exits", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(graph);

            // Walk-on exits listed first, as the real graph does - the old tie-break.
            exits[Banya] = new List<MapExit>
            {
                Walk(Banya, Phantom, "Phantom Forest", new Point(10, 250), new Point(188, 222)),
                new MapExit
                {
                    FromMapIndex = Banya, ToMapIndex = Sabuk, ToMapName = "Sabuk Keep",
                    Cells = new[] { new Point(194, 158) }, Arrival = new Point(55, 220),
                    Teleport = new TeleportRoute { Cost = 3000, ToMapIndex = Sabuk, FromMapIndex = Banya }
                }
            };
            exits[Phantom] = new List<MapExit> { Walk(Phantom, Zuma, "Zuma Temple Lv 1", new Point(42, 47), new Point(20, 20)) };
            exits[Sabuk] = new List<MapExit> { Walk(Sabuk, Zuma, "Zuma Temple Lv 1", new Point(21, 104), new Point(20, 20)) };
            return graph;
        }

        private static MapExit Walk(int from, int to, string name, Point cell, Point arrival) => new MapExit
        {
            FromMapIndex = from, ToMapIndex = to, ToMapName = name, Cells = new[] { cell }, Arrival = arrival
        };

        [Fact]
        public void RichBotTakesTheShortHexaStoneHopOverTheLongFreeWalk()
        {
            List<MapExit> route = Graph().Route(Banya, Zuma, MirClass.Warrior, 42, 2_000_000,
                start: new Point(151, 164));

            Assert.Equal(new[] { "Sabuk Keep", "Zuma Temple Lv 1" }, route.ConvertAll(x => x.ToMapName));
        }

        [Fact]
        public void DeathsOnATransitMapPushTheRouteAroundIt()
        {
            WorldGraph graph = Graph();

            // Make the free walk genuinely shorter: start right beside the Phantom Forest exit.
            List<MapExit> walk = graph.Route(Banya, Zuma, MirClass.Warrior, 42, 2_000_000,
                start: new Point(12, 248));
            Assert.Equal("Phantom Forest", walk[0].ToMapName);

            List<MapExit> safe = graph.Route(Banya, Zuma, MirClass.Warrior, 42, 2_000_000,
                start: new Point(12, 248), dangerTiles: map => map == Phantom ? 450 : 0);
            Assert.Equal("Sabuk Keep", safe[0].ToMapName);
        }

        [Fact]
        public void FareWeighsMoreForAPoorerBot()
        {
            // 3,000 gold is 1.5 tiles to a bot holding two million and 150 to one holding twenty thousand.
            MapExit stone = Graph().ExitsFrom(Banya)[1];

            Assert.Equal(1 + WorldGraph.HopTiles + WorldGraph.TeleportTalkTiles + 1,
                WorldGraph.LegTiles(new Point(194, 157), stone, 2_000_000));
            Assert.Equal(1 + WorldGraph.HopTiles + WorldGraph.TeleportTalkTiles + 150,
                WorldGraph.LegTiles(new Point(194, 157), stone, 20_000));
        }
    }
}
