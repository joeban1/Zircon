# LLM-driven fake players for Zircon — design

> **Current state, recent changes and open issues live in
> [zircon-bot-state.md](zircon-bot-state.md).** Read that first for anything operational.
> Before touching items, selling or the bag, read
> [zircon-bot-inventory-model.md](zircon-bot-inventory-model.md) — it documents a bug family that
> has now been found four separate times.
>
> This document is the original design and the accumulated internals; parts of it describe
> intentions rather than what was built.


Design for bot "players" that log into our Mir3 server over the normal network protocol and are
driven by TypeSafe's **Jev** for behavioural decisions.

Status: **steps 1-2 built and verified** (2026-09-18) — `MirBot/` in the Zircon fork connects, logs
in, equips its gear and fights on a deterministic brain. Steps 3-5 are still design.
Written 2026-09-18.

Every codebase claim below was checked against the repo at
`/mnt/storage1/files/share/MIR3/Zircon`, not assumed.

## What Jev can and cannot do here

Jev is a "System One" model: structured state in, typed decisions out, 70-500ms, in one parallel
pass. It has exactly three output primitives and **no text generation at all** — TypeSafe's docs are
explicit that System One models do not write replies or explanations.

| Primitive | Returns | Our use |
| --- | --- | --- |
| `choice` | one option of up to 255, plus full probability distribution and confidence | target selection, next action, which skill |
| `noul` | probability 0-1 that a condition holds (no separate confidence) | "am I in danger", "is that player hostile" |
| `score` | probability-weighted position on ordered levels, plus confidence | threat level, how depleted am I |

So the split is fixed and not negotiable:

- **Behaviour** — Jev. Good fit, this is exactly what it is for.
- **Chat dialogue** — an ordinary text LLM. Rare, latency-tolerant, cheap at low volume.
- **Pathing, legality, cooldowns, inventory rules** — plain C#. Better than any model, and free.

The bots are convincing because of the third row far more than the first. Resist putting movement
mechanics in the model.

## Decision: headless client, not server-side AI

**Chosen: a separate console process that connects as a real account over TCP.**

The alternatives and why they lose:

| Option | Verdict |
| --- | --- |
| Override `MonsterObject.ProcessAI` (`ServerLibrary/Models/MonsterObject.cs:1113`, virtual) | Easiest hook, but clients render and treat the result as a **monster**. Not a fake player. |
| Headless `PlayerObject` inside the server | `PlayerObject` is a `partial class` welded to a live `SConnection` and character records. Faking that is invasive surgery on live server code. |
| **Headless client process** | Zero server changes. The bot *is* a real player to every other system — combat, chat, guilds, drops. Crash-isolated from the server. |

### The headless route is cleaner than expected

The important finding: **`LibraryCore` is completely UI-free.**

```xml
<!-- LibraryCore/LibraryCore.csproj — the entire file -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
```

Plain `net10.0`, no package references, no WinForms, no SharpDX. It holds `BaseConnection` and every
packet type (`Library.Network.ClientPackets` / `ServerPackets` / `GeneralPackets`).

Do **not** try to reuse `Client/Envir/CConnection.cs`. It is `sealed` and imports `Client.Controls`,
`Client.Scenes.Views`, `System.Windows.Forms` and `System.Drawing` — it drags the whole renderer in.

Instead write a new `BotConnection : BaseConnection`. The base class asks for very little:

- `protected abstract TimeSpan TimeOutDelay { get; }`
- `public abstract void TryDisconnect();`
- `public abstract void TrySendDisconnect(Packet p);`

and hands back `ConcurrentQueue<Packet> ReceiveList` / `SendList`, `BeginReceive()`, `Enqueue(p)`
and a `Process()` pump. Packet handlers are discovered by reflection into `PacketMethods`, keyed on
`(ConnectionType, PacketType)` — so the bot implements handlers only for the packets it cares about,
and `ProcessUnhandledPacket` (virtual) silently absorbs the rest.

Consequence worth having: the bot project depends only on `LibraryCore`, so it is portable. It does
**not** need the Windows VM — it can run on the Ubuntu host next to everything else.

## Process layout

```
MirBot (new console project, net10.0, references LibraryCore only)
  |
  +-- BotConnection : BaseConnection      TCP to the Zircon server, same protocol as the real client
  +-- WorldModel                          mirrors what the bot knows: self, nearby objects, map, inventory
  +-- BotBrain                            tick loop; owns the decision cache
  |     +-- JevClient                     async HTTP; never blocks the tick
  |     +-- ScriptedFallback              deterministic C# AI used while a decision is in flight
  +-- ChatResponder                       separate text LLM, only when spoken to
```

One process can host several bots; each gets its own `BotConnection` + `WorldModel` + `BotBrain`.

## The tick loop — the one hard constraint

**Never block on the network inside a tick.** This applies to the bot's own loop, and it is the
reason we are not doing this server-side at all: `ServerLibrary/Envir/SEnvir.cs` runs a tight
single-threaded loop paced with `Thread.Sleep(1)`. A 70-500ms call made inline there would stall
every player and every monster on the server, ten times a second.

The bot uses a **decision cache**:

```
tick():
    world.ApplyPendingPackets()

    if decision.IsStale(world) and not requestInFlight:
        requestInFlight = true
        fire-and-forget JevClient.Ask(BuildState(world), Questions)   // completes on a worker thread

    act(decision ?? ScriptedFallback.Decide(world))                   // ALWAYS returns immediately
```

Rules that fall out of this:

- A decision carries the world-version it was computed from. **Check freshness before applying it** —
  an "attack the mob to my east" answer is worthless if the mob died 400ms ago. Discard and fall back.
- Exactly one request in flight per bot. Do not queue up a backlog under latency.
- The scripted fallback is not a stub. It must be able to run the bot indefinitely on its own, so
  that an API outage degrades the bots rather than freezing them.

## The Jev request

`POST https://api.typesafe.ai/v1/systemone`, `Authorization: Bearer <key>`.

**Pin the model version** — `"model": "jev-1.12"`, not `jev-latest`. TypeSafe's own cookbooks pin.
Our confidence thresholds get tuned against one model's calibration, and `jev-latest` would silently
re-point them at a different model on some future Tuesday. Upgrade deliberately, re-checking
thresholds when we do.

### Fan out wide — extra questions are free

Two separate facts, often confused:

- Extra questions **cost tokens** (they share the request budget with the state).
- Extra questions **cost no latency**. TypeSafe's fan-out pattern is explicit that all questions are
  evaluated in parallel, so adding more to a call typically does not slow the response.

For a 100ms tick budget that is decisive: ask everything the tick could possibly need in **one**
round trip, including questions that only matter on branches we may not take, and let code discard
the irrelevant answers. A second request is only warranted when an answer is needed to *construct*
the next state — which, for a game tick, it never is.

Because questions cannot see each other's answers, every speculative branch must state its premise
explicitly in its own `instructions`, and code picks which answers to consume.

**Only closed sets become questions.** Anything unbounded — coordinates, gold amounts, how many
steps to walk — never goes to the model; code computes it and keeps its own default. The model picks
among options we defined; it does not produce numbers.

### State (one object per tick, kept lean)

Keep this small — cost and latency both scale with it. Named JSON fields, no raw dumps.

**Keep observed facts separate from inferred ones.** Everything below is observed from packets. If
we later add derived judgements ("this player seems hostile"), they go in a separate `inferred`
block, never mixed in as if they were facts — otherwise a wrong inference feeds itself on the next
tick and the bot talks itself into nonsense.

```json
{
  "self":   { "class": "Warrior", "level": 31, "hp_pct": 46, "mp_pct": 80,
              "position": [158, 233], "map": "Bichon Town" },
  "nearby_monsters": [
    { "id": 8812, "name": "Cave Maggot", "level": 28, "hp_pct": 100, "distance": 3, "attacking_me": true },
    { "id": 8814, "name": "Stone Golem", "level": 40, "hp_pct": 90, "distance": 7, "attacking_me": false }
  ],
  "nearby_players": [ { "name": "Joeban", "distance": 4, "attacked_me_recently": false } ],
  "inventory": { "healing_potions": 12, "mana_potions": 4, "free_slots": 9 },
  "recent_events": ["took 340 damage in the last 3 seconds", "killed Cave Maggot"]
}
```

### Questions (one batch per tick)

```json
{
  "next_action": {
    "type": "choice",
    "instructions": "What should this character do right now?",
    "criteria": {
      "attack":      "Engage or keep hitting a monster in range",
      "flee":        "Disengage and move away from danger",
      "heal":        "Use a healing potion before doing anything else",
      "loot":        "Pick up nearby dropped items; safe to do so",
      "roam":        "Nothing pressing; wander toward unexplored ground",
      "return_town": "Out of potions, inventory full, or badly outmatched"
    }
  },
  "target": {
    "type": "choice",
    "instructions": "Assuming the character attacks, which monster is the best target? Ids come from `nearby_monsters[].id`.",
    "criteria": {
      "8812": "Cave Maggot, distance 3, already attacking me",
      "8814": "Stone Golem, distance 7",
      "none": "No suitable target"
    }
  },
  "in_danger": {
    "type": "noul",
    "instructions": "Is this character likely to die within the next few seconds if it keeps doing what it is doing?",
    "criteria": { "true": "Death is a realistic outcome soon", "false": "Comfortably in control" }
  },
  "threat": {
    "type": "score",
    "instructions": "How dangerous is the current situation?",
    "criteria": ["Safe", "Manageable", "Losing ground", "Critical"]
  },
  "skill": {
    "type": "choice",
    "instructions": "Assuming the character attacks, which skill should it use? Consider mana cost against `self.mp_pct`.",
    "criteria": {
      "basic":     "Ordinary melee swing; no mana",
      "thrusting": "Hits several monsters in a line; worth it with 2+ targets clustered",
      "half_moon": "Hits all adjacent monsters; worth it when surrounded",
      "none":      "No skill is appropriate"
    }
  },
  "flee_direction": {
    "type": "choice",
    "instructions": "Assuming the character flees, which way is safest? Directions are compass points from the character's current position.",
    "criteria": { "north": null, "south": null, "east": null, "west": null, "town_portal": "Use a town scroll instead of walking" }
  },
  "worth_looting": {
    "type": "noul",
    "instructions": "Assuming there is loot on the ground nearby, is it worth stopping to pick up given the current danger?",
    "criteria": { "true": "Safe enough, and the loot has value", "false": "Too dangerous, or the loot is junk" }
  }
}
```

Notes that matter:

- Everything except `next_action`, `in_danger` and `threat` is **speculative** — each states its
  premise in its own `instructions`, and code ignores it unless the branch was taken. This costs
  tokens but no latency, which is the whole point of fanning out.
- Always include a `none` option where nothing may fit; the model cannot pick a value that is not
  offered. Same for `target` — if a monster is not in `criteria`, it cannot be chosen.
- Choice options are capped at **255**, which comfortably covers actions and visible monsters. It
  does **not** cover "choose one of 3,000 items" — narrow candidates in code first, then let Jev
  pick among the shortlist.
