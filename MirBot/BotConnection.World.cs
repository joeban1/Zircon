using System;
using System.Collections.Generic;
using System.Linq;
using Library;
using Library.SystemModels;
using C = Library.Network.ClientPackets;
using S = Library.Network.ServerPackets;

namespace MirBot
{
    // World-state packet handlers, kept apart from the login handshake in BotConnection.cs.
    // Everything here only records observed facts; nothing decides anything.
    public sealed partial class BotConnection
    {
        public WorldModel World { get; } = new WorldModel();
        public Backpack Items { get; } = new Backpack();

        public int ResyncCount;

        #region Self

        public void Process(S.UserLocation p)
        {
            // The server's authoritative correction after it refused a move. Snap to it and tell the
            // brain to back off - sending another move immediately just earns another correction.
            ResyncCount++;
            World.ApplyUserLocation(p.Location, p.Direction);
            OnResync?.Invoke();
        }

        public Action OnResync;

        #endregion

        #region Objects appearing

        public void Process(S.ObjectPlayer p)
        {
            World.AddPlayer(p.ObjectID, p.Name, p.Location, p.Direction);
            World.MarkSeen(p.ObjectID, asData: false);
        }

        public void Process(S.ObjectMonster p)
        {
            MonsterInfo info = ResolveMonster(p.MonsterIndex);

            World.AddMonster(p.ObjectID,
                !string.IsNullOrEmpty(p.CustomName) ? p.CustomName : info?.MonsterName ?? "monster",
                info?.AI ?? 0, p.Location, p.Direction, p.Dead,
                p.PetOwner, p.MonsterIndex, p.Poison);
            World.MarkSeen(p.ObjectID, asData: false);
        }

        public void Process(S.ObjectNPC p)
        {
            World.AddNPC(p.ObjectID, p.CurrentLocation, p.Direction);
            World.MarkSeen(p.ObjectID, asData: false);
        }

        public void Process(S.ObjectItem p)
        {
            World.AddItem(p.ObjectID, p.Item?.Info?.ItemName ?? "item", p.Item?.Info, p.Item, p.Location);
            World.MarkSeen(p.ObjectID, asData: false);
        }

        public void Process(S.DataObjectPlayer p)
        {
            World.AddPlayer(p.ObjectID, p.Name, p.CurrentLocation, MirDirection.Up);
            World.MarkSeen(p.ObjectID, asData: true);
            World.ApplyHealthMana(p.ObjectID, p.Health, p.Mana, p.Dead);
            World.ApplyMaxHealthMana(p.ObjectID, p.MaxHealth, p.MaxMana);
        }

        public void Process(S.DataObjectMonster p)
        {
            // Ownership has to be read from BOTH monster packets. Handling only S.ObjectMonster
            // leaves a pet that arrives through the data path looking wild, and a pet that looks
            // wild is a target.
            World.AddMonster(p.ObjectID, p.MonsterInfo?.MonsterName ?? "monster",
                p.MonsterInfo?.AI ?? 0, p.CurrentLocation, MirDirection.Up, p.Dead,
                p.PetOwner, p.MonsterIndex);
            World.ApplyHealthMana(p.ObjectID, p.Health, 0, p.Dead);
            World.MarkSeen(p.ObjectID, asData: true);
        }

        /// <summary>A monster was tamed, released, or a summon's tame time ran out.</summary>
        public void Process(S.ObjectPetOwnerChanged p) => World.ApplyPetOwner(p.ObjectID, p.PetOwner);

        /// <summary>
        /// The aggregate poison mask changed on something. Broadcast only on CHANGE
        /// (MapObject.cs:405), so this is the authoritative "is it poisoned" signal and the reason
        /// the bot needs no duration tracking of its own.
        /// </summary>
        public void Process(S.ObjectPoison p) => World.ApplyPoison(p.ObjectID, p.Poison);

        #region Buffs

        // Nothing here assumes a duration. S.BuffAdd carries RemainingTime, expiry arrives as an
        // ordinary S.BuffRemove, and the login dump seeds the lot - so "do I have this buff" is
        // always the server's answer, never a guess that can drift.
        //
        // All four of the others identify a buff by INDEX, which is why the store is keyed that way.

        public void Process(S.BuffAdd p) => World.AddBuff(p.Buff);

        public void Process(S.BuffRemove p) => World.RemoveBuff(p.Index);

        public void Process(S.BuffChanged p) => World.ChangeBuff(p.Index, p.Stats);

        public void Process(S.BuffTime p) => World.SetBuffTime(p.Index, p.Time);

        public void Process(S.BuffPaused p) => World.SetBuffPaused(p.Index, p.Paused);

        #endregion

        /// <summary>The server confirming a pet-mode change. One mode covers every pet we own.</summary>
        public void Process(S.ChangePetMode p) => World.ApplyPetMode(p.Mode);

        public void Process(S.DataObjectItem p)
        {
            World.AddItem(p.ObjectID, p.ItemInfo?.ItemName ?? "item", p.ItemInfo, null, p.CurrentLocation);
            World.MarkSeen(p.ObjectID, asData: true);
        }

        #endregion

        #region Objects changing

        public void Process(S.DataObjectLocation p) => World.ApplyLocation(p.ObjectID, p.CurrentLocation);

        public void Process(S.ObjectMove p)
        {
            World.ApplyMove(p.ObjectID, p.Location, p.Direction);

            // AutoPathService issues our moves itself and then waits for the client to say the move
            // began (WaitingForMovementStart). Without this reply the route stalls after one step.
            if (AutoPathing && p.ObjectID == World.SelfID)
            {
                OnAutoPathMove?.Invoke();
                Enqueue(new C.AutoPathMoveStarted());
            }
        }

        public void Process(S.ObjectDash p) => World.ApplyMove(p.ObjectID, p.Location, p.Direction);

        public void Process(S.ObjectTurn p) => World.ApplyTurn(p.ObjectID, p.Direction, p.Location);

        public void Process(S.ObjectHarvest p) => World.ApplyTurn(p.ObjectID, p.Direction, p.Location);

        /// <summary>
        /// A monster (or player) died where we can see it.
        ///
        /// The bot did not handle this at all. WorldModel.MarkDead existed and nothing ever called
        /// it, so the only way an object stopped being a live target was S.ObjectRemove - the
        /// corpse vanishing entirely, some seconds later. Until then IsLiveMonster stayed true for
        /// something already dead.
        ///
        /// Butchering needs the corpse, so this had to be handled to build it; but it is worth
        /// having on its own account.
        /// </summary>
        public void Process(S.ObjectDied p)
        {
            World.MarkDead(p.ObjectID, true);
            OnObjectDied?.Invoke(p.ObjectID);
        }

