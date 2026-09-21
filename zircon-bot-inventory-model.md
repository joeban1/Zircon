# The bot's inventory model — the contract, and the bug family that keeps breaking it

This document exists because the same bug was found and fixed **four separate times** in different
places, each time after hours of investigation, and the fifth instance is probably still out there.
Read this before touching anything in `Backpack.cs`, `TownTrip.cs` or the item packet handlers in
`BotConnection.World.cs`.

Source: `G:\Program Files (x86)\Mir3\MirBotSrc\MirBot\`
Server source (authoritative, see caveat at the bottom):
`G:\Program Files (x86)\Mir3\Server Backup\Codex Working\Zircon-master\`

---

## The one rule

> **Never change the inventory model when you SEND a request. Only when the server ANSWERS.**

The bot keeps a client-side mirror of the character's bag in `Backpack._inventory`, keyed by slot
index. Every item operation the bot performs — use, sell, drop, equip, deposit — is addressed **by
slot number**. If the bot's slot map and the server's slot map disagree by even one entry, every
subsequent operation names the wrong item, and the failures look nothing like the cause.

The server does not send inventory snapshots. There is no "tell me my bag" request. The only full
sync is `StartInformation.Items` at **login**. So a divergence, once it happens, is permanent for
the session.

## Why this keeps happening

Zircon's packet pattern is a trap for the unwary:

```csharp
// PlayerObject.cs:5580 — the response is enqueued BEFORE any validation
S.ItemChanged result = new S.ItemChanged { Link = new CellLinkInfo { GridType = link.GridType, Slot = link.Slot } };
Enqueue(result);

if (Fishing) return;                                    // Success stays false
if (Buffs.Any(x => x.Type == BuffType.DragonRepulse)) return;
if (!CanUseItem(item)) return;
if (Dead && ...) return;
if (SEnvir.Now < UseItemTime && ...) return;            // the 1-second item cooldown
...
result.Success = true;                                  // PlayerObject.cs:6476, only now
```

The answer **always arrives**. `Success` distinguishes accepted from refused. A refusal is therefore
*not* silent — but it looks silent if you never read the packet, which is exactly what the bot did.

## The four instances found so far

| # | Where | Symptom it caused |
| --- | --- | --- |
| 1 | `NoteSold` applied at send time | Sell orders voided; counts ran high |
| 2 | `NoteEquipped` via `S.ItemMove` ignored | Wizard "wore" a Flame Robe it was 3 weight short for; ~140 refused equips/minute |
| 3 | `NoteUnlocked` applied at send time | Items offered for sale while still locked server-side |
| 4 | `NoteUsed` applied at send time | **The big one.** See below. |

### Instance 4 — `NoteUsed`, found 2026-09-21

The bot called `Items.NoteUsed(slot)` immediately after sending `C.ItemUse`, for potions, skill
books and town scrolls. `Process(S.ItemChanged)` was **drop-specific**: if no drop was pending it
returned, discarding every use verdict.

The killer is the server's auto-potion path:

```csharp
// PlayerObject.cs:5568
if (SEnvir.Now < AutoPotionTime && item.Info.ItemEffect != ItemEffect.ElixirOfPurification)
{
    if (DelayItemUse != null)
        Enqueue(new S.ItemChanged { Link = DelayItemUse });   // Success = false: the PREVIOUS one is superseded
    DelayItemUse = link;
    return;
}
```

So sending two uses close together yields **one consumption and two model decrements**. Measured
live after instrumenting: **40 confirmed uses against 33 refusals** — a third of all item uses.
Every refusal used to delete an item the server still held.

Downstream, that is fatal:

1. Bot thinks slot X is empty; server still has an item there.
2. Next `S.ItemsGained` arrives with `Slot == -1`.
3. `Place()` picks X as the lowest free slot; the server skips X and picks another.
4. From here the two slot maps are permanently offset.
5. `NPCSell` names the wrong item, and **one bad link voids the entire order** with a bare `return`.

**Corroboration:** the slots that failed to sell individually (24 Slaying, 29 Slaying, 34 Poison
Dust) were exactly the items the server's own admin GUI still showed the character holding.

This also explains a symptom chased separately for hours: a town scroll that produced no movement,
no chat and no consumption. The use was **refused**; the bot deleted its copy anyway.

## Packet semantics, verified against server source

| Packet | Meaning | Notes |
| --- | --- | --- |
| `S.ItemChanged` | verdict on **use** or **drop** | `Link.Count` = what **REMAINS**; `0` = whole stack gone (`PlayerObject.cs:6486-6496`). Echoes `GridType`+`Slot` — key off that, not off a pending field. |
| `S.ItemsChanged` | verdict on a **sell** | Enqueued before validation, `Success` set after. One bad link voids all. |
| `S.ItemMove` | verdict on **equip / deposit / withdraw** | Same enqueue-then-validate shape. |
| `S.ItemLock` | verdict on **lock/unlock** | Returns silently when the slot is empty. |
| `S.ItemsGained` | items **announced**, not necessarily placed | See filtering below. |

### `S.ItemsGained` does not mean "this is in your bag"

The server sends it *before* deciding what to do with each item (`PlayerObject.cs:5441`). Three
kinds never reach the inventory, and the real client skips exactly these
(`Client/Scenes/GameScene.cs:3584-3597`):

```csharp
if (item.Info.ItemEffect == ItemEffect.Experience) continue;   // converted to exp
if ((item.Flags & UserItemFlags.QuestItem) == ...) continue;   // credited to the task, deleted
if (User.GetCurrency(item.Info) != null) { ...; continue; }    // added to a counter
```

Modelling any of these as a bag item is the **mirror image** of the optimistic-use bug: an entry we
hold that the server does not. `Backpack.OccupiesASlot` now enforces this.

### The six stack-merge conditions

`Place()` reimplements the server's `GainItem` merge (`PlayerObject.cs:5489-5500`). Both sides must
agree or slot numbering diverges. All six are required:

1. same `ItemInfo`
2. `existing.Count < StackSize`
3. neither side `UserItemFlags.Expirable`
4. `Bound` flags equal
5. `Worthless` flags equal, `NonRefinable` flags equal
6. **added stats equal** (`oldItem.Stats.Compare(item.Stats)`)

Condition 6 was once removed on a five-minute A/B that looked like a regression. That was wrong —
the sample was noise. The decisive evidence was external: the server GUI showed 19 potions in slot
1 where the client showed 21. **Our count running high is the signature of over-merging.**

`HasRoomFor` must apply the same six, or it predicts merges that `Place()` then refuses.

## Potion classification

Every potion test must go through the shared predicates. There were four duplicated inline copies;
fixing two and missing two produced a bug where `CountHealthPotions()` said 0 while
`HealthPotionWeight()` said 1 for the same item — and the reserve protects the *weight*.

- `IsHealthPotion` / `IsManaPotion` — include a `MinPotionRestore` floor (default 20).
- `IsWeakRestorative` — restores something but under the floor: **known trash, deliberately sellable**.
- Anything restoring nothing stays in the keep list: genuinely unknown (food, oddities).

Worked example — **Chicken Blood**: `ItemType.Consumable`, `Health=5`, `Mana=5`, price 10,
`SaleBonus5/10/15/20`. Every potion test said "medicine", so it counted against the health budget,
displaced real potions and was protected from sale. Its own SaleBonus stats say it is meant to be
sold in bulk.

Note the trap: excluding it from the potion buckets alone is **not enough**, because
`PlanConsumableKeeps` ends with `else keep[...] // food and oddities: not ours to judge here`. It
must be positively classified as weak, not merely un-classified.

