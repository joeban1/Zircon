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
                info?.AI ?? 0, p.Location, p.Direction, p.Dead);
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
            World.AddMonster(p.ObjectID, p.MonsterInfo?.MonsterName ?? "monster",
                p.MonsterInfo?.AI ?? 0, p.CurrentLocation, MirDirection.Up, p.Dead);
            World.ApplyHealthMana(p.ObjectID, p.Health, 0, p.Dead);
        }

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

        public void Process(S.ObjectRemove p) => World.ApplyRemove(p.ObjectID);

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

        public void Process(S.NPCRepair p)
        {
            // Nothing else reports a completed repair, so apply the server's own formula locally.
            // Without it the bot re-requests the same repair next trip and the server answers
            // RepairFailRepaired, which aborts the WHOLE batch.
            if (p.Success) Items.NoteRepaired(_pendingRepair, _pendingSpecial);

            _pendingRepair = null;
            OnRepairResult?.Invoke(p.Success);
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

                case BotAction.Deposit:
                    Enqueue(new C.ItemMove
                    {
                        FromGrid = GridType.Inventory,
                        ToGrid = GridType.Storage,
                        FromSlot = decision.FromSlot,
                        ToSlot = decision.ToSlot,
                        MergeItem = false
                    });
                    Items.NoteDeposited(decision.FromSlot, decision.ToSlot);
                    break;

                case BotAction.Withdraw:
                    Enqueue(new C.ItemMove
                    {
                        FromGrid = GridType.Storage,
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
                    Enqueue(new C.NPCSell
                    {
                        Links = decision.SellSlots
                            .Select(slot => new CellLinkInfo
                            {
                                GridType = GridType.Inventory,
                                Slot = slot,
                                Count = Items.CountInSlot(slot)
                            })
                            .ToList()
                    });
                    Items.NoteSold(decision.SellSlots);
                    break;

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
