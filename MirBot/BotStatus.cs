using System;
using System.Collections.Generic;

namespace MirBot
{
    /// <summary>One entry in the status page's travel destination list.</summary>
    public sealed record MapChoice(int Index, string Name);

    // Snapshot types.
    //
    // RULE: everything reachable from BotStatus is a value type, a string, or another record here.
    // No ClientUserItem, WorldObject, ItemInfo, Stats, Decision or NPCPage - those are mutated in
    // place on the bot thread while the web thread would be reading them. Two specific traps:
    // Backpack.Carried/Stored and WorldModel.Objects hand out the LIVE dictionaries, so leaking one
    // means the first Set/Remove on the bot thread throws "collection was modified" on the web
    // thread, mid-response.
    //
    // Lists are materialised on the bot thread before publication - never left lazy.

    /// <summary>
    /// One worn item.
    ///
    /// Carries the full ItemStatus rather than just a name, so the page can show a worn item the
    /// same way it shows a carried one. Matching worn items back to the bag by NAME - the only key
    /// the two shared before - worked for exactly those items that happened to be duplicated in
    /// the bag, which is almost none of them.
    /// </summary>
    public sealed record EquipmentStatus(
        string Slot,
        string Name,
        int Durability,
        int MaxDurability,
        bool Broken,
        bool Worn)
    {
        public ItemStatus Item { get; init; }
    }

    /// <summary>One stat line on an item, already named and signed for display.</summary>
    public sealed record ItemStat(string Name, int Amount);

    /// <summary>
    /// One summoned or tamed creature, as the bot currently sees it.
    ///
    /// Ownership is the only signal the protocol carries - S.ObjectMonster.PetOwner, a plain
    /// string - so a pet is simply a monster whose owner name matches ours. There is no packet
    /// that reports a pet's target, so what it is fighting cannot be shown; distance and health
    /// are what the world model genuinely knows.
    /// </summary>
    /// <summary>
    /// One learnt skill: what it is, how far it has been trained, and whether the bot can use it.
    ///
    /// Skill LEVEL is not character level. A magic trains 1 -> 2 -> 3 on its own experience track
    /// (MagicInfo.Experience1/2/3), gated by character level (NeedLevel1/2/3), and the two were
    /// impossible to tell apart from the outside because neither was displayed at all.
    /// </summary>
    public sealed record SkillStatus
    {
        public string Name { get; init; } = "";
        public string School { get; init; } = "";

        /// <summary>1-3. The skill's own level, not the character's.</summary>
        public int Level { get; init; }

        public long Experience { get; init; }

        /// <summary>Experience needed for the next skill level; 0 when there is none.</summary>
        public long NextExperience { get; init; }

        /// <summary>Progress toward the next skill level, 0-100. 100 when maxed.</summary>
        public int Percent { get; init; }

        /// <summary>Character level needed before the next skill level can be trained; 0 if none.</summary>
        public int NeedLevel { get; init; }

        /// <summary>The character is high enough level to use this at all.</summary>
        public bool Usable { get; init; }

        /// <summary>The bot actively drives this skill - casts it, arms it, or names it on a swing.</summary>
        public bool Castable { get; init; }

        /// <summary>
        /// "active", "passive" or "unused".
        ///
        /// Three states rather than two because a passive is neither. A bool reported a warrior's
        /// Swordsmanship as unused alongside a wizard's Fire Wall, and only one of those is a
        /// missing feature.
        /// </summary>
        public string Use { get; init; } = "";

        /// <summary>Short reason when the bot does not drive it, for the tooltip.</summary>
        public string Why { get; init; } = "";
    }

    /// <summary>A buff the server says is on the character right now.</summary>
    public sealed record BuffStatus
    {
        public string Name { get; init; } = "";
        public bool Permanent { get; init; }

        /// <summary>Seconds left now (counted down locally between S.BuffTime); null if permanent.</summary>
        public int? RemainingSeconds { get; init; }