## Recovering from a divergence

There is no resync packet. `BotInstance.NoteDivergence()` counts wholesale sell refusals and forces
a relog after `ResyncAfterSellRefusals` (3) within `ResyncWindowMinutes` (10), because login is the
only thing that rebuilds the model from the truth. Blunt, but the alternative is a bot that fails
every sale for the rest of the day.

Before this existed, the only reason long sessions ever recovered was that deploys happened to
relog the bots. That was luck, not design.

## How to investigate the next one

1. **Log both directions.** Every send and every verdict. Refused operations were invisible for
   months; the moment they were logged the cause took minutes.
2. **Print counts always, never `x{n}` only when `n > 1`.** That formatting hid a count of 0 behind
   the same text as a count of 1 and blocked one whole line of diagnosis.
3. **Compare against the server, not against your own model.** The decisive evidence twice came
   from the operator reading the server GUI and the game client side by side.
4. **Do not trust small samples.** Two of the wrong turns here came from A/B tests over five
   minutes.

### Hypotheses tested and DISPROVED — do not re-propose without new evidence

- Unlock race (`LockedSlots` only clears on the server's `S.ItemLock`).
- Order-size cap (42-slot orders succeed, 1-slot orders fail).
- Item type not accepted (`Sellable()` already filters on the live page's `Types`).
- Phantom zero-count stacks (counts now always printed; none exist).
- `Place()`'s merge rules being wrong (they match `GainItem` exactly).

## Caveat on the server source

`G:\Program Files (x86)\Mir3\Server Backup\Codex Working\Zircon-master\` is a copy of the
operator's **other** server from last year, not the live one. The operator states no code changes
were made to it, so it is trustworthy for protocol semantics — and every prediction made from it
has been confirmed live so far. Treat behavioural details as strong evidence, not gospel, and
confirm anything surprising against a log.
