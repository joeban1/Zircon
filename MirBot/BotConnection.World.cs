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
        }

        public void Process(S.ObjectMonster p)
        {
            MonsterInfo info = ResolveMonster(p.MonsterIndex);

            World.AddMonster(p.ObjectID,
                !string.IsNullOrEmpty(p.CustomName) ? p.CustomName : info?.MonsterName ?? "monster",
                info?.AI ?? 0, p.Location, p.Direction, p.Dead,
                p.PetOwner, p.MonsterIndex, p.Poison);
        }

        public void Process(S.ObjectNPC p)
        {
            World.AddNPC(p.ObjectID, p.CurrentLocation, p.Direction);
        }

        public void Process(S.ObjectItem p)
        {
            World.AddItem(p.ObjectID, p.Item?.Info?.ItemName ?? "item", p.Item?.Info, p.Item, p.Location);
        }

        public void Process(S.DataObjectPlayer p)
        {
            World.AddPlayer(p.ObjectID, p.Name, p.CurrentLocation, MirDirection.Up);
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

        public void Process(S.ObjectRemove p)
        {
            World.ApplyRemove(p.ObjectID);

            // Object ids are recycled. Anything the bot remembers ABOUT an id has to die with the
            // object, or it silently applies to whatever inherits the number next.
            OnObjectGone?.Invoke(p.ObjectID);
        }

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
                Log($"[server] {p.Text}");
        }

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

        public void Process(S.LevelChanged p) =>
            World.ApplyLevelChanged(p.Level, p.Experience, p.MaxExperience);

        public void Process(S.CurrencyChanged p) => World.ApplyCurrency(p.CurrencyIndex, p.Amount);

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

        public void Process(S.DataObjectMaxHealthMana p) =>
            World.ApplyMaxHealthMana(p.ObjectID, p.MaxHealth, p.MaxMana);

        #endregion

        #region Inventory

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

            Log($"Sell REFUSED by the server: {pending.Count} slot(s), nothing sold. The order is " +
                "all-or-nothing, so one locked, worthless or already-gone item voids it. Keeping " +
                "them in the model rather than guessing.");
        }

        public void Process(S.ItemsGained p)
        {
            if (p.Items == null) return;

            foreach (ClientUserItem item in p.Items)
                Items.Set(item);
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
                    Enqueue(new C.ItemUse
                    {
                        Link = new CellLinkInfo
                        {
                            GridType = GridType.Inventory,
                            Slot = decision.PotionSlot,
                            Count = 1
                        }
                    });
                    Items.NoteUsed(decision.PotionSlot);
                    break;

                case BotAction.Equip:
                    Enqueue(new C.ItemMove
                    {
                        FromGrid = GridType.Inventory,
                        ToGrid = GridType.Equipment,
                        FromSlot = decision.Equip.FromSlot,
                        ToSlot = decision.Equip.ToSlot,
                        MergeItem = false
                    });
                    Items.NoteEquipped(decision.Equip);
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
                    Enqueue(new C.ItemLock
                    {
                        GridType = GridType.Inventory,
                        SlotIndex = decision.FromSlot,
                        Locked = false
                    });
                    Items.NoteUnlocked(decision.FromSlot);
                    break;

                case BotAction.Loot:
                    Enqueue(new C.PickUp());
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
                        MergeItem = false
                    });
                    Items.NoteDeposited(decision.FromSlot, decision.ToSlot, decision.PartsGrid);
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
                    Items.NoteWithdrawn(decision.FromSlot, decision.ToSlot);
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
                    Enqueue(new C.ItemUse
                    {
                        Link = new CellLinkInfo
                        {
                            GridType = GridType.Inventory,
                            Slot = decision.PotionSlot,
                            Count = 1
                        }
                    });
                    // Consumed whether the roll succeeds or fails.
                    Items.NoteUsed(decision.PotionSlot);
                    break;

                case BotAction.Logout:
                    Enqueue(new C.Logout());
                    break;

                case BotAction.NPCCall:
                    Enqueue(new C.NPCCall { ObjectID = decision.TargetID });
                    break;

                case BotAction.NPCButton:
                    Enqueue(new C.NPCButton { ButtonID = decision.ButtonID });
                    break;

                case BotAction.NPCSell:
                {
                    // Skip anything we cannot state a real count for. The server voids the WHOLE
                    // order on a single bad link, so one guessed count costs every other sale in
                    // the same request.
                    List<CellLinkInfo> links = decision.SellSlots
                        .Select(slot => new CellLinkInfo
                        {
                            GridType = GridType.Inventory,
                            Slot = slot,
                            Count = Items.CountInSlot(slot)
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
                    Enqueue(new C.ItemUse
                    {
                        Link = new CellLinkInfo
                        {
                            GridType = GridType.Inventory,
                            Slot = decision.PotionSlot,
                            Count = 1
                        }
                    });
                    Items.NoteUsed(decision.PotionSlot);
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