        /// <summary>Something in view died - the boss kill log listens for this.</summary>
        public Action<uint> OnObjectDied;

        /// <summary>Items that arrived in the bag (pickups, purchases, rewards).</summary>
        public Action<ClientUserItem> OnItemGained;

        /// <summary>
        /// An item is gone from a grid for good.
        ///
        /// This had no handler at all, so the bot kept items the server had destroyed. A wizard's
        /// Candle burned down to nothing and vanished in game while the bot went on believing it
        /// was wearing one at 0 of 8 - and a phantom is not merely a wrong status line. Worn items
        /// are counted against WearWeight, so an item that does not exist can be the three points
        /// that stop a real one going on, which is the shape of bug that cost a whole morning
        /// today with a Flame Robe.
        ///
        /// Success-gated like S.ItemMove and S.ItemLock: the packet is enqueued before validation
        /// and the flag is set on the same object afterwards, so a refusal arrives here too and
        /// must not be applied.
        /// </summary>
        public void Process(S.ItemDelete p)
        {
            if (!p.Success)
            {
                Log($"Delete REFUSED by the server: {p.Grid}:{p.Slot} - nothing changed.");
                return;
            }

            Items.NoteDeleted(p.Grid, p.Slot);
        }

        /// <summary>A corpse has given up everything it is going to. See ScriptedBrain.TryButcher.</summary>
        public void Process(S.ObjectHarvested p)
        {
            OnHarvested?.Invoke(p.ObjectID);
        }

        /// <summary>Raised when a corpse is finished with.</summary>
        public Action<uint> OnHarvested;

        public void Process(S.ObjectRemove p)
        {
            // Object ids are recycled. Anything the bot remembers ABOUT an id has to die with the
            // object, or it silently applies to whatever inherits the number next - but only once
            // it is really gone: a boss still shown through the data channel is not.
            if (World.ApplyRemove(p.ObjectID)) OnObjectGone?.Invoke(p.ObjectID);
        }

        /// <summary>
        /// S.DataObjectRemove - unhandled until the quest work, which left every data-only object
        /// (a Boss Tracking boss in particular) in the model for ever as a phantom target.
        /// </summary>
        public void Process(S.DataObjectRemove p)
        {
            WorldObject gone = World.ApplyDataRemove(p.ObjectID);
            if (gone == null) return;

            OnObjectGone?.Invoke(p.ObjectID);
            OnDataObjectGone?.Invoke(gone);
        }

        /// <summary>A data-only object disappeared (e.g. a tracked boss when the buff ended).</summary>
        public Action<WorldObject> OnDataObjectGone;

        // ---- quests --------------------------------------------------------------------------

        public void Process(S.QuestChanged p)
        {
            QuestTransition transition = World.ApplyQuestChanged(p.Quest);
            if (transition != null) OnQuestChanged?.Invoke(transition);
        }

        public void Process(S.QuestCancelled p)
        {
            ClientUserQuest removed = World.RemoveQuest(p.Index);
            if (removed != null) OnQuestCancelled?.Invoke(removed);
        }

        public Action<QuestTransition> OnQuestChanged;
        public Action<ClientUserQuest> OnQuestCancelled;

        /// <summary>Raised when an object leaves our view, so per-object bookkeeping can be cleared.</summary>
        public Action<uint> OnObjectGone;

        /// <summary>True while the server is driving our movement via AutoPathService.</summary>
        public bool AutoPathing;

        public Action<Library.SystemModels.NPCPage> OnNPCPage;
        public Action OnAutoPathMove;

        public void Process(S.Chat p)
        {
            if (string.IsNullOrEmpty(p.Text)) return;

            // The server reports auto-path failures as a system chat line (Language.AutoPathNoRoute),
            // so without this the route silently never starts and the bot just stands there.
            if (p.Type == MessageType.System || p.Type == MessageType.Announcement)
            {
                Log($"[server] {p.Text}");
                OnSystemChat?.Invoke(p.Text);
            }
        }

        /// <summary>A system chat line: the only way the server explains a refused store buy.</summary>
        public Action<string> OnSystemChat;

        public void Process(S.NPCResponse p)
        {
            OnNPCPage?.Invoke(p.Page);
        }

        private List<int> _pendingRepair;
        private bool _pendingSpecial;

        public Action<bool> OnRepairResult;

        private DateTime _repairSentAt = DateTime.MinValue;

        private static readonly TimeSpan RepairReplyWindow = TimeSpan.FromSeconds(6);

        public void Process(S.NPCRepair p)
        {
            // Nothing else reports a completed repair, so apply the server's own formula locally.
            // Without it the bot re-requests the same repair next trip and the server answers
            // RepairFailRepaired, which aborts the WHOLE batch.
            // No repair of ours pending: an Oil of the War God (PlayerObject.SpecialRepair), which
            // reports itself as a special NPC repair of the slots it mended. Record it, but it is
            // not the reply a town trip's repair is waiting for.
            if (_pendingRepair == null)
            {
                if (p.Success && p.Links != null)
                    Items.NoteRepaired(p.Links.Where(x => x.GridType == GridType.Equipment)
                        .Select(x => x.Slot).ToList(), p.Special);
                return;
            }

            if (p.Success) Items.NoteRepaired(_pendingRepair, _pendingSpecial);

            _pendingRepair = null;
            _repairSentAt = DateTime.MinValue;
            OnRepairResult?.Invoke(p.Success);
        }

        /// <summary>
        /// A repair we asked for and never heard about again.
        ///
        /// When the bill is more than the character's gold, PlayerObject.NPCRepair sends a CHAT
        /// line and returns - there is no S.NPCRepair at all, success or failure
        /// (PlayerObject.cs:11730). So the obvious hook, "tell me when a repair fails", is never
        /// called, and a bot waiting to be told will wait forever.
        ///
        /// Silence is therefore the signal, and it is measured rather than parsed: matching on the
        /// message text would break the moment someone plays in another language, and this catches
        /// every silent refusal rather than only the one about gold. That is worth having - this is
        /// the third silent refusal found tonight, after an unaffordable NPCBuy and a repair whose
        /// batch was rejected wholesale.
        /// </summary>
        public void CheckRepairTimeout()
        {
            if (_pendingRepair == null || _repairSentAt == DateTime.MinValue) return;
            if (DateTime.UtcNow - _repairSentAt < RepairReplyWindow) return;

            _pendingRepair = null;
            _repairSentAt = DateTime.MinValue;
            OnRepairResult?.Invoke(false);
        }

