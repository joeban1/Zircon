using System;
using System.Collections.Generic;
using System.Globalization;

namespace MirBot
{
    /// <summary>What kind of value a setting holds, so the page can render the right control.</summary>
    public enum ConfigKind { Integer, Boolean }

    /// <summary>How a change to a setting reaches the running bot.</summary>
    public enum ConfigReach
    {
        /// <summary>Read through the shared config on every tick; an edit is live immediately.</summary>
        Live,

        /// <summary>Copied into another object; needs BotInstance.ReapplyConfig to take effect.</summary>
        Reapplied
    }

    /// <summary>One editable setting.</summary>
    public sealed record ConfigField
    {
        public string Key { get; init; } = "";
        public ConfigKind Kind { get; init; }
        public long Min { get; init; }
        public long Max { get; init; }
        public string Group { get; init; } = "";
        public string Note { get; init; } = "";
        public ConfigReach Reach { get; init; }

        /// <summary>Filled per request from the bot's own snapshot, never read live.</summary>
        public string Value { get; init; } = "";
    }

    /// <summary>
    /// The allow-list of settings the status page may change, and what a sane value looks like.
    ///
    /// An ALLOW-LIST, not a deny-list, and written out by hand rather than scraped from
    /// bot.ini.example. Three reasons, all learned the hard way:
    ///
    /// - Credentials and the server address must never be reachable from a page with no
    ///   authentication. A deny-list gets that wrong the first time somebody adds a field.
    /// - Several settings are consumed once at startup by BotHost.Prepare, from the FIRST bot's ini
    ///   only - TownMaps, DataPath, MapPath, MemoryPath, LevelBandSize, UseTeleportNPCs,
    ///   VersionPath. Editing those would appear to work and do nothing at all, which is worse than
    ///   refusing.
    /// - Parsing is not validation. "-40" parses perfectly well as HealAtPercent and would mean the
    ///   bot never drinks; "0" parses as AttackGiveUpSeconds and would mean it never stops trying.
    ///   Every field here carries the range that makes it meaningful.
    ///
    /// Reach records the other trap: a field that is COPIED somewhere at connect time keeps working
    /// from the old value until it is re-copied. Journey takes three, ScriptedBrain's loot rule
    /// takes five, and all eight are marked Reapplied so BotInstance knows to push them again.
    /// </summary>
    public static class ConfigSchema
    {
        private static readonly List<ConfigField> Fields = new List<ConfigField>
        {
            // --- staying alive -----------------------------------------------------------------
            F("HealAtPercent", 1, 99, "Survival", "drink at or below this HP%"),
            F("PanicHealPercent", 1, 99, "Survival", "below this, drink anything carried"),
            F("FleeAtPercent", 1, 99, "Survival", "disengage at or below this HP%"),
            F("EmergencyScrollAtPercent", 0, 99, "Survival", "scroll out below this, with no potions"),
            F("DrinkManaAtPercent", 0, 99, "Survival", "drink mana at or below this MP%"),
            F("HealPetAtPercent", 0, 99, "Survival", "cast Heal on our pet at or below this HP%"),
            F("ReviveAfterSeconds", 1, 600, "Survival", "wait this long before reviving"),

            // --- fighting ----------------------------------------------------------------------
            B("CastSpells", "Combat", "use spells at all"),
            F("CastRange", 1, 10, "Combat", "furthest tile a spell is offered at"),
            F("SpellManaFloorPercent", 0, 90, "Combat", "mana never spent on attack spells"),
            F("AggroRange", 1, 20, "Combat", "how far to look for a fight"),
            F("AttackPatience", 1, 60, "Combat", "swings before giving up on a target"),
            F("AttackGiveUpSeconds", 5, 300, "Combat", "seconds before abandoning a target"),
            F("PursuitPatience", 1, 60, "Combat", "steps chasing something that keeps moving"),
            B("KiteWhileCasting", "Combat", "back off to spell range when closed on"),
            F("KiteWhenCloserThan", 1, 10, "Combat", "start backing off inside this range"),
            F("KiteRetreatRange", 2, 12, "Combat", "range to retreat to"),
            F("DangerHitsToDeath", 1, 20, "Combat", "hits-to-death that marks a monster dangerous"),

            // --- looting -----------------------------------------------------------------------
            B("LootEnabled", "Loot", "pick things up"),
            F("LootRange", 1, 20, "Loot", "how far to walk for a drop"),
            F("HeavyWeightPercent", 10, 99, "Loot", "bag% above which we get fussy"),
            F("LootPoorGold", 0, 10000000, "Loot", "below this we are poor and take more", ConfigReach.Reapplied),
            F("LootRichGold", 0, 10000000, "Loot", "above this we are rich and take less", ConfigReach.Reapplied),
            F("LootGoldPerWeightPoor", 0, 100000, "Loot", "gold per weight worth taking when poor", ConfigReach.Reapplied),
            F("LootGoldPerWeightRich", 0, 100000, "Loot", "gold per weight worth taking when rich", ConfigReach.Reapplied),
            F("LootHeavyMultiplier", 1, 100, "Loot", "how much harsher a heavy bag is", ConfigReach.Reapplied),

            // --- town --------------------------------------------------------------------------
            F("TownAtWeightPercent", 10, 100, "Town", "bag% that sends us shopping"),
            F("TownAtFreeSlots", 0, 20, "Town", "free slots that send us shopping"),
            F("RestockAtPotionPercent", 0, 100, "Town", "% of the potion target that counts as short"),
            F("HealthPotionWeightPercent", 0, 90, "Town", "share of the bag budgeted for healing"),
            F("ManaPotionWeightPercent", 0, 90, "Town", "share of the bag budgeted for mana"),
            F("MinUsefulPotionBuy", 1, 100, "Town", "potions of a tier we must afford before downgrading"),
            F("PotionMaxGoldPercent", 0, 100, "Town", "most of the purse one restock may spend"),
            F("PotionGoldPerHealFactor", 0, 100, "Town", "times the cheapest gold-per-heal we will pay"),
            F("PotionMaxPoolMultiple", 0, 100, "Town", "full health bars one restock may buy"),
            F("PotionWeightPerHealFactor", 0, 100, "Town", "times the lightest weight-per-heal we will carry"),
            F("TownScrollReserve", 0, 20, "Town", "town scrolls always carried"),
            F("GoldReserve", 0, 10000000, "Town", "gold never spent on gear"),
            F("PoorGold", 0, 10000000, "Town", "below this a bot walks only - no paid teleports"),
            F("TorchGoldFloor", 0, 100000, "Town", "gold that must survive replacing a torch"),
            B("RepairEnabled", "Town", "repair worn gear"),
            F("RepairAtDurability", 1, 100, "Town", "repair at or below this durability%"),
            F("SpecialRepairMinimumLevel", 0, 250, "Town", "ordinary repair below this level"),
            B("BuyGear", "Town", "buy upgrades"),
            B("BuyBooks", "Town", "buy skill books"),
            B("BuyReagents", "Town", "buy amulets and poisons"),
            F("ReagentReserve", 0, 5000, "Town", "reagents to stock up to"),
            F("ReagentGoldPercent", 0, 100, "Town", "most of our gold one reagent order may cost"),
            B("KeepTorchLit", "Town", "replace a burnt-out torch"),

            // --- getting about -----------------------------------------------------------------
            F("TeleportGoldFloor", 0, 10000000, "Travel", "gold a fare must leave behind",
              ConfigReach.Reapplied),
            F("TeleportMaxGoldPercent", 0, 100, "Travel", "most of our gold one fare may cost",
              ConfigReach.Reapplied),
            F("VendorTalkRange", 1, 10, "Travel", "how close to stand to talk", ConfigReach.Reapplied),
            F("TravelGoldPerHop", 0, 10000000, "Travel", "gold budgeted per map hop"),
            F("TravelGoldPerFreeHop", 0, 10000000, "Travel", "gold budgeted per WALKED map hop"),
            F("HuntingChoices", 1, 10, "Travel", "how many known maps to choose between"),
            F("HuntingDeathPenaltyPercent", 0, 300, "Travel", "baseline death discount for hunting maps"),
            F("HuntingPickWeightPower", 0, 4, "Travel", "0 uniform; larger favours the strongest map"),
            F("BookHuntChancePercent", 0, 100, "Travel", "chance to seek a wanted book when ordinary maps also qualify"),
            F("LossBookHuntChancePercent", 0, 100, "Travel", "book-hunt chance while sustained gold loss is active"),
            F("MaxConsecutiveBookHunts", 1, 10, "Travel", "book-priority choices before an ordinary hunt is forced"),
            F("TownHuntLevelGap", 0, 100, "Travel", "defer shopping towns this many levels below us; 0 off"),
            B("AoeEnabled", "Combat", "aim learned area spells at monster groups"),
            F("AoeMinimumTargets", 1, 10, "Combat", "distinct hostiles required before an area cast"),
            F("LossWatchHours", 1, 48, "Travel", "hours of adjusted gold history to watch"),
            F("LossWatchDropGold", 0, 10000000, "Travel", "operating loss that enables safer map ranking; 0 off"),
            F("LossWatchRecoverGold", 0, 10000000, "Travel", "gain needed after a completed town trip to exit"),
            F("LossDeathPenaltyPercent", 0, 300, "Travel", "death-penalty floor during sustained loss"),
            F("LossWatchMaxHours", 1, 72, "Travel", "hard cap on loss ranking before cooldown"),
            F("ExploreUntilMapsKnown", 0, 100, "Travel", "explore until this many maps are measured"),
            F("ExploreChancePercent", 0, 100, "Travel", "chance of exploring anyway"),
            F("UpgradeExploreChancePercent", 0, 100, "Travel", "explore chance with a safe unmeasured gear upgrade"),
            F("GearHuntBonusPercent", 0, 100, "Travel", "maximum score bonus for a meaningful gear upgrade"),
            F("MaxConsecutiveGearHunts", 1, 10, "Travel", "gear-priority choices before an ordinary hunt"),
            F("ExploreLevelsAbove", 0, 30, "Travel", "levels above ours a map may be"),
            F("MinimumSampleMinutes", 1, 120, "Travel", "shortest window worth banking"),
            F("ExperienceSampleMinutes", 1, 120, "Travel", "length of a full sample window"),

            // --- moving ------------------------------------------------------------------------
            B("AllowRunning", "Movement", "run rather than walk"),
            B("AvoidMapExits", "Movement", "steer away from doorways while hunting"),
            F("DoorClearance", 1, 40, "Movement", "tiles to keep clear of a doorway"),
            F("DoorClearSeconds", 1, 120, "Movement", "how long to keep clearing one"),
            B("RoamToDestinations", "Movement", "wander to a chosen spot rather than a direction"),
            F("RoamRadius", 1, 100, "Movement", "how far a wander target may be"),
            F("SweepAfterIdleSeconds", 0, 600, "Movement", "idle seconds before heading for the next floor"),
            F("UnproductiveMinutes", 0, 600, "Travel", "minutes with no experience before leaving the map"),
            F("FightThroughRange", 0, 10, "Travel", "while travelling, stop and fight anything this close"),
            F("FightThroughSeconds", 0, 600, "Travel", "seconds of fighting a leg may absorb before the stall watchdog resumes"),
            F("StuckSeconds", 0, 600, "Movement", "seconds pinned on one cell before breaking state"),
            F("HuntLevelsBelow", 0, 100, "Travel", "skip grounds whose typical monster is this far below us"),
            F("RecoveryGold", 0, 10000000, "Travel", "below this, enter latched poverty recovery"),
            F("RecoveryExitGold", 0, 10000000, "Travel", "post-restock balance that ends recovery"),
            F("RecoveryCaveMinimumLevel", 0, 250, "Travel", "minimum level for recovery money caves"),
            F("RecoveryCaveSupplyPercent", 0, 100, "Travel", "potion target required for a recovery money cave"),
            F("MinPotionRestore", 0, 10000, "Potions", "below this restored, a consumable is loot not medicine"),
            F("ResyncAfterSellRefusals", 0, 100, "Inventory", "voided sell orders before relogging to resync"),
            F("ResyncWindowMinutes", 1, 1440, "Inventory", "window for counting those refusals"),
            F("HuntingHalfLifeHours", 0, 168, "Travel", "hours for a hunting measurement to lose half its weight"),
            F("GatherSafeRange", 0, 20, "Combat", "loot and butcher before a new fight when nothing is this close"),
            F("BookHuntBonusPercent", 0, 1000, "Travel", "score bonus per drop-only skill book a map supplies"),
            B("SweepEntersNextFloor", "Movement", "walk through to the next floor on arrival"),
            F("RoamRetargetSeconds", 1, 300, "Movement", "how long to keep one"),
            F("ReturnWithin", 1, 60, "Movement", "close enough to count as back at the hunt")
        };

