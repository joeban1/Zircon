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
        public int HealAtPercent = 60;   // never drink above this HP%
        // Below this, drink whatever is carried even if most of it is wasted. Above it, a potion is
        // only drunk when the MISSING health is at least what the potion restores, so nothing is
        // poured into a nearly full bar.
        //
        // Needed because potion tiers differ sharply - 30, 70, 110, 170, 250 HP on this server -
        // so a single percentage cannot express "do not overheal" for every character and every
        // tier. A 216 HP wizard at the 60% mark is missing 86, and a tier IV potion heals 170: more
        // than half of it hits the cap and is lost.
        public int PanicHealPercent = 30;
        public int FleeAtPercent = 25;   // disengage at or below this HP%
        public int AggroRange = 8;       // how far to look for a monster worth engaging
        public bool AllowRunning = true; // run (2 tiles) instead of walking when the way is clear
        public bool LootEnabled = true;
        public int LootRange = 6;        // how far to detour for a dropped item
        public int HeavyWeightPercent = 70;  // above this, only loot consumables and upgrades
        public int TownAtWeightPercent = 90; // above this, town-teleport out if a scroll is carried
        // Go to town when this few inventory slots remain free. Weight and slots are separate
        // limits: a bag of rings and crafting drops fills all 48 slots at a third of the weight
        // cap, and until this existed nothing noticed - the bot simply stopped being able to loot.
        public int TownAtFreeSlots = 3;
        // How long to spend walking into a safe zone to bank. Vendors are NOT reliably inside
        // one - a trip ending at Henry in Banya Village finishes outside it - and banking is
        // impossible outside (PlayerObject.cs:7421), so the bot walks in rather than skipping.
        public int BankWalkSeconds = 30;
        // Reserves work both ways: below them the bot buys, above them it sells.
        public int HealthPotionReserve = 10; // always carry this many healing potions
        // Out of potions and below this much health: scroll out rather than die. 0 disables.
        public int EmergencyScrollAtPercent = 20;
        // Share of the bag set aside for potions. The flat reserves above become floors:
        // a level 40 warrior with a big bag should not carry a level 1 character's ten.
        //
        // Raised from 25 because the budget is in WEIGHT and potion tiers do not weigh the same:
        // Healing Potion (II) is 1 each but (IV) is 3, measured from bag deltas across three bots.
        // So a quarter of a 123-weight bag bought thirty tier-II potions or only TEN tier-IV, and
        // the bots buy the strong ones - which is how a wizard with a "full" potion target ran
        // itself dry thirty times and spent nine emergency scrolls in one session.
        //
        // Half the bag still leaves plenty for loot: the town trip triggers at 90% weight, and
        // slots rather than weight are usually what fills first.
        public int HealthPotionWeightPercent = 50;
        public int ManaPotionWeightPercent = 10;
        // NOTE: PotionHealPercentTarget was removed. It set a FLOOR on potion size, which became
        // redundant and then actively contradictory once purchasing gained a CEILING derived from
        // MaxHealth and HealAtPercent - see BestPotion. Two settings that can disagree about the
        // same decision are worse than one, whichever wins.
        public int ManaPotionReserve = 15;   // same for mana potions
        public int TownScrollReserve = 3;    // always carry this many town teleport scrolls
        public bool KeepTorchLit = true;     // replace the torch when the slot is empty
        // Clear the Locked flag on surplus consumables so they can actually be sold.
        public bool UnlockToSell = true;
        // The ONLY maps a town trip will shop on. Comma separated map descriptions.
        // Empty means anywhere, which is how the bot once decided to restock on
        // Infernal Island because one NPC there sold both scrolls and potions.
        //
        // HOST-WIDE, and taken from the FIRST bot's ini only (BotHost.Prepare). Every other bot's
        // value is parsed and then ignored, so setting it per-bot does nothing.
        //
        // Lost Paradise was added after a level 13 Taoist spent thirteen levels stranded there
        // unable to cast: the map holds Cory (amulets, poison, town scrolls) and Flora (jewellery,
        // repairs), but because it was not listed, ShoppableOn returned NOTHING for every selector
        // - not "no book seller here" but "no vendors anywhere" - so her trips aborted and she
        // could not even buy the scroll that would have let her leave.
        public string TownMaps = "Bichon Town,Banya Village,Lost Paradise";
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
        public int ExperienceSampleMinutes = 10;  // window for a map's experience-per-hour
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
        public long PoorGold = 10000;
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

        // Gold that must survive buying a replacement torch.
        //
        // A torch burns down and gets replaced, which is fine while there is money. While there is
        // not, it is the only purchase a broke bot can still make, and it quietly eats the coins
        // that would have bought the thing that keeps it alive: buy a Candle for 10, sell the burnt
        // one back for 1, nine gold gone, every three and a half minutes. Two bots were watched
        // riding that down to single figures - 55 to 10, and 990 to 1 - while a healing potion cost
        // 80 and neither could afford one.
        //
        // 500 is about six of those potions. Light is a convenience; the potion is not.
        public long TorchGoldFloor = 500;
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
        // Keep self-buffs up - Magic Shield and the like. Cast only when the SERVER says the buff
        // is missing (S.BuffAdd / S.BuffRemove / the login dump), so there is no duration guessing
        // anywhere and no timer that can drift out of step with the real thing.
        public bool MaintainBuffs = true;

        // --- Reagents --------------------------------------------------------------------
        // Amulets and poison are consumed by Taoist summons, buffs and poisons, from the EQUIPMENT
        // slot and matched by exact Shape. Bought only when the character actually knows a spell
        // that eats them, so a Taoist who has not learnt one yet does not carry dead weight.
        public bool BuyReagents = true;
        // How many to keep. Amulet costs run from 1 per cast (Magic Resistance, Summon Skeleton) to
        // 20 (Celestial Light), so a few hundred is a modest stack that lasts a session.
        public int ReagentReserve = 300;
        // ...but never spending more than this share of everything we own on them in one trip.
        //
        // The count alone is the wrong unit. 300 is a sensible stock for a Taoist with money and
        // most of a level 14 Taoist's net worth otherwise: one ordered 300 Green Poison for roughly
        // 10,000 gold when it held 11,165, and spent the rest of the session too poor to buy a
        // healing potion or repair a broken ring. It bought the right thing in the wrong quantity.
        //
        // The reserve stays the target; this is the pace at which a poor bot is allowed to approach
        // it. At 15% that Taoist would have bought about fifty and kept enough to stay alive, then
        // topped up on later trips as it earned. 0 disables the share test.
        public int ReagentGoldPercent = 15;

        // --- Summons ---------------------------------------------------------------------
        // Keep a Summon Skeleton up. Only that one: the five Taoist summons look alike but do not
        // share a lifecycle - Summon Dead needs a dead target, a different amulet shape and a random
        // success roll, and has no same-type recall path at all.
        public bool KeepSummon = true;
        // Wait this long after a summon attempt before trying again. The server consumes the amulet
        // in MagicCast BEFORE checking the two-pet cap, so a refused summon costs a reagent and
        // says nothing - this is what stops that repeating.
        public int SummonRetrySeconds = 20;

        // --- Teleport NPCs ---------------------------------------------------------------
        // Pay an NPC to teleport across the world rather than walking every map in between.
        public bool UseTeleportNPCs = true;
        // Never pay a teleport fee that would leave less than this much gold. Sized so the
        // bot can still restock potions and a way home after arriving.
        public long TeleportGoldFloor = 20000;
        // And never spend more than this share of everything we own on a single fare, however
        // comfortably the absolute floor above is cleared.
        //
        // The floor alone is the wrong shape. It is a fixed line, so it happily lets a bot holding
        // 75,000 gold pay out 35,000 in fares - every hop legal, every hop leaving more than
        // 20,000 - and a wizard did exactly that, hopping repeatedly to a map that killed it each
        // time, until deaths and restocking finished what the travelling started. A fare should be
        // affordable in proportion to what we have, not merely survivable once.
        //
        // At 25%: a 5,000 hop needs 20,000 (the floor binds first), a 10,000 hop needs 40,000.
        // Cheap local travel is unaffected; long expensive hops become something a bot has to be
        // genuinely well off to choose. 0 disables the share test and leaves only the floor.
        public int TeleportMaxGoldPercent = 25;

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

        // --- Doorways --------------------------------------------------------------------
        // Cells that change the map are treated as obstacles while hunting, so the bot cannot
        // wander back out of a map it just walked into. Off means the old behaviour.
        public bool AvoidMapExits = true;
        // How far from the nearest exit the bot walks before it settles in to hunt, in Chebyshev
        // tiles. Measured rather than guessed: arriving in Banya Cave Lv 1 put the bot at 132,175
        // with the way back at 124-126,180-182 - exactly 6 tiles, so a clearance of 6 would never
        // have fired. The server drops you NEAR the return exit rather than on it, so this has to
        // clear the landing zone, not just the cell.
        public int DoorClearance = 12;
        // Give up clearing the doorway after this long and just get on with it - an entrance in a
        // dead-end pocket may have nowhere further to go.
        public int DoorClearSeconds = 15;

        // --- Roaming ---------------------------------------------------------------------
        // Wander by picking a spot and pathing to it, rather than by walking in a random compass
        // direction. The old way is a blind hill-climber: against a tree it bumps, re-rolls, and
        // takes a fresh random draw, so the bot mills in whatever pocket it starts in. A* goes
        // round the tree. Straight from the Mir 2 agents (BaseAI roam + GetRandomPoint).
        //
        // This does NOT make the bot run past monsters. Roaming is the last branch in Decide, so
        // any target inside AggroRange preempts it on the very next decision - the bot walks
        // towards its spot and stops to fight whatever it meets, rather than towing a train.
        public bool RoamToDestinations = true;
        // How far a wander destination may be, in tiles. Mir 2 uses 50 with hundreds of agents
        // covering a map; ours is smaller so the bot stays in ground it is actually clearing.
        public int RoamRadius = 25;
        // Give up on a wander destination after this long and pick another.
        public int RoamRetargetSeconds = 20;

        // --- Kiting ----------------------------------------------------------------------
        // Casters back away to keep their range instead of standing still while something closes
        // on them. A wizard with a ten-tile spell fighting toe to toe has thrown away its class.
        //
        // Only for characters that actually have something to cast - a caster out of mana should
        // close and swing, not retreat from a fight it has no ranged answer to.
        public bool KiteWhileCasting = true;
        // Back away once the target is this close...
        public int KiteWhenCloserThan = 4;
        // ...and stop once it is this far. Below Globals.MagicRange (10) so ordinary drift does
        // not put the target out of reach the moment we arrive.
        public int KiteRetreatRange = 7;

        // --- Status page reachability ----------------------------------------------------
        // Extra hostnames/addresses the status page listens on, beyond loopback. Comma separated,
        // empty means loopback only.
        //
        // SECURITY: this page has no authentication. Anything that can reach it can start and stop
        // bots, force town trips and send them travelling. Only put a LAN address here on a network
        // you trust, and never a public one.
        //
        // On Windows, HttpListener refuses any non-loopback prefix unless the URL is reserved for
        // the account first:
        //     netsh http add urlacl url=http://+:8642/ user=%USERDOMAIN%\%USERNAME%
        // and the port has to be open in the firewall. The host logs plainly when a prefix is
        // rejected rather than failing silently.
        public string StatusExtraHosts = "";

        /// <summary>
        /// The ini this was read from, so an edit can be written back to it.
        ///
        /// Not a setting - nothing parses it, nothing writes it to the file. It exists because
        /// BotHost.Load knows the path, uses it once and throws it away, which left the bot thread
        /// with no way to answer "which file do I persist this to?".
        /// </summary>
        public string SourcePath = "";

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


        /// <summary>
        /// Apply one key=value to a config. The ONLY place ini keys are interpreted.
        ///
        /// Lifted out of Load so that loading a file and editing a setting at runtime cannot drift
        /// apart: two parsers for the same keys would eventually disagree about one of them, and
        /// the disagreement would show up as a setting that works from the file and not from the
        /// page, or the reverse.
        ///
        /// Returns false for an unknown key. A value that will not parse throws, and the caller
        /// decides whether that is fatal - see the comment at the call site in Load.
        /// </summary>
        public static bool Apply(BotConfig config, string key, string value, out string error)
        {
            error = null;

            try
            {
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
                case "panichealpercent": config.PanicHealPercent = int.Parse(value); break;
                case "fleeatpercent": config.FleeAtPercent = int.Parse(value); break;
                case "aggrorange": config.AggroRange = int.Parse(value); break;
                case "allowrunning": config.AllowRunning = bool.Parse(value); break;
                case "lootenabled": config.LootEnabled = bool.Parse(value); break;
                case "lootrange": config.LootRange = int.Parse(value); break;
                case "heavyweightpercent": config.HeavyWeightPercent = int.Parse(value); break;
                case "townatweightpercent": config.TownAtWeightPercent = int.Parse(value); break;
                case "townatfreeslots": config.TownAtFreeSlots = int.Parse(value); break;
                case "bankwalkseconds": config.BankWalkSeconds = int.Parse(value); break;
                case "healthpotionreserve": config.HealthPotionReserve = int.Parse(value); break;
                case "emergencyscrollatpercent": config.EmergencyScrollAtPercent = int.Parse(value); break;
                case "healthpotionweightpercent": config.HealthPotionWeightPercent = int.Parse(value); break;
                case "manapotionweightpercent": config.ManaPotionWeightPercent = int.Parse(value); break;
                // Accepted and ignored: an ini written before the setting was removed must
                // still load rather than failing on an unknown key.
                case "potionhealpercenttarget": break;
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
                case "maintainbuffs": config.MaintainBuffs = bool.Parse(value); break;
                case "buyreagents": config.BuyReagents = bool.Parse(value); break;
                case "reagentreserve": config.ReagentReserve = int.Parse(value); break;
                case "torchgoldfloor": config.TorchGoldFloor = long.Parse(value); break;
                case "reagentgoldpercent": config.ReagentGoldPercent = int.Parse(value); break;
                case "keepsummon": config.KeepSummon = bool.Parse(value); break;
                case "summonretryseconds": config.SummonRetrySeconds = int.Parse(value); break;
                case "useteleportnpcs": config.UseTeleportNPCs = bool.Parse(value); break;
                case "teleportgoldfloor": config.TeleportGoldFloor = long.Parse(value); break;
                case "teleportmaxgoldpercent": config.TeleportMaxGoldPercent = int.Parse(value); break;
                case "reviveafterseconds": config.ReviveAfterSeconds = int.Parse(value); break;
                case "reconnectondisconnect": config.ReconnectOnDisconnect = bool.Parse(value); break;
                case "learnblockedcells": config.LearnBlockedCells = bool.Parse(value); break;
                case "blockedcellevidence": config.BlockedCellEvidence = int.Parse(value); break;
                case "avoidmapexits": config.AvoidMapExits = bool.Parse(value); break;
                case "doorclearance": config.DoorClearance = int.Parse(value); break;
                case "doorclearseconds": config.DoorClearSeconds = int.Parse(value); break;
                case "roamtodestinations": config.RoamToDestinations = bool.Parse(value); break;
                case "roamradius": config.RoamRadius = int.Parse(value); break;
                case "roamretargetseconds": config.RoamRetargetSeconds = int.Parse(value); break;
                case "kitewhilecasting": config.KiteWhileCasting = bool.Parse(value); break;
                case "kitewhencloserthan": config.KiteWhenCloserThan = int.Parse(value); break;
                case "kiteretreatrange": config.KiteRetreatRange = int.Parse(value); break;
                case "statusextrahosts": config.StatusExtraHosts = value; break;
                case "autostart": config.AutoStart = bool.Parse(value); break;
                case "buybooks": config.BuyBooks = bool.Parse(value); break;
                case "learnbooks": config.LearnBooks = bool.Parse(value); break;
                case "bankunlearntbooks": config.BankUnlearntBooks = bool.Parse(value); break;
                case "vendortalkrange": config.VendorTalkRange = int.Parse(value); break;
                case "autopathbycoordinates": config.AutoPathByCoordinates = bool.Parse(value); break;
                case "logpath": config.LogPath = value; break;
                    default: return false;
                }
            }
            catch (Exception ex)
            {
                error = $"{key}: {ex.Message}";
                return false;
            }

            return true;
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

                if (!Apply(config, key, value, out string problem))
                {
                    // Unknown keys stay SILENT here, and only here.
                    //
                    // An ini written for a newer build, or one carrying a setting since removed,
                    // must still load - a bot that refuses to start because of a stale line is a
                    // worse failure than a line quietly ignored. The API path reports the same
                    // condition as an error, because there somebody is watching and expecting the
                    // edit to take effect.
                    if (problem != null) throw new InvalidOperationException(problem);
                }
            }

            List<string> problems = new List<string>();
            if (string.IsNullOrWhiteSpace(config.EMailAddress)) problems.Add("EMailAddress is required");
            if (string.IsNullOrWhiteSpace(config.Password)) problems.Add("Password is required");
            if (problems.Count > 0)
                throw new InvalidOperationException("Bad config: " + string.Join("; ", problems));

            config.SourcePath = path;

            return config;
        }
    }
}