- Total request budget is roughly **32,000 tokens** shared between state and questions. The batch
  above lands around **1,400**. Plenty of headroom, but it does cap how much history we can shovel
  in, and the budget is the reason wide fan-out is not entirely free.
- Handle `429` with backoff; a rate-limited tick just uses the fallback.

### Using confidence

`choice` and `score` answers carry `confidence`; `noul` does not — its probability *is* the answer,
and ~0.5 means genuinely torn, not "medium danger".

Gate by consequence, not one global threshold:

| Answer | Threshold | Behaviour below it |
| --- | --- | --- |
| `next_action = flee` / `return_town` | act on moderate confidence | cheap to be wrong, prefer caution |
| `next_action = attack` | high | fall back to scripted target selection |
| anything with real cost (dropping items, spending gold) | do not let the model decide at all | keep in code |

Low confidence on a harmless preference (roam left vs. right) is fine to ignore — spread probability
often just means several answers are acceptable.

## Cost

Output tokens are free; input is **$0.042 / MTok**. So cost is driven entirely by state size and
tick rate.

```
$/hour/bot  =  tokens_per_request x requests_per_second x 3600 x 0.042 / 1,000,000
```

At ~1,400 tokens per request (the fanned-out batch above, plus state):

| Tick rate | Per bot | 5 bots, 4 h/day | 5 bots, 24/7 |
| --- | --- | --- | --- |
| 2/sec (active combat) | $0.42/hr | ~$8.50/day | ~$51/day |
| 0.5/sec (roaming) | $0.11/hr | ~$2/day | ~$13/day |
| 0.25/sec (idle) | $0.053/hr | ~$1/day | ~$6.50/day |

**Adaptive tick rate is the single biggest cost lever** and should be in v1, not deferred: poll fast
only when something is happening. An idle bot standing in town does not need ten decisions a second.
The widely quoted "$7/hour" from TypeSafe's Doom demo reflects a much larger state payload than ours
and should not be used as our estimate.

Second lever: skip the request entirely when the scripted layer is certain. Nothing nearby, full HP,
nothing to loot — no call needed.

## Dialogue

Out of scope for v1, but the shape is known. When another player speaks near the bot:

- Debounce, then call an ordinary small text LLM with the recent chat and a short persona.
- Rate-limit hard — a few lines per minute per bot at most.
- Cache and reuse stock replies for common openers.
- Jev is still useful *around* it: a `noul` for "is this addressed to me" and a `choice` for
  "greeting / trade offer / insult / question" gate whether the expensive text call happens at all.

This is the right division: Jev decides **whether and what kind**, the text model writes the words.

## Operational safety

- **API key lives in the bot process only**, from an environment variable. Never in `System.db`,
  never in the client, never committed. TypeSafe's own guidance is to keep credentials server-side.
- **Kill switch** — a GM command or a sentinel file that stops all bots immediately.
- **Hard caps in code**, not in the model: no trading, no dropping items, no gold transfers, no
  guild actions. The model selects among actions we have already decided are safe.
- Bots use **their own accounts**, clearly named, so their activity is distinguishable in logs.
- Log every decision with its state hash, answer, confidence and latency. When a bot behaves
  strangely, the question is always "bad state, bad answer, or bad code" — you cannot tell without
  the record.

## Build order

1. **Connect and stand there.** `MirBot` project, `BotConnection : BaseConnection`, login, spawn,
   stay alive through timeouts. No AI at all. This de-risks the protocol work, which is the largest
   unknown.
2. **World model + scripted AI.** Roam, attack, potion, flee on thresholds. Deterministic. A useful
   bot on its own, and permanently the fallback layer.
3. **Jev on top.** Decision cache, adaptive tick, confidence gates, telemetry. Compare against the
   scripted layer — if it is not visibly better, the state or the questions are wrong, not the model.
4. **Multi-bot** in one process.
5. **Dialogue**, if still wanted.

Stop after 2 if the behaviour is already convincing. Step 2 is most of the perceived realism.

## Step 1 as built — the login protocol and its traps

Everything here was established against the live server, not read off and assumed.

### Handshake

```
server: G.Connected        -> bot echoes G.Connected
server: G.CheckVersion     -> bot sends G.Version { ClientHash }
server: G.GoodVersion      -> bot sends C.SelectLanguage, then C.Login
server: S.Login (Success)  -> bot sends C.StartGame { CharacterIndex }
server: S.StartGame        -> in game; thereafter echo every G.Ping
```

- **`ClientHash` is SHA256 of the client's `Zircon.dll`**, compared against the file at the server's
  `VersionPath`. The bot hashes that file at runtime, so it stays correct across client rebuilds; a
  pinned hex hash is the fallback and goes stale on every rebuild.
- **`CheckSum` on `C.Login` is never validated** — the server only writes it to the log. Send empty.
- **No encryption on the wire.** `GoodVersion.DatabaseKey` is for client-side database files only,
  and arrives as an empty array when `EncryptionEnabled=False`.
- **Passwords must be 5-15 non-whitespace characters** (`Globals.PasswordRegex`). A longer one fails
  with `LoginResult.BadPassword`, which reads like a wrong password but is a length rejection.
- Accounts must be `Activated`. Creating one **from the server machine's own IP auto-activates** it;
  from anywhere else the server tries to send an activation email, which fails here (no SMTP auth).

### Four traps, in the order they will bite

**1. The game database must be loaded before any packet arrives.** Packet deserialization invokes
`[CompleteObject]` methods, and `ClientUserItem.Complete` resolves its `ItemInfo` through
`Globals.ItemInfoList` (`LibraryCore/Globals.cs:511`). Unloaded, the first packet carrying an item —
the inventory sent during `StartGame` — throws. Mirror `CEnvir.LoadDatabase`, but point at a
**private copy of `System.db`**: `Session.Initialize` can write migrations, and the client's `Data`
folder belongs to the real client. Only `System.db` (~5.7 MB) is needed; the multi-gigabyte `.Zl`
archives beside it are sprites for the renderer.

**2. `BaseConnection` swallows receive-side exceptions** unless `AdditionalLogging = true`. Without
it, *any* deserialization failure looks like a silent socket close with nothing logged anywhere.
`CConnection` sets it. Set it first, before debugging anything else — trap 1 is invisible without it.

**3. Packet handlers must never call `Disconnect()`.** Handlers run inside `BaseConnection.Process`'s
loop over `ReceiveList`, and `Disconnect()` nulls that list mid-iteration. Set `Disconnecting = true`
and let the pump tear down afterwards, as `CConnection.Process(G.Disconnect)` does.

**4. `ProcessUnhandledPacket` throws by default**, and `Process` treats `NotImplementedException` as
grounds to disconnect. A bot handles a handful of the hundreds of packet types, so override it to
count and drop. Counting them is free reconnaissance: a 90-second idle session yielded the exact
inventory of packets a world model has to consume.

### Operational shape

The bot project references **only `LibraryCore`** and builds clean on `net10.0`. Do not try to reuse
`Client/Envir/CConnection.cs` — it is `sealed` and imports `Client.Controls`, `Client.Scenes.Views`,
`System.Windows.Forms` and `System.Drawing`.

Build and run happen on the VM: it is the only machine with the .NET 10 SDK. See
`mir3-server-vm.md` for the SSH traps around long-running and detached processes — the bot must
write its own log file rather than stream stdout back.

## Step 2 as built — navigation and trading

### Server-side auto-path is unavailable here

`C.AutoPathStart` hands navigation to the server: `AutoPathService` computes the route and calls
`PlayerObject.Move` itself, across map transitions, with the client only confirming each step via
`C.AutoPathMoveStarted`. It would remove the need for any pathfinding in the bot.

**It is refused on our maps.** `AutoPathRoutePlanner.TryBuild` (`:38`) rejects every request unless
*both* the current and destination maps have `MapInfo.CanAutoPath`, and **Bichon Town has it off**.
That gate precedes routing, so targeting coordinates (`C.AutoPathWaypoint`) instead of an NPC does
not help — same check. Enabling the flag in `System.db` would fix it, but it is live game data and
would turn on click-to-travel for real players too.

So the bot walks itself. What is worth taking from `AutoPathService` is not the feature but the
**algorithm**: `AutoPathRoutePlanner.TryFindPath` is a compact A* over the map's walkable cells, and
it ports into the bot in about sixty lines. See below.

### The bot reads the map itself

`MapPath` points at the client's `Map` folder and the bot parses the `.map` files directly. The
format is documented by the server's own loader, `ServerLibrary/Models/Map.cs:58-88`, and it is
short: width and height at byte 22, a back-tile block of three bytes per four cells, then 14 bytes
per cell whose first byte is a flag. **A cell is walkable only when the bottom two bits are both
set.** Nothing else in the file matters to a bot.

Verified against the live data: all 244 maps parse, Bichon Town is 350×350 at 72% walkable, and the
character's own standing cell reads walkable. `MirBot.exe --check-maps` prints that table and is the
first thing to run after changing `MapPath` — a wrong path is otherwise silent, because the bot just
falls back to blind steering and looks like pathfinding simply not working.

Grids are shared across bots (`MapLibrary`), loaded once per map, and cost about a megabyte each
against nearly four hundred on disk.

### Why blind steering had to go

A blocked move produces **no error** — the server silently answers `S.UserLocation` to snap the
client back. The only evidence is a position that stops changing.

Fanning out one step at a time does not work: step aside, re-aim at the target, blocked again, step
back the other way, forever. Committing to a detour for several steps helps against a straight wall
(**server resyncs 80 → 8** on one run) and cannot solve a concave one — a wall with a gap in it is
exactly the shape a greedy hill-climber cannot cross.

**The failure was worse than slow, it was unbounded.** The give-up test counted *consecutive blocked*
moves, and a bot oscillating in front of a wall is moving successfully every tick, so the counter
reset every tick and never reached its threshold. One run logged `Approach x405` against a chicken
behind a wall near the town safe zone and would have continued indefinitely.

Two fixes, both kept:

- **A\*** (`PathFinder`) over the grid, with a node budget so a hopeless search cannot stall the bot
  thread. `goalRange` of 1 to end up beside a monster, 0 to stand on a drop. The valuable half is
  the negative result: a failed search means *no route exists*, so the target is written off on the
  first tick instead of the 405th. Live objects are only routed around once something has actually
  blocked a step, otherwise a monster in a doorway reads as a wall.
- **Progress-based patience** (`PursuitPatience`), measuring distance to the target rather than the
  success of individual steps. This is the backstop for what a path cannot express — a monster
  kiting us, a drop on a cell nothing can stand on — and stays useful with pathfinding in place.

All three walk sites (monster, loot, town trip) funnel through one `TrySteer`, and it falls back to
the old blind behaviour when no grid is available, so a wrong `MapPath` degrades the bot rather than
stopping it.

Also: an overweight character cannot run, so asking for `Distance = 2` while over capacity earns a
resync on every single move. Running along a path is only safe when the next two steps continue in
the same direction.

### Looting: paired slots, and what a drop is actually worth