        /// <summary>A timed item buff pauses in a safe zone.</summary>
        public bool Paused { get; init; }
        public IReadOnlyList<ItemStat> Stats { get; init; } = Array.Empty<ItemStat>();
    }

    /// <summary>One quest in the character's log.</summary>
    public sealed record QuestStatus
    {
        public string Name { get; init; } = "";

        /// <summary>"7/10 Pig".</summary>
        public string Progress { get; init; } = "";
        public bool Completed { get; init; }
        public bool ReadyToHandIn { get; init; }
        public bool Daily { get; init; }
    }

    /// <summary>
    /// One looted item, recovered from the log. See BotHost.LootSearch.
    /// </summary>
    public sealed record LootRow
    {
        public string Time { get; init; } = "";
        public string Bot { get; init; } = "";
        public string Character { get; init; } = "";
        public string Class { get; init; } = "";
        public string Item { get; init; } = "";
        public int Level { get; init; }
        public string MapName { get; init; } = "";
        public int MapIndex { get; init; }
        public int X { get; init; }
        public int Y { get; init; }
    }

    public sealed record PetStatus
    {
        public string Name { get; init; } = "";
        public int Level { get; init; }
        public int Health { get; init; }
        public int MaxHealth { get; init; }
        public int HealthPercent { get; init; }
        public int Distance { get; init; }
    }

    /// <summary>
    /// One carried, worn or stored item, with enough to render the tooltip the client shows.
    ///
    /// Flattened on the bot thread, as the rule at the top of this file demands: ItemInfo.Stats is
    /// a live SortedDictionary owned by the game database and ClientUserItem.AddedStats is mutated
    /// in place when the server re-sends an item, so neither may be handed to the web thread.
    ///
    /// Image is the index into the client's item sprite archive. Carried whether or not anything
    /// can decode that archive yet - the page falls back to a coloured tile keyed on Type, and
    /// picks up real artwork the day the icons exist without anything here changing.
    /// </summary>
    public sealed record ItemStatus
    {
        public int Slot { get; init; }
        public string Name { get; init; } = "";
        public long Count { get; init; }
        public string Type { get; init; } = "";
        public string Flags { get; init; } = "";
        public bool CanSell { get; init; }

        public int Image { get; init; }
        public int Durability { get; init; }
        public int MaxDurability { get; init; }
        public int Weight { get; init; }
        public long Price { get; init; }

        /// <summary>What the item demands of its wearer, as the client phrases it.</summary>
        public string Requirement { get; init; } = "";

        public string Description { get; init; } = "";

        /// <summary>The item's own stats, from the database.</summary>
        public IReadOnlyList<ItemStat> Stats { get; init; } = Array.Empty<ItemStat>();

        /// <summary>Extra stats rolled onto this particular instance.</summary>
        public IReadOnlyList<ItemStat> Added { get; init; } = Array.Empty<ItemStat>();

        /// <summary>Gems socketed into it, by name.</summary>
        public IReadOnlyList<string> Sockets { get; init; } = Array.Empty<string>();
    }

    public sealed record HistoryStatus(
        string Action,
        string Subject,
        string Detail,
        int Count,
        string LastAt);

    /// <summary>
    /// One bot, frozen at a moment, for the web thread to serialise at its leisure.
    ///
    /// INIT PROPERTIES, NOT A POSITIONAL RECORD. It used to be positional with 48 members and an
    /// Idle() factory that restated every one of them by position. Adding a field meant editing
    /// two long argument lists in step, and getting them out of step would still COMPILE - two
    /// adjacent ints or strings silently swapping places, with nothing but a wrong number on a web
    /// page to show for it. Named assignment makes that class of mistake impossible, and lets
    /// Idle() say only what is actually known about a bot that has never run.
    ///
    /// Every member keeps its default here so neither constructor has to supply it.
    /// </summary>
    public sealed record BotStatus
    {
        public string Id { get; init; } = "";
        public string State { get; init; } = "";
        public string CurrentAction { get; init; } = "";
        public string CurrentSubject { get; init; } = "";
        public string CurrentDetail { get; init; } = "";
        public string ExitReason { get; init; }
        public string LastError { get; init; }

