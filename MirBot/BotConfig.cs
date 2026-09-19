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
        // No vendor is named: VendorDirectory derives who buys what from System.db.
        public int ScrollIfFurtherThan = 25;  // use a town scroll rather than walk beyond this
        public int ReturnWithin = 6;          // close enough to the old hunting spot
        public int DetourSteps = 4;           // steps to commit to when walking around an obstacle
        // The client's Map folder. With it the bot pathfinds; without it it steers blind.
        public string MapPath = "";
        // Where the learned memory banks live. Relative paths are beside the executable.
        public string MemoryPath = "memory";
        public int ExperienceSampleMinutes = 15;  // window for a map's experience-per-hour
        // Maps worth trying first, in order, before falling back to a random reachable one.
        // Comma separated map descriptions, e.g. "Deserted Mine Lv 1,Ant Cave North".
        public string PreferredMaps = "";
        // Experience rates are recorded per band of levels rather than per exact level, so a
        // measurement survives levelling up. 1 reverts to exact-level records.
        public int LevelBandSize = 5;
        // Look for somewhere better to hunt when a town trip finishes.
        public bool AutoTravel = false;
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
                    case "scrolliffurtherthan": config.ScrollIfFurtherThan = int.Parse(value); break;
                    case "returnwithin": config.ReturnWithin = int.Parse(value); break;
                    case "detoursteps": config.DetourSteps = int.Parse(value); break;
                    case "mappath": config.MapPath = value; break;
                    case "memorypath": config.MemoryPath = value; break;
                    case "experiencesampleminutes": config.ExperienceSampleMinutes = int.Parse(value); break;
                    case "preferredmaps": config.PreferredMaps = value; break;
                    case "levelbandsize": config.LevelBandSize = int.Parse(value); break;
                    case "autotravel": config.AutoTravel = bool.Parse(value); break;
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