Two independent decisions hide behind "should I pick this up".

**Is it an upgrade?** Rings and bracelets each have a *left and a right* slot. The equip logic
handled that; the loot check did not — it asked `SlotFor(ItemType)`, which maps `Ring → RingL`, and
so compared a ground ring against the left slot alone. Live symptom: a Horned Ring on the floor was
refused because an identical Horned Ring was worn on the left, while the right slot held a Taoist
ring scoring zero for a warrior. The bar has to be the **weakest** slot the item could displace,
which is what `PendingEquips` would actually do with it once carried.

**Is it worth money?** Before this the bot had no idea what anything was worth: non-equipment was
taken whenever the bag was not yet heavy and refused once it was, so a gemstone and a rotten carcass
were the same decision.

The scarce resource is **bag weight, not gold** — weight is what forces the walk to town — so value
is judged as gold per unit of weight, never in absolute gold. `ClientUserItem.Price(count)`
(`LibraryCore/Globals.cs:569`) gives exactly what the vendor will pay, including durability, added
stats and the `Worthless` flag; `ClientUserItem.Weight` already accounts for stack size and the
Poison/Amulet exception where weight is per stack, not per unit.

The bar slides with wealth (`LootValueRule`): a skint character takes nearly anything that sells, a
rich one only the good stuff, interpolating between two gold marks and clamping outside them. It is
multiplied once the bag is heavy, so potions and real upgrades still win contested space.

Four things must bypass the value test or the bot quietly breaks:

- **Gold drops as an ordinary ground item** (`SEnvir.GoldInfo`) and is weightless. Anything
  weightless is free to carry, so it is taken unconditionally — including when the bag is full.
- **Town scrolls** already bypass everything: the only way home.
- **Books** are never cargo. A book is a skill to learn now, one to bank until the level is right,
  or a sale; that judgement is made in the bag, so always pick one up.
- **Opposite-gender gear** can never be worn but still sells, so it goes through the value test
  rather than being refused outright.

### Vendors are discovered, never configured

Each `NPCPage` carries `Types` (what that NPC **buys** from the player) and `Goods` (what it
**sells**). `VendorDirectory` walks every NPC's dialogue tree from `System.db` at startup — 196
trading pages on this server — recording both, plus the button presses that reach each page. The bot
then picks whichever NPC buys the most of what is actually in the bag.

Naming a vendor in config was the first attempt and was wrong: the chosen NPC turned out to buy none
of the armour and weapons the bot was carrying, and the failure was silent.

Two traps when selling:

- **The server rejects the entire batch** if any single item fails its checks
  (`PlayerObject.cs:10302`) — one unsellable item loses the whole sale. Filter to `Info.CanSell`
  and to types in the page's `Types` list before sending `C.NPCSell`.
- **An empty `Types` list means the NPC buys nothing at all**, the same rule as a scripted sell
  window (see `zircon-npc-authoring.md`).

A restocking trip usually needs **two stops**: the best buyer rarely also sells town scrolls.

### Shopping is a whitelist, not a ranking

Three separate bugs came from trying to *rank* vendors well. The fix that finally held was to stop
ranking across the whole world and name the maps where shopping is allowed (`TownMaps`), which puts
150 of the 195 trading pages out of reach of every selection path.

What went wrong first, in order, because each is a distinct trap:

1. **One vendor for two needs.** `BestRestockerFor` returned a single NPC, sorted by proximity
   *before* coverage. On a map with a potion seller and a scroll seller, the potion seller won the
   tie and scrolls were never bought by anyone. Independent needs need independent stops.
2. **A shortcut that ran before every guard.** "One stop is better than two" found the only NPC on
   the server selling scrolls *and* potions from one page — **Lavar, on Infernal Island**, which has
   no route from Bichon — and returned him unconditionally, defeating every proximity rule added
   after it.
3. **Adding a stock count to a tile count.** The gear seller scored `itemsStocked + 1000 if same map
   + (500 - distance)`. A general store with 200 items beats a shop ten tiles away on inventory
   alone. Criteria measuring different things cannot be summed; rank them in order instead.

Also: **a town splits its trade across NPCs and across dialogue pages.** In Bichon, Mr. Kang sells
weapons, Linda sells armour on one page and helmets and shoes on another, Amy sells rings, bracelets
and necklaces on three. Asking for "the best seller of all gear" returns one NPC and one page, so the
bot bought a helmet and kept wearing level 1 armour from the same vendor. Resolve **one shop per kind
of gear**, then take every page of that NPC stocking it — the "already on the itinerary" preference
stops that multiplying the stops.

### Consumables: count by role, budget by weight

Two counting mistakes, both of which look correct in isolation:

- **Count by role, not by `ItemInfo`.** Reserves keyed per item type give every tier its own full
  allowance. A wizard hoarded 17 healing potions across two tiers while the restock step, which
  counts them together, saw no shortage — and sat at 105% weight, unable to run.
- **A weight budget is not a count.** Spending "25% of the bag" as *36 potions* only works if a
  potion weighs 1. At tier four it filled the bag: 115 of 147 on leaving the shops. Divide the
  budget by what one actually weighs, and size the order *after* choosing the tier.

Tier selection matters as much as quantity. `FindGood` returns the **cheapest** match — right for
town scrolls, wrong for potions, which is how a level 16 warrior with a 422 health pool ended up
buying potions that restore 30.

### Two item flags that quietly break selling

**`Locked` is clearable.** It is a player convenience and the server clears it on request
(`C.ItemLock { Locked = false }`) with no cost and no checks beyond the slot existing. A bot that
respects the flag but can never clear it just accumulates.

**Item parts are not junk.** A part carries a *generic* `ItemInfo` and names the real item through
`AddedStats[Stat.ItemIndex]` — the same lookup the client uses to draw the right icon. Without it an
Iron Shield part is a nameless trinket of no type, which is exactly what the disposal rules sell.
Parts accumulate toward `Info.PartCount` and have their own 1000-slot `PartsStorage` grid.

### Repair: special is worth double

Ordinary repair does `MaxDurability -= (Max - Current) / DuraLossRate` **permanently** before
restoring. Repeat it enough and good gear becomes worthless. Special repair restores with no maximum
loss at twice the cost, with a per-item cooldown visible to the client as `NextSpecialRepair`.

One item on cooldown **rejects the entire batch**, so they must be filtered out up front. And the
local durability model must apply the ordinary formula *only* for ordinary repairs — doing it after a
special repair shrinks our copy while the server leaves the item alone, and every later decision
works off that.

### Four packet-level facts that are easy to get wrong

**`HealthChanged` is a delta, not a value.** The client applies it as `ob.CurrentHP += p.Change`.
It arrives on every hit; the full `DataObjectHealthMana` is far rarer. Ignore it and the bot's idea
of its own health goes minutes stale, which silently disables *every* HP threshold — it will fight
to death at what it believes is 78%.

**Items from `S.ItemsGained` carry `Slot = -1`.** The server snapshots the item before assigning a
slot (`PlayerObject.cs:6081`, then `:6275`). Server and client each place it by the same rule —
merge into a matching stack, else lowest free slot — and stay in agreement without any packet
carrying the result. A bot must replicate that rule; otherwise every later `C.ItemUse` or
`C.ItemMove` addresses the wrong item.

**Per-instance item stats are in `ClientUserItem.AddedStats`, not `Info.Stats`.** A base DC 0-1
sword that rolled +1 special is really DC 0-2, and the bonus lives only on the instance. Comparing
definitions alone makes every special drop look identical to a plain one.

**Guards are `MonsterInfo.AI == -1`** (`MonsterObject.GetMonster`). They are monsters in the data
model but town furniture in practice. AI 1 and 2 are passive harvestable animals — chickens, pigs —
and are perfectly good low-level targets, so filter on the guard value specifically, not on
"passive".

### Account storage needs no new protocol

The bank is `GridType.Storage`, moved with the same `C.ItemMove` used for equipping — no dedicated
packets. Three facts make it work:

- Its contents arrive in **`S.Login.Items`**, which is account storage, *not* the character's bag.
  The character's own items are in `StartInformation.Items`. Confusing these is easy and silent.
- Moves are gated on being **in a safe zone** (`PlayerObject.cs:7421`), tracked by
  `S.SafeZoneChanged { InSafeZone }` and seeded from `StartInformation.InSafeZone`.
- Whether an item is wearable *yet* is `RequiredType` + `RequiredAmount` (Level, MaxLevel, DC, AC,
  MR, SC, MC, Health, Mana, Accuracy, Agility), checked by the server at
  `PlayerObject.cs:7313`. Evaluating it bot-side needs the player's own `Stats`, which arrive in
  `S.StatsUpdate`.

That gives a three-way split for anything picked up: **wearable now and better** → equip;
**wrong class or gender** → sell, since that will never change; **right class/gender, a future
requirement this class can realistically meet, and better than the weakest worn slot** → bank it.
Do not infer future value from an unmet requirement alone: a Warrior/Assassin grows DC, a Wizard
MC, and a Taoist SC. A small AC bonus can make an off-class ring's score positive without making
it a real upgrade. Class-appropriate unlearned skill books and item parts use separate banking
rules; failed book learning consumes the copy, so retain *all* unlearned duplicates. Obsolete
withdrawals happen after the normal vendor circuit, so a sell-only pass must follow banking.

### Survival must outrank everything — and must not become a trap

A first version let the town trip own the brain while travelling. The bot stood still sending one
routing request and was chewed from 60% HP to 10% over 160 seconds without healing or fighting back.
Healing and fleeing are now evaluated before any trip step. Any long-running behaviour added later
must sit below survival in the same way.

**But a survival rule that assumes a resource becomes a deadlock once that resource runs out.**
Three of these shipped and all three had to be found in live logs. They share a shape worth
recognising early, because the next one will look the same:

| Rule | What it assumed | What happened without it |
| --- | --- | --- |
| Flee below the health threshold | there is something to flee *to* | fled in a random direction with nothing chasing; flee sits above the town block, so the trip that would restock could never start. One bot logged **145 Flee decisions and 21 of anything else** at 16% health |
| Abort a town trip when in danger | a potion can be drunk instead | with an empty bag, low health is permanent, so every trip aborted instantly. The status read `interrupted at 30% HP` and the Town button appeared dead |
| Keep a reserve of each consumable | one kind of each | reserves keyed per `ItemInfo` gave every potion *tier* its own full allowance, so weak tiers were never surplus — and sat at the lowest slot, so they were drunk first |

Two more of exactly this shape turned up while testing the overnight work, and they chain:

| Rule | What it assumed | What happened without it |
| --- | --- | --- |
| Go to town when the bag is full | the bag fills faster than the potions empty | true for a warrior, false for a caster. A wizard drinks through a stack while looting almost nothing, so it sat at 23% health with no potions, no scroll and a 66% bag, casting at chickens — with *nothing on the map* able to change its situation, because no trip trigger looked at supplies at all. With auto-revive now on, that is a death loop that lasts all night |
| Keep a gold reserve so there is always money for repairs and potions | the reserve is spent on something else | the reserve was subtracted before pricing potions, so a wizard with 914 gold and a 5,000 reserve had nothing "spendable" and could never buy a healing potion *while broke* — precisely when it needs one. The reserve now applies only to optional gear, which is the thing it was protecting the budget from |

