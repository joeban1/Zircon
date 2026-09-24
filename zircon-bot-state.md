# MirBot — current state and open issues

Last updated: **2026-09-23**. Written as a handover so a fresh session has full context without
re-deriving it. Read [zircon-bot-inventory-model.md](zircon-bot-inventory-model.md) first if you
are touching items, selling or the bag.

## 2026-09-23 System.db Notice Board/fame update

The active VM master, VM client, desktop client and MirBot data copy now hash-match patched
`System.db` version `2026.09.23.2` (SHA-256
`59B0059527480E6A061938B0658FAA92301B36C763E797264A7ABB56AB160E98`). It includes
missing Bichon Notice Board quest children, donor fame buffs/rewards, and the operator's existing
shoe PartCount edits from the newer VM master. MirBot PID 18200 loaded this version; after the
operator restarted the game Server, all eight bots returned to Playing without a version mismatch. Full source,
backup and validation detail is in [zircon-npc-authoring.md](zircon-npc-authoring.md).

## 2026-09-23 poison restock and cave-descent journeys

- **Jill never bought poison.** `TownTrip.ReagentNeeded` returned one type, amulets first, and one
  reagent purchase per stop was allowed. Seven/Lennard sell Poison and Amulets on the SAME page and
  a Taoist is always a few talismans short, so the stop's purchase was always talismans. Now
  `ReagentsNeeded` returns every short type, the buy step latches per type per stop, and routing
  adds a seller for each. Verified live: `300 x Green Poison` bought and equipped after the talisman
  top-up at Seven (16:10:51), then `Cast (Poison Dust on Devouring Ghost)` at 16:15:52.
- **Journeys died near deep-cave stairs** (22 "no route to the exit" + 30 "stuck N tiles" failures
  across two log files). Dreadlord, 14:04-14:06: the 90-second fight-through hold was TOTAL time,
  so it expired mid-pack; the next bumped move made every monster an A* obstacle; one failed search
  aborted the journey; `EscapeOnScroll` scrolled out at 80% HP; the next choice re-rolled to Flea
  Cave. Three changes, all only active while `Travel.Active` (on-map roaming untouched):
  1. When the monster-aware route fails, retry on walls only. If that works and a target is
     adjacent, fight (holding the stall watchdog only while `WinningFights`) instead of aborting.
     If walls alone block, try the exit's other cells (`Journey.TryAnotherExitCell`) before
     aborting.
  2. `FightThroughSeconds` (90) is now measured from the latest kill; new `FightThroughMaxSeconds`
     (600) caps one engagement.
  3. An abandoned hunting journey (not deaths or planning failures) is remembered and resumed at
     the next travel decision, up to `JourneyRetries` (3) within `JourneyRetryWindowMinutes` (60).
  Log lines to watch: `boxed in by monsters`, `trying X,Y instead`, `will resume the journey`,
  `resuming after N failed attempt(s)`, `reached X after N failed attempt(s)`. Not yet observed
  live at deploy time.
- 82/82 tests (6 new in `JourneyAndReagentTests`), zero warnings. Backups (runtime and source before
  and after) are in `C:\HomeServer\mirbot-deploy-backups\2026-09-23-reagents-journey`. Host PID
  34564, all eight bots Playing.
- **Follow-up, 18:14 deploy: travelling loot filter.** Observed 17:02-18:10: fix 2 held fights
  95-274s (past the old 90s cut-off) and Dreadlord reached Flea Cave Lv 2, but Wizzler's three
  Deserted Mine Lv 2 runs each ended at 45/48 slots, 43-91 tiles from the stairs (up to 121
  pickups per run) and restarted from the entrance. While `Travel.Active`, `ScriptedBrain.
  FindWorthwhileLoot` now also requires `Backpack.WorthLootingOnJourney`: books, item parts, real
  health/mana potions, town scrolls, gold, gear upgrades, or sale value >= `JourneyLootMinValue`
  (1500). Off below `PoorGold` and while poverty recovery is active (`ScriptedBrain.InRecovery`).
  Also logs fix 1's previously silent walls-only branch (`route blocked by creatures`). 84/84
  tests; backups in `mirbot-deploy-backups\2026-09-23-journey-loot`; host PID 17200.
- **18:27 refinement:** the first run still filled 20 slots in five minutes, mostly other-class
  books (1,000 gold, one slot each) and weightless Zombie Bone. While travelling, books now pass
  only with a `MagicBooks.Judge` verdict of Wanted/TooEarly; other books face the price rule.
  The free pass is limited to `ItemType.Currency` and items that `OccupiesASlot` says take no slot.
  86/86 tests; backups in `2026-09-23-journey-books`; host PID 18552.
- **18:32:** Wizzler reached Deserted Mine Lv 2 in 3.5 minutes (11->12 slots) after four
  bag-full failures earlier in the evening.
- **19:24 deploy (stackables, doors, route line):** while travelling, any stackable
  (`StackSize > 1`: bones, husks, gems, eggs; books and ores never stack here) is taken
  regardless of price. `/api/map` now carries `doors` (walk-on `MapExit` regions: centre cell,
  destination, cell count; NPC teleports excluded), drawn as labelled purple squares on the status
  minimap. `/api/status` carries `route` (flattened x,y pairs of the A* path from
  `ScriptedBrain.TrySteer`, trimmed to ~200 points, stale after 3s) and `routeKind`
  (travel/town/target/roam/loot/move), drawn as a coloured line with the heading cross matching.
  Page JS was syntax-checked with Node and rendered in headless Edge. 88/88 tests; backups in
  `2026-09-23-map-route`; host PID 28328.
- **20:44 deploy (Info filters):** Map selections and trips gained search, character,
  destination and status (active/left/never arrived) filters over 2,000 rows; Recent deaths gained
  search, character, map and killer filters over 500 rows. Dropdowns only rebuild when their
  options change, so the 30s refresh no longer closes an open one. Verified by driving headless
  Edge over CDP. Host PID 29476; backups in `2026-09-23-info-filters`.

- **21:00 deploy (phone notifications):** new `Notifier` (queued background HTTP POST to a Home
  Assistant webhook) hooked into level-up, confirmed upgrade, successful skill, death, terminal
  fault/exception and a once-per-drought idle alert. Settings and setup are in the runbook's
  "Phone notifications" section. 90/90 tests (2 new in `NotifierTests`); ini and binaries backed
  up in `2026-09-23-notify`; host PID 55160.
- **21:35 deploy (Notifications tab):** a dedicated tab with switches for each type, the
  idle-minutes control, a test button and recent-notification history with outcomes. The first
  build had a race: saving a switch re-read `/api/config` before the bots had applied the queued
  change, so the switch flipped back and the next click inverted the setting. It now holds the new
  value until the bots agree (10s cap). Fixed and verified through a temporary `status.html`,
  then compiled in and the override removed. The Notify settings are back to their defaults.
  Backups in `2026-09-23-notify-tab`; host PID 10704.

- **22:10 deploy (route costing + trip deaths):** Mirbot died three times crossing Phantom Forest
  (20:21, 20:24, 21:03) on the way to Zuma Temple Lv 1 / Red Moon Valley Lv 1. Two causes:
  `WorldGraph.Route` was a BFS on map changes only, and on a tie walk-on exits (listed before
  NPCs) won, so Banya -> Phantom Forest -> Zuma (~320 tiles) beat Banya -> Hexa Stone (3,000g) ->
  Sabuk Keep -> Zuma (~160). And `Lethal()` exempts any map with a measured rate; since deaths
  close the XP window, 4 minutes of kills between 3 deaths read as 1.46M exp/h, so the map was
  never avoided. (The Phantom Forest -> Zuma exit is real - operator checked in game.)
  Now `Route` is a cost search in estimated tiles: walk from entry point (new `MapExit.Arrival`)
  to exit, +15 per hop, teleports +10 plus fare x 1000 / gold held, plus `Journey.DangerTiles`
  for maps crossed = 150 per death of this class (`HuntingMemory.DeathsByMap`, own and previous
  band, no has-paid exemption), capped at 1,500. `Lethal()`/`Avoid` and hunting choice unchanged.
  `--check-travel` accepts `"Origin@x,y>Destination"` and prints L42/2M-gold routes with leg costs.
  Map trips now close as `died on the way in <map> (<killer>)` or `died (<killer>)` at death.