        public string CharacterName { get; init; } = "";
        public string Class { get; init; } = "";
        public int Level { get; init; }
        public bool Dead { get; init; }

        public int Health { get; init; }
        public int MaxHealth { get; init; }
        public int HealthPercent { get; init; }
        public int Mana { get; init; }
        public int MaxMana { get; init; }
        public int ManaPercent { get; init; }

        // Experience and gold are STRINGS on purpose: System.Text.Json writes decimal unquoted and
        // JavaScript's JSON.parse turns it into a double, losing precision above 2^53 - which Mir
        // experience crosses at higher levels. The percentage is computed here instead.
        public string Experience { get; init; } = "0";
        public string MaxExperience { get; init; } = "0";

        /// <summary>null = unknown or max level; render as a dash, not 0%.</summary>
        public double? ExperiencePercent { get; init; }
        public bool AtMaxLevel { get; init; }
        public string XpRatePerHour { get; init; }
        public int XpCoverageSeconds { get; init; }
        public long? EstimatedNextLevelSeconds { get; init; }
        public string Gold { get; init; } = "0";

        public int MapIndex { get; init; }
        public string MapName { get; init; } = "";
        public int X { get; init; }
        public int Y { get; init; }
        public bool InSafeZone { get; init; }

        /// <summary>
        /// The last committed hunting-ground choice, not the map currently crossed on a journey
        /// or a temporary town stop. Empty until this bot has made a choice in this host run.
        /// </summary>
        public string FarmingDestinationName { get; init; } = "";
        public string FarmingDestinationReason { get; init; } = "";

        /// <summary>
        /// Where the bot is walking to on this map, or null when it is not walking anywhere.
        ///
        /// Taken from the live decision first and the roam target second: the decision covers town
        /// errands and travel legs, the roam target covers ordinary wandering, and between them
        /// they answer the question the map is actually asked - "why is it over there?".
        /// </summary>
        public int? DestX { get; init; }
        public int? DestY { get; init; }

        /// <summary>
        /// The A* route the bot is walking, flattened x,y pairs from the next step to the goal,
        /// and what it is for: town, travel, target, roam, loot or move. Empty when not walking.
        /// </summary>
        public int[] Route { get; init; } = Array.Empty<int>();
        public string RouteKind { get; init; } = "";

        /// <summary>none | local | map-wide. Fallback sweep/random roaming report none.</summary>
        public string ExplorationMode { get; init; } = "none";
        public int ExplorationVisitedSectors { get; init; }
        public int ExplorationTotalSectors { get; init; }

        /// <summary>Null means either no exploration target or a sector never visited before.</summary>
        public string ExplorationTargetLastVisitedUtc { get; init; }

        public int BagWeight { get; init; }
        public int MaxBagWeight { get; init; }
        public int BagPercent { get; init; }

        public string TripPhase { get; init; } = "";
        public string TripStatus { get; init; } = "";

        /// <summary>Which trip this is, since login. Lets the page work out trips per hour.</summary>
        public int TripSequence { get; init; }

        /// <summary>
        /// hunting | town | travel | offline. Computed on the bot thread, because only it can see
        /// TownTrip and Journey.
        /// </summary>
        public string Activity { get; init; } = "offline";

        /// <summary>
        /// Seconds since experience last arrived, or null when none has this session.
        ///
        /// Null is NOT zero and must not be rendered as a fresh kill, nor as a stale one: a bot
        /// that has just logged in has earned nothing yet and deserves a dash, not a red light
        /// ten minutes later.
        /// </summary>
        public int? SecondsSinceGain { get; init; }

        /// <summary>
        /// Seconds since the character entered the world, or null if it has not.
        ///
        /// What the idle clock counts from before the first kill. Kept SEPARATE from
        /// SecondsSinceGain rather than folded into it, so the page can still tell the two apart
        /// and say "no kill yet" instead of claiming a kill that never happened.
        /// </summary>
        public int? SecondsInGame { get; init; }