And one that is not a resource gate but is the same failure to check:

**Size an order by what you can pay for, not by what you want.** The server refuses an unaffordable
`C.NPCBuy` in silence and refuses the WHOLE order, so asking for more than the purse covers does not
yield a smaller stack — it yields nothing, with no error, and a bot that believes it restocked. The
tier was being chosen against a budget; the *quantity* was checked against nothing. A broke wizard
ordered 22 Life Pills with 914 gold, received none, and set off for town again two minutes later to
do it again. It now orders four, and gets four.

That one is only visible from the data: the log said `NPCBuy (22 x Life Pill)` and the trip reported
success. Reading the inventory afterwards is what showed the shelf was still empty.

**Spend the purse in the order things keep you alive.** The itinerary is walked in order and the
money goes as it goes, so whichever stop comes first has first call on it. Both the itinerary
(`BestRestockersFor`) and the per-page buy ladder put town scrolls before healing potions, so a
wizard with 2,503 gold spent 1,500 at the scroll seller, reached the potion seller with 1,003, left
town with no healing potions at all, and died ninety seconds later. A scroll is an escape; a potion
is what stops you needing one. Potions now come first in both places.

Worth noting that the potion block already carried the comment *"Topping up healing potions matters
more than the scrolls: without them the heal branch has nothing to drink and the bot fights until it
dies"* — sitting directly below the scroll block that ran before it. The intent was written down and
the order contradicted it, which is the easiest kind of bug to read straight past.

A fourth, found the same night and the worst of the lot because it never ends:

| Rule | What it assumed | What happened without it |
| --- | --- | --- |
| Broken gear bypasses the town-trip cooldown | the trip will FIX the break | a level 14 wizard with a broken ring, 22 gold and a 611-gold bill walked the same four-NPC circuit every 43 seconds, forever. It never hunted, so it never earned the money, so the ring stayed broken, so the next trip started at once. Six trips in four minutes |

A refused repair now puts that bypass to sleep for fifteen minutes, so the ordinary cooldown applies
and the bot goes back to earning. It was only visible at all because the itinerary had just been
written into the log — the underlying behaviour had presumably been there for weeks.

**And the refusal itself is silent.** When the bill exceeds the character's gold,
`PlayerObject.NPCRepair` sends a chat line and returns — no `S.NPCRepair` packet, success or
failure (`PlayerObject.cs:11730`). So the obvious hook, *tell me when a repair fails*, is never
called, and a bot waiting to be told waits forever. Silence is the signal, so it is **timed** rather
than parsed: matching the message text would break in another language, and a timeout catches every
silent refusal rather than only the one about money. That matters, because this was the third silent
refusal of the night — the others being an unaffordable `C.NPCBuy` and a special-repair batch
containing one item on cooldown.

**The pattern worth carrying forward: on this server, "no" is usually said by saying nothing.**
Any request that can be refused needs a timeout, not just a failure handler.

The general rule: **before gating an action on a resource, ask what the bot does when it has none.**
If the answer is "the thing this gate forbids", the gate is a trap. Fleeing now requires an actual
threat and is skipped entirely when there is neither a potion nor a scroll; interrupting a trip
requires a potion to drink; a forced trip is never second-guessed.

### Measure the thing that actually matters

A second family, found the same way — by watching the bot rather than by reading the code.

The brain was careful about whether it was getting *closer* to a target: `_pursuitBest` tracks the
best distance ever reached and abandons a chase that stops improving, which is what killed the
405-approaches-to-a-chicken bug. Nothing measured whether the target was getting *weaker*.

So a bot was seen surrounded, with a monster standing on its **own cell** — Mir lets a player and a
monster step onto the same tile in the same tick. It had committed to that target. Distance was 0,
so the approach watchdog never ran. Every attack was aimed at the cell in front of it, which the
monster was not in. It swung at nothing until it was forced back to town by hand.

Two fixes, and the second is the general one:

- **The specific cure.** Distance 0 is now a step *off* the cell, not an attack. One move makes the
  target adjacent and the fight starts working.
- **The watchdog.** A target that has not lost health in `AttackPatience` swings is written off and
  something else is chosen. The server broadcasts `S.HealthChanged` for monsters as well as for the
  player (`MapObject.ProcessHPMP`), so "is this fight working" is genuinely observable rather than
  something to infer. Any drop at all resets the patience, so a long fight against something tough
  is never cut short.

The rule: **for every loop the bot can get stuck in, name the observable that says it is working.**
Distance answers "am I getting there". Only the target's health answers "am I getting anywhere".

### Code that cannot run is worse than code that runs wrongly

The post-mortem for a death lived inside `RunBrain`, after the decision had been acted on:

```csharp
Decision decision = _brain.Decide(...);
if (decision == null || decision.Action == BotAction.Idle) return;   // <-- here
...
if (world.Dead && !_wasDead) { /* record the death */ }
```

`Decide` returns `Idle` for a dead character, and `Idle` returns above. So from the moment the bot
actually died, every line of the death handling was unreachable — the history note, the danger
record, the hunting record, the voided experience window, all of it. It read correctly and had never
once executed.

It now runs on the *tick*, not on the decision, which is where anything that reacts to state rather
than to an action belongs. The same audit found a second one: a non-retryable disconnect left the
state `Offline` while `_wantRunning` stayed set, and `Offline` with `_wantRunning` reconnects on the
very next tick — so a wrong password produced an unthrottled reconnect loop at roughly ten attempts
a second, which is exactly how the server's packet guard turns one bad config line into an IP ban
for every bot on the machine. Terminal failures now Fault.

Both were found by asking of each branch: *when does this actually execute?* Neither would have been
caught by reading it for correctness.

### Decide what to do when the resource is gone

Two smaller versions of the same lesson:

- **Emergency escape.** Out of potions at or below the flee threshold, use a town scroll. Running
  from a fight you cannot win with nothing to drink only postpones the death.
- **Plan against the state you will be in, not the one you are in.** The itinerary was planned using
  the scroll count *before* the trip spent one teleporting home, so the bot arrived one short and
  every book stop refused with "restock first". Count what you will have **on arrival**.

## Roadmap for the deterministic bot

Steps 1-2 produced a bot that fights, loots, equips, sells, restocks and banks. What follows is the
work that would make it play the game properly rather than grind one field. Sized honestly, with
dependencies — several of these are gated on travel.

**Travel is the keystone.** Four of the items below are worthless until the bot can leave its
starting map, so that is the thing to solve first.

| # | Capability | Size | Depends on | Shape |
| --- | --- | --- | --- | --- |
| 1 | **Cross-map travel** | Large | — | rule |
| 2 | **Repair gear** | Small | town trip (done) | rule |
| 3 | **Learn skill books** | Small-medium | banking (done) | rule |
| 4 | **Buy skill books at the right level** | Medium | 3, vendor directory (done) | rule |
| 5 | **Use skills in combat** | Medium | 3 | mixed |
| 6 | **Choose a hunting ground** | Medium | 1 | judgement |
| 7 | **Hunt specific gear** | Medium | 1, 6 | judgement |
| 8 | **Refine and upgrade** | Large | town trip | rule, fiddly |

### 1. Cross-map travel

The blocker is known: `MapInfo.CanAutoPath` is off for Bichon Town, so the server's navigator
refuses every route (see above). Two routes forward:

- **Enable `CanAutoPath` in `System.db`** and let `AutoPathService` do the work. Cheapest by far,
  and the `C.AutoPathStart` plumbing is already written and left in place. It is live game data and
  changes behaviour for real players, so it is the server owner's call.
- **Build our own.** `MovementInfo` (already loaded from `System.db`) holds `SourceRegion` /
  destination pairs — the map-link graph. Combined with the same-map walker, a route is "walk to the
  exit region, cross, repeat".

**Built.** `WorldGraph` reads `MovementInfo` at startup — **554 exits across 195 maps, 0 unusable**,
with each source region's bitmap decoded to concrete walkable cells using the map width. Level gates,
class gates and exits needing an item or an instance are excluded *while planning*, so the bot never
walks into one and bounces. `Journey` plans the sequence of transitions with a cost search over
maps (originally breadth-first; since 2026-09-23 it weighs estimated tiles walked, +15 per hop,
teleport fares against gold held, and this class's recorded deaths on each map crossed - see
`WorldGraph.Route`) and plain A* for each leg, re-planned on arrival because the server drops the
player at a random point inside the destination region.

Verify without sending a character anywhere:

```
MirBot.exe --dir <folder> --log <file> --check-travel "Banya Village"
```

A destination reporting `no route` is usually correct rather than broken — Infernal Island genuinely
has none from Bichon, which is why the vendor whitelist matters.

**Teleport NPCs: built, and much smaller than expected.** `TeleportDirectory` walks every NPC's
dialogue tree the same way `VendorDirectory` does, looking for `NPCActionType.Teleport` with a
`MapParameter1` (instance teleports are skipped — the server refuses to move between instances).
Destination, landing cell, button path, level gate and price all come out of `System.db` at startup,
and each route is registered as an extra `MapExit` so one route search weighs a paid hop
against the walk instead of the two being planned separately. `Journey` executes such a leg as a
conversation — walk into talking range, `C.NPCCall`, replay the recorded button path, wait for the
map change — rather than as a step onto a trigger cell.

The price is deliberately assembled from two places, because a dialogue charges in two ways:
`NPCActionType.TakeGold` on a page takes it, and `NPCCheckType.Gold` *guards* the page, and either
can appear anywhere along the button path. A page that demands 50,000 gold to open costs at least
that much to use, whether or not it says so.

**The premise did not survive contact with the data.** This was meant to turn cross-country travel
from a four-map walk into what a real player does. `--teleports` says the whole server has three
routes, all free, and none of them a travel network.

It was still worth building, and the reason is better than the one expected. Two Hexa Holy Stones
inside Banya Temple are not shortcuts, they are the only way in: with teleports off, Bichon reaches
113 maps at level 14 and Banya Temple Hall is unreachable; with them on it reaches 121, and the Hall
is five map changes away. Eight maps that no amount of walking can get to.

The third route taught the lesson worth keeping. The NPC in Bichon Town leads to Assassin's Hideout
and is **Assassin only** — and that gate is not on the destination map, where `MapInfo.RequiredClass`
is unset. It is an `NPCCheckType.Class` on the NPC's own entry page, which the first version of this
did not read: it accumulated Gold and Level checks and ignored the rest. So a level 18 Warrior was
routed to Assassin's Hideout, walked ninety tiles across Bichon, sent `C.NPCCall`, and stood there
while the server quietly declined to do anything.

That is the general shape and it is worth stating: **a gate the planner cannot see is a journey the
bot cannot be talked out of.** A journey is committed to before it starts, so a condition discovered
on arrival costs the entire walk and produces no diagnosis — the NPC simply does not respond. The
directory now reads the Class checks as well, and treats any check type it cannot evaluate as making
the whole route unplannable. Refusing a route that might have worked is much cheaper than committing
to one that cannot.

