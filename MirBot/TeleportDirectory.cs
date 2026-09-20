using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    /// <summary>One "pay this NPC and end up over there" route, read out of System.db.</summary>
    public sealed class TeleportRoute
    {
        public NPCInfo NPC;

        /// <summary>Buttons to press from the entry page to reach the page that teleports.</summary>
        public List<int> ButtonPath = new List<int>();

        public int FromMapIndex;
        public int ToMapIndex;
        public string ToMapName = "";

        /// <summary>The destination map itself, for the level and class gates it carries.</summary>
        public MapInfo Destination;

        /// <summary>Where we land, or Empty when the NPC drops us somewhere random.</summary>
        public Point ToPoint;

        /// <summary>Gold the dialogue takes on the way, from TakeGold actions and Gold checks.</summary>
        public long Cost;

        /// <summary>Highest level a check on the path demands. 0 when ungated.</summary>
        public int RequiredLevel;

        /// <summary>Classes the dialogue will accept, or null when it does not care.</summary>
        public HashSet<MirClass> Classes;

        /// <summary>
        /// The path carries a condition we cannot evaluate - a quest, an item, a flag.
        ///
        /// Such a route is never planned through. The alternative is worse than it sounds: a
        /// journey is committed to before it starts, and a gate discovered on arrival means the
        /// bot has already walked the whole way for nothing and has no idea why the NPC ignored it.
        /// </summary>
        public bool UnknownGate;

        public bool Allows(MirClass mirClass, int level)
        {
            if (UnknownGate) return false;
            if (RequiredLevel > level) return false;

            return Classes == null || Classes.Contains(mirClass);
        }

        public string FromMapName => NPC?.Region?.Map?.Description ?? "?";

        public Point NPCPoint =>
            NPC?.Region?.PointRegion != null && NPC.Region.PointRegion.Length > 0
                ? NPC.Region.PointRegion[0]
                : Point.Empty;

        public override string ToString() =>
            $"{NPC?.NPCName} on {FromMapName} at {NPCPoint.X},{NPCPoint.Y} -> {ToMapName}" +
            (ToPoint == Point.Empty ? " (random spot)" : $" at {ToPoint.X},{ToPoint.Y}") +
            $" for {Cost:N0} gold" +
            (RequiredLevel > 0 ? $", level {RequiredLevel}+" : "") +
            (Classes != null ? $", {string.Join("/", Classes)} only" : "") +
            (UnknownGate ? ", HAS A CONDITION WE CANNOT READ - not planned through" : "") +
            (Destination != null && Destination.MinimumLevel > 0
                ? $", map needs level {Destination.MinimumLevel}+" : "") +
            (Destination != null && Destination.RequiredClass != RequiredClass.None &&
             Destination.RequiredClass != RequiredClass.All
                ? $", {Destination.RequiredClass} only" : "") +
            $" [{(ButtonPath.Count == 0 ? "entry page" : string.Join(">", ButtonPath))}]";
    }

    /// <summary>
    /// Every NPC on the server that will move a character to another map for money.
    ///
    /// This is what turns cross-map travel from a long walk into what a real player actually does.
    /// Journey already routes over the map-link graph and walks each leg, which is correct and very
    /// slow: getting from Bichon to a cave three maps away means crossing three full maps on foot at
    /// 600ms a tile. A player pays the teleport NPC.
    ///
    /// The route is derived, not configured. NPCActionType.Teleport carries the destination map in
    /// MapParameter1 and the landing cell in IntParameter1/2 (0,0 meaning "anywhere on it", which is
    /// what Map.GetRandomLocation does server-side), so the whole thing is readable from System.db
    /// the same way VendorDirectory reads who buys what.
    ///
    /// The price is the part worth being careful about. It is not one field: a dialogue takes gold
    /// through NPCActionType.TakeGold and separately GUARDS the page with an NPCCheckType.Gold, and
    /// either can appear anywhere along the button path. Both are accumulated, and the check is
    /// treated as an entry fee rather than merely a gate, because a page that demands 50,000 gold
    /// to open is a page that costs at least that much to use whether or not it says so.
    ///
    /// Instance teleports are ignored. They need an instance to be available, the server refuses to
    /// move between instances, and nothing the bot does has any business inside one.
    /// </summary>
    public sealed class TeleportDirectory
    {
        private readonly List<TeleportRoute> _routes = new List<TeleportRoute>();

        public int Count => _routes.Count;
        public IEnumerable<TeleportRoute> Routes => _routes;

        public void Build()
        {
            _routes.Clear();

            IEnumerable<NPCInfo> npcs;

            try
            {
                npcs = Globals.NPCInfoList?.Binding?.ToList() ?? new List<NPCInfo>();
            }
            catch
            {
                return;
            }

            foreach (NPCInfo npc in npcs)
            {
                if (npc?.EntryPage == null || npc.Region?.Map == null) continue;

                foreach (TeleportRoute route in Explore(npc))
                    _routes.Add(route);
            }
        }

        private static IEnumerable<TeleportRoute> Explore(NPCInfo npc)
        {
            // Cost and level gate accumulate DOWN the path, so each queued page carries the totals
            // that reaching it implies rather than only what its own page says.
            Queue<(NPCPage Page, List<int> Path, long Cost, int Level, HashSet<MirClass> Classes,
                   bool Unknown)> queue =
                new Queue<(NPCPage, List<int>, long, int, HashSet<MirClass>, bool)>();

            HashSet<NPCPage> seen = new HashSet<NPCPage>();

            queue.Enqueue((npc.EntryPage, new List<int>(), 0, 0, null, false));
            seen.Add(npc.EntryPage);

            while (queue.Count > 0)
            {
                (NPCPage page, List<int> path, long cost, int level, HashSet<MirClass> classes,
                 bool unknown) = queue.Dequeue();

                if (page.Checks != null)
                    foreach (NPCCheck check in page.Checks)
                        switch (check.CheckType)
                        {
                            case NPCCheckType.Gold:
                                cost = Math.Max(cost, check.IntParameter1);
                                break;

                            case NPCCheckType.Level:
                                if (check.Operator == Operator.GreaterThanOrEqual ||
                                    check.Operator == Operator.Equal)
                                    level = Math.Max(level, check.IntParameter1);
                                break;

                            case NPCCheckType.Class:
                                // The server compares (int)ob.Class against IntParameter1
                                // (NPCObject.cs:423), so this is one class, not a flag set.
                                classes = Narrow(classes, check);
                                if (classes == null) unknown = true;
                                break;

                            case NPCCheckType.Random:
                            case NPCCheckType.Gender:
                                break;   // harmless: neither stops the page being reachable

                            default:
                                unknown = true;
                                break;
                        }

                if (page.Actions != null)
                {
                    foreach (NPCAction action in page.Actions)
                        if (action.ActionType == NPCActionType.TakeGold)
                            cost += action.IntParameter1;

                    foreach (NPCAction action in page.Actions)
                    {
                        if (action.ActionType != NPCActionType.Teleport) continue;
                        if (action.MapParameter1 == null) continue;
                        if (action.InstanceParameter1 != null) continue;

                        yield return new TeleportRoute
                        {
                            NPC = npc,
                            ButtonPath = new List<int>(path),
                            FromMapIndex = npc.Region.Map.Index,
                            ToMapIndex = action.MapParameter1.Index,
                            ToMapName = action.MapParameter1.Description,
                            Destination = action.MapParameter1,
                            ToPoint = action.IntParameter1 == 0 && action.IntParameter2 == 0
                                ? Point.Empty
                                : new Point(action.IntParameter1, action.IntParameter2),
                            Cost = cost,
                            RequiredLevel = level,
                            Classes = classes,
                            UnknownGate = unknown
                        };
                    }
                }

                if (page.Buttons == null) continue;

                foreach (NPCButton button in page.Buttons)
                {
                    NPCPage next = button.DestinationPage;
                    if (next == null || !seen.Add(next)) continue;

                    queue.Enqueue((next, new List<int>(path) { button.ButtonID }, cost, level,
                        classes, unknown));
                }
            }
        }

        /// <summary>
        /// Apply one Class check to the set of classes still allowed, or return null when the
        /// check is one we cannot turn into a set - which the caller treats as an unknown gate.
        /// </summary>
        private static HashSet<MirClass> Narrow(HashSet<MirClass> current, NPCCheck check)
        {
            MirClass wanted = (MirClass)check.IntParameter1;

            if (!Enum.IsDefined(typeof(MirClass), wanted)) return null;

            HashSet<MirClass> allowed;

            switch (check.Operator)
            {
                case Operator.Equal:
                    allowed = new HashSet<MirClass> { wanted };
                    break;

                case Operator.NotEqual:
                    allowed = new HashSet<MirClass>((MirClass[])Enum.GetValues(typeof(MirClass)));
                    allowed.Remove(wanted);
                    break;

                default:
                    return null;
            }

            if (current == null) return allowed;

            allowed.IntersectWith(current);
            return allowed;
        }

        public string Describe()
        {
            int maps = _routes.Select(x => x.FromMapIndex).Distinct().Count();
            int free = _routes.Count(x => x.Cost == 0);

            return $"{_routes.Count} teleport routes from {maps} maps ({free} free)";
        }
    }
}