        public void Process(S.ItemDurability p) =>
            Items.NoteDurability(p.GridType, p.Slot, p.CurrentDurability);

        /// <summary>An oil's +1 / -1 Luck or Strength on the weapon (added to what it had).</summary>
        public void Process(S.ItemStatsChanged p) =>
            Items.NoteStatsChanged(p.GridType, p.Slot, p.NewStats, replace: false);

        public void Process(S.ItemStatsRefreshed p) =>
            Items.NoteStatsChanged(p.GridType, p.Slot, p.NewStats, replace: true);

        public void Process(S.CombatTime p) => World.ApplyCombat();

        /// <summary>
        /// The server arming or spending an attack skill. Ignoring this - as the bot did until
        /// now - means every Slaying proc is rolled and then wasted, because a plain attack does
        /// not consume it and the server will not re-roll while it is already armed.
        /// </summary>
        public Action<MagicType, bool> OnMagicToggle;

        public void Process(S.MagicToggle p) => OnMagicToggle?.Invoke(p.Magic, p.CanUse);

        /// <summary>Spell index and how long until it may be cast again.</summary>
        public Action<int, int> OnMagicCooldown;

        public void Process(S.MagicCooldown p) => OnMagicCooldown?.Invoke(p.InfoIndex, p.Delay);

        /// <summary>Manual revive from the status page - never automatic.</summary>
        public void Revive() => Enqueue(new C.TownRevive());

        public void Process(S.InformMaxExperience p) => World.ApplyMaxExperience(p.MaxExperience);

        public void Process(S.GainedExperience p) => World.ApplyExperienceGain(p.Amount);

        public void Process(S.LevelChanged p)
        {
            int previousLevel = World.Level;
            if (World.SelfID != 0 && p.Level > previousLevel && previousLevel > 0)
                OnLevelUp?.Invoke(previousLevel, p.Level);
            World.ApplyLevelChanged(p.Level, p.Experience, p.MaxExperience);

            // A level up raises WearWeight, so gear refused as too heavy may now fit.
            Items.ClearEquipRefusals();
        }

        public Action<int, int> OnLevelUp;

        public void Process(S.CurrencyChanged p)
        {
            long before = World.Gold;
            World.ApplyCurrency(p.CurrencyIndex, p.Amount);
            if (_pendingCapital && DateTime.UtcNow - _pendingCapitalAt <= BuyAnswerWindow &&
                before == _pendingCapitalGold && World.Gold < before)
                _pendingCapitalDebit = before - World.Gold;
        }

        public void Process(S.NewMagic p) => World.ApplyMagic(p.Magic);

        public void Process(S.MagicLeveled p) =>
            World.ApplyMagicLevel(p.InfoIndex, p.Level, p.Experience);

        public void Process(S.WeightUpdate p) => World.ApplyWeight(p.BagWeight);

        public void Process(S.StatsUpdate p) => World.ApplyStats(p.Stats);

        public void Process(S.SafeZoneChanged p) => World.ApplySafeZone(p.InSafeZone);

        /// <summary>Who last hit us, and when. See OnDamaged.</summary>
        private uint _lastAttacker;
        private DateTime _lastStruckAt = DateTime.MinValue;

        private static readonly TimeSpan StrikeWindow = TimeSpan.FromMilliseconds(500);

        /// <summary>Monster name and the damage it just did to us.</summary>
        public Action<string, int> OnDamaged;

        /// <summary>
        /// S.ObjectStruck names an attacker but carries no number; S.HealthChanged carries the
        /// number but not the attacker. Neither is enough alone, so the strike is remembered for a
        /// moment and claimed by the health loss that follows it.
        /// </summary>
        public void Process(S.ObjectStruck p)
        {
            if (p.ObjectID != World.SelfID) return;

            _lastAttacker = p.AttackerID;
            _lastStruckAt = DateTime.UtcNow;
        }

        public void Process(S.HealthChanged p)
        {
            World.ApplyHealthDelta(p.ObjectID, p.Change);

            if (p.ObjectID != World.SelfID || p.Change >= 0 || p.Miss) return;
            if (_lastAttacker == 0 || DateTime.UtcNow - _lastStruckAt > StrikeWindow) return;

            WorldObject attacker = World.Find(_lastAttacker);
            _lastAttacker = 0;

            if (attacker == null || attacker.Kind != ObjectKind.Monster) return;

            // Never learn danger from an owned monster. A pet cannot hurt its owner, but another
            // player's can, and either way the damage says nothing about what that monster KIND is
            // worth fighting - which is the only thing MonsterMemory is for. One mislearnt entry
            // makes the bot refuse a whole species for ever.
            if (attacker.IsPet) return;

            OnDamaged?.Invoke(attacker.Name, -p.Change);
        }

        /// <summary>The last monster to hit us, for blaming a death on something.</summary>
        public string LastAttackerName =>
            World.Find(_lastAttacker)?.Name ?? _lastKnownAttackerName;

        private string _lastKnownAttackerName;

        public void NoteAttackerName(string name) => _lastKnownAttackerName = name;

        public void Process(S.ManaChanged p) => World.ApplyManaDelta(p.ObjectID, p.Change);

        public void Process(S.DataObjectHealthMana p) =>
            World.ApplyHealthMana(p.ObjectID, p.Health, p.Mana, p.Dead);

        /// <summary>
        /// A MONSTER reports its maximum through Stats, not through MaxHealth.
        ///
        /// The packet carries three fields and the server fills different ones depending on what
        /// is being described: PlayerObject sets MaxHealth/MaxMana (PlayerObject.cs:2299), while
        /// MonsterObject sets Stats and leaves them at zero (MonsterObject.cs:847). Reading only
        /// MaxHealth therefore recorded every monster - and every PET - as having a maximum of
        /// zero.
        ///
        /// That is why the status page showed a healthy skeleton as "hp=300/0", and much worse,
        /// why a Taoist never once healed its pet: ChoosePetHeal opens with
        /// `if (pet.MaxHealth &lt;= 0) continue`, a guard against dividing by zero that silently
        /// disabled the whole feature because the value it guards was never populated. The spell
        /// was known, the code was called, and the first line threw every candidate away.
        /// </summary>
        public void Process(S.DataObjectMaxHealthMana p)
        {
            int maxHealth = p.MaxHealth > 0 ? p.MaxHealth : p.Stats?[Stat.Health] ?? 0;
            int maxMana = p.MaxMana > 0 ? p.MaxMana : p.Stats?[Stat.Mana] ?? 0;

            World.ApplyMaxHealthMana(p.ObjectID, maxHealth, maxMana);
        }

        #endregion

        #region Inventory