The gold floor is enforced at both planning and execution time, sized so the fare can never leave the
bot unable to buy potions or a way home. Every route is free today; a paid one is one content edit
away.

### 2. Repair

`C.NPCRepair { Links, Special, GuildFunds }` on a repair page, which `VendorDirectory` can already
find the same way it finds buyers. Durability arrives in the `ItemDurability` packets the bot
currently counts and drops. Slots naturally into the existing town trip beside selling.

### 3-4. Skill books

A book's `ItemInfo.Shape` maps to a `MagicInfo` (`PlayerObject.cs:7116`), which carries the school
and the magic it teaches. So the bot can tell what a book is for, whether its class can learn it,
and at what level — all from `System.db`, no guessing. Learning is `C.ItemUse` on the book.

Books the class cannot use yet already get banked by the storage rules. Buying them is the vendor
directory plus a "what am I missing for my level" check against `StartInformation.Magics`.

### 5. Using skills

**Partly built: attack skills only.** There are two different mechanisms and only one is done.

*Attack skills* ride along on an ordinary attack through `C.Attack.AttackMagic`. The server decides
when one is available and says so with `S.MagicToggle` — which the bot was dropping, so every Slaying
proc was armed and then thrown away. Three traps:

- A mismatch makes `PlayerObject.Attack` **refuse the whole attack**, resync the bot, and write an
  `[ERROR]` line into the server log (`PlayerObject.cs:14731`). Only ever name a skill the server has
  explicitly armed; default to `None` when unsure.
- **Swordsmanship must never be named.** It is an `AttackSkill` and it does apply on every swing, but
  its `AttackCast` never sets `Cast`, so requesting it is rejected. It needs no bot support at all.
- Sustained toggles — Thrusting, HalfMoon, DestructiveSurge, FlameSplash — are set once with
  `C.MagicToggle` and stay on. The one-shot kind — Blade Storm, Dragon Rise, Flaming Sword — cost
  mana per arm: each `C.MagicToggle` pays, starts the cooldown and arms the next swing for 12
  seconds. Built 2026-09-24 as `SkillSet.PendingCharge`, sent only while in contact; a swing names
  an armed charge first, then Destructive Surge over Half Moon. Shoulder Dash is left out.

*Cast skills* — Fire Ball and everything a wizard actually does — go through `C.Magic`, and are now
**built** in `SpellBook`. Until this, a wizard would walk to town, spend its gold on a spell book,
read it, and go back to punching chickens.

The choice is shaped like the Mir 2 agents' `GetBestMagic`: price every known spell at its current
level, drop what is unaffordable, prefer the highest level requirement on the assumption that a
spell gated behind a higher level is the better spell. The rules it is checked against are Zircon's,
read from the server rather than assumed:

- **Mana** is `UserMagic.Cost` = `BaseCost + Level * LevelCost / 3`, already computed on
  `ClientUserMagic`. `MagicObject.CheckCost` refuses the cast outright above current mana.
- **Range** is `Globals.MagicRange`, 10.
- **Pace** is `Globals.MagicDelay`, 2000ms between casts against 1500ms between swings — so a cast
  has to be worth about a swing and a third to be the right move at all.
- **Cooldown** is per spell, announced by `S.MagicCooldown` and carried on `ClientUserMagic.Cooldown`.

**Mana does not come back while hunting, and that changes the whole design.** A mana floor was the
obvious first move — keep a share of the pool back for emergencies — and it was wrong twice over.
Nothing the bot casts is defensive; its escape is a town scroll, which is an item. And watching a
wizard showed mana is simply static during a session: one logged in at 30/167, cast five times, and
sat at 25/167 for as long as it was watched while its health regenerated normally around it. A floor
above zero therefore does not ration casting, it *ends* it.

So the floor defaults to 0, and the real budget is potions. `CountManaPotions`, `ManaPotionReserve`
and `ManaPotionTarget` all already existed — the bot bought mana potions, carried them, reserved them
against selling, and had **no code path anywhere that drank one**. A caster spent its pool in the
first few minutes of a session and meleed for the rest of the night. Adding the branch took the same
wizard from 30/167 to 108/167 and from 5 casts to a steady stream.

The branch is skipped below the heal threshold, because item use is gated on one global timer: a mana
potion drunk at 20% health is a health potion not drunk.

The spell list is a whitelist, for the same reason `SkillSet` keeps one: the client's own cast
handler is a two-hundred-line switch where each group of spells fills the packet differently — some
take a target, some a direction, some a ground cell, some a corpse. Everything on the list is from
the group that sends `Target` = the monster's `ObjectID`, so one packet shape covers all of it.

**No line-of-sight check, deliberately.** The Mir 2 agents walk a line to the target before casting
and copying that would have been the obvious thing to do — but `FireBall.MagicCast` tests
`CanAttackTarget` and range and nothing else. A spell through a wall lands on this server, and
refusing to cast one would be inventing a restriction the game does not have.

Verified live rather than by inspection: a level 13 wizard casting gained **773 experience in 45
seconds** against **152** for a level 17 warrior swinging, with one server resync in 44 decisions —
a refused cast answers with `S.UserLocation`, so a low resync count is the proof the casts were
accepted.

Element choice, when it comes, should read resistances from `MonsterInfo` in `System.db` rather than
measuring them. The learned `monsters.json` is for what the database cannot say — real damage taken,
real kill counts — and measuring something exact and free would be slower, noisier and occasionally
wrong.

### 6-7. Where to hunt

This is the most interesting one, and it is judgement-shaped rather than rule-shaped.

The data is all present: `MonsterInfo` carries level and stats, map spawns give what lives where,
and the drop tables are already understood from the item-browser work in the client. So "which map
suits my level" is computable, and "where do I go to find a specific upgrade" is a query over drop
data the bot could already answer.

What is not computable is the trade-off: safer ground versus faster experience, a long trip for a
better drop chance, when to give up on a spot. That is exactly the shape of decision a model is
better at than a threshold.

**Partly built.** `hunting.json` records experience per hour per (map, class, level band) and
`monsters.json` records what actually hurts; `AutoTravel` consults them at the end of a town trip,
which is the one moment travelling is safe — bag empty, supplies restocked, nothing mid-fight.

Two design choices worth keeping:

- **Band the levels.** The Mir 2 agents key records to an *exact* level, which works when hundreds of
  agents share a memory because someone else already ground through yours. With two bots it means the
  memory is empty almost every time it is consulted.
- **Seed the exploration order, never the measurements.** `PreferredMaps` decides which map gets
  measured *first*. Writing invented exp rates into the memory would poison every real observation
  ranked against them.
- **Read the same memory at two granularities.** `monsters.json` was being written in full detail and
  then consulted only to judge whole maps, which is far too blunt — one nasty thing wandering through
  good ground should be walked around, not cause the ground to be abandoned. Per-monster avoidance
  now reads the same records when *choosing* a fight, measured against **current** health rather than
  maximum, so caution grows as a fight goes badly. Something already attacking is still fought back;
  the health thresholds decide when to disengage.

Expect the first journeys to be poor: with nothing measured the danger filter has no data either.
That is the honest cost of a memory that only contains what it has actually seen.

### Rebuilt after a five-hour run that produced nothing

The first version exploited far too hard, and a night of logs showed exactly how. A warrior made
**396 town trips and 4 travel decisions in five hours**, tried one map other than its starting town,
and was pulled straight back. Six things were wrong and they compounded:

| | Was | Now |
| --- | --- | --- |
| Ranking | best rate **ever** seen | **mean** rate, discounted by deaths |
| Deaths | recorded, used by nothing | divide the rate by `1 + n × 0.15` |
| Short windows | discarded entirely | banked when they ran ≥ `MinimumSampleMinutes` |
| Choice | always the single top map | at **random from the top N**, as Mir 2 does |
| Exploration | only when *nothing* was measured | until N maps are known, then a residual chance |
| Unvisited maps | judged by a memory that had never seen them | judged from `System.db` before going |

**Why not simply shorten the sample window.** It was the obvious fix — the mine missed by 99 seconds
— and it would have made things worse. Short windows are noisier, and while the ranking kept the
*best* sample, more windows meant more chances to roll a freak high one. Closing the window on
departure captures the identical data and adds no noise.

**Why coverage rather than Mir 2's epsilon.** They roll a flat 1-in-20 to explore, which fires
constantly across hundreds of agents. We only reconsider when a town trip ends — four decision
points in five hours — so the same roll would explore about once a day. Exploring until N maps are
measured self-tunes and needs no guess. It is the same adaptation as banding the levels, for the
same reason: their numbers assume a crowd.

**The database filter is what makes exploring safe.** `MonsterMemory` only knows maps that have
already hurt us, so it is silent precisely when the question is asked — before the first visit.
`MapProfile` reads what actually spawns on a map beforehand. Its one rule is the **median** monster
level, and that is a finding rather than a design: see the runbook for why `IsBoss` and `MaxLevel`
had to be thrown away.

**Caves are nested, so explore them from the top.** 140 of 210 maps are numbered floors, and Banya
Temple runs to Lv 10. Choosing Lv 3 to "explore" is really choosing three unmeasured maps at once in
ascending order of difficulty, with the whole of Lv 1 and Lv 2 to cross on the way and the least
health on arrival. A floor is a candidate only once every shallower floor of the same cave has been
measured. Exploration also prefers the nearest maps, so the frontier expands in rings rather than
leaping across the world.

**Money gates travel.** A journey costs `TravelGoldPerHop` per map transition, and below `PoorGold`
the bot is restricted to the named town maps entirely. Poverty is the one state where wandering is
actively harmful: it cannot buy potions, a way back, or a repair, so every problem it meets is
permanent. The restriction is logged whenever it applies, so it can never quietly become a
"too poor to leave, too poor at home to ever leave" trap — which is the failure family this
codebase keeps rediscovering.

### 8. Refining and upgrading

`C.NPCRefine { RefineType, RefineQuality, Ores, Items }` plus `NPCRefinementStone` and
`NPCRefineRetrieve`. Mechanically straightforward packets, but the *policy* — what is worth
refining, at what risk, with which ores — is deep game knowledge. Worth doing last, and worth
specifying from the server owner's own rules rather than inferred.

### Where this meets Jev

Marking the shape of each item above is deliberate. The rule-shaped ones (1-4, 8) should stay
deterministic: they have correct answers, and a model would be slower, costlier and harder to debug.
The judgement-shaped ones (6, 7, and the mana half of 5) are where a model earns its cost, because
there is no threshold that is right in every situation.

That split is the honest case for step 3, and it is much narrower than "let the model play the game".

## Patterns worth knowing before touching this code

Written after a day that produced a dozen bugs of only four shapes. If you are picking this up with
no context, these are the things that will save you the most time.

### On this server, "no" is usually said by saying nothing

The server refuses a great many requests by **doing nothing at all** - no error packet, no failure
result, sometimes a chat line the bot does not read. Three found in one day:

