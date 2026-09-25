using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

        /// <summary>
        /// A consumable restoring less than this is loot, not a potion. See Backpack.MinPotionRestore.
        /// </summary>
        public int MinPotionRestore = 20;

        /// <summary>
        /// Wholesale sell refusals within ResyncWindowMinutes before forcing a relog. 0 disables.
        ///
        /// A refused sell order is the clearest evidence available that our inventory model and
        /// the server's have diverged: NPCSell voids the whole order when any one link does not
        /// match what the server holds in that slot. Once the two slot maps disagree there is no
        /// way back from incremental packets - the protocol has no "send me my inventory" request
        /// - so the only repair is a fresh login, where StartInformation.Items arrives complete.
        ///
        /// Today's session survived only because every deploy happened to relog the bots. That is
        /// luck, not design, and a bot left running for a day had no way to recover at all.
        /// </summary>
        public int ResyncAfterSellRefusals = 3;

        public int ResyncWindowMinutes = 10;
        public int FleeAtPercent = 25;   // disengage at or below this HP%
        public int AggroRange = 8;       // how far to look for a monster worth engaging
        public bool AllowRunning = true; // run (2 tiles) instead of walking when the way is clear
        public bool LootEnabled = true;
        public int LootRange = 6;        // how far to detour for a dropped item

        /// <summary>
        /// Collect loot and butcher carcasses before starting a NEW fight, so long as nothing is
        /// within this many tiles. 0 restores the old order, where gathering only happened once
        /// the area was completely clear.
        ///
        /// Two tiles is "in contact or about to be": anything closer than that is a fight already
        /// happening, and fighting has to win. Anything further is a fight the bot would be
        /// CHOOSING, and choosing one over the loot already lying on the floor is what made a
        /// wizard spend mana potions faster than it earned the gold to replace them.
        /// </summary>
        public int GatherSafeRange = 2;

        /// <summary>
        /// Butcher the corpses of animals that carry a harvest yield.
        ///
        /// Meat sells for real money and is invisible without this: a chicken or a deer drops
        /// NOTHING when killed, so a bot farming them earns experience and no gold at all.
        /// </summary>
        public bool ButcherEnabled = true;

        /// <summary>
        /// Monster AI numbers always treated as butcherable, whatever their drop table says.
        ///
        /// 1 Chicken, 2 Cow/Deer/Pig/Sheep, 5 Carnivorous Plant on this server. Deliberately NOT
        /// the Mir 2 agents' { 1, 2, 4, 5, 7, 9 }: here 7 is Ant Needler and the archers and 9 is
        /// the sorcerers, all ordinary combat monsters. Add 4 to include Chestnut Tree if the
        /// gathering nodes turn out to be worth the detour - its drop table has five chestnut
        /// tiers but only two of any one name, so the repeat detector does not catch it.
        /// </summary>
        // The server's NeedHarvest AIs (MonsterObject.GetMonster): every drop of these - ordinary
        // loot and quest items alike - stays in the corpse until the killer butchers it. 3 Wolf/
        // Scorpion, 6 Spitting Spider (Venom)/Visceral Worm, 8 Spider Bat (Spider Curare)/Cave
        // Maggot/Wedge Moth were missing, so their loot and two quest items were never collected.
        public string ButcherAIs = "1,2,3,5,6,8";

        /// <summary>How far to walk to reach a corpse. Beyond this it is not worth the trip.</summary>
        public int ButcherRange = 8;

        /// <summary>
        /// Give up on a corpse after this many cuts.
        ///
        /// A harvest ends with S.ObjectHarvested or the corpse being removed, but a refusal is
        /// silent - the same shape as a refused pick-up - so a corpse that will not yield has to
        /// be abandoned by counting. Six comfortably covers the six-roll meat tables.
        /// </summary>
        public int ButcherAttempts = 8;
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
        // This went 25 -> 50 -> 30, and the middle step was a fix for the wrong problem.
        //
        // The 25 -> 50 raise was reasoned from weight: the budget is in WEIGHT and potion tiers do
        // not weigh the same - Healing Potion (II) is 1 each but (IV) is 3, measured from bag
        // deltas across three bots - so a quarter of a 123-weight bag bought thirty tier-II potions
        // or only TEN tier-IV. A wizard on that budget ran itself dry thirty times.
        //
        // That is all true and it still does not follow that the answer is more weight. What the
        // budget actually buys is HEALING, and healing per unit of weight roughly doubles up the
        // tiers: 30/1 for tier one against 170/3 for tier four. The wizard was not short of budget,
        // it was short of the good tiers - and BestPotion was quietly buying the cheap ones
        // whenever gold was tight, which it usually was. With that fixed the same healing fits in
        // far less bag.
        //
        // And half the bag was expensive. A level 18 assassin with a 138-weight bag reserved 69 of
        // it for potions, held sixty-nine, had nothing surplus to sell, filled the remainder with
        // loot in about two minutes and went back to town - five times in nineteen minutes.
        //
        // 30% of a 138-weight bag is 41: thirteen tier-four potions, about 2,200 healing, or seven
        // full bars for that character. The restock floor scales with this number (see
        // shortOfPotions in TownTrip), so a smaller target means fewer restock trips, not more.
        public int HealthPotionWeightPercent = 30;
        public int ManaPotionWeightPercent = 10;
        // NOTE: PotionHealPercentTarget was removed. It set a FLOOR on potion size, which became
        // redundant and then actively contradictory once purchasing gained a CEILING derived from
        // MaxHealth and HealAtPercent - see BestPotion. Two settings that can disagree about the
        // same decision are worse than one, whichever wins.
        public int ManaPotionReserve = 15;   // same for mana potions
        // How many of a tier we must be able to afford before settling for a weaker one.
        //
        // Deliberately small. The question this answers is "is buying these worth the trip?", not
        // "have we finished shopping" - the target above decides that, and a half-filled target is
        // topped up on the next visit at no extra cost, because the bot was going to town anyway.
        // Set it high and the bot starts downgrading its potions whenever it is briefly short of
        // gold, which is exactly the failure this exists to prevent.
        public int MinUsefulPotionBuy = 4;
        // Largest share of the purse one potion restock may spend, once we are above PoorGold.
        //
        // The potion purchase deliberately ignores GoldReserve: that reserve exists to stop
        // optional GEAR shopping eating the survival budget, and applying it to potions blocks the
        // one purchase it is being held for. Correct - but "do not block it" quietly became "spend
        // everything", and once potion ranking started preferring the best healing per unit of
        // weight the best tier stopped being cheap. An assassin with 40,687 gold bought 29 Life
        // Pill (III) at about 1,400 each and walked away with THIRTY-NINE gold: no repair, no
        // gear, no fare, and an hour of selling undone in one transaction.
        //
        // Same shape as TeleportMaxGoldPercent, and for the same reason: a purchase should be
        // affordable in proportion to what we have, not merely survivable once. Below PoorGold the
        // share does not apply at all - a bot that is already broke needs something to drink more
        // than it needs a balance.
        public int PotionMaxGoldPercent = 50;
        // How many times the CHEAPEST option's gold-per-healing a potion may cost and still be
        // considered. 0 disables the test.
        //
        // Healing per unit of WEIGHT was the whole ranking, and weight is only one of the two
        // things a bot is short of. Life Pill (IV) weighs nothing and heals 170 - the same as a
        // Healing Potion (IV), which weighs 3 - so on healing-per-weight the pill scored 170 to
        // the potion's 57 and won every single time. It costs 3,500 against a few hundred: about
        // twenty gold per point of healing against two.
        //
        // A level 27 warrior bought 150 of them and went from 667,936 gold to 253,329 overnight,
        // in purchases of 33 and 76 at a time, while every other bot did the same at smaller
        // scale. Nothing in the ranking had an opinion about money.
        //
        // Three times the cheapest is deliberately loose: it is meant to strike out the absurd,
        // not to force the bot into the bargain bin. Paying a premium for a genuinely lighter or
        // stronger potion is fine; paying ten times for an identical heal is not.
        public int PotionGoldPerHealFactor = 3;
        // Ceiling on a single potion purchase, as a multiple of the character's full pool.
        //
        // The quantity is normally limited by the weight budget, which stops being a limit at all
        // when the potion WEIGHS NOTHING: RoomFor divides by max(1, weight), so a weightless pill
        // is costed as though it weighed one, and an 87-weight budget authorises 87 of them. At
        // 3,500 each that is a 300,000 gold restock. Total healing is the honest bound when weight
        // is not one - twelve full health bars is already a long session.
        public int PotionMaxPoolMultiple = 12;
        // How many times the LIGHTEST option's weight-per-healing a potion may weigh and still be
        // considered. 0 disables. The mirror of PotionGoldPerHealFactor, on the other resource.
        //
        // This exists so the two sanity bounds can be bounds and the RANKING can be about what
        // actually matters. Elixir Of Life (V) weighs nineteen for 250 healing - roughly eight
        // times the weight per point of an ordinary potion - and is struck out here rather than
        // having to be out-argued by the ranking.
        public int PotionWeightPerHealFactor = 3;
        public int TownScrollReserve = 3;    // always carry this many town teleport scrolls
        public bool KeepTorchLit = true;     // replace the torch when the slot is empty

        // Throw away the starting weapon and armour once something better is worn.
        //
        // They are flagged Worthless on the INSTANCE, so no vendor will take them however sellable
        // the database item looks - a Commoner Outfit is 5 weight and a Trainee's Armour 11, held
        // for ever on bots that spend their lives near the weight cap. Narrow by design: see
        // Backpack.IsReplacedStarterKit for the four conditions it requires.
        public bool DropReplacedStarterKit = true;
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

        // Phone notifications. The URL is HOST-WIDE (read off the first bot's ini) and is kept out
        // of ConfigSchema on purpose: the status page has no authentication and the webhook id is
        // the only secret guarding the Home Assistant automation. Empty disables notifications.
        public string NotifyWebhookUrl = "";
        public bool NotifyLevelUp = true;
        public bool NotifyUpgrade = true;
        public bool NotifySkill = true;
        public bool NotifyDeath = false;
        public bool NotifyFault = true;
        public bool NotifyQuest = true;
        public bool NotifyFame = true;
        public bool NotifyCombine = true;

        /// <summary>
        /// Take a full set of combination pieces (Rusty / Cracked / Worn ...) to the NPC and
        /// combine them. Pieces are banked until a set is complete (see CombineBook).
        /// </summary>
        public bool EnableCombine = true;

        /// <summary>
        /// NPCs whose page trees are read for combination recipes. Host-wide: the book is built
        /// once, so the host uses the default unless the first bot's ini overrides it.
        /// </summary>
        public string CombineNPCs = "Payton";

        /// <summary>
        /// Skills whose books are never a reason to choose a hunting map, first learn or level 4
        /// training. Potion Mastery is sold by vendors and its training drops are too rare to
        /// chase. Host-wide like CombineNPCs. A copy that drops is still looted and read.
        /// </summary>
        public string NoHuntSkills = "Potion Mastery";

        /// <summary>Take and complete the database's quests (see QuestBook).</summary>
        public bool EnableQuests = true;

        /// <summary>Level from which a quest whose target is a mini-boss is taken.</summary>
        public int QuestMiniBossMinLevel = 40;

        /// <summary>Chance (%) a hunting choice pursues accepted quests, when quest maps exist.</summary>
        public int QuestHuntChancePercent = 30;

        /// <summary>Quest-goal hunting choices in a row before another goal is forced.</summary>
        public int MaxConsecutiveQuestHunts = 2;

        /// <summary>Buy fame ranks from the fame NPC (Frost Village) when affordable.</summary>
        public bool EnableFame = true;

        // Optional per-bot filter on quest GIVERS by NPC name, comma separated. Empty (the default)
        // means every quest giver in the database; "Joeban" restores the original Bichon-only set.
        public string QuestNPCs = "";

        /// <summary>QuestNPCs as a set; empty means no filter.</summary>
        public System.Collections.Generic.HashSet<string> QuestNpcFilter() =>
            new System.Collections.Generic.HashSet<string>(
                (QuestNPCs ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0),
                System.StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A quest whose target is a HEAVY mini-boss (QuestBook.HeavyBossHealth, 3,000 HP or more -
        /// in practice Level 40 - Well done's Crazed Warrior) is only taken from this level; lighter
        /// mini-bosses use QuestMiniBossMinLevel and real bosses are never taken. From this level - the operator's call, above the server's own level 40. Raised from 45
        /// to 50 on 2026-09-24: Mirbot at 45 fled from Lost Paradise Forest's monsters at 8% HP,
        /// poisoned and out of potions, without reaching the boss.
        /// </summary>
        public int QuestBossMinLevel = 50;

        // The game store, paid for with Hunt Gold. The shopping list and its order are fixed in
        // GameStore.Plan: every permanent first, then temporaries kept topped up.
        public bool EnableStore = true;
        // A temporary store buff is rebought (and the spare drunk, which extends it) once it has
        // this little time left.
        public int StoreRebuyMinutes = 60;
        public int NotifyIdleMinutes = 20;
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

        /// <summary>
        /// How many hours a map must have been measured for before it can be CHOSEN from memory.
        /// Below this it still ranks and still counts, but only as somewhere to go and find out.
        /// 0 disables the test.
        ///
        /// A rate is a claim about an hour. Computed over three minutes it is an extrapolation
        /// dressed as a measurement, and the ranking could not tell the two apart: Sindo committed
        /// to Despair Valley on 0.3 hours that read as 751,454 exp/hour, beating Deserted Mine's
        /// 578,794 measured over 1.9 hours, and died there seven times for 300,000 gold in potions.
        /// No sustained hour on that map could ever have produced the number it was chasing.
        ///
        /// Confidence is summed across every band that still carries, not read off the single
        /// freshest record - otherwise crossing a band would reset a map to "unproven" the moment
        /// the first short window landed in the new band, which is the same amnesia Best() already
        /// had to be fixed for once.
        /// </summary>
        public double MinimumSampleHours = 0.25;
        // Maps worth trying first, in order, before falling back to a random reachable one.
        // Comma separated map descriptions, e.g. "Deserted Mine Lv 1,Ant Cave North".
        public string PreferredMaps = "";

        // Maps never chosen as a place to HUNT - by any goal: experience, exploring, books, gear,
        // quests or boss journeys. Routes still cross them (fighting whatever blocks the way, the
        // same as a journey through the upper floors of a cave). Bichon Castle: a huge map whose
        // monsters crowd one small, hard-to-reach corner; its Oma Warlords (Advanced Bloody
        // Flower) also live in Goru Cave, which is well populated.
        public string NoHuntMaps = "Bichon Castle";
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
        public int HuntingDeathPenaltyPercent = 25;
        public int HuntingPickWeightPower = 2;
        // When both book and ordinary grounds qualify, choose the goal before ranking maps.
        // A score bonus alone can still leave the book map as the only shortlisted candidate.
        public int BookHuntChancePercent = 70;
        public int LossBookHuntChancePercent = 20;
        public int MaxConsecutiveBookHunts = 2;
        // Only shopping towns are deferred when their typical monster is this far below us.
        // 0 disables; caves remain eligible regardless of their median level.
        public int TownHuntLevelGap = 10;
        public bool AoeEnabled = true;
        public int AoeMinimumTargets = 3;
        // Class-specific override: every area spell the bot aims (AoeGeometry.Shapes) is a
        // Wizard's, and a wizard's area cast on two monsters already beats Expel Undead or a
        // single-target spell on one of them. Other classes use AoeMinimumTargets.
        public int AoeMinimumTargetsWizard = 2;

        /// <summary>The area-cast threshold for this character's class.</summary>
        public int AoeMinimumFor(Library.MirClass mirClass) => Math.Clamp(
            mirClass == Library.MirClass.Wizard ? AoeMinimumTargetsWizard : AoeMinimumTargets, 1, 10);
        public int LossWatchHours = 4;
        public long LossWatchDropGold = 20000;
        public long LossWatchRecoverGold = 20000;
        public int LossDeathPenaltyPercent = 60;
        public int LossWatchMaxHours = 12;
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
        // Only when a safe, reachable unmeasured map can drop a meaningful wearable upgrade.
        public int UpgradeExploreChancePercent = 25;
        // Capped multiplier within the current ordinary-hunt pool; books keep their own goal.
        public int GearHuntBonusPercent = 50;
        public int MaxConsecutiveGearHunts = 2;
        // Do not explore a map whose TYPICAL monster is more than this far above us. Checked
        // against System.db, because the learned danger model only knows maps that have
        // already hurt us and is therefore silent about anywhere new.
        /// <summary>
        /// How long it takes for a hunting measurement to lose half its weight. 0 keeps the old
        /// behaviour, where every sample ever taken counts equally for ever.
        ///
        /// A plain lifetime mean cannot forget. Bichon Town held a 122,976 exp/hour average built
        /// from 0.4 hours of sampling while its most recent window read 30,285, and Deserted Mine
        /// carried best 793,927 against last 15,033 - so a map measured once, luckily, outranked
        /// maps measured honestly, and no amount of ordinary evidence could catch up.
        ///
        /// Decaying the accumulated experience AND the accumulated hours together leaves the
        /// average itself untouched at rest - it is a ratio, and both halves shrink equally - and
        /// changes only how much the next real sample is allowed to move it. Four hours means a
        /// morning's stale reading is worth about a sixth of a fresh one by evening.
        /// </summary>
        public double HuntingHalfLifeHours = 4;

        /// <summary>
        /// How much to favour a hunting ground that drops a skill book we want but cannot buy,
        /// as a percentage bonus PER wanted book. 0 disables the whole idea.
        ///
        /// This multiplier ranks maps WITHIN the book-hunt pool. The separate book-goal chance
        /// above decides whether to hunt a book at all when ordinary grounds also qualify;
        /// a hard book-only filter formerly sent the warrior back to Desert after 13 deaths.
        ///
        /// It also needs no off switch. The bonus exists only while there is a book to want, so
        /// when the last one is learnt the ranking silently goes back to being about experience.
        /// </summary>
        /// 100 rather than a gentler number because of a real near-miss: a level 22 Taoist chose
        /// Ant Cave North at 248,148 exp/hour over Bichon Cave Lv 1 at 105,728, and Bichon Cave
        /// was the one dropping BOTH Summon Skeleton and Magic Resistance. At 60% per book that
        /// came to 232,602 against 248,148 - it lost by six percent, to a map that drops no skill
        /// book for any class at all. Ant Cave can never make that character stronger; Summon
        /// Skeleton roughly doubles what a Taoist does, permanently. One session of experience is
        /// the wrong thing to weigh against that.
        public int BookHuntBonusPercent = 100;

        /// <summary>
        /// Extra levels above ExploreLevelsAbove that a map dropping a wanted skill book may be
        /// explored at. The danger memory, the three-deaths lethal rule and the death penalty
        /// still decide whether the bot stays; this only lets it look.
        /// </summary>
        public int BookExploreExtraLevels = 10;

        public int ExploreLevelsAbove = 3;

        /// <summary>
        /// Skip a hunting ground whose TYPICAL monster is this many levels below us. 0 disables.
        ///
        /// Every level test in this bot had an upper bound and no lower one. WorthExploring rules
        /// out a map that outclasses us and says nothing whatever about a map we have outgrown, so
        /// nothing anywhere could express "too easy to be worth the walk".
        ///
        /// A level 22 Taoist spent the morning killing chickens in Bichon Town - median monster
        /// level 10, no drops without butchering - because one 0.4 hour sample had recorded it at
        /// 122,976 exp/hour and the ranking had no reason to doubt it. The two starter towns sit at
        /// median 10, the first caves at 18, the mines at 20; eight levels of slack keeps a
        /// character in its own tier and out of the previous one without being so tight that a bot
        /// levelling quickly runs out of anywhere to go.
        /// </summary>
        public int HuntLevelsBelow = 8;

        // --- Travelling and gold ---------------------------------------------------------
        // Never set off on a journey without enough gold to restock on arrival. A bot that
        // lands three maps from home with an empty purse cannot buy potions, cannot buy a way
        // back, and dies. Scaled per map transition, since each hop is another map to cross on
        // foot if it goes wrong.
        public long TravelGoldPerHop = 3000;
        // The same float, for a leg that is WALKED rather than bought.
        //
        // TravelGoldPerHop is the right order for a paid journey: the fare out, a fare home, and
        // enough left to be somewhere far from a vendor. None of that applies to a map you can walk
        // onto. The way back is the same walk, it costs nothing, and the town scroll in the bag is
        // the real emergency exit.
        //
        // Charging the paid rate for a free step is how the poverty deadlock actually held: a level
        // 18 assassin with 907 gold was refused one free map because it could not afford 3,000, and
        // stayed in the two worst hunting grounds it had ever measured for over an hour. Small but
        // not zero - each map crossed is still another map to fight back across.
        public long TravelGoldPerFreeHop = 250;
        // Below this much gold, hunt only on the maps named in TownMaps. Poverty is the one
        // state where wandering is actively harmful: it cannot buy its way out of trouble.
        //
        // Set well above "can afford one potion" on purpose. The bar is not survival for the next
        // ten minutes, it is being able to absorb the ordinary costs of being away from home - a
        // repair bill, a full restock, and a way back - without the trip turning into the death
        // spiral that a broke wizard demonstrated all night. One broken ring cost 611 gold, so a
        // few thousand is barely a handful of repairs.
        public long PoorGold = 10000;

        /// <summary>
        /// Below this, enter recovery mode. 0 disables.
        ///
        /// Permitting the cheap maps turned out not to be enough. The outgrown filter already
        /// lifted while poor, so Bichon Town was allowed - but the hunting ranking is experience
        /// only, so Deserted Mine at 422,000 an hour still won every time. A level 24 wizard with
        /// 23 gold and no mana stood in it at zero of 428 mana meleeing ghosts, spending half its
        /// actions drinking the health potions it could not replace either; its bag fell from 46%
        /// to 7% in eight minutes. Permitted is not preferred, and only preferred gets a broke
        /// character back on its feet.
        ///
        /// Separate from PoorGold, which means "too broke for paid teleports and fussy shopping".
        /// This is the harder line: too broke to be anywhere but the beginner ground.
        /// </summary>
        public long RecoveryGold = 5000;

        /// <summary>
        /// Recovery does not finish merely by crossing RecoveryGold. It stays latched until a
        /// completed town trip leaves this much gold and a usable supply load, so buying the next
        /// batch of potions cannot send the character straight back into poverty.
        /// </summary>
        public long RecoveryExitGold = 25000;

        /// <summary>
        /// Where a broke character earns. Weak mobs it can melee with no mana and no potions, and
        /// the only maps on the server carrying butcherable animals - which is the point, because
        /// meat is what turns those kills into gold.
        /// </summary>
        public string RecoveryMaps = "Bichon Town,Banya Village";

        /// <summary>
        /// Once an experienced recovery-mode character has enough potions and its scroll reserve,
        /// use a better money ground rather than continuing to farm beginner animals.
        /// </summary>
        public string RecoveryCaveMaps = "Flea Cave Lv 1";
        public int RecoveryCaveMinimumLevel = 20;
        public int RecoveryCaveSupplyPercent = 60;
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
        // Low-level gear is replaced too quickly for preserved maximum durability to repay the
        // doubled repair bill. Below this level, skip special and use the ordinary pass instead.
        public int SpecialRepairMinimumLevel = 30;
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
        // Heal our own visible pet at or below this health percentage. Zero disables.
        public int HealPetAtPercent = 60;
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
        // Seconds pinned on one cell before the bot decides it is stuck, whatever else it seems
        // to be achieving. 0 disables.
        //
        // Every other give-up here watches ONE axis: blocked moves, a target that will not die, a
        // drop that cannot be reached, a trip that fails. A bot can fail on all of them at once
        // and trip none, because each looks locally like progress.
        //
        // An assassin did exactly that. Pinned on 51,292 in Deserted Mine for ninety seconds, one
        // tile from the exit, swinging at ghosts and drinking a potion every three seconds - bag
        // 59 down to 23 - while its health sawed between 51% and 84% and never improved. The
        // attack watchdog saw attacks landing. Travel saw an active trip. Nothing was watching the
        // only number that mattered: it had not moved at all.
        //
        // The Mir 2 agents carry the same idea and reach for it sooner - five seconds stationary
        // and they break state and re-roam. Twenty is the equivalent here, because our bot stands
        // still legitimately while fighting something adjacent.
        public int StuckSeconds = 20;
        // How often to re-lay a self-centred area effect that grants no buff - PoisonousCloud is
        // the only one today. It lasts Magic.GetPower() seconds, which the client cannot read, so
        // this is a plain interval. A re-cast while the cloud is still up is refused silently, so
        // erring long costs a little uptime and erring short costs nothing but a wasted request.
        public int SelfAoeRecastSeconds = 25;
        // Seconds of finding nothing to fight before a wander becomes a SWEEP towards the next
        // floor. 0 disables and roaming stays purely local.
        //
        // Roaming picks a random walkable point within RoamRadius of where the bot stands, which
        // is a local search: it explores the pocket it is already in and has no way to conclude
        // "there is nothing here, go further in". In a cave that is exactly the wrong shape. The
        // caves run about a third of the monster density of the town maps - Deserted Mine Lv 1
        // measured a median of 3 monsters in view against 16 for Banya Village, and nothing at all
        // 14% of the time - so a bot that walks a decent way in and then mills back and forth is
        // the expected behaviour of a local search on a sparse map, not a bug in the pathing.
        //
        // The sweep gives the wander a direction: head for the stairs down. Combat still preempts
        // it - Wander is the last branch of Decide - so anything met on the way is fought and the
        // sweep resumes afterwards, which is the "fight, then carry on" pattern asked for.
        public int SweepAfterIdleSeconds = 45;

        /// <summary>
        /// Minutes without a single point of experience before the hunting ground is reconsidered.
        /// 0 disables.
        ///
        /// The bot has always MEASURED this - WorldModel.LastExperienceGainUtc feeds the "last
        /// kill: 26 minutes ago" line on the status page - and no decision anywhere consulted it.
        /// So the one number that says plainly "whatever I am doing is not working" was collected,
        /// displayed to a human, and ignored by the bot.
        ///
        /// Travel was reconsidered on exactly four events: a town trip ending, the map having no
        /// spawns at all, the map being outgrown, and a storage errand. A level 25 wizard walled
        /// into a pocket of Banya Cave Lv 1 - a map with 280 spawns, so not barren, and level
        /// appropriate, so not outgrown - matched none of them. It roamed for twenty-five minutes
        /// with no trip to finish, and nothing would ever have moved it.
        ///
        /// The cave sweep is the local answer to this and it is not enough: it walks towards the
        /// next floor, and a bot that cannot REACH the next floor abandons the sweep (correctly)
        /// and goes back to roaming the same pocket. Leaving the map is the escalation that was
        /// missing.
        /// </summary>
        public int UnproductiveMinutes = 12;

        /// <summary>
        /// While travelling, stop and fight anything this close. 0 keeps running regardless.
        ///
        /// Travel deliberately outranks combat - a bot that stops to kill everything never gets
        /// anywhere - and that is right for crossing an empty map. It is badly wrong for crossing
        /// a full one. A level 25 Taoist routed through Lost Paradise Cave Lv 1 with THIRTY-SEVEN
        /// live monsters on screen, and for fourteen seconds its only decisions were Heal,
        /// Approach, Heal, Approach. It moved two tiles. Monsters occupy cells and the server
        /// refuses a move into one, so the things hitting it were also the things blocking it -
        /// and the bot was choosing, every tick, to walk rather than remove the obstacle.
        ///
        /// One tile by default: an adjacent monster is in contact, is blocking, and cannot be
        /// outrun because monsters follow. Anything further away is a fight we would be choosing,
        /// and the journey is more important than that choice.
        /// </summary>
        public int FightThroughRange = 1;

        /// <summary>
        /// Seconds of fighting WITHOUT A KILL before the journey's stall watchdog is allowed to
        /// fire again. 0 means never pause it.
        ///
        /// Fighting does not close the distance to the exit, so without this the 30-second
        /// no-progress watchdog would abort a journey precisely because the bot did the right
        /// thing. It used to be total fight time, which released a winning assassin into a
        /// 35-monster pack beside Deserted Mine's stairs mid-fight. Measured from the latest kill
        /// now, and capped overall by FightThroughMaxSeconds.
        /// </summary>
        public int FightThroughSeconds = 90;

        /// <summary>
        /// Hard ceiling on one fight-through engagement however well it is going, so a respawn
        /// point cannot hold a journey for ever. 0 means no ceiling.
        /// </summary>
        public int FightThroughMaxSeconds = 600;

        /// <summary>
        /// How many times an abandoned journey to the same hunting ground is resumed (after the
        /// scroll-out and town trip) before the bot makes a fresh choice. 0 disables resuming.
        /// </summary>
        public int JourneyRetries = 3;

        /// <summary>Failures older than this no longer count against JourneyRetries.</summary>
        public int JourneyRetryWindowMinutes = 60;

        /// <summary>
        /// While travelling, ordinary drops must sell for at least this much to be picked up;
        /// books, item parts, potions, town scrolls, gold and gear upgrades are always taken.
        /// Suspended below PoorGold and during poverty recovery. 0 disables.
        /// </summary>
        public long JourneyLootMinValue = 1500;

        /// <summary>
        /// Monster AI numbers that never start a fight, so crossing a map need not stop for them.
        ///
        /// The passive animals - 1 Chicken, 2 Cow/Deer/Pig/Sheep, 5 Carnivorous Plant, the same
        /// set the butcher index uses. Walking past a deer costs nothing, and a Taoist that stops
        /// to kill every chicken between Bichon Town and a cave arrives with its mana gone and
        /// nothing to show for it. Fighting through is for things that are actually hitting us.
        /// </summary>
        public string HarmlessAIs = "1,2,5";

        /// <summary>
        /// While travelling, a monster whose WORST recorded hit is below this percentage of our
        /// maximum health is not worth stopping for. 0 disables the damage test.
        ///
        /// HARMLESS IS A RELATIONSHIP, NOT A PROPERTY. The fixed HarmlessAIs list only covers the
        /// animals that never fight at all; it says nothing about a Claw Cat, which is a real
        /// threat to a level 13 wizard and completely beneath a level 32 warrior. Judging by AI
        /// alone therefore had the warrior hold its journey to kill things that could not
        /// meaningfully hurt it, burning mana and potions crossing a starter map.
        ///
        /// Measured rather than assumed: MonsterMemory already records the worst hit every
        /// monster has landed on us, which is the honest answer to "can this thing hurt me" and
        /// improves as the character grows.
        /// </summary>
        public int FightThroughHarmlessPercent = 3;

        /// <summary>
        /// Fallback when we have never been hit by it: treat it as harmless when its level is at
        /// least this far below ours. 0 disables the fallback, so an unknown monster is respected.
        ///
        /// The damage record is the better signal but it only exists after the thing has hit us.
        /// Level is what we can know in advance, from the monster database.
        /// </summary>
        public int FightThroughLevelsBelow = 12;

        /// <summary>
        /// Once fighting through has started, keep fighting until nothing hostile is within this
        /// range. 0 falls back to FightThroughRange, i.e. the old behaviour.
        ///
        /// WITHOUT HYSTERESIS THE RULE FIGHTS ITS OWN PURPOSE. Settling the moment nothing is
        /// ADJACENT meant travel resumed with two dozen monsters still on screen: the bot killed
        /// the one in its face, took a step, pulled two more, killed one, took a step. A Taoist
        /// crossing Lost Paradise Cave Lv 1 did that 56 times in a couple of minutes, walking
        /// deeper into the cave between every fight and dragging a bigger train each time.
        ///
        /// Clearing the area before moving on is both safer and faster: the fight happens once,
        /// standing still, instead of continuously while being chased.
        /// </summary>
        public int FightThroughClearRange = 8;
        // Walk through to the next floor on reaching it, rather than stopping at the stairs.
        //
        // The sweep only ever aims at a DEEPER floor of the same cave - matched on the map name,
        // so "Deserted Mine Lv 1" will aim at "Deserted Mine Lv 2" and never back towards town.
        // Entering is off by default because the brain cannot see the hunting memory or the danger
        // tables that Travel consults, so it cannot tell that the next floor is one that has been
        // killing us. With it off the bot sweeps the full length of the map and turns around,
        // which is the useful half of the behaviour and carries no new risk.
        public bool SweepEntersNextFloor = false;
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

            int unit = System.Math.Max(1, itemWeight);
            int weightBudget = maxBagWeight * percent / 100;
            int count = weightBudget / unit;

            int target = System.Math.Max(floor, count);

            // THE FLOOR IS A COUNT AND THE BUDGET IS A WEIGHT, so for anything heavy the floor can
            // demand far more bag than the budget allows - and then the two halves of the system
            // disagree about the same number. The buyer tops up to the floor; the keeper trims to
            // the budget and calls the difference surplus; the next trip buys it back.
            //
            // A level 26 warrior found the case: Elixir Of Life (V) weighs NINETEEN, so an 82-weight
            // budget is four of them, but HealthPotionReserve is ten. Buy to ten (190 weight), keep
            // four, sell six, buy six. A shopping loop that also spends the gold each way.
            //
            // The floor exists for small bags, where a percentage of very little is not enough to
            // survive on. It has no business overriding a budget that already affords more than one.
            if (count >= 1 && (long)target * unit > weightBudget) target = count;

            return System.Math.Max(1, target);
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
                case "minpotionrestore": config.MinPotionRestore = int.Parse(value); break;
                case "resyncaftersellrefusals": config.ResyncAfterSellRefusals = int.Parse(value); break;
                case "resyncwindowminutes": config.ResyncWindowMinutes = int.Parse(value); break;
                case "fleeatpercent": config.FleeAtPercent = int.Parse(value); break;
                case "aggrorange": config.AggroRange = int.Parse(value); break;
                case "allowrunning": config.AllowRunning = bool.Parse(value); break;
                case "lootenabled": config.LootEnabled = bool.Parse(value); break;
                case "lootrange": config.LootRange = int.Parse(value); break;
                case "gathersaferange": config.GatherSafeRange = int.Parse(value); break;
                case "butcherenabled": config.ButcherEnabled = bool.Parse(value); break;
                case "butcherais": config.ButcherAIs = value; break;
                case "butcherrange": config.ButcherRange = int.Parse(value); break;
                case "butcherattempts": config.ButcherAttempts = int.Parse(value); break;
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
                case "dropreplacedstarterkit": config.DropReplacedStarterKit = bool.Parse(value); break;
                case "unlocktosell": config.UnlockToSell = bool.Parse(value); break;
                case "townmaps": config.TownMaps = value; break;
                case "notifywebhookurl": config.NotifyWebhookUrl = value; break;
                case "notifylevelup": config.NotifyLevelUp = bool.Parse(value); break;
                case "notifyupgrade": config.NotifyUpgrade = bool.Parse(value); break;
                case "notifyskill": config.NotifySkill = bool.Parse(value); break;
                case "notifydeath": config.NotifyDeath = bool.Parse(value); break;
                case "notifyfault": config.NotifyFault = bool.Parse(value); break;
                case "notifyquest": config.NotifyQuest = bool.Parse(value); break;
                case "enablequests": config.EnableQuests = bool.Parse(value); break;
                case "questnpcs": config.QuestNPCs = value; break;
                case "nohuntmaps": config.NoHuntMaps = value; break;
                case "questminibossminlevel": config.QuestMiniBossMinLevel = int.Parse(value); break;
                case "questhuntchancepercent": config.QuestHuntChancePercent = int.Parse(value); break;
                case "maxconsecutivequesthunts": config.MaxConsecutiveQuestHunts = int.Parse(value); break;
                case "enablefame": config.EnableFame = bool.Parse(value); break;
                case "notifyfame": config.NotifyFame = bool.Parse(value); break;
                case "notifycombine": config.NotifyCombine = bool.Parse(value); break;
                case "enablecombine": config.EnableCombine = bool.Parse(value); break;
                case "combinenpcs": config.CombineNPCs = value; break;
                case "nohuntskills": config.NoHuntSkills = value; break;
                case "questbossminlevel": config.QuestBossMinLevel = int.Parse(value); break;
                case "enablestore": config.EnableStore = bool.Parse(value); break;
                case "storerebuyminutes": config.StoreRebuyMinutes = int.Parse(value); break;
                case "notifyidleminutes": config.NotifyIdleMinutes = int.Parse(value); break;
                case "returnwithin": config.ReturnWithin = int.Parse(value); break;
                case "detoursteps": config.DetourSteps = int.Parse(value); break;
                case "mappath": config.MapPath = value; break;
                case "memorypath": config.MemoryPath = value; break;
                case "experiencesampleminutes": config.ExperienceSampleMinutes = int.Parse(value); break;
                case "minimumsampleminutes": config.MinimumSampleMinutes = int.Parse(value); break;
                case "minimumsamplehours": config.MinimumSampleHours = double.Parse(value); break;
                case "preferredmaps": config.PreferredMaps = value; break;
                case "levelbandsize": config.LevelBandSize = int.Parse(value); break;
                case "autotravel": config.AutoTravel = bool.Parse(value); break;
                case "huntingchoices": config.HuntingChoices = int.Parse(value); break;
                case "huntingdeathpenaltypercent": config.HuntingDeathPenaltyPercent = int.Parse(value); break;
                case "aoeenabled": config.AoeEnabled = bool.Parse(value); break;
                case "aoeminimumtargets": config.AoeMinimumTargets = int.Parse(value); break;
                case "aoeminimumtargetswizard": config.AoeMinimumTargetsWizard = int.Parse(value); break;
                case "huntingpickweightpower": config.HuntingPickWeightPower = int.Parse(value); break;
                case "bookhuntchancepercent": config.BookHuntChancePercent = int.Parse(value); break;
                case "lossbookhuntchancepercent": config.LossBookHuntChancePercent = int.Parse(value); break;
                case "maxconsecutivebookhunts": config.MaxConsecutiveBookHunts = int.Parse(value); break;
                case "townhuntlevelgap": config.TownHuntLevelGap = int.Parse(value); break;
                case "losswatchhours": config.LossWatchHours = int.Parse(value); break;
                case "losswatchdropgold": config.LossWatchDropGold = long.Parse(value); break;
                case "losswatchrecovergold": config.LossWatchRecoverGold = long.Parse(value); break;
                case "lossdeathpenaltypercent": config.LossDeathPenaltyPercent = int.Parse(value); break;
                case "losswatchmaxhours": config.LossWatchMaxHours = int.Parse(value); break;
                case "exploreuntilmapsknown": config.ExploreUntilMapsKnown = int.Parse(value); break;
                case "explorechancepercent": config.ExploreChancePercent = int.Parse(value); break;
                case "upgradeexplorechancepercent": config.UpgradeExploreChancePercent = int.Parse(value); break;
                case "gearhuntbonuspercent": config.GearHuntBonusPercent = int.Parse(value); break;
                case "maxconsecutivegearhunts": config.MaxConsecutiveGearHunts = int.Parse(value); break;
                case "explorelevelsabove": config.ExploreLevelsAbove = int.Parse(value); break;
                case "bookhuntbonuspercent": config.BookHuntBonusPercent = int.Parse(value); break;
                case "bookexploreextralevels": config.BookExploreExtraLevels = int.Parse(value); break;
                case "huntinghalflifehours": config.HuntingHalfLifeHours = double.Parse(value); break;
                case "huntlevelsbelow": config.HuntLevelsBelow = int.Parse(value); break;
                case "travelgoldperhop": config.TravelGoldPerHop = long.Parse(value); break;
                case "poorgold": config.PoorGold = long.Parse(value); break;
                case "unproductiveminutes": config.UnproductiveMinutes = int.Parse(value); break;
                case "fightthroughrange": config.FightThroughRange = int.Parse(value); break;
                case "fightthroughseconds": config.FightThroughSeconds = int.Parse(value); break;
                case "fightthroughmaxseconds": config.FightThroughMaxSeconds = int.Parse(value); break;
                case "journeyretries": config.JourneyRetries = int.Parse(value); break;
                case "journeyretrywindowminutes": config.JourneyRetryWindowMinutes = int.Parse(value); break;
                case "journeylootminvalue": config.JourneyLootMinValue = long.Parse(value); break;
                case "harmlessais": config.HarmlessAIs = value; break;
                case "fightthroughharmlesspercent": config.FightThroughHarmlessPercent = int.Parse(value); break;
                case "fightthroughlevelsbelow": config.FightThroughLevelsBelow = int.Parse(value); break;
                case "fightthroughclearrange": config.FightThroughClearRange = int.Parse(value); break;
                case "sweepafteridleseconds": config.SweepAfterIdleSeconds = int.Parse(value); break;
                case "recoverygold": config.RecoveryGold = long.Parse(value); break;
                case "recoveryexitgold": config.RecoveryExitGold = long.Parse(value); break;
                case "recoverymaps": config.RecoveryMaps = value; break;
                case "recoverycavemaps": config.RecoveryCaveMaps = value; break;
                case "recoverycaveminimumlevel": config.RecoveryCaveMinimumLevel = int.Parse(value); break;
                case "recoverycavesupplypercent": config.RecoveryCaveSupplyPercent = int.Parse(value); break;
                case "pursuitpatience": config.PursuitPatience = int.Parse(value); break;
                case "lootpoorgold": config.LootPoorGold = long.Parse(value); break;
                case "lootrichgold": config.LootRichGold = long.Parse(value); break;
                case "lootgoldperweightpoor": config.LootGoldPerWeightPoor = int.Parse(value); break;
                case "lootgoldperweightrich": config.LootGoldPerWeightRich = int.Parse(value); break;
                case "lootheavymultiplier": config.LootHeavyMultiplier = int.Parse(value); break;
                case "storagesize": config.StorageSize = int.Parse(value); break;
                case "repairatdurability": config.RepairAtDurability = int.Parse(value); break;
                case "preferspecialrepair": config.PreferSpecialRepair = bool.Parse(value); break;
                case "specialrepairminimumlevel": config.SpecialRepairMinimumLevel = int.Parse(value); break;
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
                case "healpetatpercent": config.HealPetAtPercent = int.Parse(value); break;
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
