using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace MirBot
{
    public enum JourneyPhase { Idle, Walking, Crossing, Talking, Arrived, Failed }

    /// <summary>
    /// Walking to another map, one leg at a time.
    ///
    /// The route is a list of exits from WorldGraph. For each one the bot walks to the nearest of
    /// that exit's trigger cells - ordinary A* on the current map, the same steering everything else
    /// uses - and stepping onto it hands the rest to the server, which drops the character somewhere
    /// inside the destination region. Because that landing point is chosen at random by the server,
    /// each leg has to be re-planned on arrival rather than computed up front.
    ///
    /// Progress is measured, not assumed. A leg that stops getting closer is abandoned, and an exit
    /// that does not change the map after enough attempts is written off for the rest of the
    /// journey - the alternative is a bot pressed against a gate forever.
    /// </summary>
    public sealed class Journey
    {
        private static readonly TimeSpan NoProgressLimit = TimeSpan.FromSeconds(30);
        private const int MaxCrossAttempts = 12;

        private readonly WorldGraph _graph;

        private List<MapExit> _route;
        private int _leg;
        private Point _aim = Point.Empty;
        private int _bestDistance = int.MaxValue;
        private DateTime _lastProgress = DateTime.MinValue;
        private int _crossAttempts;
        private int _legStartMap = -1;

        // A teleport leg is a conversation rather than a step, so it needs its own little state:
        // the buttons still to press, and a deadline, because an NPC that never answers would
        // otherwise hold the journey open forever.
        private readonly Queue<int> _buttonPath = new Queue<int>();
        private DateTime _talkDeadline = DateTime.MinValue;
        private bool _called;

        /// <summary>How close we have to be to talk, and how much gold to keep back. Set by the
        /// owner from config; the journey itself has no opinion about either.</summary>
        public int TalkRange = 5;
        public long Gold;
        public long GoldFloor;

        public JourneyPhase Phase { get; private set; } = JourneyPhase.Idle;
        public string Status { get; private set; } = "";
        public int DestinationMapIndex { get; private set; } = -1;
        public string DestinationName { get; private set; } = "";

        public bool Active => Phase == JourneyPhase.Walking || Phase == JourneyPhase.Crossing ||
                              Phase == JourneyPhase.Talking;

        public Journey(WorldGraph graph)
        {
            _graph = graph;
        }

        /// <summary>Plan a journey, or fail immediately when no route exists.</summary>
        public bool Begin(WorldModel world, int destinationMapIndex, string destinationName)
        {
            Reset();

            if (_graph == null)
            {
                Fail("no world graph");
                return false;
            }

            if (world.MapIndex == destinationMapIndex)
            {
                Phase = JourneyPhase.Arrived;
                Status = "already there";
                return true;
            }

            Gold = world.Gold;

            List<MapExit> route = _graph.Route(world.MapIndex, destinationMapIndex, world.Class,
                world.Level, Gold, GoldFloor);

            if (route == null || route.Count == 0)
            {
                Fail($"no route from {world.MapName} to {destinationName}");
                return false;
            }

            _route = route;
            _leg = 0;
            DestinationMapIndex = destinationMapIndex;
            DestinationName = destinationName ?? "";

            StartLeg(world);
            return true;
        }

        public void Abort(string why)
        {
            Reset();
            Phase = JourneyPhase.Failed;
            Status = why;
        }

        private void Fail(string why)
        {
            Phase = JourneyPhase.Failed;
            Status = why;
        }

        private void Reset()
        {
            _route = null;
            _leg = 0;
            _aim = Point.Empty;
            _bestDistance = int.MaxValue;
            _lastProgress = DateTime.MinValue;
            _crossAttempts = 0;
            _legStartMap = -1;
            _buttonPath.Clear();
            _talkDeadline = DateTime.MinValue;
            _called = false;
            Phase = JourneyPhase.Idle;
            Status = "";
            DestinationMapIndex = -1;
        }

        private void StartLeg(WorldModel world)
        {
            if (_route == null || _leg >= _route.Count)
            {
                Phase = JourneyPhase.Arrived;
                Status = "arrived";
                return;
            }

            MapExit exit = _route[_leg];

            _aim = Nearest(exit.Cells, world.Location);
            _bestDistance = int.MaxValue;
            _lastProgress = DateTime.UtcNow;
            _crossAttempts = 0;
            _legStartMap = world.MapIndex;
            _buttonPath.Clear();
            _talkDeadline = DateTime.MinValue;
            _called = false;

            Phase = JourneyPhase.Walking;
            Status = $"leg {_leg + 1}/{_route.Count}: {world.MapName} to {exit.ToMapName}" +
                     (exit.IsTeleport
                         ? $" via {exit.Teleport.NPC?.NPCName}" +
                           (exit.Cost > 0 ? $" for {exit.Cost:N0} gold" : " (free)")
                         : "");
        }

        private static Point Nearest(IReadOnlyList<Point> cells, Point from)
        {
            Point best = Point.Empty;
            int bestDistance = int.MaxValue;

            foreach (Point cell in cells)
            {
                int distance = Math.Max(Math.Abs(cell.X - from.X), Math.Abs(cell.Y - from.Y));

                if (distance >= bestDistance) continue;

                bestDistance = distance;
                best = cell;
            }

            return best;
        }

        /// <summary>
        /// The next step of the journey, or null when there is nothing to do this tick. The caller
        /// steers: this only ever says where to head.
        /// </summary>
        public Decision Next(WorldModel world)
        {
            if (_route == null || Phase == JourneyPhase.Arrived || Phase == JourneyPhase.Failed)
                return null;

            // The server moved us. Either we crossed, or we were teleported by something else.
            if (world.MapIndex != _legStartMap)
            {
                MapExit crossed = _route[_leg];

                if (world.MapIndex == crossed.ToMapIndex)
                {
                    _leg++;

                    if (_leg >= _route.Count)
                    {
                        Phase = JourneyPhase.Arrived;
                        Status = $"arrived at {DestinationName}";
                        return null;
                    }

                    StartLeg(world);
                    return Walk(world);
                }

                // Somewhere unexpected - a scroll, a death, a one-way exit. Re-plan from here.
                if (!Begin(world, DestinationMapIndex, DestinationName)) return null;

                return Walk(world);
            }

            if (_aim == Point.Empty)
            {
                Abort("exit has no reachable cell");
                return null;
            }

            int distance = WorldModel.Distance(world.Location, _aim);

            if (distance < _bestDistance)
            {
                _bestDistance = distance;
                _lastProgress = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - _lastProgress > NoProgressLimit)
            {
                Abort($"stuck {distance} tiles from the exit to {_route[_leg].ToMapName}");
                return null;
            }

            // A teleport leg is talked through rather than walked onto. Anywhere within talking
            // range is close enough - an NPC's reported tile is not always the first point of its
            // region, the same mismatch that once left the town trip standing beside a vendor
            // unable to recognise him.
            if (_route[_leg].IsTeleport && distance <= Math.Max(1, TalkRange))
                return Teleport(world);

            // Standing on the trigger and still here: the server refused, or this cell is not
            // really the movement. Try another cell of the same exit before giving up on it.
            if (distance == 0)
            {
                Phase = JourneyPhase.Crossing;

                if (++_crossAttempts > MaxCrossAttempts)
                {
                    Abort($"exit to {_route[_leg].ToMapName} did not move us");
                    return null;
                }

                Point alternative = Nearest(
                    _route[_leg].Cells.Where(x => x != _aim).ToList(), world.Location);

                if (alternative != Point.Empty) _aim = alternative;

                return Walk(world);
            }

            Phase = JourneyPhase.Walking;
            return Walk(world);
        }

        /// <summary>
        /// Pay the NPC and be moved.
        ///
        /// The dialogue is not explored at runtime: TeleportDirectory read the whole button path
        /// out of System.db when the host started, along with what it costs, so this is only the
        /// playing back of a route already known to end in a Teleport action. Gold is re-checked
        /// here as well as during planning, because the fare was affordable when the route was
        /// chosen and a repair bill since then may have changed that.
        /// </summary>
        private Decision Teleport(WorldModel world)
        {
            MapExit exit = _route[_leg];
            TeleportRoute route = exit.Teleport;

            if (world.Gold - route.Cost < GoldFloor)
            {
                Abort($"{route.NPC?.NPCName} wants {route.Cost:N0} gold and we only have " +
                      $"{world.Gold:N0}, keeping {GoldFloor:N0} back");
                return null;
            }

            Phase = JourneyPhase.Talking;

            if (_talkDeadline == DateTime.MinValue)
                _talkDeadline = DateTime.UtcNow.AddSeconds(25);

            if (DateTime.UtcNow > _talkDeadline)
            {
                Abort($"{route.NPC?.NPCName} did not teleport us");
                return new Decision { Action = BotAction.NPCClose, Reason = "teleport timed out" };
            }

            if (!_called)
            {
                WorldObject npc = NearestNPC(world, route.NPCPoint);

                if (npc == null)
                {
                    // In range of where the database says it stands, but nothing is there. Walking
                    // closer is the only move left, and the deadline above bounds it.
                    return Walk(world);
                }

                _called = true;
                foreach (int button in route.ButtonPath) _buttonPath.Enqueue(button);

                return new Decision
                {
                    Action = BotAction.NPCCall,
                    Reason = route.NPC?.NPCName ?? "teleporter",
                    Subject = $"travelling to {DestinationName}",
                    TargetID = npc.ObjectID
                };
            }

            if (_buttonPath.Count > 0)
            {
                int button = _buttonPath.Dequeue();

                return new Decision
                {
                    Action = BotAction.NPCButton,
                    Reason = $"button {button}",
                    Subject = $"travelling to {DestinationName}",
                    ButtonID = button
                };
            }

            // Buttons all sent. The map change is what ends this leg, and Next sees it at the top.
            return null;
        }

        /// <summary>
        /// S.ObjectNPC carries no name, so an NPC is identified by where it is standing - matched
        /// by proximity rather than equality, for the reason noted above.
        /// </summary>
        private WorldObject NearestNPC(WorldModel world, Point spot)
        {
            if (spot == Point.Empty) return null;

            WorldObject best = null;
            int bestDistance = int.MaxValue;

            foreach (WorldObject ob in world.Objects)
            {
                if (ob.Kind != ObjectKind.NPC) continue;

                int distance = WorldModel.Distance(ob.Location, spot);
                if (distance > Math.Max(1, TalkRange) || distance >= bestDistance) continue;

                best = ob;
                bestDistance = distance;
            }

            return best;
        }

        private Decision Walk(WorldModel world) => new Decision
        {
            Action = BotAction.WalkTo,
            Reason = $"{Status} ({WorldModel.Distance(world.Location, _aim)} tiles)",
            Subject = $"travelling to {DestinationName}",
            Destination = _aim
        };
    }
}