| Request | How it is refused |
| --- | --- |
| `C.NPCBuy` for more than you can afford | silence, and the WHOLE order is dropped - not a partial fill |
| `C.NPCRepair` costing more than your gold | a chat line and `return` - **no `S.NPCRepair` at all**, success or failure (`PlayerObject.cs:11730`) |
| `C.Move` into a blocked cell | `S.UserLocation` snapping you back, which is a correction, not an error |
| `C.NPCSell` containing one bad link | the validation loop `return`s on the **first** offender, so the WHOLE order is dropped - no chat line, nothing |

So **any request that can be refused needs a timeout, not just a failure handler.** A callback named
`OnRepairResult` looks like it covers the failure case and does not. Where a batch is involved, the
server prices the whole batch and refuses all of it, so the fix is to compute the cost client-side
and ask only for what you can pay for - `ClientUserItem.RepairCost` and `NPCGood.Price` are both
available and are the same arithmetic the server runs.

### ...but check whether it answers before deciding it does not

The rule above is right often enough to become a habit, and the habit is the trap. `NPCSell`
**does** answer. It enqueues `S.ItemsChanged` at the top of the handler and sets `Success` on that
same object at the bottom - and because the server queues the packet *object* and serialises it
later (`BaseConnection.Enqueue`, `SendList.Enqueue`), the verdict does reach the client.

A comment in `Backpack` had recorded the opposite: that the packet is "echoed before the server
validates the sale, so it is not a reliable success signal". The echo part was true. The conclusion
was not, and it justified updating the local model on *send* instead. One refused order then left
the bot permanently blind to items it was still carrying - it crossed thirty-five slots off, the
server took none, and nothing short of a relog recovered it. A level 24 warrior spent twenty-four
consecutive town trips reporting "0 sellable of 7 slots" while carrying forty-two.

**Optimistic local updates are only safe where refusal is impossible.** Where it is not, err
towards believing you still hold something: an item you wrongly think you have is re-offered next
trip and sold then, while an item you wrongly think is gone is invisible for ever.

### A predicate that answers a weaker question than its caller asks

`SpellBook.HasCastable(world)` answers *"does this character know an attack spell"*. The kiting
branch used it to decide *"should I back away so I can cast"*, which is a different question - it
needs mana, and cooldowns, and the mana floor.

The comment above the call even said so: *"A caster out of mana is a melee character for the moment
and should behave like one."* The gate was there, correctly reasoned, and asked the wrong thing. A
Taoist at 0 of 74 mana walked out to spell range, could not cast, walked back in, and repeated
until the no-progress watchdog gave up on the target - 182 abandoned chases against **zero** for
either melee bot, and about one attack every twelve minutes.

The tell is a predicate whose name is a *capability* (`Has…`, `Knows…`, `Can…`) being used to gate
an *action right now*. Those are the same question only when nothing is consumable.

### A filter makes "not present" ambiguous

`HuntingMemory.Best()` drops any entry with no measured rate - sensible for ranking, since a map
with no samples cannot be compared. `TryExplore` then built its "already been there" set from
`Best()`, so a map absent from the ranking read as **unexplored**.

The maps missing a rate are exactly the ones that killed the bot before a sample window could
complete. So the worse a map was, the fresher it looked: Phantom Forest took one wizard eight
times, banked no experience at all, and was re-chosen every time at 5,000-10,000 gold a trip. All
nineteen deaths were sitting in memory being read by nothing.

**A derived view that drops rows changes the meaning of absence for every consumer.** If one caller
asks "is it worth going?" and another asks "have we been?", they cannot share a list.

### A cache keyed on change freezes anything keyed on time

`PublishIfDue` skipped rebuilding the status snapshot unless `WorldModel.Version` or the history
had changed. That is a sound optimisation for anything derived from world state and wrong for
everything derived from the clock - uptime, seconds since the last experience gain, a ten-minute
stuck threshold.

A motionless bot stops changing `Version`, which is precisely what a wedged bot looks like. So
every indicator built to catch that case froze at the moment it became worth reading. Rebuild on
change **or** on a tick; keep the version check only to avoid rebuilding faster than anyone polls.

### A comment that disagrees with the code below it is usually right

Twice in one day the intent was written down correctly and the code did the opposite:

```csharp
// Topping up healing potions matters more than the scrolls ...
if (!_boughtScrolls) { ... }     // ... and scrolls were bought first
if (!_boughtPotions) { ... }

// Books are never cargo ... so always pick one up.
if (info.ItemType == ItemType.Book) return !heavy;    // ... unless the bag is 70% full
```

Both cost real money and levels. When a comment and the code disagree, the comment is the
specification and the code is the bug.

### Sentinel values that collide with real values

```csharp
.Select(slot => items.InSlot(slot)?.Info?.ItemType ?? ItemType.Nothing)
.Where(t => t != ItemType.Nothing)
```

`ItemType.Nothing` meant "the lookup failed" **and** is the genuine type of every crafting drop -
chestnuts, bones, galls. Filtering the sentinel filtered the items, so no buyer was ever sought for
them and a warrior accumulated 96 chestnuts with a vendor for them in the same town. Filter on the
thing that can actually fail (the lookup), never on a value the domain also uses.

### A town splits its trade, so resolve one vendor per NEED

Fixed three times, in three places, because each was written independently:

- **Buying gear** - Bichon splits weapons/armour/accessories across Mr. Kang, Linda and Amy, and
  Linda splits her own stock across two dialogue pages
- **Selling** - Joeban buys 37 item types and Loy buys exactly one (`Nothing`); asking for the single
  best buyer always returns Joeban
- **Repairing** - Mr. Kim does weapons, Sara armour, Dakota accessories, so a trip-wide "already
  repaired" latch means one trip can only ever use one of them

The shape is always the same: a `BestXFor()` that returns ONE vendor, where the need is plural. Cover
the needs, not the best single match.

### Per-trip state belongs in exactly one reset

`Abort()` cleared nine flags, `PageChanged` cleared five, and the SUCCESS path cleared none. So
`_repaired`, `_repairedSpecial` and `_boughtBook` - the three only `Abort` touched - stayed true for
the rest of the connection after the first trip that completed normally. **A bot whose trips
succeeded repaired once per login and then silently never again**, while a bot whose trips kept
failing repaired every time, because aborting is what reset it.

One `ResetTripState()`, called from both places. If you add a per-trip flag, add it there.

### A latch that gates the trigger instead of the cooldown becomes a deadlock

"One urgent trip per problem" is a good rule for **skipping the cooldown**. Wired into *whether to
go at all*, it means a problem the trip could not fix stops being a reason to go - and if only the
trip could have fixed it, nothing ever will. Keep the two questions separate:

```csharp
bool reason = overweight || repairable || shortOfPotions || _forced;   // go at all
bool urgent = brokenUrgent || supplyUrgent || _forced;                 // go NOW
if (!reason) return null;
if (OnCooldown && !urgent) return null;
```

This is the same family as the three survival deadlocks above it in this document. Before gating an
action on a resource, ask what the bot does when it has none.

### The Mir 2 agents' numbers assume a crowd

Three of their constants had to be adapted, all for the same reason - they run hundreds of agents
sharing one memory, we run four:

| Theirs | Ours | Why |
| --- | --- | --- |
| Exp keyed to an **exact level** | keyed to a **band** of 5 | with four bots the memory is empty almost every time it is read |
| Explore on a flat **1-in-20** roll | explore until **N maps measured**, then 15% | we reconsider only at the end of a town trip - four decision points in five hours, so 1-in-20 explores about once a day |
| Keep the **best** rate ever seen | keep the **mean**, discounted by deaths | volume drowns outliers at their scale; at ours one freak window becomes an unbeatable anchor |

Read their code for the *shape* of a decision. Re-derive the constants.

### Travel routes cross maps you must not hunt on

49 of the server's 244 maps have no respawns at all - castle interiors, halls, connecting corridors.
Sabuk Keep is one, and it sits on the route from Bichon Town to Banya Temple. Crossing it is correct;
being left on it is not, and a bot stranded there roams for ever because the only thing that
reconsidered a hunting ground was the end of a town trip, and a bot with nothing to fight never fills
a bag to trigger one.

**`WorldGraph` must stay ignorant of monsters** or those routes vanish. The check belongs at the
point of standing still, not at the point of routing.

### The way in is also the way out, and the server puts you on it

Stepping onto a movement cell is what changes the map, and the server lands an arriving character
at a **random point of the destination region**, which sits right beside the exit coming back.
Measured rather than assumed: arriving in Banya Cave Lv 1 put the bot at 132,175 with the way back
at 124-126,180-182 - **6 tiles**. Not on the door, but well inside the distance a random walk
crosses in seconds. Everything the bot does next is a few unlucky steps from undoing the journey.

Observed: bot 1 oscillated between Sabuk Keep and Banya Village repeatedly, and only escaped when
monsters happened to stand on the cell. It also entered Banya Cave Lv 1, fought, wandered into the
return cell and came straight back out. Two things made it inevitable:

- **Roaming does not use the pathfinder.** `NextRoamDirection` picks one of eight compass
  directions and commits to 3-12 steps, re-rolling after two blocked moves. Every other movement
  mode goes through `TrySteer` and A\*. So against a tree the bot does not route around it - it
  bumps, re-rolls, and takes a fresh random draw. That is a random walk in a bounded pocket, and a
  random walk returns to its origin far sooner than intuition suggests.
- **Nothing outside routing knew where the doors were.** `WorldGraph` has had every transition cell
  for every map since it was written, parsed from `MovementInfo`. Its only consumers were `Journey`
  and a menu filter.

The fix is the Mir 2 agents' and it is three lines of intent: **exit cells go into the same avoid
set as blocking creatures, and stop being obstacles while a journey is running.** Theirs is
`MovementHelper.BuildObstacles`, gated on `radius > 0` - obstacles apply to local wandering, not to
a deliberate crossing. Ours gates on `Travel.Active`, which is the same idea with better data: they
had to *learn* movement cells by walking through them and writing down where they came out, and we
read all 557 of them out of `System.db` at startup.

`MirBot.exe --exits` prints the footprint, and the numbers are what make this safe: exit cells are
**0.01% to 2.06% of a map's walkable ground**, worst case Numa Ruins Lv 5 (58 exits, a teleport
maze). Banya Cave Lv 1 is 17 cells of 8,432. Avoiding them cannot wall the bot in.

Avoidance alone is not enough, because it cannot help with the cell the bot was *put* on and
roaming does not path at all. So arrival also walks clear of the exits before hunting starts -
`ClearDoorway`, below Heal and Flee so it can never hold position in a doorway while dying.

### The silent-refusal table, extended

The original three entries were only the beginning. Every one of these was found the same way -
the bot recorded an action as done, the server had quietly declined, and nothing anywhere
disagreed until somebody read the login dump.

