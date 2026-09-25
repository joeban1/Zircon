using System;
using System.Collections.Generic;
using System.Drawing;
using Library;

namespace MirBot
{
    /// <summary>
    /// A* over a map's walkable cells, ported from the server's own route planner
    /// (ServerLibrary/Models/AutoPath/AutoPathRoutePlanner.TryFindPath).
    ///
    /// This is deliberately the server's algorithm rather than a new one: it walks the same eight
    /// directions, over the same grid, so a route it finds is a route the server will accept, and
    /// a route it cannot find does not exist. That second half is the valuable one - the bot can
    /// now tell "cannot get there" from "have not got there yet", which blind steering never could.
    ///
    /// The server's version is not reusable directly: it lives in ServerLibrary, takes a live Map
    /// and MapObject, and is gated on MapInfo.CanAutoPath, which is false on the very maps the bot
    /// hunts. Only the search is worth having.
    /// </summary>
    public static class PathFinder
    {
        private static readonly MirDirection[] Directions =
        {
            MirDirection.Up, MirDirection.UpRight, MirDirection.Right, MirDirection.DownRight,
            MirDirection.Down, MirDirection.DownLeft, MirDirection.Left, MirDirection.UpLeft
        };

        /// <summary>
        /// Cap on cells examined before giving up. A hopeless search on a large map would otherwise
        /// visit every cell on it while the bot stands still: this runs on the bot's own thread, and
        /// a stalled tick is how the bot got itself killed before the threading was split out.
        /// </summary>
        public const int NodeBudget = 20000;

        /// <summary>
        /// Budget for a journey leg: one long walk across a map to its exit, searched once per leg
        /// and then followed (ScriptedBrain caches it), not every step. Taoist Temple is 500x500 and
        /// its walk from the arrival stone to Hyunmoon Temple's entrance needs about 22,400 cells
        /// examined - over NodeBudget - so Sindo was told "no route" to a map it could reach, twice.
        /// A search this size takes a few tens of milliseconds.
        /// </summary>
        public const int JourneyNodeBudget = 150000;

        public static List<Point> Find(MapGrid grid, Point start, Point goal, int goalRange,
            HashSet<Point> avoid) =>
            Find(grid, start, goal, goalRange, avoid, NodeBudget, out _);

        /// <summary>
        /// Steps from start to within goalRange of the goal, start excluded. Null when no route
        /// exists, or when the search ran out of budget.
        ///
        /// goalRange is what makes this usable for both jobs: 0 to stand on a dropped item, 1 to
        /// end up adjacent to a monster, whose own cell is not somewhere we can stand.
        /// </summary>
        /// <param name="budgetExhausted">
        /// True when the search gave up for lack of budget rather than proving there is no route.
        /// The two used to be the same null, and a journey abandoned on the first as if it were the
        /// second.
        /// </param>
        public static List<Point> Find(MapGrid grid, Point start, Point goal, int goalRange,
            HashSet<Point> avoid, int budget, out bool budgetExhausted)
        {
            budgetExhausted = false;
            if (grid == null) return null;
            if (Reached(start, goal, goalRange)) return new List<Point>();

            PriorityQueue<Point, int> open = new PriorityQueue<Point, int>();
            Dictionary<Point, Point> previous = new Dictionary<Point, Point>();
            Dictionary<Point, int> costs = new Dictionary<Point, int> { [start] = 0 };
            HashSet<Point> closed = new HashSet<Point>();

            open.Enqueue(start, Heuristic(start, goal));

            int examined = 0;

            while (open.TryDequeue(out Point current, out _))
            {
                if (!closed.Add(current)) continue;

                if (Reached(current, goal, goalRange))
                    return Reconstruct(previous, start, current);

                if (++examined > budget)
                {
                    budgetExhausted = true;
                    return null;
                }

                foreach (MirDirection direction in Directions)
                {
                    Point next = Functions.Move(current, direction);

                    if (closed.Contains(next)) continue;
                    if (!grid.Walkable(next)) continue;

                    // Something is standing there. The goal itself is exempt: a monster occupies the
                    // cell we are walking to, and refusing it would make every chase unroutable.
                    if (avoid != null && next != goal && avoid.Contains(next)) continue;

                    int cost = costs[current] + 1;

                    if (costs.TryGetValue(next, out int existing) && existing <= cost) continue;

                    costs[next] = cost;
                    previous[next] = current;

                    open.Enqueue(next, cost + Heuristic(next, goal));
                }
            }

            return null;
        }

        /// <summary>Chebyshev: one move covers a diagonal, so it is the true step count.</summary>
        private static int Heuristic(Point from, Point to) =>
            Math.Max(Math.Abs(from.X - to.X), Math.Abs(from.Y - to.Y));

        private static bool Reached(Point point, Point goal, int goalRange) =>
            Heuristic(point, goal) <= goalRange;

        private static List<Point> Reconstruct(Dictionary<Point, Point> previous, Point start,
            Point end)
        {
            List<Point> path = new List<Point>();

            Point current = end;

            while (current != start)
            {
                path.Add(current);

                if (!previous.TryGetValue(current, out current)) return null;
            }

            path.Reverse();
            return path;
        }
    }
}
