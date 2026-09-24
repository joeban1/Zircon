using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    /// <summary>
    /// A short stop on a map with quest NPCs (Bichon Town, Banya Village, Lost Paradise): hand in
    /// what is done, take what is available - one NPC at a time, nearest first, each asked only
    /// about ITS quests - and kill the targets that live here, then let the bot carry on.
    ///
    /// Only runs while the bot is ALREADY on the NPC's map (Joeban: Bichon Town), which is the
    /// operator's "only when passing through" rule; the bot never travels here for a quest on its
    /// own - only the operator's "Do quests" button sends it (BotInstance.DoQuests). While
    /// it runs it owns movement the way a town trip does - a journey passing through is held, not
    /// replaced - but it never replaces combat: hunting only nominates priority targets and the
    /// ordinary fight/loot/heal/roam logic does the rest.
    ///
    /// Every server answer is checked, never assumed. An accept is acknowledged only when that
    /// quest appears in the log, a hand-in only when that quest's Completed goes true; the kill
    /// updates that arrive as S.QuestChanged in between are not acknowledgements. A refusal is
    /// silent on this server, so each request is tried twice and then skipped for the visit.
    /// </summary>
    public sealed class QuestErrand
    {
        public enum ErrandPhase { Idle, Talking, Hunting }

        private enum RequestKind { Accept, Complete }

        // A visit is given up after this long WITHOUT PROGRESS - a quest transition, or a target
        // in sight. It used to be eight minutes from the start of the visit, which on a busy
        // visit (hand in a daily, take the next, hunt Pigs) left four minutes for the Forest Yetis:
        // Mirbot gave up ten tiles from its second one. VisitCap still bounds a visit overall.
        private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(8);
        private static readonly TimeSpan VisitCap = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan GiveUpCooldown = TimeSpan.FromMinutes(45);
        private static readonly TimeSpan PageWait = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan NpcSearch = TimeSpan.FromSeconds(10);
        private const int Tries = 2;

        private readonly BotConfig _config;
        private readonly QuestBook _book;
        private readonly Action<string> _log;

        public ErrandPhase Phase { get; private set; } = ErrandPhase.Idle;
        public string Status { get; private set; } = "";

        public bool Active => Phase != ErrandPhase.Idle;
        public bool OwnsMovement => Active;

        /// <summary>Set when a visit ends; BotInstance consumes it as a travel trigger.</summary>
        public bool JustFinished;

        /// <summary>While hunting: the monster indexes to prefer.</summary>
        public HashSet<int> HuntTargets { get; } = new HashSet<int>();

        private int _mapIndex = -1;
        private DateTime _visitStart;
        private DateTime _progressAt;
        private DateTime _cooldownUntil = DateTime.MinValue;
        private readonly HashSet<int> _skipped = new HashSet<int>();

        /// <summary>NPCs given up on for this visit (not where the database says, or silent).</summary>
        private readonly HashSet<int> _skippedNpcs = new HashSet<int>();

        /// <summary>The NPC being talked to. Switching resets the conversation (ResetTalk).</summary>
        private NPCInfo _npc;

        /// <summary>Set by BotInstance: this bot's acceptance rules (levels, huntable maps, filter).</summary>
        public Func<QuestRules> Rules;

        private bool _pageOpen;
        private DateTime _calledAt = DateTime.MinValue;
        private int _calls;
        private DateTime _atNpcSince = DateTime.MinValue;

        private int _pendingQuest = -1;
        private RequestKind _pendingKind;
        private DateTime _pendingUntil;
        private readonly Dictionary<int, int> _tries = new Dictionary<int, int>();

        public QuestErrand(BotConfig config, QuestBook book, Action<string> log)
        {
            _config = config;
            _book = book;
            _log = log ?? (_ => { });
        }

        // ---- inputs from the connection ----------------------------------------------------

        /// <summary>S.NPCResponse arrived. Only meaningful while we are talking.</summary>
        public void PageChanged(NPCPage page)
        {
            if (Phase == ErrandPhase.Talking) _pageOpen = true;
        }

        public void QuestChanged(QuestTransition transition)
        {
            if (Active && transition?.Quest != null) _progressAt = DateTime.UtcNow;

            if (transition?.Quest == null || _pendingQuest != transition.Quest.QuestIndex) return;

            bool answered = _pendingKind == RequestKind.Accept
                ? transition.Accepted
                : transition.Completed;
            if (!answered) return;   // a kill update for the same quest, not our answer

            _pendingQuest = -1;
        }

        /// <summary>A town trip took over: stop without penalty; the errand can start again.</summary>
        public void Suspend(string why)
        {
            if (!Active) return;
            _log($"Quest: pausing the errand - {why}.");
            End(cooldown: false, finished: false);
        }

        /// <summary>The operator asked for quests now: forget an earlier give-up.</summary>
        public void ClearCooldown() => _cooldownUntil = DateTime.MinValue;

        public void Abort(string why, bool cooldown)
        {
            if (!Active) return;
            _log($"Quest: errand abandoned - {why}" +
                 (cooldown ? $"; not trying again for {GiveUpCooldown.TotalMinutes:0} minutes." : "."));
            End(cooldown, finished: false);
        }

        private void End(bool cooldown, bool finished)
        {
            Phase = ErrandPhase.Idle;
            Status = "";
            HuntTargets.Clear();
            _pendingQuest = -1;
            _pageOpen = false;
            _calls = 0;
            _calledAt = DateTime.MinValue;
            _atNpcSince = DateTime.MinValue;
            if (cooldown) _cooldownUntil = DateTime.UtcNow + GiveUpCooldown;
            JustFinished = finished || cooldown;
        }

        // ---- the work ----------------------------------------------------------------------

        private QuestRules CurrentRules() =>
            Rules?.Invoke() ?? new QuestRules
            {
                MiniBossMinLevel = _config.QuestMiniBossMinLevel,
                BossMinLevel = _config.QuestBossMinLevel
            };

        private static Point NpcPoint(NPCInfo npc) =>
            npc?.Region?.PointRegion != null && npc.Region.PointRegion.Length > 0
                ? npc.Region.PointRegion[0] : Point.Empty;

        /// <summary>This NPC's hand-ins and accepts, minus anything skipped this visit.</summary>
        private (List<ClientUserQuest> HandIns, List<QuestInfo> Accepts) Work(NPCInfo npc,
            WorldModel world, QuestRules rules)
        {
            var (handIns, accepts) = _book.WorkFor(npc, world, rules);
            handIns.RemoveAll(q => _skipped.Contains(q.QuestIndex));
            accepts.RemoveAll(q => _skipped.Contains(q.Index));
            return (handIns, accepts);
        }

        private bool HasTalk(NPCInfo npc, WorldModel world, QuestRules rules)
        {
            var (handIns, accepts) = Work(npc, world, rules);
            return handIns.Count > 0 || accepts.Count > 0;
        }

        /// <summary>The nearest NPC on this map with something to say to us, or null.</summary>
        private NPCInfo NextNpc(WorldModel world, QuestRules rules) =>
            _book.NpcsOn(world.MapIndex)
                .Where(n => !_skippedNpcs.Contains(n.Index) && NpcPoint(n) != Point.Empty &&
                            HasTalk(n, world, rules))
                .OrderBy(n => WorldModel.Distance(world.Location, NpcPoint(n)))
                .FirstOrDefault();

        /// <summary>Accepted, unfinished, non-boss targets that spawn on THIS map.</summary>
        private List<QuestKillTarget> HuntableHere(WorldModel world) => HuntableOn(world, world.MapIndex);

        private List<QuestKillTarget> HuntableOn(WorldModel world, int mapIndex) =>
            _book.KillTargets(world)
                .Where(t => !t.IsBoss && t.Maps.Contains(mapIndex)).ToList();

        /// <summary>Is there anything for the errand to do here, right now?</summary>
        public bool HasWork(WorldModel world) => HasWorkOn(world, world.MapIndex);

        /// <summary>
        /// Would a visit to this map find anything to do - a hand-in or accept with one of ITS
        /// NPCs, or a target living here? Lets the travel logic and the "Do quests" button ask about
        /// a town while the bot is somewhere else. Ignores this visit's skips.
        /// </summary>
        public bool HasWorkOn(WorldModel world, int mapIndex)
        {
            if (_book == null || _book.Quests.Count == 0 || !_config.EnableQuests) return false;
            QuestRules rules = CurrentRules();
            foreach (NPCInfo npc in _book.NpcsOn(mapIndex))
            {
                var (handIns, accepts) = _book.WorkFor(npc, world, rules);
                if (handIns.Count > 0 || accepts.Count > 0) return true;
            }
            return _book.NpcsOn(mapIndex).Any() && HuntableOn(world, mapIndex).Count > 0;
        }

        /// <summary>Every quest-NPC map with work for us now (Do quests, hand-in travel).</summary>
        public List<int> MapsWithWork(WorldModel world) =>
            _book == null ? new List<int>() :
            _book.Npcs.Select(n => n.Region?.Map?.Index ?? -1).Where(m => m >= 0).Distinct()
                .Where(m => HasWorkOn(world, m)).ToList();

        /// <summary>
        /// The next step, or null to let the brain carry on (hunting, or nothing to do).
        /// Call only when no town trip is active.
        /// </summary>
        public Decision Next(WorldModel world, Backpack bag)
        {
            if (!_config.EnableQuests || _book == null) return null;

            if (Active)
            {
                if (world.Dead) { Abort("died", cooldown: false); return null; }
                if (world.MapIndex != _mapIndex) { Abort("left the map", cooldown: false); return null; }
                if (DateTime.UtcNow - _progressAt > IdleLimit)
                {
                    Abort($"no quest progress for {IdleLimit.TotalMinutes:0} minutes", cooldown: true);
                    return null;
                }

                if (DateTime.UtcNow - _visitStart > VisitCap)
                {
                    Abort($"still busy after {VisitCap.TotalMinutes:0} minutes", cooldown: true);
                    return null;
                }
            }
            else
            {
                if (world.Dead || DateTime.UtcNow < _cooldownUntil || !HasWork(world)) return null;

                Phase = ErrandPhase.Talking;
                _mapIndex = world.MapIndex;
                _visitStart = DateTime.UtcNow;
                _progressAt = _visitStart;
                _skipped.Clear();
                _skippedNpcs.Clear();
                _tries.Clear();
                ResetTalk(null);
                _log("Quest: errand on " + world.MapName + " - " + Describe(world) + ".");
            }

            QuestRules rules = CurrentRules();

            // Talking: stay with the current NPC while it has work (or a request is in flight);
            // otherwise re-pick - an accept or hand-in can open work at another NPC (Curing the
            // Poison hands off between Mr. Kang and David).
            if (_npc != null && _pendingQuest < 0 && !HasTalk(_npc, world, rules))
                ResetTalk(null);

            NPCInfo npc = _npc ?? NextNpc(world, rules);
            if (npc != null)
            {
                if (npc != _npc) ResetTalk(npc);
                Phase = ErrandPhase.Talking;
                HuntTargets.Clear();
                return Talk(world, bag, npc, rules);
            }

            List<QuestKillTarget> hunt = HuntableHere(world);
            if (hunt.Count > 0)
            {
                if (Phase != ErrandPhase.Hunting)
                    _log("Quest: hunting " + string.Join(", ",
                        hunt.Select(t => $"{t.MonsterName} ({t.Have}/{t.Need})").Distinct()) + ".");
                Phase = ErrandPhase.Hunting;
                ResetTalk(null);
                HuntTargets.Clear();
                foreach (QuestKillTarget t in hunt) HuntTargets.Add(t.MonsterIndex);

                // A target in sight is progress in all but name: the kill is coming.
                if (world.Objects.Any(x => x.IsLiveMonster && !x.IsPet && HuntTargets.Contains(x.MonsterIndex)))
                    _progressAt = DateTime.UtcNow;
                Status = "quest: hunting " + string.Join(", ",
                    hunt.Select(t => $"{t.MonsterName} {t.Have}/{t.Need}").Distinct());
                return null;
            }

            _log("Quest: errand done on " + world.MapName + ".");
            End(cooldown: false, finished: true);
            return null;
        }

        /// <summary>Start talking to a (different) NPC from scratch.</summary>
        private void ResetTalk(NPCInfo npc)
        {
            _npc = npc;
            _pageOpen = false;
            _calledAt = DateTime.MinValue;
            _calls = 0;
            _atNpcSince = DateTime.MinValue;
            _pendingQuest = -1;
        }

        private string Describe(WorldModel world)
        {
            QuestRules rules = CurrentRules();
            List<string> parts = new List<string>();
            foreach (NPCInfo npc in _book.NpcsOn(world.MapIndex))
            {
                var (handIns, accepts) = _book.WorkFor(npc, world, rules);
                if (handIns.Count > 0)
                    parts.Add($"hand in {string.Join(", ", handIns.Select(q => q.Quest.QuestName))} to {npc.NPCName}");
                if (accepts.Count > 0)
                    parts.Add($"accept {string.Join(", ", accepts.Select(q => q.QuestName))} from {npc.NPCName}");
            }
            List<QuestKillTarget> hunt = HuntableHere(world);
            if (hunt.Count > 0) parts.Add("hunt " + string.Join(", ", hunt.Select(t => t.MonsterName).Distinct()));
            return string.Join("; ", parts);
        }

        /// <summary>Give up on one NPC for this visit and move on to the next.</summary>
        private void SkipNpc(NPCInfo npc, string why)
        {
            _log($"Quest: skipping {npc.NPCName} this visit - {why}.");
            _skippedNpcs.Add(npc.Index);
            ResetTalk(null);
        }

        private Decision Talk(WorldModel world, Backpack bag, NPCInfo npc, QuestRules rules)
        {
            Point spot = NpcPoint(npc);

            int range = Math.Max(1, _config.VendorTalkRange);
            int distance = WorldModel.Distance(world.Location, spot);

            if (distance > range)
            {
                _pageOpen = false;
                _atNpcSince = DateTime.MinValue;
                Status = $"quest: walking to {npc.NPCName}";
                return new Decision
                {
                    Action = BotAction.WalkTo,
                    Destination = spot,
                    Reason = $"walking to {npc.NPCName} for quests ({distance} tiles)",
                    Subject = npc.NPCName
                };
            }

            if (_atNpcSince == DateTime.MinValue) _atNpcSince = DateTime.UtcNow;

            // NPC objects carry no name: the one standing at (or next to) his database spot.
            WorldObject ob = world.Objects
                .Where(x => x.Kind == ObjectKind.NPC && WorldModel.Distance(x.Location, spot) <= 2)
                .OrderBy(x => WorldModel.Distance(x.Location, spot)).FirstOrDefault();

            if (ob == null)
            {
                if (DateTime.UtcNow - _atNpcSince > NpcSearch)
                    SkipNpc(npc, "not where the database says");
                return null;
            }

            if (!_pageOpen)
            {
                if (_calledAt != DateTime.MinValue && DateTime.UtcNow - _calledAt < PageWait)
                    return new Decision { Action = BotAction.Idle, Reason = $"waiting for {npc.NPCName}" };

                if (_calls >= Tries)
                {
                    SkipNpc(npc, "did not answer");
                    return null;
                }

                _calls++;
                _calledAt = DateTime.UtcNow;
                Status = $"quest: talking to {npc.NPCName}";
                return new Decision
                {
                    Action = BotAction.NPCCall,
                    TargetID = ob.ObjectID,
                    Reason = $"talking to {npc.NPCName} about quests",
                    Subject = npc.NPCName
                };
            }

            // One request in flight at a time.
            if (_pendingQuest >= 0)
            {
                if (DateTime.UtcNow < _pendingUntil)
                    return new Decision { Action = BotAction.Idle, Reason = "waiting for the quest answer" };

                _tries.TryGetValue(_pendingQuest, out int tried);
                if (tried >= Tries)
                {
                    QuestInfo info = Globals.QuestInfoList?.Binding?.FirstOrDefault(q => q.Index == _pendingQuest);
                    _log($"Quest: no answer for '{info?.QuestName ?? _pendingQuest.ToString()}' " +
                         $"after {Tries} tries - skipping it this visit.");
                    _skipped.Add(_pendingQuest);
                    _pendingQuest = -1;
                    return null;
                }

                return Send(_pendingQuest, _pendingKind);
            }

            var (handIns, accepts) = Work(npc, world, rules);

            // Hand in first: it frees the quest slot and the rewards may matter for what follows.
            foreach (ClientUserQuest ready in handIns)
            {
                var rewards = QuestBook.RewardsFor(ready.Quest, world.Class)
                    .Select(r => (r.Item, (long)r.Amount, r.Bound, r.Duration > 0));
                if (!bag.HasRoomForRewards(rewards))
                {
                    _log($"Quest: no bag room for the '{ready.Quest.QuestName}' rewards - " +
                         "handing in next visit.");
                    _skipped.Add(ready.QuestIndex);
                    continue;
                }
                return Send(ready.QuestIndex, RequestKind.Complete);
            }

            foreach (QuestInfo quest in accepts)
                return Send(quest.Index, RequestKind.Accept);

            return null;
        }

        private Decision Send(int questIndex, RequestKind kind)
        {
            _tries.TryGetValue(questIndex, out int tried);
            _tries[questIndex] = tried + 1;
            _pendingQuest = questIndex;
            _pendingKind = kind;
            _pendingUntil = DateTime.UtcNow + AnswerWait;

            QuestInfo info = Globals.QuestInfoList?.Binding?.FirstOrDefault(q => q.Index == questIndex);
            string name = info?.QuestName ?? questIndex.ToString();
            Status = kind == RequestKind.Accept ? $"quest: accepting {name}" : $"quest: handing in {name}";

            return new Decision
            {
                Action = kind == RequestKind.Accept ? BotAction.QuestAccept : BotAction.QuestComplete,
                QuestIndex = questIndex,
                Reason = (kind == RequestKind.Accept ? "accepting " : "handing in ") + name,
                Subject = name
            };
        }
    }
}
