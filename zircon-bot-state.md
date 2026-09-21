# MirBot — current state and open issues

Last updated: **2026-09-21**. Written as a handover so a fresh session has full context without
re-deriving it. Read [zircon-bot-inventory-model.md](zircon-bot-inventory-model.md) first if you
are touching items, selling or the bag.

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

- **Level floor** (`HuntLevelsBelow`, default 8): skip grounds whose *median* monster level is that
  far below the character. Previously every level test had an upper bound and no lower one, so a
  level 22 Taoist farmed chickens in Bichon Town (median 10) on the strength of one lucky sample.
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

## Open issues

### 1. Book hunting can't stick

`BookDrops` map scoring works (`BookHuntBonusPercent` 100, multiplicative per book), but the final
pick is **random among the top 3**, so the bonus improves the odds and cannot hold a bot on a book
map. The randomisation exists to stop one sample pinning a bot forever — the two goals conflict.
Options: exempt book maps from the randomisation, or drop `HuntingChoices` to 2.

### 2. Skill books need pages

`Failed to learn skill, not enough pages` — book items carry `durability = 100` as a page count and
dropped books arrive partially used. Looting a book is **not** the same as getting the skill. No
mechanism exists to combine partial books.

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