        /// <summary>What we last asked to drop, pending the server's answer.</summary>
        private CellLinkInfo _pendingDrop;
        private DateTime _dropDeadline = DateTime.MinValue;

        /// <summary>
        /// A drop is out and unanswered.
        ///
        /// Without this the brain re-decided "drop that" on the very next tick, before the first
        /// request had been answered - so one item produced a stream of drops, the first succeeded,
        /// and every one after it was refused because the slot was already empty. The deadline
        /// exists because a request that is never answered must not wedge the bot for ever.
        /// </summary>
        public bool DropPending => _pendingDrop != null && DateTime.UtcNow < _dropDeadline;

        /// <summary>Told when the server throws a drop out, so the brain can stop asking.</summary>
        public Action<int> OnDropRefused;

        /// <summary>Told which slot the server refused to buy, when the order named only one.</summary>
        public Action<int> OnSellRefused;

        /// <summary>Told about EVERY refusal, however many slots it named.</summary>
        public Action OnSellOrderRefused;

        /// <summary>
        /// The server's verdict on a drop.
        ///
        /// S.ItemChanged is enqueued before validation and has Success set on the same object
        /// afterwards, the same shape as the sell path. The server refuses a drop outright when
        /// CanDrop is false, the item is Locked or Marriage-bound, or there is no free cell to
        /// drop onto - and a bot standing in a crowded doorway hits that last one routinely.
        /// </summary>
        public void Process(S.ItemChanged p)
        {
            CellLinkInfo pending = _pendingDrop;
            int usedSlot = _pendingUse;
            string usedName = _pendingUseName;

            // THE SLOT COMES FROM THE SERVER, not from what we think we asked about.
            //
            // The handler used to be drop-only: if no drop was pending it returned, throwing away
            // every potion, book and town-scroll verdict the server ever sent. Correlating by a
            // single pending field is also unsafe - a late item-use answer arriving while a drop
            // is outstanding would be read as the drop's verdict - and unnecessary, because
            // S.ItemChanged echoes the GridType and Slot it concerns (PlayerObject.cs:5581).
            int slot = p.Link?.Slot ?? -1;
            bool inventory = p.Link == null || p.Link.GridType == GridType.Inventory;
            bool answersUse = inventory && usedSlot >= 0 && slot == usedSlot;
            bool answersDrop = inventory && pending != null && slot == pending.Slot;

            if (answersUse)
            {
                // The server's item cooldown starts from an ACCEPTED use. See ItemUseCooldown.
                if (p.Success) _lastAcceptedUse = DateTime.UtcNow;

                OnItemUseVerdict?.Invoke(usedSlot, p.Success);

                _pendingUse = -1;
                _pendingUseName = null;
                _pendingUseAt = DateTime.MinValue;

                // Logged both ways. Refused uses were completely invisible before, and they are
                // the event that desynchronised the whole model.
                Log(p.Success
                    ? $"Used {usedName ?? "item"} from slot {usedSlot} - server confirms, " +
                      $"{p.Link?.Count ?? 0} left."
                    : $"Use REFUSED for {usedName ?? "item"} in slot {usedSlot} - the model is " +
                       "left alone, the item is still there.");
            }

            if (answersDrop)
            {
                _pendingDrop = null;
                _dropDeadline = DateTime.MinValue;
            }

            if (!p.Success)
            {
                // NOTHING IS APPLIED. Not for a drop, not for a use.
                if (!answersDrop) return;

                // Said once, and the slot is not asked about again for a good while. The server
                // refuses a drop when there is no free cell to put it on - standing in a doorway
                // or a crowd is enough - and retrying that every tick is a spin, not a strategy.
                Log($"Drop REFUSED for slot {pending.Slot} - keeping it, and not asking again " +
                    "for a while.");

                OnDropRefused?.Invoke(pending.Slot);
                return;
            }

            // Link.Count is what REMAINS in the slot, and zero means the whole stack went
            // (PlayerObject.cs:6486-6496). Applied to the slot the SERVER named.
            long remaining = p.Link?.Count ?? 0;

            if (p.Link != null && slot >= 0 &&
                (p.Link.GridType == GridType.Inventory ||
                 p.Link.GridType == GridType.Equipment))
                Items.NoteSlotCount(p.Link.GridType, slot, remaining);

            if (answersDrop)
                Log(remaining <= 0
                    ? $"Dropped all of slot {pending.Slot}."
                    : $"Dropped part of slot {pending.Slot}, {remaining} left.");
        }

        /// <summary>The slot of an item use awaiting the server's S.ItemChanged verdict.</summary>
        private int _pendingUse = -1;
        private string _pendingUseName;
        private DateTime _pendingUseAt = DateTime.MinValue;

        /// <summary>
        /// A normal use answers immediately; a use queued behind the server's auto-potion clock
        /// answers when UseItemTime opens again. Five seconds covers that delay without allowing a
        /// genuinely lost answer to stop healing for the rest of the connection.
        /// </summary>
        private static readonly TimeSpan ItemUseAnswerWindow = TimeSpan.FromSeconds(5);

        /// <summary>
        /// The server's own gap between accepted item uses (PlayerObject.cs: `SEnvir.Now <
        /// UseItemTime` is a bare return, i.e. a silent refusal).
        ///
        /// Waiting for the previous VERDICT is not the same as waiting for the previous
        /// COOLDOWN, and that difference cost a bot its escape route. Wizzler drank a Mana Potion
        /// at 15:55:21.603, the verdict arrived immediately, so the pending gate opened - and the
        /// town scroll went out 33ms later, inside the server's one-second window. It was refused,
        /// the trip decided "the scroll did not move us", and the map was marked scroll-proof.
        /// Seven refusals in one session landed within a second of an accepted use.
        ///
        /// 1100ms rather than 1000: the clock starts server-side, and a round trip is not free.
        /// </summary>
        private static readonly TimeSpan ItemUseCooldown = TimeSpan.FromMilliseconds(1100);

        private DateTime _lastAcceptedUse = DateTime.MinValue;

        /// <summary>Told (slot, accepted) for every item use the server answers.</summary>
        public Action<int, bool> OnItemUseVerdict;

        public bool ItemUsePending => _pendingUse >= 0;

        /// <summary>True while the server's item cooldown would silently refuse another use.</summary>
        public bool ItemUseOnCooldown =>
            DateTime.UtcNow - _lastAcceptedUse < ItemUseCooldown;