| Request | How it is refused | What it cost |
| --- | --- | --- |
| `C.PickUp` with no free slot | `ItemObject.PickUpItem` returns false. No packet. | Bot stood on an item asking for ever |
| `C.ItemMove` of a part into `GridType.Storage` | wrong grid, dropped | **Every part deposit ever made** |
| `C.ItemMove` to storage outside a safe zone | dropped | banking, entirely |
| Town scroll where `MapInfo.AllowTT` is false | chat line, no teleport | a scroll, on 4 Phantom Ship maps |

The pattern underneath all of them: **the bot updates its own model optimistically the moment it
sends the packet.** `Items.NoteUsed`, `Items.NoteDeposited` and friends assume success. That is
fine for latency, and it is a trap for verification - the bot's own status will happily confirm a
thing that never happened. **The authoritative read is what the server sends at login**
(`S.Login.Items`, `StartInformation.Items`), and a discrepancy between those and the running model
is the signal that something is being refused.

### Slots and weight are different limits

`Globals.InventorySize` is a flat 48 with no expansion. `BagWeight` is separate, and the bot had
only ever checked weight - for the town trip, for looting, for everything. A bag of rings and
crafting drops reaches 48 of 48 at **73% of the weight cap**, and in that state the bot silently
stops being able to loot while every rule it owns says there is room.

`CanGainItems(checkWeight: false, ...)` is the server's own predicate and is worth mirroring
exactly, including its exemptions: quest items, `ItemEffect.Experience`, and
`SEnvir.IsCurrencyItem` need no slot at all. Getting the currency exemption wrong would stop a
full bot collecting the gold that pays for the trip that empties it.

### Two storages, not one

`PlayerObject` has `Storage[1000]` **and** `PartsStorage[1000]`, addressed as `GridType.Storage`
and `GridType.PartsStorage`. Which one an item belongs to is decided by its slot on arrival:
anything at or above `Globals.PartsStorageOffset` (2000) is a part (`PlayerObject.cs:194`).

The bot modelled one grid and always sent `GridType.Storage`, so parts were refused every time.
They also cannot be sold (`DisposableSlots` and `Sellable` both refuse them, deliberately), so the
one way out of the bag was the one way that did not work.

### Vendors are not in safe zones

Storage needs a safe zone (`PlayerObject.cs:7421`). The town trip attempted banking wherever the
itinerary happened to finish, on the stated assumption that "vendors normally stand in one".

`MirBot.exe --safezones "Banya Village"` says otherwise: of fourteen NPCs, **thirteen are outside
the zone**. The only one inside is Joeban, and only because he was placed programmatically on a
`ValidBindPoint`. So banking had never once run - zero `Deposit` and zero `Withdraw` decisions
across an entire log - and because `WorthStoring` items are held back from selling *on the promise
that they will be banked instead*, the unkept promise meant gear accumulated permanently.

The bot now walks into the zone rather than giving up. That needs `SafeZoneInfo`, which is not one
of the collections `GameDatabase` binds onto `Globals` - read it from the session directly.

### A town scroll goes to the bind point, and the bind point moves

`UpdateBindPoint(CurrentCell.SafeZone)` runs whenever the character stands in a safe zone
(`PlayerObject.cs:1454`). So the bind point is not "home", it is **wherever the character last
stood in a safe zone** - and walking through Sabuk Keep on the way somewhere re-binds there.

Bot 1 then scrolled itself back to Sabuk Keep with a full bag. Sabuk Keep has a safe zone and not
one vendor. The scroll was gone, the trip aborted, and only travel walking it to Bichon saved it.
The bot can know this without being told: watch `S.SafeZoneChanged`, remember the map, and refuse
to spend a scroll when that map has no vendors.

### "Store it for later" needs later to be possible

`WorthStoring` banked anything equippable whose requirement was not yet met, on the reasoning that
levelling would unlock it. True for `RequiredType.Level`. False for `MC` on a warrior, whose
`MaxMC` stays near zero for ever.

`CanEquip` does not catch it, because such items are usually `RequiredClass.All` - not forbidden to
a warrior, merely pointless. A warrior banked a Platinum Necklace this way. The three offensive
stats are each one class's primary, so a requirement on the wrong one is permanent, not temporary.

### A measurement that is never taken looks exactly like one not worth taking

The bounce above cost more than the walking. `CloseExperienceSample` discards a window shorter than
`MinimumSampleMinutes`, so a map the bot bounced out of in two minutes recorded **nothing**. And
`TryExplore` builds its "already measured" set from `Hunting.Best(...)` - maps with a recorded rate.

So an unmeasurable map stays an explore candidate **for ever**, and the bot keeps choosing it and
keeps bouncing. Worse, the shallowest-floor-first rule requires shallower floors to be *measured*
before deeper ones become candidates, so one bouncing cave entrance silently locks out every floor
below it - Banya Temple runs to Lv 10.

The general shape: **when "not yet known" and "known to be bad" are the same state, a failure to
measure becomes a loop rather than a gap.** Worth checking anywhere a candidate set is built by
subtracting what has been measured.

### The same budget, measured three different ways

`HealthPotionWeightPercent` sets a budget in **weight**. Three separate places then compared it to
a **count**, and each one failed differently. They were found one at a time, over an hour, each
looking like an unrelated bug:

| Where | The comparison | What it produced |
| --- | --- | --- |
| `Backpack.Fill` | weight budget / best tier weight, counted in units | exact only while every stack is one tier |
| `BotConfig.Target` | `Math.Max(floor, count)` with a count floor | buy/sell churn on heavy consumables |
| `TownTrip.shortOfPotions` and the book gate | `CountHealthPotions() < budget * pct / 100` | a bot declaring itself short while over-stocked |

The middle one is the sharpest. `HealthPotionReserve` is 10 *potions*; the budget for a level 26
warrior is 82 *weight*. An `Elixir Of Life (V)` weighs **nineteen**, so the budget affords four and
the floor demands ten. The buyer tops up to ten, the keeper trims to four and calls six surplus, and
the next trip buys them back - a shopping loop that also pays the vendor's margin each way.

The third produced `restock first (10 potions, need 20)` on a bot carrying 44 of its 82 budget:
comfortably stocked, declaring itself short, and taking a town trip it could not satisfy, because
buying more would have blown the budget it was already inside. That is the *original* trip-churn
symptom arriving by a completely different route, which is the point - fixing the sell side alone
would have left the churn in place and looked like a failed fix.

**If a quantity has a unit, say so at every boundary it crosses.** A default argument of
`potionWeight = 1` is what let a weight silently become a count in four call sites.

### "The biggest one that fits" is half a ranking

`BestPotion` ranked by restore, capped at what the character can absorb, which is the correct
*ceiling* - a potion whose healing is thrown away against the health cap is wasted however cheap it
is. It is not a complete ordering, because the bag is the binding constraint on these bots: it is
what sends them to town.

So what the potion budget actually buys is **restore per unit of weight**, and that is not monotonic
in tier. The ordinary tiers climb - 30/1, 70/1, 110/2, 170/3 - and then `Elixir Of Life (V)` lands at
nineteen weight, comfortably inside a level 26 warrior's 311-point gap, and "biggest that fits" chose
it: about a quarter of the healing per unit of bag that a `Healing Potion (IV)` gives, at five times
the weight per drink.

Ceiling first, then per-weight, then bigger-heal as the tie-break, because fewer drinks means less
time standing still taking damage.

### A restart is an unplanned teleport

`ConsiderTravel` runs only at the end of a town trip. That is fine while journeys complete, and it
means a bot which arrives somewhere by any other route hunts there until its bag fills, however bad
the map.

Restarting the host twice to deploy fixes put a level 26 warrior down mid-journey in Bichon Town,
where it spent the next several minutes killing chickens and Claw Cats with 414,675 exp/hour recorded
two free map-steps away. Nothing in the loop was capable of noticing: there is no maximum time on a
hunting ground and no periodic re-evaluation.

The operator fix is the existing `travel` endpoint. The real fix is a re-evaluation that does not
depend on having gone shopping.

### A unit conversion that is exact in the common case

`HealthPotionTarget` returns a **weight** budget - a share of the bag - and every sell-side caller
passed it on as though it were a **count**, because its `potionWeight` argument defaults to 1 and
tier-one potions really do weigh 1. Everything agreed for as long as the bag held one tier.

`Backpack.Fill` then compounded it: given a weight budget, it divided by whatever the single
strongest tier weighed and counted *units* against the result. For a bag of twelve tier-two and
fifty-two tier-one potions - all weight 1 - the answer came out right by arithmetic accident, and
would have been wrong in either direction the moment a tier-four (weight 3) was mixed in.

The tell is a conversion whose correctness depends on a value being 1. Spend the real quantity
instead: `ClientUserItem.Weight` already accounts for `Count`, so accumulating it against the budget
removes the conversion rather than improving it.

### An affordability test that asks for the whole order

`BestPotion` sorted the tiers correctly - biggest that fits the healing gap first, which for a 310
health assassin at a 60% threshold is tier three - and then asked each candidate *"can I afford the
full target of these?"*. The full target was sixty-nine. A bot with 900 gold can afford sixty-nine
of nothing, so it fell through every good tier into the cheapest-on-the-page fallback and came home
with fifty-two Healing Potions restoring 30 apiece.

Nothing in the log says "downgraded": the purchase line reads `NPCBuy (43 x Healing Potion)` and
looks like a bot stocking up sensibly. It was visible only by pricing the tiers by hand.

**Gold decides how many, never which.** A half-filled target is topped up on the next visit at no
extra cost, because the bot was going to town anyway; a bag full of the wrong tier persists until
something sells it. And the wrong tier is not merely weaker - thirty healing against a 124-point gap
is four drinks to top up, taking damage throughout, and half the bag gone to carry them. That bot
was making five town trips in nineteen minutes and died in Flea Cave drinking tier ones.

### Bucketing a continuum turns a boundary into amnesia

`HuntingMemory` keys every measurement by `LevelBand = (level - 1) / LevelBandSize`, and `Best()`
and `MeasuredCount()` matched the band **exactly**. So every five levels a bot forgets everything it
has ever measured: `MeasuredCount` drops to zero, `ExploreUntilMapsKnown` forces exploration, and it
wanders off to whatever unmeasured map is nearest.

A level 26 warrior did this in front of us. It held 414,675 exp/hour for Deserted Mine and 387,508
for Ant Cave North, levelled 25 -> 26, crossed from band 4 into band 5, logged
`exploring - only 0 of 4 maps measured`, and paid 5,000 gold to teleport to a map its own memory
rated at 127,796. It was not choosing badly - it could no longer see what it knew.

The reason for the band is real: a rate measured at level 10 says little about level 25. But **"less
relevant" is not "unknown", and the honest expression of it is a discount, not a filter.** `Lethal()`
had already met this and solved it - it accepts the current band and the ones below within a window -
which is the strongest possible hint that the other two readers were wrong. Two things to get right
when widening the window: rank stale entries with a penalty so a current-band reading still wins, and
**dedupe by map**, or one map competes with itself for several slots in the `take`.

### Two guards for one risk, and the blunt one decides

