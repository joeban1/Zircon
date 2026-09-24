using System;
using System.Collections.Generic;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    /// <summary>One kill target of a quest task still to do.</summary>
    public sealed class QuestKillTarget
    {
        public QuestInfo Quest;
        public QuestTask Task;
        public int MonsterIndex;
        public string MonsterName = "";

        /// <summary>Where a kill counts AND the monster actually spawns.</summary>
        public HashSet<int> Maps = new HashSet<int>();
        public bool IsBoss;
        public long Have;
        public long Need;
    }

    /// <summary>
    /// The quests the bots may take, and every rule about them - built once from System.db.
    ///
    /// Deliberately narrow: only quests both STARTED and FINISHED by a whitelisted NPC
    /// (`QuestNPCs`, default Joeban in Bichon Town) and made entirely of KillMonster tasks with
    /// monster details. Anything else - item-gathering, region visits, a finish NPC elsewhere - is
    /// excluded and named in the startup log, so database drift is visible before a bot acts on it.
    ///
    /// Acceptance mirrors PlayerObject.QuestCanAccept exactly (a refused accept is silent, so a
    /// guess here is a request that simply never answers), plus one bot rule: a quest whose target
    /// is a boss waits for QuestBossMinLevel.
    /// </summary>
    public sealed class QuestBook
    {
        private readonly List<QuestInfo> _quests = new List<QuestInfo>();
        private readonly HashSet<int> _rewardItems = new HashSet<int>();
        private readonly Dictionary<int, HashSet<int>> _spawnMaps = new Dictionary<int, HashSet<int>>();

        public IReadOnlyList<QuestInfo> Quests => _quests;

        /// <summary>The NPCs resolved from QuestNPCs, for walking to them.</summary>
        public IReadOnlyList<NPCInfo> Npcs { get; private set; } = Array.Empty<NPCInfo>();

        public List<string> Report { get; } = new List<string>();

        public void Build(string npcNames)
        {
            _quests.Clear();
            _rewardItems.Clear();
            _spawnMaps.Clear();
            Report.Clear();

            try
            {
                HashSet<string> names = new HashSet<string>(
                    (npcNames ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0),
                    StringComparer.OrdinalIgnoreCase);

                // Resolve by name to the NPCs that actually START quests - Joeban exists in both
                // Bichon Town and Banya Village, and only the Bichon one (#141) gives these.
                List<QuestInfo> all = Globals.QuestInfoList?.Binding?.ToList() ?? new List<QuestInfo>();
                List<NPCInfo> npcs = all.Select(q => q.StartNPC)
                    .Where(n => n != null && names.Contains(n.NPCName ?? ""))
                    .Distinct().ToList();
                Npcs = npcs;

                foreach (NPCInfo npc in npcs)
                    Report.Add($"quest NPC {npc.NPCName} #{npc.Index} on " +
                               $"{npc.Region?.Map?.Description ?? "?"}");

                foreach (MonsterInfo monster in Globals.MonsterInfoList?.Binding ??
                                                Enumerable.Empty<MonsterInfo>())
                {
                    if (monster?.Respawns == null) continue;
                    foreach (RespawnInfo respawn in monster.Respawns)
                    {
                        int map = respawn?.Region?.Map?.Index ?? -1;
                        if (map < 0 || respawn.EventSpawn || respawn.Count <= 0) continue;
                        if (!_spawnMaps.TryGetValue(monster.Index, out HashSet<int> set))
                            _spawnMaps[monster.Index] = set = new HashSet<int>();
                        set.Add(map);
                    }
                }

                foreach (QuestInfo quest in all)
                {
                    if (quest?.StartNPC == null || !npcs.Contains(quest.StartNPC)) continue;

                    string why = Exclusion(quest, npcs);
                    if (why != null)
                    {
                        Report.Add($"excluded '{quest.QuestName}': {why}");
                        continue;
                    }

                    _quests.Add(quest);
                    foreach (QuestReward reward in quest.Rewards ?? Enumerable.Empty<QuestReward>())
                        if (reward?.Item != null) _rewardItems.Add(reward.Item.Index);

                    Report.Add($"included '{quest.QuestName}' ({quest.QuestType}): " +
                               string.Join("; ", quest.Tasks.Select(t =>
                                   $"kill {t.Amount} " + string.Join("/", t.MonsterDetails
                                       .Select(d => d.Monster.MonsterName +
                                                    (d.Map != null ? " on " + d.Map.Description : ""))))));
                }
            }
            catch (Exception ex)
            {
                _quests.Clear();
                Report.Add("quest data did not load: " + ex.Message);
            }
        }

        private static string Exclusion(QuestInfo quest, List<NPCInfo> npcs)
        {
            if (quest.FinishNPC == null || !npcs.Contains(quest.FinishNPC))
                return $"finished by {quest.FinishNPC?.NPCName ?? "nobody"} elsewhere";
            if (quest.Tasks == null || quest.Tasks.Count == 0) return "no tasks";
            foreach (QuestTask task in quest.Tasks)
            {
                if (task.Task != QuestTaskType.KillMonster) return $"a {task.Task} task";
                if (task.MonsterDetails == null || task.MonsterDetails.Count == 0 ||
                    task.MonsterDetails.Any(d => d?.Monster == null))
                    return "a kill task with no monster";
            }
            return null;
        }

        public bool IsQuestReward(ItemInfo info) => info != null && _rewardItems.Contains(info.Index);

        /// <summary>A stat buff to drink as soon as it is held (not the Boss Tracking scroll).</summary>
        public bool IsOrdinaryBuffReward(ItemInfo info) =>
            IsQuestReward(info) && IsItemBuff(info) && info.Stats[Stat.BossTracker] <= 0;

        public bool IsBossTrackerReward(ItemInfo info) =>
            IsQuestReward(info) && IsItemBuff(info) && info.Stats[Stat.BossTracker] > 0;

        /// <summary>Consumable Shape 1 is ItemBuffAdd on the server (PlayerObject ItemUse).</summary>
        public static bool IsItemBuff(ItemInfo info) =>
            info != null && info.ItemType == ItemType.Consumable && info.Shape == 1;

        public static bool ClassAllowed(RequiredClass required, MirClass mirClass)
        {
            RequiredClass mine = mirClass switch
            {
                MirClass.Warrior => RequiredClass.Warrior,
                MirClass.Wizard => RequiredClass.Wizard,
                MirClass.Taoist => RequiredClass.Taoist,
                MirClass.Assassin => RequiredClass.Assassin,
                _ => RequiredClass.None
            };
            return (required & mine) == mine && mine != RequiredClass.None;
        }

        /// <summary>The rewards this class would receive.</summary>
        public static IEnumerable<QuestReward> RewardsFor(QuestInfo quest, MirClass mirClass) =>
            (quest?.Rewards ?? Enumerable.Empty<QuestReward>())
            .Where(r => r?.Item != null && ClassAllowed(r.Class, mirClass) && !r.Choice);

        public static string RewardNames(QuestInfo quest, MirClass mirClass) =>
            string.Join(", ", RewardsFor(quest, mirClass)
                .Select(r => r.Amount > 1 ? $"{r.Item.ItemName} x{r.Amount}" : r.Item.ItemName));

        private static bool InLog(WorldModel world, QuestInfo quest, bool completedOnly = false) =>
            world.Quests.Any(q => q.QuestIndex == quest.Index && (!completedOnly || q.Completed));

        /// <summary>PlayerObject.QuestCanAccept, plus the boss-level rule.</summary>
        public bool CanAccept(QuestInfo quest, WorldModel world, int bossMinLevel)
        {
            if (quest == null || world == null) return false;
            if (InLog(world, quest)) return false;

            foreach (QuestRequirement req in quest.Requirements ?? Enumerable.Empty<QuestRequirement>())
            {
                switch (req.Requirement)
                {
                    case QuestRequirementType.MinLevel:
                        if (world.Level < req.IntParameter1) return false;
                        break;
                    case QuestRequirementType.MaxLevel:
                        if (world.Level > req.IntParameter1) return false;
                        break;
                    case QuestRequirementType.NotAccepted:
                        if (req.QuestParameter != null && InLog(world, req.QuestParameter)) return false;
                        break;
                    case QuestRequirementType.HaveCompleted:
                        if (req.QuestParameter == null ||
                            !InLog(world, req.QuestParameter, completedOnly: true)) return false;
                        break;
                    case QuestRequirementType.HaveNotCompleted:
                        if (req.QuestParameter != null &&
                            InLog(world, req.QuestParameter, completedOnly: true)) return false;
                        break;
                    case QuestRequirementType.Class:
                        if (!ClassAllowed(req.Class, world.Class)) return false;
                        break;
                    default:
                        return false;   // a rule we cannot evaluate: do not guess
                }
            }

            if (HasBossTarget(quest) && world.Level < bossMinLevel) return false;
            return true;
        }

        public bool HasBossTarget(QuestInfo quest) =>
            quest?.Tasks?.Any(t => t.MonsterDetails.Any(d => d.Monster?.IsBoss == true)) == true;

        public IEnumerable<QuestInfo> Acceptable(WorldModel world, int bossMinLevel) =>
            _quests.Where(q => CanAccept(q, world, bossMinLevel));

        /// <summary>Our log entries for these quests that are done and not yet handed in.</summary>
        public IEnumerable<ClientUserQuest> ReadyToHandIn(WorldModel world) =>
            world.Quests.Where(q => !q.Completed && q.Quest != null && _quests.Contains(q.Quest) &&
                                    AllTasksDone(q));

        private static long Progress(ClientUserQuest quest, QuestTask task) =>
            quest.Tasks?.FirstOrDefault(t => t.TaskIndex == task.Index)?.Amount ?? 0;

        public static bool AllTasksDone(ClientUserQuest quest) =>
            quest?.Quest?.Tasks != null &&
            quest.Quest.Tasks.All(task => Progress(quest, task) >= task.Amount);

        /// <summary>What is still to be killed for accepted, unfinished quests.</summary>
        public List<QuestKillTarget> KillTargets(WorldModel world)
        {
            List<QuestKillTarget> targets = new List<QuestKillTarget>();

            foreach (ClientUserQuest quest in world.Quests)
            {
                if (quest.Completed || quest.Quest == null || !_quests.Contains(quest.Quest)) continue;

                foreach (QuestTask task in quest.Quest.Tasks)
                {
                    long have = Progress(quest, task);
                    if (have >= task.Amount) continue;

                    // Rows within one task are alternatives feeding the same counter.
                    foreach (QuestTaskMonsterDetails detail in task.MonsterDetails)
                    {
                        MonsterInfo monster = detail.Monster;
                        _spawnMaps.TryGetValue(monster.Index, out HashSet<int> spawns);
                        HashSet<int> maps = new HashSet<int>(spawns ?? new HashSet<int>());
                        if (detail.Map != null) maps.IntersectWith(new[] { detail.Map.Index });

                        targets.Add(new QuestKillTarget
                        {
                            Quest = quest.Quest, Task = task, MonsterIndex = monster.Index,
                            MonsterName = monster.MonsterName ?? "monster", Maps = maps,
                            IsBoss = monster.IsBoss, Have = have, Need = task.Amount
                        });
                    }
                }
            }

            return targets;
        }

        /// <summary>"7/10 Pig", for the status page.</summary>
        public static string ProgressText(ClientUserQuest quest)
        {
            if (quest?.Quest?.Tasks == null) return "";
            return string.Join(", ", quest.Quest.Tasks.Select(t =>
                $"{Math.Min(Progress(quest, t), t.Amount)}/{t.Amount} " +
                string.Join("/", t.MonsterDetails.Select(d => d.Monster?.MonsterName ?? "?"))));
        }
    }
}