- **22:50 deploy (vendorless scroll landing, journey clock, Lv 3 book floors):** backup
  `mirbot-deploy-backups/2026-09-23-trip-books`, host PID 12188.
  1. The Hexa Stone route walks through Sabuk Keep's safe zone, so the town scroll then binds
     there - no vendors. Mirbot scrolled out of Ant Cave North at 45/48 slots, the trip aborted
     "landed on Sabuk Keep with nothing to do there" and travel went straight back to the cave.
     New `TownTrip.NeedsVendor` (overweight/repairable/out of slots) joins ShortOfSupplies and
     NeedsStorage in ConsiderTravel's "go to the nearest vendor town" rule.
  2. `Journey`'s 30s no-progress clock ran during town trips (Banking/Returning return null
     ticks, so the brain fell through to `Travel.Next`), giving "stuck 197 tiles" after every
     shop and then a forced scroll trip. The brain now skips the journey while `Town.Active`
     and calls `Journey.Hold()`.
  3. Summon Shinsu (L30) and Summon Jin Skeleton (L33) drop 1/20 only from Skeleton Lord (Bichon,
     Banya, Lost Paradise Cave Lv 3) and Ghoul Champion (Deserted Mine Lv 3), both IsBoss L250.
     Taoists never saw them: exploit needs measured maps, and TryExplore held back Lv 3 because a
     shallower floor was unmeasured at band 6. `HeldBackFloor` exempts a wanted-book floor whose
     median is <= level - ExploreLevelsAbove, and a reachable one raises explore chance to the
     book-hunt chance (`HasSafeUnmeasuredBookMap`).
