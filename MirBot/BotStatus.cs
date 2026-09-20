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

    public sealed record EquipmentStatus(
        string Slot,
        string Name,
        int Durability,
        int MaxDurability,
        bool Broken,
        bool Worn);

    /// <summary>Flags matter: a Locked or Bound item cannot simply be sold.</summary>
    public sealed record ItemStatus(int Slot, string Name, long Count, string Type,
        string Flags, bool CanSell);

    public sealed record HistoryStatus(
        string Action,
        string Subject,
        string Detail,
        int Count,
        string LastAt);

    public sealed record BotStatus(
        string Id,
        string State,
        string CurrentAction,
        string CurrentSubject,
        string CurrentDetail,
        string ExitReason,
        string LastError,

        string CharacterName,
        string Class,
        int Level,
        bool Dead,

        int Health,
        int MaxHealth,
        int HealthPercent,
        int Mana,
        int MaxMana,
        int ManaPercent,

        // Experience and gold are STRINGS on purpose: System.Text.Json writes decimal unquoted and
        // JavaScript's JSON.parse turns it into a double, losing precision above 2^53 - which Mir
        // experience crosses at higher levels. The percentage is computed here instead.
        string Experience,
        string MaxExperience,
        double? ExperiencePercent,   // null = unknown or max level; render as a dash, not 0%
        bool AtMaxLevel,
        string Gold,

        int MapIndex,
        string MapName,
        int X,
        int Y,
        bool InSafeZone,

        int BagWeight,
        int MaxBagWeight,
        int BagPercent,

        string TripPhase,
        string TripStatus,

        int UptimeSeconds,
        int Decisions,
        int Resyncs,
        int Detours,
        int DroppedPackets,
        int KnownMagics,

        // The overnight counters. Each one is a behaviour that used to be invisible until
        // somebody read a log file: casts landing, fights given up on, monsters walked past,
        // map cells learned the hard way.
        int Casts,
        int FightsAbandoned,
        int DangerAvoided,
        int LearnedBlockedCells,
        string BankStatus,

        IReadOnlyList<EquipmentStatus> Equipment,
        IReadOnlyList<ItemStatus> Inventory,
        IReadOnlyList<HistoryStatus> History)
    {
        /// <summary>A card for a bot that has never run, so the page is never blank.</summary>
        public static BotStatus Idle(string id, string state) => new BotStatus(
            id, state, "", "", "", null, null,
            "", "", 0, false,
            0, 0, 0, 0, 0, 0,
            "0", "0", null, false, "0",
            0, "", 0, 0, false,
            0, 0, 0,
            "", "",
            0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, "",
            Array.Empty<EquipmentStatus>(),
            Array.Empty<ItemStatus>(),
            Array.Empty<HistoryStatus>());
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