        /// <summary>
        /// Release a use whose verdict was lost. A late successful S.ItemChanged is still applied
        /// by Process even after this correlation record is gone, because the server-supplied slot
        /// and remaining count stay authoritative.
        /// </summary>
        public void CheckPendingItemUse()
        {
            if (_pendingUse < 0) return;
            if (DateTime.UtcNow - _pendingUseAt < ItemUseAnswerWindow) return;

            Log($"Use verdict timed out for {_pendingUseName ?? "item"} in slot {_pendingUse} " +
                $"after {ItemUseAnswerWindow.TotalSeconds:0}s - allowing another use; any late " +
                "server verdict will still be applied.");

            _pendingUse = -1;
            _pendingUseName = null;
            _pendingUseAt = DateTime.MinValue;
        }

        /// <summary>
        /// The only path that sends C.ItemUse. Zircon keeps one delayed manual use while its
        /// auto-potion clock is active; sending another replaces that delayed request and emits a
        /// refusal for the first. Keep exactly one outstanding until its slot-specific verdict.
        /// </summary>
        private bool TryItemUse(int slot)
        {
            if (ItemUsePending) return false;
            if (ItemUseOnCooldown) return false;

            _pendingUse = slot;
            _pendingUseName = Items.InSlot(slot)?.Info?.ItemName;
            _pendingUseAt = DateTime.UtcNow;

            Enqueue(new C.ItemUse
            {
                Link = new CellLinkInfo
                {
                    GridType = GridType.Inventory,
                    Slot = slot,
                    Count = 1
                }
            });

            return true;
        }

        /// <summary>
        /// Sales the server threw out whole, this connection.
        ///
        /// Per-connection rather than persistent, because the thing it detects - the model and the
        /// bag drifting apart - is itself cured by a relog. A non-zero count here means the page
        /// should stop trusting the bag listing until the bot reconnects.
        /// </summary>
        public int SellRefusals;

        /// <summary>What we last asked a shop to take, pending its answer.</summary>
        private List<CellLinkInfo> _pendingSell;

        /// <summary>
        /// The server's verdict on a sale.
        ///
        /// Success carries the links it actually took - ParseLinks merges duplicate slots on the
        /// way in, so these are authoritative in a way the request is not. A false Success means
        /// the order was refused ENTIRELY, not partially: NPCSell returns out of its validation
        /// loop on the first unacceptable link.
        ///
        /// The refusal is worth logging loudly. It is silent on the wire - no chat line, no error -
        /// and when the bot used to assume success instead, the bag and the bot's idea of the bag
        /// drifted apart permanently.
        /// </summary>
        public void Process(S.ItemsChanged p)
        {
            List<CellLinkInfo> pending = _pendingSell;
            _pendingSell = null;

            if (pending == null) return;      // not ours: repairs and other flows echo this too

            if (p.Success)
            {
                Items.NoteSoldConfirmed(p.Links ?? pending);
                return;
            }

            SellRefusals++;

            // NAME EVERY SLOT IN THE ORDER, because the server never will.
            //
            // Six hypotheses have been argued at this refusal from the outside - an unlock race,
            // items dropped on death, an unconfirmed unlock, a negative total price, untyped
            // items, a non-BuySell page - and correlation studies rejected five of them. The one
            // that survived was found by comparing code, not by inference, and the fix for it made
            // things worse. That is a long way to go without ever looking at what was in the bag.
            //
            // Every remaining way the server can refuse is a bare `return` (PlayerObject.cs:9565-
            // 9571): the slot is empty, the count exceeds the stack, CanSell is false, the item is
            // Locked, Marriage-bound or Worthless, or its type is not on the vendor's page. All
            // six are visible in what we THINK we are holding, so printing that turns the next
            // refusal into an answer instead of another theory:
            //
            //   NOTHING IN MODEL       -> our slot map has a phantom
            //   offering N of M        -> our count is ahead of the server's
            //   a flag or odd type     -> our Sellable filter has a hole
            //   everything looks fine  -> the disagreement is about the slot INDEX
            System.Text.StringBuilder detail = new System.Text.StringBuilder();

            foreach (CellLinkInfo link in pending)
            {
                if (detail.Length > 0) detail.Append(", ");

                ClientUserItem held = Items.InSlot(link.Slot);

                if (held?.Info == null)
                {
                    detail.Append($"[{link.Slot}] NOTHING IN MODEL (offered {link.Count})");
                    continue;
                }

                detail.Append($"[{link.Slot}] {held.Info.ItemName}");

                if (link.Count != held.Count) detail.Append($" offering {link.Count} of {held.Count}");
                else detail.Append($" x{held.Count}");

                detail.Append($" {held.Info.ItemType}");

                if (held.Flags != default) detail.Append($" {held.Flags}");
                if (!held.Info.CanSell) detail.Append(" CANNOTSELL");
            }

            Log($"Sell REFUSED contents: {detail}");

            OnSellOrderRefused?.Invoke();

            // ONE slot means we know exactly which item is at fault.
            //
            // The all-or-nothing rule usually hides the culprit, so the honest response to a
            // multi-slot refusal is to keep everything and say so. A single-slot order has no such
            // ambiguity: that item passed every check we can make - CanSell, Locked, Marriage,
            // Worthless, item part, the vendor's accepted types - and the server still refused it.
            // Our model of it is wrong in a way we cannot see, most likely a count or an item that
            // is already gone.
            //
            // Without this the slot is offered again at the next vendor, and the next. A taoist
            // spent thirty-three minutes at 90% bag walking a vendor circuit re-offering the same
            // refused item, never selling, never hunting. Repeating a refused request unchanged is
            // the failure this codebase produces most often; the loot and drop paths already have
            // their own sentences for it, and selling had none.
            if (pending.Count == 1)
            {
                Log($"Sell REFUSED for slot {pending[0].Slot} alone - our model of it must be " +
                    "wrong, so it is not offered again for a while.");

                OnSellRefused?.Invoke(pending[0].Slot);
                return;
            }

            Log($"Sell REFUSED by the server: {pending.Count} slot(s), nothing sold. The order is " +
                "all-or-nothing, so one locked, worthless or already-gone item voids it. Keeping " +
                "them in the model rather than guessing.");
        }

        /// <summary>
        /// The server's verdict on a lock change. The only thing that may clear the flag.
        ///
        /// Sent for every lock request it actually performs, and not sent at all for one it
        /// declines - so its absence is the refusal, and the absence is handled by simply never
        /// having changed anything.
        /// </summary>
        public void Process(S.ItemLock p)
        {
            if (p.Grid != GridType.Inventory) return;

            if (p.Locked) Items.NoteLocked(p.Slot);
            else Items.NoteUnlocked(p.Slot);
        }