- **2026-09-24 07:0x deploy (boss books, skills, level 4):** backup
  `mirbot-deploy-backups/2026-09-24-boss-books`. 101 tests.
  - Charged warrior skills: Blade Storm / Dragon Rise / Flaming Sword need C.MagicToggle to arm
    the next swing (mana + cooldown, 12s); SkillSet.PendingCharge sends it in contact, and
    ChooseAttackMagic names charges first, then Destructive Surge over Half Moon. Shoulder Dash
    excluded. Mirbot/Banner had Dragon Rise/Blade Storm at skill level 0 before this.
  - Expel Undead (Wizard) via SpellBook.ChooseExpel: undead, non-boss, monster level < 70 and
    <= ours - 2, target >= 50% HP, 2 tries per target (server instant-kill roll).
  - Taoist summons: best of Jin Skeleton (2 amulets) > Shinsu (5) > Skeleton (1); added beside
    a weaker pet (cap 2). NeedsAmulet covers the new summons.
  - Level 4: a known level 3 skill + DROPPED book (not NonRefinable - bought books are refused)
    judges Wanted; each successful read adds the book's durability as pages, 500 for level 4.
    CheckPendingLearn logs "Trained X: +N pages". BookDropIndex now indexes every dropped book;
    Wanted() adds trainable skills, which count on low caves too (operator's call).
  - Loot: a wanted book on the floor (unlearned first, then training) is picked before anything.
  - MapProfile.EffectiveLevel: labels above 60 (and bosses) re-estimated from HP and max DC/MC
    against the <=60 curve; Desert Dungeon 68 -> 55, Underground 68 -> 56, endgame maps ~55-57
    (server combat does not use monster level). `--maps` shows "(stated N)".
  - Book maps may be explored up to ExploreLevelsAbove + BookExploreExtraLevels (10) above us.
  - Boss lairs (BossLairIndex, 86 regions): on a map with a MINI-boss (>=2 spawns, <=60 min) that
    drops a wanted book, roaming walks to its lair (checked again after its respawn time), a
    visible one is targeted ahead of nearer monsters (not ahead of one hitting us), and the
    journey loot filter applies while > 12 tiles from the lair. Tainted Terror/world bosses excluded.
  - Boss kill log: boss-kills.json, /api/boss-kills, Info tab "Boss kills" (search/filters,
    loot gained within 2 min). A kill = boss dies within 30s of this bot hitting it.
  - Follow-up deploy (backup `2026-09-24-learn-fix`): Spirit Sword marked passive (it is the
    Taoist Swordsmanship, accuracy on every swing), and a second book read now resolves the
    pending one first (Jill's Spirit Sword read was overwritten by a Poison Dust read 3s later).
    Jill's Spirit Sword was genuinely level 3 (112/500 pages); a consumed book on a known skill
    proves level >= 3, since the server refuses below that without consuming it.
  - Boss drop tracking (backup `2026-09-24-boss-drops`): at a boss death the ground items within
    8 tiles are baselined; items new 2s later are its drop, each followed for 4 minutes as taken
    (vanished within 5s of a C.PickUp on its cell) / gone / left. Stored as BossKillEntry.Dropped,
    logged as "Boss drop for X: ...", shown in the Info tab's Dropped column. Context: this
    server's Ghoul Champion drops no gear - 90,000 gold, 10x 1/4 Rejuvenation Potion, ~45 book
    rolls (all classes, 1/10-1/50), oils; other classes' books are left unless worth 1,500+.
  - Scrolls / stranded bots (backup `2026-09-24-scrolls`): Sindo spent its last scroll on the
    "stuck, scroll clear" rule in Banya Village, the trip was cut short at 35% HP, and the journey
    resume sent it to Deserted Mine Lv 3 with no scrolls; there it filled 48/48 slots and the
    trip aborted every time ("no town scroll to reach one") because a trip that begins and aborts
    in one tick was only promoted to travel for storage. Now: out of town scrolls is a supply
    shortage (latched off if a completed trip still bought none); ConsiderTravel promotes
    NeedsStorage/NeedsVendor/ShortOfSupplies/WalkToTownRequested (30s rate limit); NeedsVendor
    on slots needs something disposable; TryResumeJourney waits while supplies/vendor are needed;
    StartTravel stays in town when the last trip never traded (TownTrip.LastTripTraded) and is
    still short. The Town button sets WalkToTownRequested, so without a scroll it walks.
    Deploy gotcha: Copy-Item right after Stop-Process can hit a still-locked MirBot.dll; copy
    with retry and start only when all three hashes match. Stops sent while bots are still
    logging in are ignored - wait for Playing before stopping.
  - LOMCN vs ZirconBuild System.db: same 173 magics/levels; 10 differ only in School; Summon
    Skeleton SOLD vs drop-only, Scorched Earth the reverse. Cross Half Moon exists in neither DB
    nor in `LibraryCore/Enum.cs` MagicType.
  Verified live at 22:11: Mirbot to Zuma Temple planned `Banya Village to Sabuk Keep via Hexa
  Holy Stone for 3,000 gold`. 93/93 tests (3 new `RoutePlanningTests`); backups in
  `2026-09-23-route-cost`; host PID 50052.

## Bosses and mini-bosses in System.db (checked 2026-09-23)

There is **no mini-boss flag**. `MonsterInfo.Flag` is `None` for all of them, and
`IsBoss` is set on 68 monsters, 65 of which are also level 250 (the level is a boss marker, not
difficulty). What separates mini-bosses from real bosses is the spawn data (read with the new
`SPAWNDUMP_MONSTERFIELDS=1` mode, run against a copy of System.db):

| Kind | Examples | HP | Spawns | Respawn |
| --- | --- | --- | --- | --- |
| Mini-boss | Ghoul Champion (Deserted Mine Lv 3), Skeleton Lord, Terror Spike, Oma Hero, Ant Commander | 330-7,700 | 2-8 | mostly 30 min |
| Boss | Arch Lich Taedu, Zuma King, Emperor Sa'Woo, Uma King, Red Moon The Fallen | 13,000+ | 1 | 120-360 min |

A workable rule: `IsBoss && spawnCount >= 2 && maxDelay <= 60` = mini-boss (Chaos Knight,
1 spawn / 40 min / 6,600 HP, is the borderline case).
- **Open:** after restart every bot logged `System.db version mismatch - bot has 2026.09.22.3,
  server has 2026.09.23.1`. The 14:11 session had no warning, so the server master changed later
  that day; the desktop client and MirBot copies are still the identical 09.22.3 file.

## 2026-09-23 Info-tab progress logs and part lookup

- The Info tab now has **Upgrades** and **Skills** tables, both filterable by character. Confirmed
  equipment replacements are recorded only after a successful `S.ItemMove`, with prior/new item
  names and the class-specific score delta; empty-slot equips and reagent merges are excluded.
  Book attempts use the existing three-second outcome check and record success/failure with bot,
  character, class and attempt time. Both feeds persist in `memory/progress.json` and are served by
  `/api/upgrades` and `/api/skills-history`. The host's periodic and shutdown flush include it.
- On first launch with no progress history, dated `LearnBook` and `Learned`/`LEARN REFUSED` pairs
  are imported idempotently from the current bot log and its two rotations. This launch imported
  19 skill outcomes. Undated lines are skipped. Historic `Equip` lines are **requests**, not server
  verdicts, so they cannot safely be backfilled as confirmed upgrades.
- The Loot decision now uses the ground packet's full item instance and `Backpack.Describe` to
  name a part by its target, e.g. `Iron Shield (part 1/5)`, so future drop-lookup searches can find
  it. Older `Loot ([Part])` lines lack target metadata and remain generic. The server's
  `ItemObject.GetInfoPacket` sends the full client item, including added stats; the bot retains it
  when later data packets update the ground object.
- Deployment passed 76/76 tests and a warning-free Release build; the previous runtime binaries
  are in `C:\HomeServer\mirbot-deploy-backups\2026-09-23-progress-history`. The hidden host was
  restarted, and all eight bots returned to Playing with no reported faults. The served page
  contains both tables and the Skills API returned 19 backfilled rows. A live future gear
  replacement/part pickup has not yet occurred in this verification window.

## 2026-09-22 base bag-weight parity

- The VM server master at `C:\ZirconBuild\Debug\Server\Database\System.db` was the source of
  truth; a newer mob-spawn edit made its initial SHA-256
  `C6E42FBB225AE92EAE1447544254CB3070CF29930DD5A6930332BF876778CC03`, unlike the older
  desktop client copy. A scoped `BaseStat.BagWeight` patch copied the Warrior value to Wizard,
  Taoist and Assassin at each matching level 1-90 (264 differing rows). No level rows were
  missing or duplicated. Read-back found zero differences. The canonical respawn/region
  fingerprint was unchanged: `1F246DB9AEB26BC758E971D52382D1ABE6BD9ECD0560211BE40527C2F50327A6`.
- With the Server application closed and port 7000 down, the patched file was copied to the VM
  server and VM client, desktop client, and MirBot data directory. All four hashes match SHA-256
  `BE24361B90CD8C12B1B38DA56D0A3B8EE2EEEB82F8BE26CB1661FFE7391757D0` (database
  version `2026.09.22.3`). Source, patched copy, audit tool and desktop backups are in
  `C:\HomeServer\bagweight-sync-20260922`; VM master/client backups are in
  `C:\ZirconBuild\BagWeightStage-20260922`. Bot host was restarted to load the new database,
  but the Server remained stopped awaiting the operator's restart.
- After the operator restarted the Server, TCP 7000 was reachable and all eight bots returned to
  `Playing`; the host API reported database version `2026.09.22.3`.

## 2026-09-23 gear-targeted hunting

- The bot now indexes normal monster equipment drops per map and checks each bot's current
  class, gender, level, worn score, bag and storage before treating a drop as an upgrade. A
  target needs a meaningful score gain and enough ordinary spawn/drop opportunity; boss,
  event, seasonal and over-level sources do not qualify. A target retires automatically when the item
  is acquired or no longer beats the worn equipment.
- With at least four maps measured, the normal 15% exploration chance rises to 25% when an
  affordable, safe, reachable unmeasured map has a worthwhile upgrade. Exploration keeps the
  existing level and death checks, shallow-floor rule and near-town preference. Ordinary
  measured hunts receive up to a 50% bounded score bonus, scaled by upgrade value and drop
  opportunity. Gear-priority choices stop after two consecutive selections, and gear priority
  is suspended during poverty recovery or loss watch. Book maps keep their existing separate
  choice rule.
- A gear-driven choice now fills **Farming destination (last choice)** with the chosen map and
  `looking for gear upgrade: <item> (score +N)`. This is the reason in both the status API and
  selected bot GUI; the current location remains separate. The field is blank after restart
  until that bot makes a new destination choice.
- The final release build passed 55/55 tests with no warnings. The prior binaries were backed up at
  `C:\HomeServer\mirbot-deploy-backups\2026-09-23-gear-hunt`; the final host started on
  2026-09-23 at 07:24 local time, PID 23492. At 135 seconds of host uptime `/api/status`
  reported all eight bots Playing on database version `2026.09.22.3`. Live GUI reasons included
  Wizzler choosing Flea Cave Lv 1 for Wooden Shield and Sindo choosing Phantom Forest for Iron
  Shield; the interim build had shown Wizzler choosing Phantom Forest for Signet Of Vigor.

## 2026-09-23 assassin skill coverage and capacity sample

- Sindo and XxXDreadLordXxX each knew 16 assassin skills. The status page called many "unused"
  because its supported-skill list only covered Hell Fire, Poisonous Cloud and two attack
  toggles. Server source shows Pledge Of Blood and Ghost Walk augment Cloak/Summon Puppet,
  Touch Of The Departed augments Wraith Grip, and Willow Dance/Bloody Flower are passive
  stats. These now report as passive with the relevant explanation.
- Summon Puppet is a five-second explosive decoy represented as a player object, not a
  persistent monster pet. It teleports and cloaks the caster. The bot now casts it in close
  combat with a 60-second local throttle. Wraith Grip now applies to valid targets once per
  pending/confirmed poison window. Both are suppressed during frugal recovery combat.
  Pledge Of Blood can level from Puppet's Cloak application; it is not cast on its own.
  The standalone Cloak remains unautomated: the server charges health, rejects it for ten
  seconds after combat, and an ordinary attack removes it. Live status now shows it is the
  only remaining "unused" skill on either assassin; automating it needs a separate stealth
  travel decision rather than a combat cast.
- Full Bloom, White Lotus, Red Lotus and Sweetbrier are C.Attack AttackMagic skills selected
  by the real client, not C.Magic spells or MagicToggle skills. The bot now selects them,
  respects server cooldown packets, and favours their buff chain. Calamity Of Full Moon is
  included among server-armed melee skills. Assassins with melee skills now cast Hell Fire
  as an occasional opening hit then close to melee, instead of staying at spell range.
- The initial build passed 58/58 tests, zero warnings. The previous binaries were backed up at
  `C:\HomeServer\mirbot-deploy-backups\2026-09-23-assassin-skills`; the interim host started
  at 07:48 local time (PID 25656). At 07:49 Sindo cast Wraith Grip and Summon Puppet,
  used Full Bloom and White Lotus as melee AttackMagic, and Pledge Of Blood experience
  rose from 0 to 8. This proves the augment actually activated on the server. The live log
  also exposed Sindo briefly attacking its own Puppet: it arrived without `PetOwner`, so the
  ordinary pet exclusion missed it. `WorldModel.IsValidTarget` now excludes monsters identified
  as Summon Puppet by name or database flag. The final build passed 59/59 tests, zero warnings;
  the interim binaries were backed up at
  `C:\HomeServer\mirbot-deploy-backups\2026-09-23-puppet-target`. The final host started at
  07:53 local time (PID 49952). At 126 seconds of uptime `/api/status` showed all eight
  bots Playing on DB `2026.09.22.3`. At 07:55 XxXDreadLordXxX summoned a Puppet, then
  cast Wraith Grip and attacked a Wolf with White Lotus; the immediate follow-up log had
  no attack on Puppet. Its Pledge Of Blood experience was 28. This verifies the final
  build's use of the skill paths and the Puppet exclusion in that observed fight.
- Capacity snapshot with eight bots: desktop 12 logical CPUs, about 17.6 GiB RAM free and
  2% aggregate CPU at one instant; MirBot used 170 MiB and 0.094 CPU seconds over a 10-second
  sample (about 0.08% of all 12 logical processors). VM WMI reported 100% CPU at one instant;
  its QEMU process showed 105% of one Ubuntu CPU over its lifetime. VM memory was 12.27 million
  KiB total, 7.93 million KiB free. This suggests the VM CPU is the capacity constraint, but
  neither sample establishes a safe maximum bot count.

## 2026-09-22 Banya Temple gear-targeting review

- Mirbot was level 39 after the database restart and wore Medium Armour (M): AC 4-9, MR 2-3.
  Iron Plate Armour (M) is wearable from level 33 and has AC 5-15, MR 3-4, but no NPC stocks
  it. Banya Temple's normal minotaurs are level 45 and drop each gender's Iron Plate Armour at
  1/6000 per monster drop definition. Banya Temple Lv 1 has 696 configured normal spawns.
- Before the 2026-09-23 change the bot did not target equipment drops. An unmeasured map passes
  `MapProfile.WorthExploring` only if its median monster level is at most character level plus
  `ExploreLevelsAbove=3`, so Mirbot at 39 cannot pick the level-45 Temple. It first qualifies
  at level 42. After four measured maps, exploration is only a 15% draw at each destination
  choice; wanted drop-only books compete via a 70% book-goal draw. There is thus no guarantee
  that reaching level 42 will promptly produce a Temple trip or the armour. The new gear goal
  improves the chance once the level gate permits it; it does not guarantee a rare drop or
  override the level gate.

## 2026-09-22 farming-destination GUI, pets and stranded potion

- The selected bot card now has a separate **Farming destination (last choice)** field with the
  accepted map and the reason (book priority, hunting score, exploration or poverty). Town trips
  do not overwrite it. It starts blank after a host restart until that bot makes another hunting
  choice; current location remains independent. Source: `BotInstance`, `BotStatus`, `StatusPage`.
- The GUI's permanent “no pet summoned” was a snapshot omission: `WorldModel.OwnPets` was used by
  combat, but `BotInstance.Build` never filled `BotStatus.Pets` or `PetMode`. Both are now copied
  into the snapshot. After deployment Toby's live API reported one Skeleton at distance 2 and
  pet mode Both; all eight bots reconnected. Jill had no visible pet in that same snapshot.
- Sindo had 29 Healing Potions in regular storage slot 1. This was already present in the oldest
  retained current log snapshot (21:31 on the previous log day); the actual deposit is not in
  retained logs, so its historical cause cannot be proven. Current `WorthStoring` would not choose
  a normal potion, but `StorageReclaims` skipped it too because it is usable and has no equipment
  slot. Consumables are now explicitly excluded from future deposits, and stored health/mana
  potions are reclaimed through the normal server-confirmed withdrawal path. Book, gear and part
  storage paths are otherwise unchanged. Sindo's live bank diagnostic now reports one banked
  item usable, then withdrew the stack at 19:27:31; live storage slot 1 became empty and carried
  tier-one Healing Potions increased to 33.
- The tested build (53/53 tests) was deployed with backup
  `C:\HomeServer\mirbot-deploy-backups\2026-09-22-pets-storage`; new host PID 10904. All eight
  bots returned to Playing. The server-confirmed potion withdrawal was subsequently witnessed.

## 2026-09-22 weapon-score and quest audit

- Mirbot's Power Axe is 0-22 DC and scores 220 under `Backpack.ScoreFrom` for a warrior weapon
  (`10 * MaxDC + MinDC`). Sword Of Purification is 10-16 DC and scores 170, so the bot does not
  equip it. The log confirms Mirbot looted the sword at 08:03:48 and kept Power Axe; Banner's
  prior equip log also explicitly calls Power Axe (222) an upgrade over that sword (170). At
  neutral luck, the server's `MapObject.GetDC` rolls uniformly from min to max inclusive, making
  the weapon-only averages 11 and 13 respectively. Thus the current max-heavy score does **not**
  optimize mean melee damage. This is a scoring-policy question, not an equip-packet failure; no
  scoring change has been deployed yet. Positive Luck increases the value of maximum DC, so a
  future change should account for Luck and attack modifiers rather than simply swapping weights.
- Comparison of the VM's 2025 Joe donor `System.db` with the current master found five Joeban
  quests missing four `KillMonster` tasks and five requirement rows (including their level gates).
  The scoped repair tool in `C:\HomeServer\quest-audit-20260922` was tested on a copy, then
  deployed with the Server application closed on 2026-09-22. The original live master and both
  desktop copies were backed up there. All four deployed `System.db` copies hash-match SHA-256
  `BC4CB9B91238AD6383DC526DD5EA044189C7517CF9B1B28A605A4B55C84D5A38`. Read-back
  reports zero missing rows. The server was restarted; the old MirBot host logged an in-memory
  DB mismatch (`2026.09.21.7` versus server `2026.09.22.1`). On request, all eight bots were
  stopped through their normal API, their memory banks flushed, and the orphaned host process
  replaced with a fresh one. The new host loaded `2026.09.22.1`, logged no version mismatch,
  and all eight bots returned to `Playing`. See `zircon-npc-authoring.md` for rows.

## 2026-09-22 dated logs and book-map choice

- `BotLog` now prefixes every new line with local date and time (`yyyy-MM-dd HH:mm:ss.fff`).
  Historical time-only lines remain unchanged. `LevelBackfill` reads both formats, including
  dated lines without a host-open anchor. The Info loot lookup displays the new full timestamp.
- Book-bearing measured maps no longer hard-restrict exploitation. When both book and ordinary
  eligible maps exist, a per-bot goal draw chooses a book hunt 70% of the time, or 20% while the
  sustained-loss watch is active. After two book-priority choices, the next eligible ordinary
  hunt is forced. Map scoring and the top-three weighted draw then run WITHIN the chosen goal's
  pool, so the book bonus cannot remove ordinary alternatives from the shortlist. Recovery,
  safety, affordability, and exploration still run before this choice. Settings are
  `BookHuntChancePercent`, `LossBookHuntChancePercent`, `MaxConsecutiveBookHunts`.
- Both releases were deployed separately with hash checks and backups at
  `C:\HomeServer\mirbot-deploy-backups\2026-09-22-dated-logs-stage` and
  `C:\HomeServer\mirbot-deploy-backups\2026-09-22-book-goal-stage`. The suite passed 48 tests;
  all eight bots reconnected. The goal-selection rule is deployed but has not yet been observed
  on a live travel decision.
- Current observation: level-30 Wizzler was sent to Banya Village by `TryExplore` because it was
  unmeasured and zero hops from a town. `HuntLevelsBelow=0` disables the lower-level exploration
  filter, so a starter town remains eligible at level 30. Banner's planned journeys out of Banya
  were interrupted by deployment relogs. Both had healthy gold and positive adjusted gold trends,
  not poverty recovery. The new book-goal rule does not address this exploration behavior.

## 2026-09-22 starter-town and exploration follow-up

- The vendor-sold Potion Mastery book does **not** exempt a town from the new outgrown rule.
  `BookDropIndex.Wanted` includes only books that cannot be bought from a real NPC; the town
  exception requires such a wanted book to actually drop from monsters on that map.
- `TownHuntLevelGap=10` defers shopping towns whose median monster level trails the character by
  ten or more, when healthy and outside recovery. It applies to unmeasured exploration, measured
  exploitation, and the in-place outgrown trigger after a relog/town trip. It does not block
  shopping, through-travel, manual destinations, lower-level caves or poverty recovery. Deferred
  towns remain a fallback when no safer/reachable alternative exists; a bot already on an
  outgrown town stays put rather than bouncing to another equally outgrown town.
- Unmeasured exploration now uses the same book-goal draw (70% normally, 20% during loss watch) and
  two-book-choice streak guard as measured exploitation. The old hard restriction on unmeasured
  book maps is removed. The goal is picked before the nearest-from-town distance ring so zero-hop
  towns do not automatically eliminate a desired book map.
- 50 tests passed. The build was deployed after backing up binaries to
  `C:\HomeServer\mirbot-deploy-backups\2026-09-22-town-explore-stage`; all eight bots reconnected.
  Live evidence: Jill, level 30, relogged in Bichon Town; the new trigger logged the typical
  level-10 monsters as outgrown and planned Ant Cave North. Her arrival there was not yet checked.
- Banya Stone Cave Lv 1 is one hop from Banya Village and has 350 spawns of level-35 monsters;
  levels 2-5 are also median 35. No hunting-rate sample or past travel log entry was found.
  `ExploreLevelsAbove=3` means level-30 bots are ineligible; currently only level-38 Mirbot and
  level-32 Sindo meet that median-level test. Exploration is still a 15% residual draw once enough
  maps are measured, then chooses the nearest unmeasured map from town. Banya Village's zero-hop
  priority and unmeasured book-map restriction previously competed against Stone Cave. The new
  rules remove those two obstacles but do not guarantee a visit; no bot has yet proven it safe or
  profitable.

## 2026-09-22 plan deployment and GUI filter fix

- The built-in page (no `status.html` override) has Bots, Info and Settings tabs. Info contains
  Hunting memory, Recent deaths, Drop lookup with Class, and a persisted level-up table. The
  level-up character filter originally used a null character inside HTML option values; HTML
  parsing changed it, so choosing a bot returned no rows. The filter now uses an HTML-safe key.
  Verified in the live browser with Jill-only history, all eight bot cards, and the other Info
  panels. If the old page remains open in a browser, reload it once to fetch the new script.
- Level history persists in `memory/levels.json`; the one-time log backfill imported 165 inferred
  events and is idempotent. Inferred timestamps/intervals are marked approximate. New level-ups
  are observed and persisted. XP checkpoints are in `memory/xp.ndjson`; the card shows signed
  XP/hour over up to two hours of connected play and ETA only when the sample supports it.
- Hunting-map choice now draws with squared weights from the eligible shortlist, using a 25%
  baseline death penalty. Each bot can raise its own next-travel penalty to 60% during sustained
  adjusted gold loss. Confirmed equipment/book spending is tracked as cumulative capital spend
  in new `gold.ndjson` records; old records remain readable but cannot be retroclassified.
  Per-bot loss-watch latch files are `memory/profit-<bot>.json`. Manual gold grants are not
  identifiable and may mask a loss during one watch window.
- The spell book now aims verified area spells at clusters, including empty centres and all ray
  directions. Fire Wall/Tempest reservations are predictive (the bot does not ingest spell-object
  confirmations) and expire after the expected server lifetime. `AoeEnabled` and
  `AoeMinimumTargets` control the feature; frugal recovery combat still suppresses optional
  damage spells. Live Wizzler logs show Fire Wall and Scorched Earth casts with mana debits, but
  they are **not** proof of damage from those spells; confirm health/effect evidence before
  claiming effectiveness.
- The final suite passed 43 tests. The deployed host uses staggered 15-second autostart for its
  eight bots. Before swapping binaries, ask every bot to Stop, wait for Offline, stop the exact
  `MirBot.exe` PID, then verify the process is gone before copying. Windows may retain the DLL
  lock briefly after a Stop; never continue to Start after a failed copy, and compare source and
  deployed hashes. Backups: `C:\HomeServer\mirbot-deploy-backups\2026-09-22-gui-stage`,
  `2026-09-22-gui-hotfix`, `2026-09-22-level-filter`, `2026-09-22-combined-plan`, and
  `2026-09-22-aoe-refinement`.

---

## Where everything is

| Thing | Path |
| --- | --- |
| Bot source | `G:\Program Files (x86)\Mir3\MirBotSrc\MirBot\` |
| Shared game library | `G:\Program Files (x86)\Mir3\MirBotSrc\LibraryCore\` |
| Read-only DB tool | `G:\Program Files (x86)\Mir3\MirBotSrc\SpawnDump\` |
| Deployed bot | `G:\Program Files (x86)\Mir3\MirBot\` |
| Per-bot config | `G:\Program Files (x86)\Mir3\MirBot\bot-Mirbot*.ini` |
| Log | `G:\Program Files (x86)\Mir3\MirBot\mirbot.log` |
| Learned memory | `G:\Program Files (x86)\Mir3\MirBot\memory\` (`hunting.json`, `gold.ndjson`, `deaths.json`, `monsters.json`, `navdata.json`) |
| **Zircon server source** | `G:\Program Files (x86)\Mir3\Server Backup\Codex Working\Zircon-master\` |
| Live server (VM) | `192.168.1.108`, `C:\ZirconBuild\Debug\Server\` |
| Network share | `joeban@192.168.1.104:/mnt/storage1/files/share/MIR3/Zircon/MirBot/` |

The server source is a copy of the operator's other server from last year. No code changes were
made to it, so it is reliable for protocol semantics. **It was not discovered until late in the
session** — several problems were investigated by guesswork that this source would have answered
immediately. Check it first.

### Build and deploy

```bash
cd "/g/Program Files (x86)/Mir3/MirBotSrc" && dotnet build MirBot/MirBot.csproj -c Release
# kill MirBot.exe, copy MirBot.{dll,exe,pdb} from bin/Release to the deployed folder, restart
```

Deploying relogs the bots, which incidentally resyncs the inventory model — do not mistake that for
a fix working.

### The VM

SSH to `192.168.1.108` needs a ProxyCommand through `192.168.1.104` (the VM firewall only permits
SSH from `.104`). Documented in [mir3-server-vm.md](mir3-server-vm.md). `Server.exe` is a GUI app
and cannot be started over SSH — the operator must start it.

---

## The four bots

| Bot | Character | Class | Notes |
| --- | --- | --- | --- |
| Mirbot | Mirbot | Warrior | Consistently the richest (~580k). Its gear is drops, not shop stock. |
| Mirbot2 | Wizzler | Wizard | Was bankrupted by the duplicate-buy bug; recovering. |
| Mirbot3 | Jill | Taoist | |
| Mirbot4 | Sindo | Assassin | |

---

## What changed on 2026-09-21

### Database (System.db)

- Operator raised Wizard `WearWeight` L1–61. Copied the same curve to **Taoist** L1–60 and extended
  both to L90 with the fitted formula `floor(0.05·L² − 0.1·L + 16)`, which reproduces the
  operator's hand-entered values for levels 8–61 exactly.
- Propagated to VM server, VM client, desktop client and the bot's `Data\`. **All four must match**
  — the DB version is bumped on every write and a mismatch breaks the client.

### Inventory correctness

See [zircon-bot-inventory-model.md](zircon-bot-inventory-model.md). Summary: `NoteUsed` no longer
applied at send time; `S.ItemChanged` is authoritative and keyed on the server-supplied slot;
`S.ItemsGained` filters experience/quest/currency; `HasRoomFor` gained the added-stats test;
`NoteDivergence()` forces a relog after repeated voided sell orders.

### Gear buying — a gold leak

`ScoreInfo` (shop item) and `Score` (worn item) were **different formulas**. For any item with
`MinAC = 0` the shop copy scored double, so the same item read as a permanent upgrade and was
re-bought every trip. Wizzler re-bought its own Flame Robe + Magic Bronze Helmet + Necklace Of
Lantern at **17,000 gold per lap**, roughly every 20 minutes, and sold them back at vendor rates.

Both now route through one `ScoreFrom(Func<Stat,int>, ItemType, MirClass)`, plus a guard that an
item identical to the one worn is never an upgrade.

### Hunting ground selection

- **Level floor** (`HuntLevelsBelow`) — **DISABLED (set to 0) 2026-09-22.** It skipped grounds whose
  *median* monster level was that far below the character. It was wrong twice over:
  - **It blocked every skill-book source.** The entire early book catalogue (Fire Ball, Heal,
    Thunder Bolt, Teleportation, Poison Dust, Summon Skeleton, **Fire Wall**) drops only from L18
    Skeletons and L20 Ghosts, which live exclusively on Banya/Bichon/Lost Paradise Caves Lv 1-3
    (median 18) and Deserted Mine Lv 1-3 (median 20). At `HuntLevelsBelow=8` every one of those
    twelve maps is excluded from level 27. Wizzler was structurally incapable of ever learning
    Fire Wall. Worse, it is an **ordering** bug: `BotInstance` strips outgrown maps out of the loop
    *before* building `candidates`, and the drop-only-book restriction filters `candidates` — so the
    `shortlist = int.MaxValue` expansion added specifically to protect book maps is defeated one
    filter later.
  - **It was redundant.** Measured exp/hour already ranks these maps down on merit within a band
    (Assassin band 5: Deserted Mine 578,794 vs Bichon Town 105,178). The codebase already argues
    this against itself in `HuntingMemory.Best()`: *"'less relevant' is not 'unknown', and the
    honest expression of it is a discount, not a filter."*

  The symptom it was added for (a level 22 Taoist farming Bichon Town) traced to **poverty
  recovery**, not to a missing level test — hence the fossil `if (!poor && ...)` guard on it.
  Code left in place but inert; `OutgrownBy` returns false on `levelsBelow <= 0`.
- **Confidence floor** (`MinimumSampleHours`, default 0.25): a map may be *ranked* on any rate but
  cannot be *chosen* from memory until it has been measured for this long; below that it stays an
  exploration target. A rate is a claim about an hour, and computed over three minutes it is an
  extrapolation dressed as a measurement. Sindo committed to Despair Valley on **0.3 hours** reading
  751,454 exp/hour, beating Deserted Mine's 578,794 over 1.9 hours, and died there seven times for
  ~300,000 gold in potions. Confidence is **summed across every carrying band**, not read off the
  freshest record, or crossing a band would reset a map to "unproven" — the same amnesia `Best()`
  was already fixed for once. `MeasuredCount` applies the identical floor so the two agree about
  what the bot knows. Empty result hands the thin maps back rather than stranding the bot.
- **Deaths were charged, earnings were not** (fixed 2026-09-22). `RecordDeath` fired
  unconditionally; `CloseExperienceSample` discarded any window under `MinimumSampleMinutes` (4),
  and **death did not close the window at all** — it was closed only on *left the map*, *levelled
  up*, *window complete*. So a map was credited with 100% of its deaths and only some of its
  earnings, and the faster a map killed you the more completely it was slandered: rate stays 0,
  which excludes it from `Best()`, and three such visits put it on `Lethal()`, which excludes it
  from exploration too. Sixteen entries were in that state, including **Ant Cave North — written
  off at zero by the Wizard while the Warrior measured 680,656 exp/hour there**. Death now closes
  the window with `force: true`, bypassing the minimum, because a window that ended *because we
  died* is the whole truth about that visit. Safe against noise since `Record()` is hours-weighted:
  three minutes joins a 1.9h history at three minutes' worth. Not permanent as first thought —
  `Lethal()`'s `forgetAfterBands = 2` expires the blacklist after ~10 levels.
- **Outgrown-here trigger**: travel was only reconsidered when a town trip ended, so a bot parked
  on an outgrown map stayed there indefinitely. Now checked in place, rate-limited to 2 minutes,
  suspended while poor.
- **Poverty recovery** (`RecoveryGold` 5000, `RecoveryMaps` "Bichon Town,Banya Village"): below the
  threshold the whitelist is the *only* hunting destination, checked above exploration and the
  experience ranking. Permitting the cheap maps was not enough — the ranking still sent a broke
  wizard to a ghost cave with no mana.
- **Journey failures are now logged** (`Journey.OnFailed`). They were silent, which made a bot look
  like it was ignoring its own travel decisions.

### Butchering (new)

`ButcherIndex` + `BotAction.Butcher`. Corpses of AI ∈ {1,2,5} (Chicken; Cow/Deer/Pig/Sheep;
Carnivorous Plant) are harvested with `C.Harvest{Direction}` on a 600ms clock from an adjacent
tile, abandoning if a live monster comes within 2 tiles. Meat is real money: Beef 250, Pork 180,
Mutton/Deer Meat 200, Chicken Meat 80, Carnivorous Plant Fruit 300 at **zero weight**.

**Do not copy the Mir 2 agents' `AutoHarvestAIs = {1,2,4,5,7,9}`.** On this server AI 7 is Ant
Needler and the archers, AI 9 is the sorcerers — ordinary combat monsters.

A drop-table heuristic ("same item repeated 4+ times") was tried and **removed**: across all 309
monsters it matched 46 including Arch Lich Taedu and Chaos Knight, because ordinary tables repeat
common consumables freely. Five hand-picked examples agreed with it; the full table did not.

### Mana

- Mana potions now get their own **routing** leg (`SellsManaPotion`, `needMana`). Previously
  `needPotions` asked only about health and the directory only knew `SellsHealthPotion`, so a
  caster full of healing potions and empty of mana had no way to ask for a shop that sells mana.
- Mana trips are gated on **affordability** (`MinUsefulPotionBuy` × cheapest mana potion). A broke
  caster used to buy three potions, walk 180 tiles back, run dry and repeat.

### Other

- Equip refusals are remembered (`item -> slot`) until a level-up or a successful equip, ending a
  ~140-attempt-per-minute retry storm.
- Scenery (AI 4: Chestnut Tree, Blood/Dark/Life Stone, Enshrinement Box) is no longer a valid
  combat target, and `NoDamageTo` now guards the **cast** path as well as melee — it was melee-only,
  so a wizard threw Cyclone at a tree until its mana ran out.
- `WorthStoring` gained a class-usefulness test (`ScoreInfo <= 0`). `RequirementCanGrow` only asked
  "could I ever wear this", so a Taoist banked a DC sword whose requirement does grow, uselessly.

### Item-use serialization

- Only one `C.ItemUse` may now be outstanding. Potions, books and town scrolls wait for the
  slot-specific `S.ItemChanged` verdict before another use is offered; a five-second timeout keeps
  a lost answer from disabling healing for the rest of the connection.
- `S.ItemChanged` now clears pending use/drop state only when the server-echoed inventory slot
  matches. A late verdict for another slot can no longer be attributed to the newest request.
- Survival and combat remain independent: while a delayed potion is pending, the brain may still
  attack, cast, flee or move; only another item use and the town-trip scroll state machine pause.

### Hunting-average recency

- `HuntingHalfLifeHours` defaults to 4. Before adding a fresh sample, `HuntingMemory.Record` ages
  both the accumulated experience and accumulated hours by the same exponential factor. The old
  average does not drift toward zero merely because a map was left alone, but a new measurement can
  quickly replace a lucky historical window instead of being diluted by a lifetime total.
- The setting is documented in `bot.ini.example`. Set it to 0 to retain lifetime averages.

---

### Stuck in a cave — four defects, 2026-09-21 late

Two bots sat in Bichon Cave Lv 1 for ~40 minutes doing nothing useful, unresponsive to the forced
town-trip button. The causal chain, in order:

1. **Item uses were sent inside the server's 1-second `UseItemTime`.** The serialization gate waits
   for the previous *verdict*, which is not the same as the previous *cooldown*. Wizzler drank a
   mana potion, the verdict came back immediately, and the town scroll went out 33ms later and was
   silently refused. Seven refusals that session landed within a second of an accepted use.
   Fixed: `ItemUseCooldown` (1100ms) gates `TryItemUse`, clocked from the last **accepted** use.
2. **A refused scroll was recorded as "this map is scroll-proof."** `_scrollFailedHere` was set
   whenever the bot did not move, without asking whether the server had accepted the scroll at all.
   That passed a permanent sentence on a temporary condition and locked a wizard holding **twelve
   scrolls** out of ever leaving. Fixed: only marked when `S.ItemChanged` confirms consumption.
3. **The cave sweep had no give-up.** It is deliberately exempt from the ordinary roam re-roll so
   it can cross a map — but nothing watched whether it was getting closer, so an unreachable
   stairway produced an immortal walk. Fixed: `SweepStalled` / `AbandonSweep`, 45s without
   improving on the best distance, then that target is barred for 10 minutes.
4. **It was invisible.** The log only writes when the action *type* changes, and `Roam` never
   changed, so both bots went silent mid-session and looked crashed. The stuck watchdog could not
   help either — it measures a bot pinned on one cell, and these were walking the whole time.
   **When diagnosing "the bot froze", check `/api/status` before the log.**

Verified after deploy: refusals within 1s of an accepted use went from **7 to 0**. Defects 2 and 3
are deployed but **unproven** — the deploy relogged the bots, which cleared the stuck state.

### Roaming a populated map for 25 minutes, 2026-09-21 late

Wizzler roamed Banya Cave Lv 1 for 25 minutes with no kill. **The cave sweep was not the cause** —
a first attempt to fix this by adding a sweep give-up was aimed at the wrong mechanism, and the
sweep was in fact working (7 engagements, 5 abandonments in the same session).

The real cause: `WorldModel.LastExperienceGainUtc` is measured and shown on the status page
("last kill: 26 minutes ago") and **no decision path consults it**. `ConsiderTravel` ran on exactly
four events — trip ended, map barren, map outgrown, storage errand. A bot walled into a pocket of a
280-spawn, level-appropriate map matches none of them, so nothing would ever have moved it.

Fixed with a fifth trigger, `UnproductiveMinutes` (12), mirroring its siblings: barren asks whether
the map has spawns, outgrown asks whether they are worth killing, this asks whether any of it is
actually happening.

**Two follow-up defects found when it still did not work:**

1. **The trigger could not fire for the worst case.** It required `LastExperienceGainUtc` to be
   set, but a bot that has killed NOTHING since login has no gain timestamp at all — and a relog
   into a dead pocket is exactly how a bot ends up with no kills. Two bots sat in Bichon Cave
   showing "last kill: never" while the check that should have moved them was structurally unable
   to become true. Now falls back to `_inGameAt` via `UnproductiveSince()`.
2. **Leaving the map was planned as a walk, and the walk kept failing.** Every journey failure
   observed is `journey abandoned - stuck N tiles from the exit`. A level 24 Taoist carrying
   **eight town scrolls** planned a five-leg walk to a town one scroll would have reached
   instantly. `EscapeOnScroll` now forces a town trip when a journey gives up and a scroll is
   carried — the town trip is the only code that knows how to scroll, and it already handles a
   bind point without vendors. Rate-limited to one every 2 minutes so a run of failures cannot
   burn the bag.

**Still unverified** — the trigger needs 12 minutes of drought to arm. Watch for
`No experience at all on <map> for N minutes` and `carrying a town scroll - forcing a town trip`.

### Follow-up audit, 2026-09-21 evening

The final cave-stall changes were reviewed against their callers after deployment. The accepted
item-use cooldown, pending-use gate, sweep progress watchdog, and journey-failure scroll escape are
sound. Two follow-up defects were corrected:

- A town trip treated every accepted item-use verdict as the result of its scroll. The verdict is
  now correlated to the exact scroll slot for the active teleport attempt, including emergency and
  unstick scrolls. A potion or late reply can no longer make a refused scroll look consumed and
  blacklist the map.
- `UnproductiveMinutes` originally measured from the last XP gain anywhere in the session. A bot
  arriving on a fresh map after an old drought could therefore reject it immediately. `WorldModel`
  now records `MapEnteredUtc`; the drought window begins at the latest of login, map entry, or XP.
- The 45-second sweep had the same fresh-session hole: it required `_lastHitAt` to exist, so a bot
  that had not fought anything since reconnect could wander forever without ever sweeping. It now
  measures from the later of map entry or last hit. Verified live: Wizzler changed from ordinary
  wandering to `sweeping towards the next floor` 45 seconds after reconnect with no targets seen.

Sindo's learned Hell Fire was also missing from `SpellBook.Castable`, even though the client and
server both treat it as an ordinary hostile-target cast. It is now supported and was observed live
casting on Voracious Ghost and Devouring Ghost after deployment.

Diagnostic note: `SweepAfterIdleSeconds` had no `case` in the ini parser, so it was never loadable
from config. Added along with `unproductiveminutes`. Worth auditing the rest of `BotConfig` for
other settings that are documented but unreadable.

### Per-bot exploration coverage, 2026-09-21 evening

CrystalFork's `NavData` was re-reviewed at commit `d55d003`: it is exploration coverage, not a
pathfinding correction. It removes confirmed visited cells from a walkable set and chooses from the
remainder, going map-wide one time in five. MirBot already has the separate pieces Crystal lacks:
the `.map` walkability grid, A*, exit avoidance and learned server-refused cells. What it lacked was
a memory of which otherwise-valid areas it had recently searched.

MirBot now divides each map into **16x16 sectors**, independently per bot and per connection. Every
authoritative position refreshes that sector. Ordinary roaming chooses the least-recently visited
sector within `RoamRadius`; after `SweepAfterIdleSeconds` without combat progress, it chooses across
the whole map. This is timestamped rather than permanently depleted, so after full coverage the
oldest sector naturally becomes attractive again as monsters respawn.

Map-wide targets do not expire on the ordinary 20-second roam clock. Combat and gathering still
pre-empt roaming, but the same destination survives and resumes afterwards; the progress watchdog
is paused while combat owns the decision. Entering any cell in the target sector completes the leg.
An A* failure or 45 seconds without getting closer bars that sector for 10 minutes. The old
next-floor/far-side sweep remains a fallback if coverage cannot supply a target.

Coverage is deliberately **not shared and not persisted**. One character's visit does not suppress
another character's hunting area, and reconnecting starts a fresh session. `/api/status` and the
status page show visited/total sectors, local versus map-wide mode and whether the selected sector
was unseen.

Verified after deployment: 8 deterministic tests passed; all four bots reconnected; Sindo selected
a 195-tile map-wide target after the 45-second threshold, interrupted it to fight Voracious Ghost
and Corpse Raising Ghost, and repeatedly resumed the same `263,305` target. No exceptions or bot
faults appeared after deployment.

### Two-stage poverty recovery, 2026-09-21 late

Recovery is now latched rather than ending at 5,001 gold. Falling below `RecoveryGold` (5,000)
enters recovery; ordinary hunting resumes only when a completed, adequately supplied town trip
still leaves `RecoveryExitGold` (25,000). This makes the exit balance a post-restock balance rather
than money that is immediately spent again.

Recovery has two stages:

- On `RecoveryMaps` (`Bichon Town,Banya Village`), optional combat spending is disabled: no attack
  spell, attack skill, self-buff, summon, poison, kiting for a spell, or mana-potion use. Healing,
  fleeing, emergency scrolling, melee, existing pets, loot and butchering are unchanged.
- At level 20+, reaching `RecoveryCaveSupplyPercent` (60%) of both relevant potion weight targets
  plus the configured town-scroll reserve promotes the bot to `RecoveryCaveMaps` (`Flea Cave Lv
  1`). This stage is itself latched until a later town trip, so drinking one potion cannot cause
  map churn.

Verified after deployment: 17 tests passed; all eight bots reconnected with no faults. Jill entered
recovery at 2,498 gold, routed from Ant Cave to Bichon, logged frugal melee on arrival and issued no
further Cast or MagicToggle action there. Her first trip ended with sufficient potion percentages
but only two of three reserved town scrolls and 7 gold, so she correctly remained on beginner
ground rather than taking Flea Cave without the configured return reserve.

### Quests and the game store, 2026-09-24

**Quests** (`QuestBook`, `QuestErrand`, `QuestLogMemory`): Joeban's four kill quests in Bichon Town,
done only when passing through (or on the bot card's **Do quests** button). Accepts and hand-ins
count only on the matching `S.QuestChanged` transition.
- Rewards are drunk automatically. Scrolls of Boss Tracking are read on a map with a wanted boss.
- The Level 40 boss quest is for level 45+ only.
- Verified live:
  - three dailies and two "Getting Started" completed and handed in;
  - buffs used;
  - phone notifications sent.
- The visit watchdog was first 8 minutes from arrival. Mirbot gave up 1/5 Forest Yetis in, so it is
  now 8 minutes without progress (30 minutes at most).

**Game store** (`GameStore`, `StoreShopper`): Hunt Gold buys a fixed per-class list, all permanents
first, then temporaries kept topped up.
- The purchase is confirmed by Hunt Gold dropping; the reply packet proves nothing.
- On deploy every bot held about 3,100 Hunt Gold. Within two minutes seven had bought their Mir
  Package [P], class tonic and class mark (equipped in the torch slot), and as many other
  permanents as they could afford.
- Banner was mid-fight and waits for a lull.

**Mana tier bug:** a warrior's 182-MP pool gave a gap of 109, just below tier II's 110, so both
warriors bought 40-point potions and drank about one a minute. Mana potions may now overshoot by a
tenth of the pool, and Warriors and Assassins carry `ManaPotionWeightPercent=15`.

## Open issues

### 1. Book hunting choice (updated 2026-09-22)

The old hard book-map restriction is superseded by the 70%/20% goal draw and two-book-streak cap
documented above. It deliberately allows normal hunting rather than holding a bot indefinitely
on a book map. The top-three randomisation now runs within the selected goal pool.

### 2. Skill-book learning is probabilistic; duplicate retention fixed in source 2026-09-23

`Failed to learn skill, not enough pages` is the server's failure message. The actual server check
is a roll against the dropped book's `CurrentDurability`; failure consumes that copy. Looting one
is **not** the same as learning the skill, and duplicates give additional independent attempts.
The old one-of-each bank rule sold TooEarly duplicates, including Dreadlord's extra Ghost Walk and
Waning Moon copies. `Backpack.DisposableSlots` now retains every class-appropriate unlearned copy
(`TooEarly` or `Wanted`) until the skill is confirmed known, then ordinary sale rules apply. This
source change passed the regression suite and was deployed 2026-09-23.

### 2a. Map-selection history in source 2026-09-23

The Info tab now has a persistent map-trip table, backed by
`memory/map-trips.json` and `/api/map-trips`. It records the chosen destination and reason,
arrival and departure, and why the visit ended. The “kills” column counts positive XP awards
while on the selected map, an approximate credited-kill measure (item/quest XP can contribute).
No historical backfill is attempted.
The first four rows captured during deployment were preserved. Periodic memory flushing includes
this bank; without that wiring it would only have saved on a graceful host shutdown.

### 2b. Equipment count and class-wide bank eligibility, 2026-09-23

The server consumes a broken Torch with a successful `S.ItemChanged` on the Equipment grid.
MirBot previously applied that packet only to the Inventory grid, retaining a phantom 0/8 Candle
and refusing to equip an equal-scoring spare until relog. The accepted equipment count is now
applied (including zero removal); refused packets still leave the model unchanged. Equipped
Poison/Amulet counts also benefit from this correction.

The old warrior/MC bank fix exempted Taoists from the MC-requirement guard. Jill's Iron Rings
require MC 9 yet scored above zero from their AC bonus, so they were banked even though her two
SC rings were far better. Future offensive-stat requirements now follow class roles: Warrior and
Assassin use DC, Wizard uses MC, Taoist uses SC. A future gear deposit additionally has to beat the
weakest worn slot for that class. Legacy non-upgrades and wrong-class/unwanted books are withdrawn
for sale on ordinary town trips; parts and useful unlearned books remain protected. A live Jill
trip exposed the follow-on ordering gap: banking withdrew five Iron Rings only after the vendor
circuit had finished. The town trip now makes a sell-only vendor pass after such withdrawals, with
no second round of purchases, before returning to the hunting ground. These rules passed 74 tests
and the final build was deployed. Both Jill's five and Toby's two legacy Iron Rings were then
sold on forced ordinary town trips: live bag and storage counts are zero for each bot. All eight
bots reconnected in Playing state with no faults. Candle replacement passed a focused model test;
an actual live torch burn-down has not yet been observed after deployment.

### 2c. Deserted Mine Lv 2 route finding

The database has three exit cells on Lv 1 leading to Lv 2: `(311,33)`, `(312,33)`, `(312,34)`.
The Lv 1 map's static walkability is connected, and an offline A* test from Dreadlord's failed
position `(210,128)` reached all three, even with learned nav corrections. Multiple bots' live
journeys aborted after one failed route to `(311,33)`. The evidence points to transient live
obstacles/avoidance or a route-search limit under live conditions, not a missing cave link. A
future fix should distinguish path failure causes and retry other exit cells before abandoning the
journey; this route change is not implemented yet.

### 3. Wrong-class books are just sold

Jill looted two assassin books (Full Bloom, Willow Dance) while Sindo is an assassin who wants Full
Bloom. Nothing moves books between characters, and they are on separate accounts.

### 4. Sell refusals — believed fixed, unproven

Zero since the `NoteUsed` fix, but the bots relogged on deploy so they started clean. Needs a long
run to confirm.

---

## Diagnostic habits that worked

- **Add the diagnostic before the fix.** The mana routing bug was invisible for hours and took
  seconds once the buy path explained its refusals. Same for refused item uses and journey aborts.
- **`SpawnDump`** (`G:\...\MirBotSrc\SpawnDump\`) reads `System.db` read-only via env-var modes:
  `SPAWNDUMP_TSV`, `SPAWNDUMP_BASESTATS`, `SPAWNDUMP_ITEMS`, `SPAWNDUMP_MONSTERS`,
  `SPAWNDUMP_BOOKDROPS`, `SPAWNDUMP_SELLERS`, `SPAWNDUMP_NPCBUYS`, `SPAWNDUMP_COUNTS`. Patch modes
  (`SPAWNPATCH_*`, `WEARCOPY_*`, `WEARSET_*`) open `SessionMode.System` — `SaveSystem()` silently
  writes nothing on a `Users` session and still reports success, so **always read the file back**.
- **Beware log timestamps as strings.** Yesterday's `23:xx` sorts above today's `10:xx`; an awk
  string compare produced a bogus count three times in one session. Filter from a known line
  number (e.g. the host-start line) instead.

## Units, the other recurring bug family

A **weight budget** read as a **count** has been fixed in roughly nine places. `HealthPotionTarget`
/ `ManaPotionTarget` return a *weight* when called with the default unit. Compare them against
`HealthPotionLoad()` / `ManaPotionLoad()` (also weights), never against `CountHealthPotions()`.
Invisible whenever potions weigh 1 apiece — which tier-one potions do — and wrong for everything
heavier.