Travel had three separate defences against a poor bot stranding itself: `HopCounts` prices teleport
fares against the gold in hand, `Affordable` requires `hops * TravelGoldPerHop`, and a whitelist
restricted a bot below `PoorGold` to the maps named in `TownMaps`. The whitelist's own comment said
the deadlock shape - *"cannot leave until rich, cannot get rich because home is poor"* - was worth
logging so it could never happen silently.

It was logged. It happened anyway. A level 18 assassin spent over an hour between Bichon Town and
Banya Village, the two worst maps it had ever measured, with Ant Cave and Deserted Mine two free
steps away.

Both of the remaining guards were wrong in the same way, and the second is the one that would have
survived deleting the whitelist: `TravelGoldPerHop` is 3,000, sized for a journey whose legs are
bought from a teleport NPC - fare out, fare home, enough left to be far from a vendor. Applied to a
**walk** it is nonsense. The leg costs nothing, the way back is the same leg, and the bot is carrying
a town scroll. So a bot with 907 gold was refused a free step to a better hunting ground because it
could not afford 3,000.

Being broke is a reason not to **spend**, not a reason not to **move**. Price the journey by what it
actually costs: a second `HopCounts` pass with `freeOnly: true` answers "where can I get to without
paying anything", and walked legs are charged `TravelGoldPerFreeHop` (250) rather than the fare rate.

The general lesson is that stacking a crude guard on top of precise ones does not make the system
safer - it makes the crude one the only one that matters, and hides that fact behind the precise
ones looking reasonable.

### The locked-item red herring, twice

Surplus potions that will not sell look like a permissions problem, and both times they were not.
`Backpack` has a whole unlock path - `LockedSlots`, `DisposableIncludingLocked`, `BotAction.Unlock`,
`UnlockToSell` - built the first time this came up, and it works.

It is also irrelevant when the items are **inside the keep budget**, because they never reach the
disposal list to be considered for unlocking at all. The status page says which it is, in the same
words both times: `0 sellable of 4 slots (64 health potions, 3 scrolls held)`. *Held* means the
reserve is holding them, not that the server is. Read the diagnostic before reaching for the flag.

### An unrooted Timer is collected before it fires

```csharp
new Timer(_ => captured.TryEnqueue(BotCommandKind.Start), null, delay, Timeout.InfiniteTimeSpan);
```

Assigned to nothing, so eligible for GC immediately. With two bots the delays were short enough that
it always won the race; at four bots the third bot's thirty-second timer was collected and it simply
never started - no error, no connect attempt, just Offline for ever. Hold timers in a field.

---

## The status page, and the one rule that keeps it safe

The runbook covers what the page *shows*. This is what to know before changing it, because the
obvious way to add a field to it is also the way to crash the host.

### The contract

Four bots, four threads, one web thread. `BotInstance.cs` states the rule at the top of the file
and calls it "the whole safety story":

> Everything private here is touched ONLY by the bot thread. WorldModel, Backpack, ScriptedBrain
> and TownTrip are all single-thread-affine mutable state. Other threads may touch exactly two
> members: `State` (a volatile read) and `TryEnqueue` (a bounded concurrent queue). Nothing else.

In practice there is a third: `Status`, an immutable record published by a volatile write. That is
the only path data takes **out** of a bot, and `TryEnqueue` is the only path anything takes **in**.

Adding a number to a card by reaching for `_connection.World` or `Backpack.Carried` will appear to
work for a long time and then throw `"collection was modified"` on the web thread, mid-response,
under load. `Backpack.Carried/Stored` and `WorldModel.Objects` hand out the **live** dictionaries;
the first `Set` or `Remove` on the bot thread while the serialiser is walking one is the crash.

### So everything goes through the snapshot

`BotStatus.cs` carries the rule in a comment, and it is worth restating because it is easy to
violate without noticing:

> Everything reachable from `BotStatus` is a value type, a string, or another record here. No
> `ClientUserItem`, `WorldObject`, `ItemInfo`, `Stats`, `Decision` or `NPCPage`.

Lists are **materialised on the bot thread before publication**, never left lazy - a lazy
`IEnumerable` closes over the live collection and defers the crash to the web thread, which is
worse than causing it outright. `BotInstance.DescribeItem` is the worked example: it flattens
`ItemInfo.Stats` (owned by the shared game database) and `ClientUserItem.AddedStats` (rewritten in
place whenever the server re-sends the item) into plain records before either can escape.

The same applies to the memory banks. `HuntingMemory.Best()` returns the live `HuntingEntry`
objects *after releasing the lock*, which is fine for the bot thread that asked - it owns the
decision it is about to make - and a race if the web thread serialises them. Every memory endpoint
therefore has a `Snapshot()` that copies **while holding `Sync`**.

To add a field to the page: put it on `BotStatus`, fill it in `Build()`, render it. If it cannot be
expressed as a value type, string or record, it does not belong there.

### `StatusServer` is deliberately ignorant

It is constructed with delegates and **never sees a `BotInstance`, `WorldModel` or `Backpack`
type**. That is not tidiness - it is what makes reaching live state structurally impossible rather
than merely discouraged. A new endpoint adds another `Func<>` bound in `BotHost.Run`, not a
reference to the host.

Two mechanical notes:

- **`Send` only writes UTF-8.** Binary responses (the item icons) need `SendBytes`; encoding a PNG
  as a string corrupts it.
- **The accept loop is single-threaded**, including response writes. Anything expensive - encoding
  a 1360x1500 map, reading a memory file - is cached, or it stalls every other poll behind it.

### Commands can be queued but not answered

`TryEnqueue` returns whether the command was *accepted*, and `StatusServer` replies `202` on that
basis. It cannot report what the bot later decided. So **anything that can be refused is validated
in the endpoint, before queuing** - see `ConfigSchema.Validate`. A malformed setting rejected on
the bot thread would already have been reported to the operator as a success.

Where a result genuinely can only be known later, publish it on the snapshot
(`BotStatus.ConfigResult`) rather than trying to make the queue synchronous.

### Config is now written, which changes what is safe to read

`BotConfig` is a shared mutable object read every tick. That was safe to read from anywhere only
because nothing ever wrote to it. Live editing changes that, so:

- edits are applied **on the bot thread**, via `BotCommandKind.SetConfig`;
- the values the page displays are **copied into the snapshot**, never read live off
  `BotInstance.Config`;
- one parser, `BotConfig.Apply`, serves both the ini and the API - two parsers for the same keys
  eventually disagree about one of them.

Three categories, and the middle one is the trap:

| | Behaviour | Examples |
| --- | --- | --- |
| **Live** | read through the shared reference every tick | most tuning knobs |
| **Copied** | snapshotted into another object at connect; needs `ReapplyConfig()` | `Journey`'s `TalkRange`/`GoldFloor`/`MaxGoldPercent`; `ScriptedBrain`'s five `LootValueRule` values |
| **Startup-only** | consumed once in `BotHost.Prepare`, from the FIRST bot's ini | `TownMaps`, `DataPath`, `MapPath`, `MemoryPath`, `LevelBandSize`, `UseTeleportNPCs`, `VersionPath` |

A copied setting that is edited but not re-applied looks correct in `BotConfig` while the code that
uses it carries on with the value it read at login. A startup-only setting edited at runtime looks
applied and does nothing at all, which is why the schema refuses them outright rather than letting
the page pretend.

**Credentials are not in the schema and are never serialised**, not even as read-only fields. The
page has no authentication by choice; that is a decision about *control*, and it does not extend to
handing an account password to anything on the network.

### The page is patched, not re-rendered

Cards are built once per bot and updated field by field. Only the tables are rewritten, and only
when a content signature changes.

This matters more than it sounds. The previous version reassigned `#bots`'s `innerHTML` every
second, which destroys and recreates everything underneath: an open `<select>` closed itself once a
second, and the workaround was to skip the refresh entirely while one had focus - so the numbers
stopped updating exactly when you tried to use a control. It also rules out anything owning state:
an input mid-edit, an expanded panel, a hovered tooltip, a `<canvas>` with a drawing on it.

Two corollaries for anyone extending it:

- **Any new stateful control must live outside a rewritten region.** The hunting table's search box
  and sort state sit outside the `<tbody>` that gets replaced, which is why a half-typed query
  survives a refetch.
- **Keys used in a signature must be stable.** A sequence number baked into each item cell's key
  made the generated HTML differ every tick even when the bag had not moved; the signature never
  matched, the grid was rewritten once a second, and an `<img>` was destroyed before it could
  finish loading.

`BotStatus` is a named-init record rather than a positional one for the same family of reason: at
48 members, `Build()` and `Idle()` had to agree by position, and two adjacent `int`s or `string`s
swapping places would still compile.

### Iterating without a rebuild

`StatusServer.Page()` prefers `status.html` beside the exe over the compiled `StatusPage.Html`.
Drop a file there and refresh - no rebuild, no bot restart. **Fold it back into `StatusPage.cs`
when finished**, or the next clean deploy quietly reverts everything.

## Risks

- **Jev launched 2026-09-15 and is early access.** Availability, pricing and the API surface may all
  move. Isolate every call behind `JevClient` so the blast radius of a change is one file.
- **~68% accuracy on TypeSafe's own 4-workflow benchmark** — mid-tier. Fine for "attack or flee",
  not something to trust with anything expensive.
- **Protocol drift.** The bot pins itself to our fork's packet definitions. It must be rebuilt
  whenever `LibraryCore` packets change, or it will desync silently.
- **English-primary model.** Not an issue for us; noted in TypeSafe's docs.
- **Server load.** Bots are real connections doing real work. Watch the server loop with a handful
  before running more.
- **The flood guard punishes the whole IP.** More than `MaxPacket` (50) queued packets at once, or
  1024+ bytes before a first valid packet, and the server bans the **IP** for `PacketBanTime`
  (5 minutes) and disconnects *every* connection from it — `ServerLibrary/Envir/SConnection.cs:179`
  and `:193`. One buggy bot therefore kicks your own client too, if both are on that machine. Keep
  multi-bot (step 4) on a separate host from where you play.

## Verification

1. Bot logs in, appears to a real client as a player, survives 30+ minutes idle without timeout.
2. Kill the network to `api.typesafe.ai` mid-session — bots keep playing on the fallback, no freeze,
   no crash, no packet backlog.
3. Stale-decision test: force a 2-second API delay and confirm the bot discards answers whose world
   version has moved on rather than attacking a corpse.
4. Cost check: run one bot for an hour, compare `usage.input_tokens` totals against the table above.
5. Confidence gates: log the distribution of `confidence` in real play and set thresholds from that
   data, not from the values guessed in this document.
6. Kill switch stops every bot within one tick.

## See also

- `mir3-server-vm.md` — SSH access, build recipes, environment gotchas (incl. the CRLF trap)
- `zircon-npc-authoring.md` — scripted NPCs, the non-LLM way to add characters
- TypeSafe docs: <https://docs.typesafe.ai/api.md>, <https://docs.typesafe.ai/primitives.md>,
  <https://docs.typesafe.ai/concepts/state.md>, <https://docs.typesafe.ai/confidence.md>
