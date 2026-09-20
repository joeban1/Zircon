using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace MirBot
{
    // Plain key=value config. Credentials live here and nowhere else - this file is
    // gitignored, and nothing in it is ever sent anywhere except the game server.
    public sealed class BotConfig
    {
        public string ServerAddress = "192.168.1.108";
        public int ServerPort = 7000;

        public string EMailAddress = "";
        public string Password = "";
        public string Language = "English";

        // Folder holding the bot's OWN copy of System.db. Never point this at the client's
        // live Data folder - Session.Initialize can write migrations.
        public string DataPath = "Data";

        // The server compares SHA256 of its configured Zircon.dll against what we send.
        // Prefer hashing the real file; fall back to a pinned hex hash when the file is
        // not reachable from wherever the bot runs.
        public string VersionPath = "";
        public string ClientHashHex = "";

        // Which character to play. Empty means "the first one on the account".
        public string CharacterName = "";

        // Set true only when deliberately registering a new bot account. The server
        // auto-activates accounts created from the server machine's own IP; from any
        // other IP it will try to send an activation email instead.
        public bool CreateAccountIfMissing = false;
        public MirClassChoice NewCharacterClass = MirClassChoice.Warrior;

        public TimeSpan TimeOut = TimeSpan.FromSeconds(20);

        // Scripted brain thresholds.
        public int HealAtPercent = 60;   // drink a potion at or below this HP%
        public int FleeAtPercent = 25;   // disengage at or below this HP%
        public int AggroRange = 8;       // how far to look for a monster worth engaging
        public bool AllowRunning = true; // run (2 tiles) instead of walking when the way is clear
        public bool LootEnabled = true;
        public int LootRange = 6;        // how far to detour for a dropped item
        public int HeavyWeightPercent = 70;  // above this, only loot consumables and upgrades
        public int TownAtWeightPercent = 90; // above this, town-teleport out if a scroll is carried
        // Reserves work both ways: below them the bot buys, above them it sells.
        public int HealthPotionReserve = 10; // always carry this many healing potions
        // Out of potions and below this much health: scroll out rather than die. 0 disables.
        public int EmergencyScrollAtPercent = 20;
        // Share of the bag set aside for potions. The flat reserves above become floors:
        // a level 40 warrior with a big bag should not carry a level 1 character's ten.
        public int HealthPotionWeightPercent = 25;
        public int ManaPotionWeightPercent = 10;
        // Buy the biggest potion that heals at least this share of the health pool, when
        // one is sold and affordable. Below it, a potion is mostly bag weight.
        public int PotionHealPercentTarget = 25;
        public int ManaPotionReserve = 15;   // same for mana potions
        public int TownScrollReserve = 3;    // always carry this many town teleport scrolls
        public bool KeepTorchLit = true;     // replace the torch when the slot is empty
        // Clear the Locked flag on surplus consumables so they can actually be sold.
        public bool UnlockToSell = true;
        // The ONLY maps a town trip will shop on. Comma separated map descriptions.
        // Empty means anywhere, which is how the bot once decided to restock on
        // Infernal Island because one NPC there sold both scrolls and potions.
        public string TownMaps = "Bichon Town,Banya Village";
        // No vendor is named: VendorDirectory derives who buys what from System.db.
        // NOTE: ScrollIfFurtherThan was removed. It decided whether to scroll to shorten a walk
        // to a vendor on the SAME map - but a town scroll does not shorten a walk, it teleports to
        // the character's bind point, which is a different town whenever the bot is hunting away
        // from home. The trip is now planned for the map it lands on, and a scroll is used only
        // when the current map cannot serve the trip at all, so distance no longer decides it.
        public int ReturnWithin = 6;          // close enough to the old hunting spot
        public int DetourSteps = 4;           // steps to commit to when walking around an obstacle
        // The client's Map folder. With it the bot pathfinds; without it it steers blind.
        public string MapPath = "";
        // Where the learned memory banks live. Relative paths are beside the executable.
        public string MemoryPath = "memory";
        public int ExperienceSampleMinutes = 15;  // window for a map's experience-per-hour
        // A part-finished window is recorded rather than binned when we leave a map, provided it
        // ran at least this long. The window stays at fifteen minutes deliberately - shortening it
        // would have been the obvious fix and the wrong one, because short windows are noisier and
        // the ranking used to keep the BEST sample, so more windows meant more chances to roll a
        // freak high one. Closing the window on departure captures the same data without adding
        // any noise: a warrior spent 13m21s in Deserted Mine and had all of it thrown away for
        // being 99 seconds short.
        public int MinimumSampleMinutes = 4;
        // Maps worth trying first, in order, before falling back to a random reachable one.
        // Comma separated map descriptions, e.g. "Deserted Mine Lv 1,Ant Cave North".
        public string PreferredMaps = "";
        // Experience rates are recorded per band of levels rather than per exact level, so a
        // measurement survives levelling up. 1 reverts to exact-level records.
        public int LevelBandSize = 5;
        // Look for somewhere better to hunt when a town trip finishes.
        public bool AutoTravel = false;

        // --- Choosing where to hunt ------------------------------------------------------
        // Pick at random from this many of the best-scoring maps rather than always the top
        // one. Straight from the Mir 2 agents, and it is what stops a single early sample
        // deciding where the bot lives for the rest of its career.
        public int HuntingChoices = 3;
        // Keep exploring until this many maps have a usable measurement at the current class
        // and level band; only then start exploiting what is known.
        //
        // The Mir 2 agents instead explore on a flat 1-in-20 roll, which is right for them:
        // hundreds of agents share one memory, so that fires constantly. We reconsider only
        // at the end of a town trip - the warrior had FOUR decision points in five hours - so
        // 1-in-20 would mean one exploration a day. Coverage self-tunes and needs no guess.
        public int ExploreUntilMapsKnown = 4;
        // Once coverage is met, still explore this often, so a better map found later is not
        // locked out forever.
        public int ExploreChancePercent = 15;
        // Do not explore a map whose TYPICAL monster is more than this far above us. Checked
        // against System.db, because the learned danger model only knows maps that have
        // already hurt us and is therefore silent about anywhere new.
        public int ExploreLevelsAbove = 3;

        // --- Travelling and gold ---------------------------------------------------------
        // Never set off on a journey without enough gold to restock on arrival. A bot that
        // lands three maps from home with an empty purse cannot buy potions, cannot buy a way
        // back, and dies. Scaled per map transition, since each hop is another map to cross on
        // foot if it goes wrong.
        public long TravelGoldPerHop = 3000;
        // Below this much gold, hunt only on the maps named in TownMaps. Poverty is the one
        // state where wandering is actively harmful: it cannot buy its way out of trouble.
        //
        // Set well above "can afford one potion" on purpose. The bar is not survival for the next
        // ten minutes, it is being able to absorb the ordinary costs of being away from home - a
        // repair bill, a full restock, and a way back - without the trip turning into the death
        // spiral that a broke wizard demonstrated all night. One broken ring cost 611 gold, so a
        // few thousand is barely a handful of repairs.
        public long PoorGold = 20000;
        public int PursuitPatience = 25;      // approach attempts allowed without getting closer
        // Cargo looting: how much a drop must be worth per unit of weight to be taken. The
        // bar slides between the two gold marks - poor characters take more, rich ones less.
        public long LootPoorGold = 5000;
        public long LootRichGold = 200000;
        public int LootGoldPerWeightPoor = 5;
        public int LootGoldPerWeightRich = 80;
        public int LootHeavyMultiplier = 4;   // bar multiplier once the bag is heavy
        public int StorageSize = 80;          // account storage slots; the server caps this anyway
        public int RepairAtDurability = 1;    // repair equipped gear at or below this displayed value
        // Special repair costs twice as much but does NOT eat the item's maximum durability,
        // which ordinary repair does permanently. Worth it for gear we intend to keep.
        public bool PreferSpecialRepair = true;
        // Buy weapons, armour and accessories from shops when they beat what we are wearing.
        public bool BuyGear = true;
        // Never spend below this much gold: repairs and potions come first.
        public long GoldReserve = 5000;
        // Ignore an upgrade worth less than this percent more than the slot it replaces.
        public int MinimumUpgradePercent = 10;
        // Do not walk further than this to browse a shop. Gear is optional; the walk is not.
        public int MaxShoppingDistance = 60;
        public bool RepairEnabled = true;

        // Go to town when healing potions run this far below their target, as a share of it.
        //
        // Bag weight was the only thing that ever sent the bot shopping, and that quietly assumes
        // the bag fills faster than the potions empty. For a caster it does not: a wizard drinks
        // its way through a stack while looting almost nothing, so it ends up at 20% health with a
        // half-full bag, no potions, and no reason to go anywhere. It then fights until something
        // kills it - and with auto-revive that becomes a death loop that lasts all night.
        // 0 disables, and the ordinary trip cooldown bounds how often this can fire.
        public int RestockAtPotionPercent = 25;

        // --- Combat watchdog -------------------------------------------------------------
        // Swings allowed against one monster without its health ever going down before the
        // bot concludes the fight is going nowhere and finds something else to hit.
        //
        // A bot was once seen surrounded, with a monster standing on its OWN cell: it had
        // committed to that target, every attack swung at the cell in front of it, and the
        // thing underneath took no damage at all. Nothing in the brain measured whether the
        // target was actually losing health, so it stayed there until it was forced to town.
        public int AttackPatience = 8;
        // How long a target written off by the watchdog is left alone.
        public int AttackGiveUpSeconds = 45;

        // --- Per-monster danger ----------------------------------------------------------
        // Refuse to START a fight with a monster the memory says could kill us in this many
        // hits or fewer, measured against CURRENT health rather than maximum - so something
        // survivable at full health becomes something to leave alone at forty percent.
        // 0 disables per-monster avoidance entirely.
        public int DangerHitsToDeath = 3;
        // Ignore the rule until we have actually been hit this many times by one, so a single
        // unlucky crit does not blacklist a whole species.
        public int DangerMinimumHits = 3;

        // --- Casting ---------------------------------------------------------------------
        public bool CastSpells = true;
        // Never cast below this share of the mana pool.
        //
        // Defaults to 0, and that is a considered default rather than a missing one. The reserve
        // was originally justified as keeping something back for an escape - but nothing the bot
        // casts is defensive; its escape is a town scroll, which is an item. So the floor bought
        // nothing and cost a great deal: mana does not come back during a hunting session, so a
        // wizard that stopped at 15% simply stopped casting for the rest of the night.
        public int SpellManaFloorPercent = 0;
        // Drink a mana potion at or below this share of the pool, if the character has spells to
        // spend it on. 0 disables.
        public int DrinkManaAtPercent = 40;
        // Furthest we will cast. The server's own limit is Globals.MagicRange (10).
        public int CastRange = 9;

        // --- Teleport NPCs ---------------------------------------------------------------
        // Pay an NPC to teleport across the world rather than walking every map in between.
        public bool UseTeleportNPCs = true;
        // Never pay a teleport fee that would leave less than this much gold. Sized so the
        // bot can still restock potions and a way home after arriving.
        public long TeleportGoldFloor = 20000;

        // --- Overnight resilience --------------------------------------------------------
        // Revive automatically this many seconds after dying. 0 leaves it to the button.
        public int ReviveAfterSeconds = 25;
        // Reconnect after the SERVER drops us. Terminal failures (bad password, wrong
        // version) are never retried whatever this says.
        public bool ReconnectOnDisconnect = true;

        // --- Learned walkability ---------------------------------------------------------
        // Remember cells the server refused to let us walk into, and route around them.
        public bool LearnBlockedCells = true;
        // Refusals at one cell before it is believed. Below this it is probably a monster
        // standing in a doorway rather than the map being wrong.
        public int BlockedCellEvidence = 3;

        /// <summary>Start this bot when the host launches.</summary>
        public bool AutoStart = true;

        /// <summary>
        /// How many healing potions to carry, given the bag we actually have.
        ///
        /// A flat ten is a level 1 number. A warrior with a 136 weight bag and eighty thousand gold
        /// should be carrying a stack that lasts a hunting session, and one with a 70 weight bag
        /// should not be carrying the same number as the warrior.
        /// </summary>
        public int HealthPotionTarget(int maxBagWeight, int potionWeight = 1) =>
            Target(maxBagWeight, HealthPotionWeightPercent, HealthPotionReserve, potionWeight);

        public int ManaPotionTarget(int maxBagWeight, int potionWeight = 1) =>
            Target(maxBagWeight, ManaPotionWeightPercent, ManaPotionReserve, potionWeight);

        /// <summary>
        /// The budget is a share of the BAG, so it has to be divided by what one potion weighs.
        ///
        /// Treating the weight budget as a count - on an assumption that potions weigh one apiece,
        /// which I asserted without checking - meant a warrior with a 147 weight bag bought 36
        /// potions of whatever tier it could afford. At tier four that is most of the bag: it came
        /// home from the shops at 115 of 147 with no room left to hunt.
        /// </summary>
        private static int Target(int maxBagWeight, int percent, int floor, int itemWeight)
        {
            if (maxBagWeight <= 0 || percent <= 0) return floor;

            int weightBudget = maxBagWeight * percent / 100;
            int count = weightBudget / System.Math.Max(1, itemWeight);

            return System.Math.Max(floor, count);
        }
        public bool BuyBooks = true;
        public bool LearnBooks = true;
        public bool BankUnlearntBooks = true;
        public int VendorTalkRange = 3;
        // Target the vendor by map coordinates (C.AutoPathWaypoint) instead of by NPC index.
        // Both go through the same CanAutoPath gate, so this only helps if the NPC's region
        // is the problem rather than the map.
        public bool AutoPathByCoordinates = false;

        // Where the bot writes its own log. Output does not stream back over the SSH chain to the
        // VM, so a file is the only reliable way to see what a long run did.
        public string LogPath = "mirbot.log";

        public enum MirClassChoice { Warrior, Wizard, Taoist, Assassin }

        public byte[] ResolveClientHash()
        {
            if (!string.IsNullOrWhiteSpace(VersionPath) && File.Exists(VersionPath))
            {
                using (FileStream stream = File.OpenRead(VersionPath))
                using (SHA256 sha256 = SHA256.Create())
                    return sha256.ComputeHash(stream);
            }

            if (string.IsNullOrWhiteSpace(ClientHashHex))
                return null;

            string hex = ClientHashHex.Trim().Replace("-", "").Replace(" ", "");
            if (hex.Length % 2 != 0)
                throw new FormatException("ClientHashHex has an odd number of characters.");

            byte[] result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
                result[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber);

            return result;
        }

        public static BotConfig Load(string path)
        {
            BotConfig config = new BotConfig();

            if (!File.Exists(path))
                throw new FileNotFoundException($"Config not found: {path}");

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") || line.StartsWith("["))
                    continue;

                int split = line.IndexOf('=');
                if (split <= 0) continue;

                string key = line.Substring(0, split).Trim();
                string value = line.Substring(split + 1).Trim();

                switch (key.ToLowerInvariant())
                {
                    case "serveraddress": config.ServerAddress = value; break;
                    case "serverport": config.ServerPort = int.Parse(value); break;
                    case "emailaddress": config.EMailAddress = value; break;
                    case "password": config.Password = value; break;
                    case "language": config.Language = value; break;
                    case "datapath": config.DataPath = value; break;
                    case "versionpath": config.VersionPath = value; break;
                    case "clienthashhex": config.ClientHashHex = value; break;
                    case "charactername": config.CharacterName = value; break;
                    case "createaccountifmissing": config.CreateAccountIfMissing = bool.Parse(value); break;
                    case "newcharacterclass": config.NewCharacterClass = Enum.Parse<MirClassChoice>(value, true); break;
                    case "timeoutseconds": config.TimeOut = TimeSpan.FromSeconds(int.Parse(value)); break;
                    case "healatpercent": config.HealAtPercent = int.Parse(value); break;
                    case "fleeatpercent": config.FleeAtPercent = int.Parse(value); break;
                    case "aggrorange": config.AggroRange = int.Parse(value); break;
                    case "allowrunning": config.AllowRunning = bool.Parse(value); break;
                    case "lootenabled": config.LootEnabled = bool.Parse(value); break;
                    case "lootrange": config.LootRange = int.Parse(value); break;
                    case "heavyweightpercent": config.HeavyWeightPercent = int.Parse(value); break;
                    case "townatweightpercent": config.TownAtWeightPercent = int.Parse(value); break;
                    case "healthpotionreserve": config.HealthPotionReserve = int.Parse(value); break;
                    case "emergencyscrollatpercent": config.EmergencyScrollAtPercent = int.Parse(value); break;
                    case "healthpotionweightpercent": config.HealthPotionWeightPercent = int.Parse(value); break;
                    case "manapotionweightpercent": config.ManaPotionWeightPercent = int.Parse(value); break;
                    case "potionhealpercenttarget": config.PotionHealPercentTarget = int.Parse(value); break;
                    case "manapotionreserve": config.ManaPotionReserve = int.Parse(value); break;
                    case "townscrollreserve": config.TownScrollReserve = int.Parse(value); break;
                    case "keeptorchlit": config.KeepTorchLit = bool.Parse(value); break;
                    case "unlocktosell": config.UnlockToSell = bool.Parse(value); break;
                    case "townmaps": config.TownMaps = value; break;
                    case "returnwithin": config.ReturnWithin = int.Parse(value); break;
                    case "detoursteps": config.DetourSteps = int.Parse(value); break;
                    case "mappath": config.MapPath = value; break;
                    case "memorypath": config.MemoryPath = value; break;
                    case "experiencesampleminutes": config.ExperienceSampleMinutes = int.Parse(value); break;
                    case "minimumsampleminutes": config.MinimumSampleMinutes = int.Parse(value); break;
                    case "preferredmaps": config.PreferredMaps = value; break;
                    case "levelbandsize": config.LevelBandSize = int.Parse(value); break;
                    case "autotravel": config.AutoTravel = bool.Parse(value); break;
                    case "huntingchoices": config.HuntingChoices = int.Parse(value); break;
                    case "exploreuntilmapsknown": config.ExploreUntilMapsKnown = int.Parse(value); break;
                    case "explorechancepercent": config.ExploreChancePercent = int.Parse(value); break;
                    case "explorelevelsabove": config.ExploreLevelsAbove = int.Parse(value); break;
                    case "travelgoldperhop": config.TravelGoldPerHop = long.Parse(value); break;
                    case "poorgold": config.PoorGold = long.Parse(value); break;
                    case "pursuitpatience": config.PursuitPatience = int.Parse(value); break;
                    case "lootpoorgold": config.LootPoorGold = long.Parse(value); break;
                    case "lootrichgold": config.LootRichGold = long.Parse(value); break;
                    case "lootgoldperweightpoor": config.LootGoldPerWeightPoor = int.Parse(value); break;
                    case "lootgoldperweightrich": config.LootGoldPerWeightRich = int.Parse(value); break;
                    case "lootheavymultiplier": config.LootHeavyMultiplier = int.Parse(value); break;
                    case "storagesize": config.StorageSize = int.Parse(value); break;
                    case "repairatdurability": config.RepairAtDurability = int.Parse(value); break;
                    case "preferspecialrepair": config.PreferSpecialRepair = bool.Parse(value); break;
                    case "buygear": config.BuyGear = bool.Parse(value); break;
                    case "goldreserve": config.GoldReserve = long.Parse(value); break;
                    case "minimumupgradepercent": config.MinimumUpgradePercent = int.Parse(value); break;
                    case "maxshoppingdistance": config.MaxShoppingDistance = int.Parse(value); break;
                    case "repairenabled": config.RepairEnabled = bool.Parse(value); break;
                    case "restockatpotionpercent": config.RestockAtPotionPercent = int.Parse(value); break;
                    case "attackpatience": config.AttackPatience = int.Parse(value); break;
                    case "attackgiveupseconds": config.AttackGiveUpSeconds = int.Parse(value); break;
                    case "dangerhitstodeath": config.DangerHitsToDeath = int.Parse(value); break;
                    case "dangerminimumhits": config.DangerMinimumHits = int.Parse(value); break;
                    case "castspells": config.CastSpells = bool.Parse(value); break;
                    case "spellmanafloorpercent": config.SpellManaFloorPercent = int.Parse(value); break;
                    case "drinkmanaatpercent": config.DrinkManaAtPercent = int.Parse(value); break;
                    case "castrange": config.CastRange = int.Parse(value); break;
                    case "useteleportnpcs": config.UseTeleportNPCs = bool.Parse(value); break;
                    case "teleportgoldfloor": config.TeleportGoldFloor = long.Parse(value); break;
                    case "reviveafterseconds": config.ReviveAfterSeconds = int.Parse(value); break;
                    case "reconnectondisconnect": config.ReconnectOnDisconnect = bool.Parse(value); break;
                    case "learnblockedcells": config.LearnBlockedCells = bool.Parse(value); break;
                    case "blockedcellevidence": config.BlockedCellEvidence = int.Parse(value); break;
                    case "autostart": config.AutoStart = bool.Parse(value); break;
                    case "buybooks": config.BuyBooks = bool.Parse(value); break;
                    case "learnbooks": config.LearnBooks = bool.Parse(value); break;
                    case "bankunlearntbooks": config.BankUnlearntBooks = bool.Parse(value); break;
                    case "vendortalkrange": config.VendorTalkRange = int.Parse(value); break;
                    case "autopathbycoordinates": config.AutoPathByCoordinates = bool.Parse(value); break;
                    case "logpath": config.LogPath = value; break;
                }
            }

            List<string> problems = new List<string>();
            if (string.IsNullOrWhiteSpace(config.EMailAddress)) problems.Add("EMailAddress is required");
            if (string.IsNullOrWhiteSpace(config.Password)) problems.Add("Password is required");
            if (problems.Count > 0)
                throw new InvalidOperationException("Bad config: " + string.Join("; ", problems));

            return config;
        }
    }
}