        private EquipRequest _pendingEquip;
        private (int From, int To, bool Parts, bool Merge)? _pendingDeposit;
        private (int From, int To, bool Parts)? _pendingWithdraw;
        private (int From, int To)? _pendingPartMerge;

        /// <summary>Told when the server throws an equip or bank move out.</summary>
        public Action<string> OnMoveRefused;
        /// <summary>Raised only after the server confirms a strictly better replacement.</summary>
        public Action<string, string, int> OnUpgradeConfirmed;

        /// <summary>
        /// The server's verdict on an equip, deposit or withdraw. The ONLY thing that may apply one.
        ///
        /// S.ItemMove is enqueued before any validation and has Success set on the same object
        /// afterwards (PlayerObject.cs:6653-6664) - the pattern S.ItemsChanged already relies on -
        /// so the answer always arrives and always means something. The bot ignored it entirely and
        /// applied every move to its own model the moment the request went out.
        ///
        /// That is not a cosmetic drift. A level 22 wizard tried to wear a Flame Robe weighing 12
        /// when its remaining wear allowance was 8 - Wizard L22 has WearWeight 20 and was already
        /// carrying 23 worn - so the server refused. The bot recorded the robe as worn anyway, and
        /// from that moment its equipment and inventory disagreed with the server's: the robe was
        /// in the bag as far as the server was concerned, which shifted every slot after it and
        /// got the next sell order voided wholesale.
        ///
        /// Refusing to guess also fixes the behaviour the operator wanted: an equip that does not
        /// land leaves the item in the bag, where the ordinary storage rules can deal with it,
        /// instead of vanishing into a piece of equipment the character is not wearing.
        /// </summary>
        public void Process(S.ItemMove p)
        {
            EquipRequest equip = _pendingEquip;
            (int From, int To, bool Parts, bool Merge)? deposit = _pendingDeposit;
            (int From, int To, bool Parts)? withdraw = _pendingWithdraw;
            (int From, int To)? partMerge = _pendingPartMerge;

            _pendingEquip = null;
            _pendingDeposit = null;
            _pendingWithdraw = null;
            _pendingPartMerge = null;

            if (!p.Success)
            {
                string what = equip != null ? $"equip {equip.ItemName}"
                    : deposit != null ? $"deposit from slot {deposit.Value.From}"
                    : withdraw != null ? $"withdraw from slot {withdraw.Value.From}"
                    : partMerge != null ? $"merge part slot {partMerge.Value.From} into {partMerge.Value.To}"
                    : $"move {p.FromGrid}:{p.FromSlot} -> {p.ToGrid}:{p.ToSlot}";

                Log($"Move REFUSED by the server: {what}. The model is left alone - whatever we " +
                    "thought we were moving is still where it was.");

                if (equip != null) Items.NoteEquipRefused(equip);

                OnMoveRefused?.Invoke(what);
                return;
            }

            if (equip != null)
            {
                ClientUserItem previous = Items.Worn.FirstOrDefault(x => x.Key == equip.ToSlot).Value;
                ClientUserItem incoming = Items.InSlot(equip.FromSlot);
                int gain = previous != null && incoming != null
                    ? Backpack.Score(incoming, World.Class) - Backpack.Score(previous, World.Class)
                    : 0;
                if (!equip.Merge && gain > 0)
                    OnUpgradeConfirmed?.Invoke(previous.Info.ItemName, incoming.Info.ItemName, gain);
                Items.NoteEquipped(equip);
            }
            else if (deposit != null)
                Items.NoteDeposited(deposit.Value.From, deposit.Value.To, deposit.Value.Parts,
                    deposit.Value.Merge);
            else if (withdraw != null)
                Items.NoteWithdrawn(withdraw.Value.From, withdraw.Value.To, withdraw.Value.Parts);
            else if (partMerge != null)
                Items.NotePartsMerged(partMerge.Value.From, partMerge.Value.To);
        }

        private string _pendingBuy;
        private DateTime _pendingBuyAt;
        private bool _pendingCapital;
        private DateTime _pendingCapitalAt;
        private int _pendingCapitalItemIndex;
        private long _pendingCapitalAmount;
        private long _pendingCapitalGold;
        private long _pendingCapitalDebit;
        public Action<long> OnCapitalBought;

        /// <summary>
        /// How long to wait for a purchase to arrive before calling it refused. The server answers
        /// a successful C.NPCBuy with S.ItemsGained in the same tick, so this is generous.
        /// </summary>
        private static readonly TimeSpan BuyAnswerWindow = TimeSpan.FromSeconds(3);

        /// <summary>Told when a purchase was paid for in decisions and never arrived.</summary>
        public Action<string> OnBuyRefused;

        /// <summary>
        /// Did the last purchase actually turn up?
        ///
        /// C.NPCBuy has no reply of its own: a success produces S.ItemsGained and a refusal
        /// produces NOTHING - no packet, no flag, at most a chat line nothing here consumes. So
        /// the bot logged "NPCBuy (1 x Bloody Flower)" and moved on, and nobody could tell the
        /// difference between a book bought and a book declined.
        ///
        /// An assassin bought that book on every town trip for an entire session. It never reached
        /// the inventory, never reached storage, was never learnt and was never sold - because it
        /// was never sold TO us. The only trace was a decision line describing a request.
        ///
        /// Checked on a timer rather than on the next packet, because the absence is the signal
        /// and an absence has to be waited for.
        /// </summary>
        public void CheckPendingBuy()
        {
            if (_pendingBuy == null) return;
            if (DateTime.UtcNow - _pendingBuyAt < BuyAnswerWindow) return;

            string what = _pendingBuy;
            _pendingBuy = null;
            if (_pendingCapital) Log($"Capital purchase unconfirmed: {what}; no adjustment.");
            _pendingCapital = false;

            Log($"Buy REFUSED by the server: {what} never arrived - no S.ItemsGained followed the " +
                "order. Gold was not spent; nothing was received.");

            OnBuyRefused?.Invoke(what);
        }

        public void Process(S.ItemsGained p)
        {
            if (p.Items == null) return;

            if (_pendingCapital)
            {
                int received = 0;
                bool matched = false;
                foreach (ClientUserItem item in p.Items)
                {
                    received++;
                    matched = item?.Info?.Index == _pendingCapitalItemIndex &&
                              item.Count == _pendingCapitalAmount;
                }
                if (received == 1 && matched && _pendingCapitalDebit > 0 &&
                    DateTime.UtcNow - _pendingCapitalAt <= BuyAnswerWindow)
                    OnCapitalBought?.Invoke(_pendingCapitalDebit);
                else
                    Log("Capital purchase answer did not match item, amount and gold debit; " +
                        "leaving spend in the operating trend.");
                _pendingCapital = false;
            }

            // Whatever arrived, the order was answered.
            _pendingBuy = null;

            foreach (ClientUserItem item in p.Items)
            {
                // Experience, quest items and currency are announced here but never land in the
                // bag. See Backpack.OccupiesASlot.
                if (!Backpack.OccupiesASlot(item))
                {
                    Log($"Gained {item?.Info?.ItemName ?? "something"} x{item?.Count ?? 0} - " +
                        "not a bag item, so it is not modelled as one.");
                    continue;
                }

                Items.Set(item);
                OnItemGained?.Invoke(item);
            }
        }