        private static ConfigField F(string key, long min, long max, string group, string note,
            ConfigReach reach = ConfigReach.Live) =>
            new ConfigField { Key = key, Kind = ConfigKind.Integer, Min = min, Max = max,
                              Group = group, Note = note, Reach = reach };

        private static ConfigField B(string key, string group, string note,
            ConfigReach reach = ConfigReach.Live) =>
            new ConfigField { Key = key, Kind = ConfigKind.Boolean, Min = 0, Max = 1,
                              Group = group, Note = note, Reach = reach };

        public static IReadOnlyList<ConfigField> All => Fields;

        public static ConfigField Find(string key)
        {
            foreach (ConfigField field in Fields)
                if (string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase)) return field;

            return null;
        }

        /// <summary>
        /// Is this a value we are willing to apply?
        ///
        /// Checked in the HTTP endpoint, BEFORE the command is queued, because the queue cannot
        /// answer back: StatusServer replies 202 the moment TryEnqueue succeeds, so anything only
        /// discovered later on the bot thread would be reported to the operator as a success.
        /// </summary>
        public static bool Validate(string key, string value, out string error)
        {
            error = null;

            ConfigField field = Find(key);

            if (field == null)
            {
                error = $"{key} is not an editable setting";
                return false;
            }

            if (field.Kind == ConfigKind.Boolean)
            {
                if (bool.TryParse(value, out _)) return true;

                error = $"{field.Key} must be true or false";
                return false;
            }

            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out long parsed))
            {
                error = $"{field.Key} must be a whole number";
                return false;
            }

            if (parsed < field.Min || parsed > field.Max)
            {
                error = $"{field.Key} must be between {field.Min} and {field.Max}";
                return false;
            }

            return true;
        }
    }
}
