using System;
using System.Collections.Generic;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    /// <summary>One kill (or kill-for-a-drop) target of a quest task still to do.</summary>
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

        /// <summary>GainItem: the quest item the kill drops (picked up to count), else -1.</summary>
        public int ItemIndex = -1;
        public bool IsGather => ItemIndex >= 0;
    }

    /// <summary>
    /// The bot-side rules for accepting a quest, beyond the server's own. Built per bot, per call:
    /// what a bot may hunt depends on its level, deaths and money.
    /// </summary>
    public sealed class QuestRules
    {
        /// <summary>Level for a quest whose target is a mini-boss (QuestMiniBossMinLevel).</summary>
        public int MiniBossMinLevel = 40;

        /// <summary>Level for a heavy mini-boss - Crazed Warrior (QuestBossMinLevel).</summary>
        public int BossMinLevel = 50;

        /// <summary>May this bot hunt on this map at all? Null = no map check.</summary>
        public Func<int, bool> Huntable;

        /// <summary>QuestNPCs, as a per-bot filter on start NPC names. Empty = every giver.</summary>
        public ISet<string> NpcFilter;
    }

    /// <summary>
    /// The quests the bots may take, and every rule about them - built once from System.db.
    ///
    /// Every quest giver is included (it used to be Joeban only). A quest is used when all of its
    /// tasks are ones a bot can do - kill a monster, or kill one for an item that drops on the floor
    /// (picked up, it credits the task) - or it has no tasks at all (a talk-only hand-off between
    /// two NPCs). Excluded, and named in the startup log: unsupported task types, quests above the
    /// server's level 60 cap or chained to one that is excluded (the CHB/rebirth story), and quests
    /// whose target is a REAL boss (one spawn, hours to return: Arch Lich Taedu, Razor Tusk).
    ///
    /// Acceptance mirrors PlayerObject.QuestCanAccept exactly (a refused accept is silent), plus the
    /// bot's rules: boss targets wait for a level, and every task needs a map this bot may hunt - a
    /// level 5 bot is never handed a quest for level 45 monsters.
    /// </summary>
    public sealed class QuestBook
    {
        /// <summary>A mini-boss with at least this much health uses BossMinLevel (Crazed Warrior).</summary>
        public const int HeavyBossHealth = 3000;

        private readonly List<QuestInfo> _quests = new List<QuestInfo>();
        private readonly HashSet<int> _rewardItems = new HashSet<int>();
        private readonly Dictionary<int, HashSet<int>> _spawnMaps = new Dictionary<int, HashSet<int>>();

        public IReadOnlyList<QuestInfo> Quests => _quests;

        /// <summary>Every NPC that starts or finishes an included quest.</summary>
        public IReadOnlyList<NPCInfo> Npcs { get; private set; } = Array.Empty<NPCInfo>();

        /// <summary>The included quest NPCs standing on a map.</summary>
        public IEnumerable<NPCInfo> NpcsOn(int mapIndex) =>
            Npcs.Where(n => n.Region?.Map?.Index == mapIndex);

        public List<string> Report { get; } = new List<string>();

        public void Build()
        {
            _quests.Clear();
            _rewardItems.Clear();
            _spawnMaps.Clear();
            Report.Clear();

            try
            {
                List<QuestInfo> all = Globals.QuestInfoList?.Binding?.ToList() ?? new List<QuestInfo>();

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

                // Own rules first, then anything chained to an excluded quest, to a fixed point.
                Dictionary<QuestInfo, string> excluded = new Dictionary<QuestInfo, string>();
                foreach (QuestInfo quest in all.Where(q => q != null))
                {
                    string why = Exclusion(quest);
                    if (why != null) excluded[quest] = why;
                }

                bool changed = true;
                while (changed)
                {
                    changed = false;
                    foreach (QuestInfo quest in all.Where(q => q != null && !excluded.ContainsKey(q)).ToList())
                    {
                        QuestRequirement needs = quest.Requirements?.FirstOrDefault(r =>
                            r?.Requirement == QuestRequirementType.HaveCompleted &&
                            r.QuestParameter != null && r.QuestParameter != quest &&
                            excluded.ContainsKey(r.QuestParameter));
                        if (needs == null) continue;
                        excluded[quest] = $"needs '{needs.QuestParameter.QuestName}', which is excluded";
                        changed = true;
                    }
                }

                foreach (QuestInfo quest in all.Where(q => q != null))
                {
                    if (excluded.TryGetValue(quest, out string why))
                    {
                        Report.Add($"excluded '{quest.QuestName}': {why}");
                        continue;
                    }

                    _quests.Add(quest);
                    foreach (QuestReward reward in quest.Rewards ?? Enumerable.Empty<QuestReward>())
                        if (reward?.Item != null) _rewardItems.Add(reward.Item.Index);

                    Report.Add($"included '{quest.QuestName}' ({quest.QuestType}) from " +
                               $"{quest.StartNPC.NPCName} on {quest.StartNPC.Region?.Map?.Description ?? "?"}: " +
                               Describe(quest));
                }

                // NPCs only from INCLUDED quests - the Notice Board and the Administrator start or
                // finish only the excluded story quests.
                Npcs = _quests.SelectMany(q => new[] { q.StartNPC, q.FinishNPC })
                    .Where(n => n != null).Distinct().ToList();

                foreach (IGrouping<string, NPCInfo> town in Npcs.GroupBy(n => n.Region?.Map?.Description ?? "?"))
                    Report.Add($"quest NPCs on {town.Key}: " +
                               string.Join(", ", town.Select(n => $"{n.NPCName} #{n.Index}")));
            }
            catch (Exception ex)
            {
                _quests.Clear();
                Npcs = Array.Empty<NPCInfo>();
                Report.Add("quest data did not load: " + ex.Message);
            }
        }

        private static string Describe(QuestInfo quest)
        {
            if (quest.Tasks == null || quest.Tasks.Count == 0)
                return $"talk to {quest.FinishNPC?.NPCName}";
            return string.Join("; ", quest.Tasks.Select(t =>
                (t.Task == QuestTaskType.GainItem ? $"gather {t.Amount} {t.ItemParameter?.ItemName} from " : $"kill {t.Amount} ") +
                string.Join("/", t.MonsterDetails.Select(d => d.Monster.MonsterName +
                                                              (d.Map != null ? " on " + d.Map.Description : "")))));
        }

        /// <summary>Why a quest can never be done by a bot, or null.</summary>
        private static string Exclusion(QuestInfo quest)
        {
            if (quest.StartNPC == null || quest.FinishNPC == null) return "no start or finish NPC";

            QuestRequirement min = quest.Requirements?.FirstOrDefault(r =>
                r?.Requirement == QuestRequirementType.MinLevel);
            if (min != null && min.IntParameter1 > MapProfile.LevelCap)
                return $"needs level {min.IntParameter1}, above the level {MapProfile.LevelCap} cap";

            foreach (QuestTask task in quest.Tasks ?? Enumerable.Empty<QuestTask>())
            {
                if (task == null) return "a missing task";
                if (task.Task != QuestTaskType.KillMonster && task.Task != QuestTaskType.GainItem)
                    return $"a {task.Task} task";
                if (task.MonsterDetails == null || task.MonsterDetails.Count == 0 ||
                    task.MonsterDetails.Any(d => d?.Monster == null))
                    return task.Task == QuestTaskType.GainItem
                        ? "an item that does not drop from a monster"
                        : "a kill task with no monster";
                if (task.Task == QuestTaskType.GainItem && task.ItemParameter == null)
                    return "a gather task with no item";

                foreach (QuestTaskMonsterDetails detail in task.MonsterDetails)
                    if (detail.Monster.IsBoss && detail.Monster.Respawns != null &&
                        !IsMiniBoss(detail.Monster, detail.Map))
                        return $"{detail.Monster.MonsterName} is a real boss";
            }

            return null;
        }

        /// <summary>
        /// The mini-boss rule (BotInstance.IsMiniBoss applies the same one to lairs): on some map it
        /// can be fought on, at least two spawns, all returning within an hour. From the raw
        /// respawns, so it depends on neither map grids nor build order.
        /// </summary>
        public static bool IsMiniBoss(MonsterInfo monster, MapInfo onlyMap = null)
        {
            if (monster?.Respawns == null) return false;

            foreach (IGrouping<int, RespawnInfo> map in monster.Respawns
                         .Where(r => r?.Region?.Map != null && !r.EventSpawn && r.Count > 0 &&
                                     (onlyMap == null || r.Region.Map == onlyMap))
                         .GroupBy(r => r.Region.Map.Index))
                if (map.Sum(r => r.Count) >= 2 && map.All(r => r.Delay <= 60)) return true;

            return false;
        }

        /// <summary>The level a boss target needs: a heavy one (Crazed Warrior) waits longer.</summary>
        public static int BossLevelFor(MonsterInfo monster, QuestRules rules) =>
            monster?.Stats != null && monster.Stats[Stat.Health] >= HeavyBossHealth
                ? rules.BossMinLevel
                : rules.MiniBossMinLevel;

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

        /// <summary>
        /// PlayerObject.QuestCanAccept, plus the old single boss-level rule (every boss target at
        /// bossMinLevel, no map check). Kept for callers and tests that predate QuestRules.
        /// </summary>
        public bool CanAccept(QuestInfo quest, WorldModel world, int bossMinLevel) =>
            CanAccept(quest, world, new QuestRules { MiniBossMinLevel = bossMinLevel, BossMinLevel = bossMinLevel });

        /// <summary>PlayerObject.QuestCanAccept, plus the bot's rules (QuestRules).</summary>
        public bool CanAccept(QuestInfo quest, WorldModel world, QuestRules rules)
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

            rules ??= new QuestRules();

            if (rules.NpcFilter != null && rules.NpcFilter.Count > 0 &&
                !rules.NpcFilter.Contains(quest.StartNPC?.NPCName ?? "")) return false;

            foreach (QuestTask task in quest.Tasks ?? Enumerable.Empty<QuestTask>())
            {
                foreach (QuestTaskMonsterDetails detail in task.MonsterDetails ?? Enumerable.Empty<QuestTaskMonsterDetails>())
                    if (detail?.Monster?.IsBoss == true && world.Level < BossLevelFor(detail.Monster, rules))
                        return false;

                // LEVEL-AWARE: at least one of this task's monsters must live somewhere this bot
                // may hunt. Checked per task - one impossible task makes the whole quest impossible.
                if (rules.Huntable != null && task.MonsterDetails != null && task.MonsterDetails.Count > 0 &&
                    !task.MonsterDetails.Any(d => d?.Monster != null && TargetMaps(d).Any(rules.Huntable)))
                    return false;
            }

            return true;
        }

        public bool HasBossTarget(QuestInfo quest) =>
            quest?.Tasks?.Any(t => t.MonsterDetails.Any(d => d.Monster?.IsBoss == true)) == true;

        public IEnumerable<QuestInfo> Acceptable(WorldModel world, int bossMinLevel) =>
            _quests.Where(q => CanAccept(q, world, bossMinLevel));

        public IEnumerable<QuestInfo> Acceptable(WorldModel world, QuestRules rules) =>
            _quests.Where(q => CanAccept(q, world, rules));

        /// <summary>Our log entries for these quests that are done and not yet handed in.</summary>
        public IEnumerable<ClientUserQuest> ReadyToHandIn(WorldModel world) =>
            world.Quests.Where(q => !q.Completed && q.Quest != null && _quests.Contains(q.Quest) &&
                                    AllTasksDone(q));

        /// <summary>
        /// What THIS NPC will take: the server accepts only quests in the open NPC's StartQuests and
        /// completes only those in its FinishQuests, and refuses anything else in silence.
        /// </summary>
        public (List<ClientUserQuest> HandIns, List<QuestInfo> Accepts) WorkFor(NPCInfo npc,
            WorldModel world, QuestRules rules)
        {
            List<ClientUserQuest> handIns = ReadyToHandIn(world)
                .Where(q => q.Quest.FinishNPC == npc).ToList();
            List<QuestInfo> accepts = _quests
                .Where(q => q.StartNPC == npc && CanAccept(q, world, rules)).ToList();
            return (handIns, accepts);
        }

        private static long Progress(ClientUserQuest quest, QuestTask task) =>
            quest.Tasks?.FirstOrDefault(t => t.TaskIndex == task.Index)?.Amount ?? 0;

        public static bool AllTasksDone(ClientUserQuest quest) =>
            quest?.Quest?.Tasks != null &&
            quest.Quest.Tasks.All(task => Progress(quest, task) >= task.Amount);

        /// <summary>Where a task detail's monster counts: its spawn maps, narrowed to the detail's map.</summary>
        public HashSet<int> TargetMaps(QuestTaskMonsterDetails detail)
        {
            _spawnMaps.TryGetValue(detail.Monster.Index, out HashSet<int> spawns);
            HashSet<int> maps = new HashSet<int>(spawns ?? new HashSet<int>());
            if (detail.Map != null) maps.IntersectWith(new[] { detail.Map.Index });
            return maps;
        }

        /// <summary>
        /// What is still to be killed (or gathered) for accepted, unfinished quests. With a huntable
        /// check, maps this bot may not hunt are dropped from each target.
        /// </summary>
        public List<QuestKillTarget> KillTargets(WorldModel world, Func<int, bool> huntable = null)
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
                        HashSet<int> maps = TargetMaps(detail);
                        if (huntable != null) maps.RemoveWhere(m => !huntable(m));

                        targets.Add(new QuestKillTarget
                        {
                            Quest = quest.Quest, Task = task, MonsterIndex = monster.Index,
                            MonsterName = monster.MonsterName ?? "monster", Maps = maps,
                            IsBoss = monster.IsBoss, Have = have, Need = task.Amount,
                            ItemIndex = task.Task == QuestTaskType.GainItem && task.ItemParameter != null
                                ? task.ItemParameter.Index : -1
                        });
                    }
                }
            }

            return targets;
        }

        /// <summary>
        /// A floor item that is the item of an accepted, unfinished gather task: picking it up
        /// credits the quest (the server deletes it, so it never takes a slot).
        /// </summary>
        public bool QuestItemWanted(ItemInfo info, WorldModel world)
        {
            if (info == null || world == null) return false;

            foreach (ClientUserQuest quest in world.Quests)
            {
                if (quest.Completed || quest.Quest?.Tasks == null || !_quests.Contains(quest.Quest)) continue;
                foreach (QuestTask task in quest.Quest.Tasks)
                    if (task.Task == QuestTaskType.GainItem && task.ItemParameter == info &&
                        Progress(quest, task) < task.Amount)
                        return true;
            }

            return false;
        }

        /// <summary>"7/10 Pig", "12/50 Skeletal Spine", or "talk to David", for the status page.</summary>
        public static string ProgressText(ClientUserQuest quest)
        {
            if (quest?.Quest?.Tasks == null) return "";
            if (quest.Quest.Tasks.Count == 0) return $"talk to {quest.Quest.FinishNPC?.NPCName ?? "?"}";
            return string.Join(", ", quest.Quest.Tasks.Select(t =>
                $"{Math.Min(Progress(quest, t), t.Amount)}/{t.Amount} " +
                (t.Task == QuestTaskType.GainItem && t.ItemParameter != null
                    ? t.ItemParameter.ItemName
                    : string.Join("/", t.MonsterDetails.Select(d => d.Monster?.MonsterName ?? "?")))));
        }
    }
}