        #endregion

        /// <summary>
        /// Built once by the host. The previous implementation scanned the whole MonsterInfoList
        /// for every S.ObjectMonster - a linear scan per packet, on the socket callback thread,
        /// multiplied by the number of bots.
        /// </summary>
        public static MonsterIndex Monsters;

        private static MonsterInfo ResolveMonster(int monsterIndex) =>
            Monsters?.Find(monsterIndex);

        #region Acting

        /// <summary>
        /// Turn a decision into packets. One decision produces at most one action packet: the server
        /// gates on ActionTime and answers a second early action with S.UserLocation.
        /// </summary>
        public void Act(Decision decision)
        {
            switch (decision.Action)
            {
                case BotAction.Heal:
                    // NOT applied here - see Process(S.ItemChanged) and Backpack.NoteUsed.
                    TryItemUse(decision.PotionSlot);
                    break;

                case BotAction.Equip:
                    Enqueue(new C.ItemMove
                    {
                        FromGrid = GridType.Inventory,
                        ToGrid = GridType.Equipment,
                        FromSlot = decision.Equip.FromSlot,
                        ToSlot = decision.Equip.ToSlot,

                        // TOPPING UP A STACK IS A MERGE, and the server will refuse it otherwise:
                        // ItemMove returns immediately when the destination is occupied and
                        // MergeItem is false (PlayerObject.cs ItemMove, "if (!p.MergeItem &&
                        // toItem != null) return"). Hardcoding false meant a Taoist carrying 33
                        // Green Poison and 58 Talisman beside 267 and 242 already equipped could
                        // never add them - it bought reagents it was structurally unable to use.
                        MergeItem = decision.Equip.Merge
                    });
                    // Remembered, not applied - see Process(S.ItemMove).
                    _pendingEquip = decision.Equip;
                    _pendingDeposit = null;
                    _pendingWithdraw = null;
                    _pendingPartMerge = null;
                    break;

                case BotAction.Attack:
                    Enqueue(new C.Attack
                    {
                        Direction = decision.Direction,
                        Action = MirAction.Attack,
                        AttackMagic = decision.Magic
                    });
                    break;

                case BotAction.MagicToggle:
                    Enqueue(new C.MagicToggle { Magic = decision.Magic, CanUse = true });
                    break;

                case BotAction.Cast:
                    // The single-target shape, copied from the client's own cast handler
                    // (GameScene.cs): the direction is derived from the target rather than the
                    // mouse, and Location carries the target's cell so the area-cast spells that
                    // read it - the talismans - land where the target is standing.
                    Enqueue(new C.Magic
                    {
                        Direction = decision.Direction,
                        Action = MirAction.Spell,
                        Type = decision.Magic,
                        Target = decision.TargetID,
                        Location = decision.Point
                    });
                    break;

                case BotAction.Unlock:
                    // Asked for, NOT applied. The flag changes when S.ItemLock says it changed.
                    //
                    // This used to clear Locked locally the moment the request went out, which is
                    // the same optimistic-model mistake that NPCSell had, and it fails the same
                    // way - except worse, because nothing ever corrected it.
                    //
                    // The server's ItemLock handler returns SILENTLY, sending no packet at all,
                    // when the slot is out of range or holds nothing (PlayerObject.cs:7466-7471).
                    // So whenever our slot map has drifted, the unlock does nothing, we mark the
                    // item unlocked anyway, and from then on every sell order containing that slot
                    // is voided by the server - all-or-nothing, no error, for ever. Only a relog
                    // fixed it, which is exactly what was observed: a taoist re-offering the same
                    // refused item at vendor after vendor for thirty-three minutes, 59 refusals to
                    // the assassin's zero, because casters carry the most locked consumables.
                    //
                    // Leaving the flag set when the server stays silent is the safe failure: the
                    // item is genuinely still locked, Sellable filters it, and no order is spoiled.
                    Enqueue(new C.ItemLock
                    {
                        GridType = GridType.Inventory,
                        SlotIndex = decision.FromSlot,
                        Locked = false
                    });
                    break;

                case BotAction.Loot:
                    Enqueue(new C.PickUp());
                    break;

                case BotAction.Butcher:
                    Enqueue(new C.Harvest { Direction = decision.Direction });
                    break;

                case BotAction.AutoPath:
                    Enqueue(new C.AutoPathStart { NPCIndex = decision.BuyIndex });
                    AutoPathing = true;
                    break;

                case BotAction.AutoPathPoint:
                    // The coordinate form of the same request (TryAddWaypoint -> TryStart).
                    Enqueue(new C.AutoPathWaypoint
                    {
                        MapIndex = decision.BuyIndex,
                        Location = decision.Point
                    });
                    AutoPathing = true;
                    break;

                case BotAction.AutoPathCancel:
                    Enqueue(new C.AutoPathCancel());
                    AutoPathing = false;
                    break;

                case BotAction.SetPetMode:
                    // One mode for every pet we own - the server has no per-pet command. The echo
                    // (S.ChangePetMode) is what updates our model, so nothing is assumed here.
                    Enqueue(new C.ChangePetMode { Mode = decision.PetMode });
                    break;

                case BotAction.Deposit:
                    // Parts have their own grid on the server; sending one to GridType.Storage is
                    // refused in silence and the item simply stays in the bag.
                    Enqueue(new C.ItemMove
                    {
                        FromGrid = GridType.Inventory,
                        ToGrid = decision.PartsGrid ? GridType.PartsStorage : GridType.Storage,
                        FromSlot = decision.FromSlot,
                        ToSlot = decision.ToSlot,
                        MergeItem = decision.MergeItem
                    });
                    _pendingDeposit = (decision.FromSlot, decision.ToSlot, decision.PartsGrid,
                        decision.MergeItem);
                    _pendingEquip = null;
                    _pendingWithdraw = null;
                    _pendingPartMerge = null;
                    break;

                case BotAction.Withdraw:
                    Enqueue(new C.ItemMove
                    {
                        FromGrid = decision.PartsGrid ? GridType.PartsStorage : GridType.Storage,
                        ToGrid = GridType.Inventory,
                        FromSlot = decision.FromSlot,
                        ToSlot = decision.ToSlot,
                        MergeItem = false
                    });
                    _pendingWithdraw = (decision.FromSlot, decision.ToSlot, decision.PartsGrid);
                    _pendingEquip = null;
                    _pendingDeposit = null;
                    _pendingPartMerge = null;
                    break;

                case BotAction.MergeParts:
                    Enqueue(new C.ItemMove
                    {
                        FromGrid = GridType.PartsStorage,
                        ToGrid = GridType.PartsStorage,
                        FromSlot = decision.FromSlot,
                        ToSlot = decision.ToSlot,
                        MergeItem = true
                    });
                    _pendingPartMerge = (decision.FromSlot, decision.ToSlot);
                    _pendingEquip = null;
                    _pendingDeposit = null;
                    _pendingWithdraw = null;
                    break;

                case BotAction.AssemblePart:
                    TryItemUse(decision.PotionSlot);
                    break;

                case BotAction.NPCRepair:
                    Enqueue(new C.NPCRepair
                    {
                        Links = decision.RepairSlots
                            .Select(slot => new CellLinkInfo
                            {
                                GridType = GridType.Equipment,
                                Slot = slot,
                                Count = 1
                            })
                            .ToList(),
                        Special = decision.Special,
                        GuildFunds = false
                    });
                    _pendingRepair = decision.RepairSlots;
                    _pendingSpecial = decision.Special;
                    _repairSentAt = DateTime.UtcNow;
                    break;

                case BotAction.LearnBook:
                    // Consumed whether the LEARNING roll succeeds or fails - but only if the
                    // server accepted the use at all, which is a different question and the one
                    // this bot kept getting wrong. See Process(S.ItemChanged).
                    TryItemUse(decision.PotionSlot);
                    break;

                case BotAction.Logout:
                    Enqueue(new C.Logout());
                    break;

                case BotAction.NPCCall:
                    Enqueue(new C.NPCCall { ObjectID = decision.TargetID });
                    break;

                case BotAction.QuestAccept:
                    Enqueue(new C.QuestAccept { Index = decision.QuestIndex });
                    break;

                case BotAction.QuestComplete:
                    // None of the configured quests offers a choice; ChoiceIndex is ignored then.
                    Enqueue(new C.QuestComplete { Index = decision.QuestIndex, ChoiceIndex = 0 });
                    break;

                case BotAction.UseItem:
                    // Same path as a potion: TryItemUse owns the pending/cooldown rules.
                    TryItemUse(decision.PotionSlot);
                    break;

                case BotAction.StoreBuy:
                    // The reply packet is empty and sent before validation; StoreShopper settles
                    // the purchase from the Hunt Gold balance instead.
                    Enqueue(new C.MarketPlaceStoreBuy
                    {
                        Index = decision.StoreIndex,
                        Count = 1,
                        UseHuntGold = true
                    });
                    break;

                case BotAction.NPCButton:
                    Enqueue(new C.NPCButton { ButtonID = decision.ButtonID });
                    break;

                case BotAction.DropItem:
                {
                    long count = Items.CountInSlot(decision.PotionSlot);

                    if (count <= 0)
                    {
                        Log($"Drop: slot {decision.PotionSlot} holds nothing we can count.");
                        break;
                    }

                    // Remembered, not applied - the server answers with S.ItemChanged and sets
                    // Success on it. Exactly the mistake NPCSell taught: a refusal that the bot
                    // records as a success leaves it blind to something it is still carrying.
                    _pendingDrop = new CellLinkInfo
                    {
                        GridType = GridType.Inventory,
                        Slot = decision.PotionSlot,
                        Count = count
                    };

                    _dropDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

                    Enqueue(new C.ItemDrop { Link = _pendingDrop, Slot = decision.PotionSlot });
                    break;
                }

                case BotAction.NPCSell:
                {
                    // Skip anything we cannot state a real count for. The server voids the WHOLE
                    // order on a single bad link, so one guessed count costs every other sale in
                    // the same request.
                    // Whole stack unless the brain asked for part of one, and never more than the
                    // slot actually holds - the server refuses the ENTIRE order on a single
                    // link.Count greater than the stack (PlayerObject.cs:9565), silently.
                    List<CellLinkInfo> links = decision.SellSlots
                        .Select(slot =>
                        {
                            long have = Items.CountInSlot(slot);

                            long want = decision.SellCounts != null &&
                                        decision.SellCounts.TryGetValue(slot, out long part)
                                ? part : have;

                            return new CellLinkInfo
                            {
                                GridType = GridType.Inventory,
                                Slot = slot,
                                Count = Math.Max(0, Math.Min(want, have))
                            };
                        })
                        .Where(link => link.Count > 0)
                        .ToList();

                    if (links.Count == 0)
                    {
                        Log($"Sell: nothing to send - none of the {decision.SellSlots.Count} " +
                            "chosen slots holds anything we can count.");
                        break;
                    }

                    // Remembered, not applied. The model changes only when S.ItemsChanged comes
                    // back with Success set - see Process(S.ItemsChanged).
                    _pendingSell = links;

                    Enqueue(new C.NPCSell { Links = links });
                    break;
                }

                case BotAction.NPCBuy:
                    // Remembered so the OUTCOME can be checked - see Process(S.ItemsGained).
                    _pendingBuy = decision.Reason;
                    _pendingBuyAt = DateTime.UtcNow;
                    _pendingCapital = decision.CapitalPurchase;
                    _pendingCapitalAt = _pendingBuyAt;
                    _pendingCapitalItemIndex = decision.BuyItemIndex;
                    _pendingCapitalAmount = decision.BuyAmount;
                    _pendingCapitalGold = World.Gold;
                    _pendingCapitalDebit = 0;

                    Enqueue(new C.NPCBuy
                    {
                        Index = decision.BuyIndex,
                        Amount = decision.BuyAmount,
                        GuildFunds = false
                    });
                    break;

                case BotAction.NPCClose:
                    Enqueue(new C.NPCClose());
                    AutoPathing = false;
                    break;

                case BotAction.TownTeleport:
                    TryItemUse(decision.PotionSlot);
                    break;

                case BotAction.Flee:
                case BotAction.Approach:
                case BotAction.Roam:
                    // Distance 2 is a run; 3 needs a horse (PlayerObject.Move rejects it otherwise).
                    Enqueue(new C.Move { Direction = decision.Direction, Distance = decision.Distance });
                    break;
            }
        }

        #endregion
    }
}
