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
        public int ManaPotionReserve = 15;   // same for mana potions
        public int TownScrollReserve = 3;    // always carry this many town teleport scrolls
        // No vendor is named: VendorDirectory derives who buys what from System.db.
        public int ScrollIfFurtherThan = 25;  // use a town scroll rather than walk beyond this
        public int ReturnWithin = 6;          // close enough to the old hunting spot
        public int DetourSteps = 4;           // steps to commit to when walking around an obstacle
        public int StorageSize = 80;          // account storage slots; the server caps this anyway
        public int RepairAtDurability = 3;    // repair equipped gear at or below this displayed value
        public bool RepairEnabled = true;

        /// <summary>Start this bot when the host launches.</summary>
        public bool AutoStart = true;
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
                    case "manapotionreserve": config.ManaPotionReserve = int.Parse(value); break;
                    case "townscrollreserve": config.TownScrollReserve = int.Parse(value); break;
                    case "scrolliffurtherthan": config.ScrollIfFurtherThan = int.Parse(value); break;
                    case "returnwithin": config.ReturnWithin = int.Parse(value); break;
                    case "detoursteps": config.DetourSteps = int.Parse(value); break;
                    case "storagesize": config.StorageSize = int.Parse(value); break;
                    case "repairatdurability": config.RepairAtDurability = int.Parse(value); break;
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