        /// <summary>Whole sell orders the server refused this connection. Non-zero means the
        /// bag listing may not match what the character is really carrying.</summary>
        public int SellRefusals { get; init; }

        /// <summary>
        /// The editable settings and their CURRENT values, copied on the bot thread.
        ///
        /// Copied, not read live. BotConfig is a shared mutable object and this plan makes the bot
        /// thread write to it; the web thread reading the same fields to render a form would be a
        /// plain data race. It rides along on the snapshot like everything else.
        /// </summary>
        public IReadOnlyList<ConfigField> Config { get; init; } = Array.Empty<ConfigField>();

        /// <summary>What happened to the last setting we were asked to change.</summary>
        public string ConfigResult { get; init; } = "";

        public int UptimeSeconds { get; init; }
        public int Decisions { get; init; }
        public int Resyncs { get; init; }
        public int Detours { get; init; }
        public int DroppedPackets { get; init; }
        public int KnownMagics { get; init; }

        // The overnight counters. Each one is a behaviour that used to be invisible until
        // somebody read a log file: casts landing, fights given up on, monsters walked past,
        // map cells learned the hard way.
        public int Casts { get; init; }
        public int FightsAbandoned { get; init; }
        public int DangerAvoided { get; init; }
        public int LearnedBlockedCells { get; init; }
        public int DoorwaysCleared { get; init; }
        public string BankStatus { get; init; } = "";

        // Why the bot is NOT doing something. These are the strings TownTrip already wrote for
        // itself and nobody could read: every one of them existed only in the log file, which is
        // why a bot that walked to the shops twenty-four times without selling anything went
        // unnoticed for two hours. Empty means "no opinion", and the page omits it.
        public string SellDiagnostic { get; init; } = "";
        public string WeightDiagnostic { get; init; } = "";
        public string ReagentDiagnostic { get; init; } = "";
        public string BookDiagnostic { get; init; } = "";
        public string GearDiagnostic { get; init; } = "";
        public string SupplyDiagnostic { get; init; } = "";
        public string RepairDiagnostic { get; init; } = "";
        public string MoneyDiagnostic { get; init; } = "";

        public IReadOnlyList<EquipmentStatus> Equipment { get; init; } =
            Array.Empty<EquipmentStatus>();

        /// <summary>The standing order given to pets: Both, Move, Attack, PvP or None.</summary>
        public string PetMode { get; init; } = "";

        public IReadOnlyList<PetStatus> Pets { get; init; } = Array.Empty<PetStatus>();

        public IReadOnlyList<SkillStatus> Skills { get; init; } = Array.Empty<SkillStatus>();
        public IReadOnlyList<BuffStatus> Buffs { get; init; } = Array.Empty<BuffStatus>();
        public IReadOnlyList<QuestStatus> Quests { get; init; } = Array.Empty<QuestStatus>();

        /// <summary>The quest errand's current step, empty when it is not running.</summary>
        public string QuestStatusText { get; init; } = "";

        /// <summary>Account Hunt Gold - the game store currency.</summary>
        public long HuntGold { get; init; }

        /// <summary>What the game store step is buying or saving for.</summary>
        public string StoreStatusText { get; init; } = "";
        public IReadOnlyList<ItemStatus> Inventory { get; init; } = Array.Empty<ItemStatus>();
        public IReadOnlyList<ItemStatus> Storage { get; init; } = Array.Empty<ItemStatus>();
        public IReadOnlyList<HistoryStatus> History { get; init; } = Array.Empty<HistoryStatus>();

        /// <summary>A card for a bot that has never run, so the page is never blank.</summary>
        public static BotStatus Idle(string id, string state) =>
            new BotStatus { Id = id, State = state };
    }

    /// <summary>
    /// One hunting-memory record, copied out of the bank for the page.
    ///
    /// A separate type from HuntingEntry on purpose. HuntingEntry is mutable, lives in a shared
    /// bank and is written by four bot threads; this is a frozen copy with the derived numbers
    /// already computed, so the web thread never has to call a method on live state.
    /// </summary>
    public sealed record HuntingRow
    {
        public int MapIndex { get; init; }
        public string MapName { get; init; } = "";
        public string Class { get; init; } = "";
        public int LevelBand { get; init; }
        public int Level { get; init; }
        public double AveragePerHour { get; init; }
        public double BestPerHour { get; init; }
        public double LastPerHour { get; init; }
        public int Samples { get; init; }
        public double HoursSampled { get; init; }
        public int Deaths { get; init; }

