using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    /// <summary>
    /// A PK-points condition copied verbatim off the dialogue, to be judged later.
    ///
    /// The server's rule is
    /// `Compare(op, Stats[PKPoint], amount == 0 ? Config.RedPoint : amount) || Stats[Redemption] != 0`
    /// (NPCObject.cs:573). Config.RedPoint is a server setting the client never sees, so when the
    /// dialogue leaves the amount at zero we cannot know the threshold - but we can still answer
    /// the question for a character with no PK points at all, which every one of our bots is,
    /// because they never attack players.
    /// </summary>
    public readonly struct PKGate
    {
        public readonly Operator Operator;
        public readonly int Amount;

        public PKGate(Operator op, int amount)
        {
            Operator = op;
            Amount = amount;
        }

        public bool Allows(int pkPoints)
        {
            // The threshold is the server's own RedPoint config, which we cannot read. A character
            // with zero PK points is below any positive threshold, so the "not a murderer" form of
            // this check - which is what it always is in practice - passes. Anything else with an
            // unknown threshold stays unanswerable.
            if (Amount == 0)
                return pkPoints == 0 &&
                       (Operator == Operator.LessThan || Operator == Operator.LessThanOrEqual ||
                        Operator == Operator.NotEqual);

            switch (Operator)
            {
                case Operator.Equal: return pkPoints == Amount;
                case Operator.NotEqual: return pkPoints != Amount;
                case Operator.LessThan: return pkPoints < Amount;
                case Operator.LessThanOrEqual: return pkPoints <= Amount;
                case Operator.GreaterThan: return pkPoints > Amount;
                case Operator.GreaterThanOrEqual: return pkPoints >= Amount;
                default: return false;
            }
        }

        public override string ToString() => $"PK {Operator} {(Amount == 0 ? "RedPoint" : Amount.ToString())}";
    }

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

        /// <summary>
        /// Gold the dialogue actually TAKES, from NPCActionType.TakeGold. This is what we are
        /// poorer by afterwards.
        /// </summary>
        public long Cost;

        /// <summary>
        /// Gold we must be HOLDING to get through the dialogue, from NPCCheckType.Gold.
        ///
        /// These are two different things and conflating them was a real bug. A Gold check only
        /// compares the balance - `Compare(check.Operator, ob.Gold.Amount, check.IntParameter1)`
        /// (NPCObject.cs:448) - it does not spend anything. Folding it into Cost priced a route
        /// that wants you to HAVE 50,000 and CHARGES 5,000 as a 55,000 fare, which is simply a
        /// different, larger number than either of the two facts involved.
        ///
        /// A check part-way down the path has to clear whatever has already been spent, so this
        /// accumulates as max(spentSoFar + checkedAmount).
        /// </summary>
        public long RequiredStartingGold;

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

        /// <summary>
        /// Which check types made UnknownGate true. Without this the refusal is unactionable - the
        /// route says "there is a condition I cannot read" and gives you no way to find out which,
        /// so you cannot tell a real gate from a check that simply has not been taught yet.
        /// </summary>
        public HashSet<NPCCheckType> UnknownChecks;

        /// <summary>
        /// The PK-points gate on this path, or null when there is none.
        ///
        /// Evaluated per bot at planning time rather than when the directory is built, because PK
        /// points are a live stat and differ between characters - the same reason RequiredLevel and
        /// Classes are carried rather than resolved. Every Hexa Holy Stone on this server carries
        /// one of these, so treating it as unreadable made the entire teleport network unusable.
        /// </summary>
        public PKGate? PK;

        public bool Allows(MirClass mirClass, int level, int pkPoints)
        {
            if (UnknownGate) return false;
            if (RequiredLevel > level) return false;
            if (PK.HasValue && !PK.Value.Allows(pkPoints)) return false;

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
            (RequiredStartingGold > 0 ? $" (must be holding {RequiredStartingGold:N0})" : "") +
            (RequiredLevel > 0 ? $", level {RequiredLevel}+" : "") +
            (PK.HasValue ? $", {PK.Value}" : "") +
            (Classes != null ? $", {string.Join("/", Classes)} only" : "") +
            (UnknownGate
                ? ", CANNOT READ " +
                  (UnknownChecks == null || UnknownChecks.Count == 0
                      ? "A CONDITION"
                      : string.Join("/", UnknownChecks.OrderBy(x => x.ToString()))) +
                  " - not planned through"
                : "") +
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
    /// The price is two separate questions, not one. NPCActionType.TakeGold SPENDS gold;
    /// NPCCheckType.Gold only COMPARES the balance and takes nothing. They are tracked separately
    /// as Cost and RequiredStartingGold - see those fields.
    ///
    /// The walk mirrors NPCObject.NPCCall (NPCObject.cs:24-60), which is more than "follow the
    /// buttons": a page whose checks pass runs its actions, and then, IF ITS SAY TEXT IS EMPTY,
    /// auto-advances to SuccessPage without any player input. Following buttons alone missed those
    /// chains entirely, which is how a Hexa Holy Stone standing in Banya Village - offering Bichon
    /// Town among its destinations - produced no routes at all while a bot walked the whole way.
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

        /// <summary>
        /// One walk state: where we are, how we got here, and everything the path implies so far.
        ///
        /// Cost and the gates accumulate DOWN the path, so they travel with the page rather than
        /// being read off it - the same page reached two ways can be reached at two different
        /// prices, which is exactly why the page alone cannot be the cycle-breaking key.
        /// </summary>
        private readonly struct Step
        {
            public readonly NPCPage Page;
            public readonly List<int> Path;
            public readonly long Spent;
            public readonly long NeedGold;
            public readonly int Level;
            public readonly HashSet<MirClass> Classes;
            public readonly bool Unknown;
            public readonly HashSet<NPCCheckType> UnknownChecks;
            public readonly PKGate? PK;

            public Step(NPCPage page, List<int> path, long spent, long needGold, int level,
                HashSet<MirClass> classes, bool unknown, HashSet<NPCCheckType> unknownChecks,
                PKGate? pk)
            {
                Page = page;
                Path = path;
                Spent = spent;
                NeedGold = needGold;
                Level = level;
                Classes = classes;
                Unknown = unknown;
                UnknownChecks = unknownChecks;
                PK = pk;
            }

            public string Key =>
                Page?.Index + "|" + Spent + "|" + NeedGold + "|" + Level + "|" + Unknown + "|" +
                (PK.HasValue ? PK.Value.ToString() : "") + "|" +
                (UnknownChecks == null ? "" : string.Join(".", UnknownChecks.Select(x => (int)x).OrderBy(x => x))) + "|" +
                (Classes == null ? "*" : string.Join(",", Classes.Select(x => (int)x).OrderBy(x => x)));
        }

        private static void Note(ref bool unknown, ref HashSet<NPCCheckType> checks, NPCCheckType type)
        {
            unknown = true;
            checks = checks == null
                ? new HashSet<NPCCheckType> { type }
                : new HashSet<NPCCheckType>(checks) { type };
        }

        /// <summary>How many states to walk per NPC before giving up. A malformed or cyclic
        /// dialogue must not be able to hang host startup.</summary>
        private const int StateBudget = 4000;

        /// <summary>
        /// Walk one dialogue the way the server walks it, collecting every teleport reachable by
        /// making choices rather than by failing checks.
        ///
        /// Mirrors NPCObject.NPCCall (NPCObject.cs:24-60):
        ///   - checks are evaluated first, and a page's ACTIONS only run when they pass;
        ///   - a page whose Say text is empty auto-advances to SuccessPage with no player input;
        ///   - a page with text stops and offers its buttons.
        ///
        /// That middle rule is the one this used to miss. Following buttons alone meant any
        /// teleport behind a silent linking page was invisible, which is how a Hexa Holy Stone
        /// standing in Banya Village - offering Bichon Town among its destinations - produced no
        /// routes at all while a bot walked the whole way there.
        ///
        /// FailPage is deliberately NOT followed. It is the branch taken when a check FAILS, so a
        /// teleport behind one is reachable only by arranging to fail, which is not something the
        /// bot can commit a journey to.
        /// </summary>
        /// <summary>
        /// Every NPC whose dialogue contains a Teleport action anywhere, and whether Explore could
        /// actually reach it. Diagnostic only.
        ///
        /// This exists because the failure it is looking for is SILENT: an NPC we cannot walk to
        /// the end of simply does not appear in the route list, and nothing anywhere says so. A
        /// Hexa Holy Stone in Banya Village offering Bichon Town was invisible that way while a bot
        /// walked the journey on foot. Comparing "has a teleport somewhere in its pages" against
        /// "produced a usable route" is what makes that visible.
        /// </summary>
        public IEnumerable<string> DescribeCoverage(string mapFilter)
        {
            foreach (NPCInfo npc in Globals.NPCInfoList?.Binding ?? Enumerable.Empty<NPCInfo>())
            {
                if (npc?.Region?.Map == null) continue;

                string where = npc.Region.Map.Description ?? "";

                if (!string.IsNullOrWhiteSpace(mapFilter) &&
                    where.IndexOf(mapFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                List<NPCPage> pages = AllPages(npc);

                int teleports = pages.Sum(page => page.Actions?
                    .Count(a => a.ActionType == NPCActionType.Teleport &&
                                a.MapParameter1 != null && a.InstanceParameter1 == null) ?? 0);

                if (teleports == 0) continue;

                int found = _routes.Count(x => x.NPC == npc);
                string name = string.IsNullOrEmpty(npc.NPCName) ? "(unnamed)" : npc.NPCName;

                if (found >= teleports)
                {
                    yield return $"  OK      {name} on {where}: {found} of {teleports} reachable";
                    continue;
                }

                yield return
                    $"  MISSING {name} on {where}: {found} of {teleports} reachable" +
                    (npc.EntryPage == null ? " - NO ENTRY PAGE" : "") +
                    $" - {pages.Count} page(s) walked from the entry, " +
                    $"{CountFailOnly(npc, pages)} teleport(s) sit behind a failed check only";
            }
        }

        /// <summary>Pages reachable from the entry page by the same rules Explore walks.</summary>
        private static List<NPCPage> AllPages(NPCInfo npc)
        {
            List<NPCPage> found = new List<NPCPage>();
            if (npc.EntryPage == null) return found;

            Queue<NPCPage> queue = new Queue<NPCPage>();
            HashSet<NPCPage> seen = new HashSet<NPCPage> { npc.EntryPage };
            queue.Enqueue(npc.EntryPage);

            while (queue.Count > 0 && found.Count < StateBudget)
            {
                NPCPage page = queue.Dequeue();
                found.Add(page);

                if (page.SuccessPage != null && seen.Add(page.SuccessPage))
                    queue.Enqueue(page.SuccessPage);

                if (page.Buttons == null) continue;

                foreach (NPCButton button in page.Buttons)
                    if (button.DestinationPage != null && seen.Add(button.DestinationPage))
                        queue.Enqueue(button.DestinationPage);
            }

            return found;
        }

        /// <summary>
        /// Teleports reachable ONLY by failing a check. Explore does not follow FailPage, so these
        /// are deliberately unreachable rather than accidentally missed - but they should be
        /// visible, in case a server ever puts a real destination behind one.
        /// </summary>
        private static int CountFailOnly(NPCInfo npc, List<NPCPage> reachable)
        {
            int count = 0;

            foreach (NPCPage page in reachable)
            {
                if (page.Checks == null) continue;

                foreach (NPCCheck check in page.Checks)
                {
                    NPCPage fail = check.FailPage;

                    if (fail == null || reachable.Contains(fail)) continue;

                    count += fail.Actions?.Count(a => a.ActionType == NPCActionType.Teleport) ?? 0;
                }
            }

            return count;
        }

        private static IEnumerable<TeleportRoute> Explore(NPCInfo npc)
        {
            Queue<Step> queue = new Queue<Step>();
            HashSet<string> seen = new HashSet<string>();
            int examined = 0;

            Step first = new Step(npc.EntryPage, new List<int>(), 0, 0, 0, null, false, null, null);
            queue.Enqueue(first);
            seen.Add(first.Key);

            while (queue.Count > 0)
            {
                if (++examined > StateBudget) yield break;

                Step step = queue.Dequeue();
                NPCPage page = step.Page;

                long spent = step.Spent;
                long needGold = step.NeedGold;
                int level = step.Level;
                HashSet<MirClass> classes = step.Classes;
                bool unknown = step.Unknown;
                HashSet<NPCCheckType> unknownChecks = step.UnknownChecks;
                PKGate? pk = step.PK;

                if (page.Checks != null)
                    foreach (NPCCheck check in page.Checks)
                        switch (check.CheckType)
                        {
                            case NPCCheckType.Gold:
                                // A balance test, not a charge (NPCObject.cs:448). Anything already
                                // spent above this page still has to be in hand when it is reached,
                                // so the requirement is relative to that spending.
                                if (check.Operator == Operator.GreaterThanOrEqual ||
                                    check.Operator == Operator.Equal)
                                    needGold = Math.Max(needGold, spent + check.IntParameter1);
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
                                if (classes == null) Note(ref unknown, ref unknownChecks, check.CheckType);
                                break;

                            case NPCCheckType.PKPoints:
                                // Kept rather than resolved - PK points are a live per-character
                                // stat, so this is judged when a route is planned, not now.
                                pk = new PKGate(check.Operator, check.IntParameter1);
                                break;

                            case NPCCheckType.Random:
                            case NPCCheckType.Gender:
                                break;   // harmless: neither stops the page being reachable

                            default:
                                Note(ref unknown, ref unknownChecks, check.CheckType);
                                break;
                        }

                if (page.Actions != null)
                {
                    foreach (NPCAction action in page.Actions)
                        if (action.ActionType == NPCActionType.TakeGold)
                            spent += action.IntParameter1;

                    foreach (NPCAction action in page.Actions)
                    {
                        if (action.ActionType != NPCActionType.Teleport) continue;
                        if (action.MapParameter1 == null) continue;
                        if (action.InstanceParameter1 != null) continue;

                        yield return new TeleportRoute
                        {
                            NPC = npc,
                            ButtonPath = new List<int>(step.Path),
                            FromMapIndex = npc.Region.Map.Index,
                            ToMapIndex = action.MapParameter1.Index,
                            ToMapName = action.MapParameter1.Description,
                            Destination = action.MapParameter1,
                            ToPoint = action.IntParameter1 == 0 && action.IntParameter2 == 0
                                ? Point.Empty
                                : new Point(action.IntParameter1, action.IntParameter2),
                            Cost = spent,
                            RequiredStartingGold = needGold,
                            RequiredLevel = level,
                            Classes = classes,
                            UnknownGate = unknown,
                            UnknownChecks = unknownChecks == null
                                ? null
                                : new HashSet<NPCCheckType>(unknownChecks),
                            PK = pk
                        };
                    }
                }

                // A page with nothing to say is not a stop - the server runs straight on.
                if (string.IsNullOrEmpty(page.Say))
                {
                    if (page.SuccessPage == null) continue;

                    Step onward = new Step(page.SuccessPage, step.Path, spent, needGold, level,
                        classes, unknown, unknownChecks, pk);

                    if (seen.Add(onward.Key)) queue.Enqueue(onward);
                    continue;
                }

                if (page.Buttons == null) continue;

                foreach (NPCButton button in page.Buttons)
                {
                    if (button.DestinationPage == null) continue;

                    Step next = new Step(button.DestinationPage,
                        new List<int>(step.Path) { button.ButtonID },
                        spent, needGold, level, classes, unknown, unknownChecks, pk);

                    if (seen.Add(next.Key)) queue.Enqueue(next);
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