        /// <summary>The ranking number: mean rate discounted by deaths.</summary>
        public double Score { get; init; }

        /// <summary>True when this map has killed us repeatedly and taught us nothing.</summary>
        public bool Lethal { get; init; }
        public string UpdatedUtc { get; init; } = "";
    }

    /// <summary>One death, as it happened. The counts in HuntingMemory say how many; this says
    /// which, where and to what.</summary>
    public sealed record DeathRow
    {
        public string Bot { get; init; } = "";
        public string Character { get; init; } = "";
        public string Class { get; init; } = "";
        public int Level { get; init; }
        public int MapIndex { get; init; }
        public string MapName { get; init; } = "";
        public string Killer { get; init; } = "";

        /// <summary>A string, like every other gold figure here - see BotStatus.</summary>
        public string Gold { get; init; } = "0";
        public int X { get; init; }
        public int Y { get; init; }
        public string Utc { get; init; } = "";
    }

    /// <summary>One point on a bot's gold history.</summary>
    public sealed record GoldPoint
    {
        public string Bot { get; init; } = "";
        public string Utc { get; init; } = "";
        public string Gold { get; init; } = "0";
        public long CapitalSpent { get; init; }
        public bool HasCapitalSpent { get; init; }
    }

    /// <summary>
    /// A map's walkability, for drawing.
    ///
    /// Mask is one bit per cell, row-major, base64. A grid never changes once loaded, so this is
    /// built once and served with a long cache - keyed by Version, which is derived from the file
    /// itself so a changed map cannot be served from a stale cache for a year.
    /// </summary>
    public sealed record MapMask
    {
        public int Index { get; init; }
        public string Name { get; init; } = "";
        public int Width { get; init; }
        public int Height { get; init; }
        public string Version { get; init; } = "";
        public string Mask { get; init; } = "";

        /// <summary>Walk-on exits to other maps (cave stairs, gates), one per movement region.</summary>
        public List<MapDoor> Doors { get; init; } = new List<MapDoor>();
    }

    /// <summary>One exit region: its centre cell, destination and size in cells.</summary>
    public sealed record MapDoor
    {
        public int X { get; init; }
        public int Y { get; init; }
        public string To { get; init; } = "";
        public int Cells { get; init; }
    }

    /// <summary>
    /// One setting as it stands across ALL bots.
    ///
    /// Settings are edited for the whole host rather than per bot - four characters sharing one
    /// operator's intent, not four independent configurations. But they are STORED per bot, in
    /// four ini files, so they can still disagree: an earlier per-bot edit, a hand-edited file, or
    /// a bot that was offline when a change went out. Mixed says so rather than picking one of
    /// them and quietly presenting it as the truth.
    /// </summary>
    public sealed record HostConfigField
    {
        public string Key { get; init; } = "";
        public ConfigKind Kind { get; init; }
        public long Min { get; init; }
        public long Max { get; init; }
        public string Group { get; init; } = "";
        public string Note { get; init; } = "";
        public ConfigReach Reach { get; init; }

        /// <summary>The shared value, or the first bot's when they disagree.</summary>
        public string Value { get; init; } = "";

        /// <summary>True when the bots do not all agree.</summary>
        public bool Mixed { get; init; }

        /// <summary>Per-bot values, only when Mixed.</summary>
        public IReadOnlyList<string> PerBot { get; init; } = Array.Empty<string>();
    }

    public sealed record HostStatus(
        string GeneratedAt,
        int UptimeSeconds,
        string DatabaseVersion,
        string Server,
        int VendorPages,
        int MagicCount,
        int MonsterCount,
        IReadOnlyList<BotStatus> Bots);
}
