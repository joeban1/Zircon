using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    public enum ObjectKind { Player, Monster, NPC, Item }

    public sealed class WorldObject
    {
        public uint ObjectID;
        public ObjectKind Kind;
        public string Name;
        public Point Location;
        public MirDirection Direction;

        public bool Dead;
        public int Health;
        public int MaxHealth;
        public int Level;

        /// <summary>MonsterInfo.AI. -1 is a town Guard; 1 and 2 are passive harvestable animals.</summary>
        public int AI;

        /// <summary>MonsterInfo.Index, so a summon can be told from any other monster of the same
        /// owner. Ownership alone cannot say WHICH pet this is.</summary>
        public int MonsterIndex = -1;
        public bool IsSummonedPuppet;

        /// <summary>
        /// The owning character's NAME, or null/empty for a wild monster.
        ///
        /// This is the only ownership signal the protocol carries: S.ObjectMonster.PetOwner and
        /// S.ObjectPetOwnerChanged, both plain strings. The reference client compares it against the
        /// player's own name too (Client/Models/MonsterObject.cs:2674). Without it a summoned pet is
        /// an ordinary monster, which means a valid target and a counted threat - to its own owner.
        /// </summary>
        public string PetOwner;

        /// <summary>
        /// The aggregate poison flags on this object, from S.ObjectPoison and the spawn packet.
        ///
        /// Broadcast ONLY when the mask changes (MapObject.cs:405), and it carries presence alone -
        /// no stack value, no remaining ticks, no owner. Enough to know not to re-apply.
        /// </summary>
        public PoisonType Poison;

        /// <summary>For ObjectKind.Item: what is lying there, so loot can be judged before walking to it.</summary>
        public ItemInfo ItemInfo;

        /// <summary>The full instance when the packet carried one - needed to see added stats.</summary>
        public ClientUserItem Item;

        public DateTime LastSeen;

        /// <summary>
        /// How the server is showing us this object. It sends ordinary objects (S.ObjectMonster
        /// etc.) for what is in view AND "data objects" (S.DataObjectMonster etc.) for what is in
        /// view, grouped, or - with a Boss Tracking buff - every boss on the map. Each channel has
        /// its own remove packet, so the object only goes when neither channel still shows it.
        /// </summary>
        public bool SeenNormally;
        public bool SeenAsData;

        public bool IsLiveMonster => Kind == ObjectKind.Monster && !Dead;

        /// <summary>
        /// Guards (AI -1) are monsters in the data model but are town furniture: they cannot be
        /// usefully fought and are often standing somewhere unreachable. Targeting one is how the
        /// first live run wedged itself against a wall for two minutes.
        /// </summary>
        public bool IsGuard => AI == -1;

        /// <summary>Somebody's pet - ours or another player's.</summary>
        public bool IsPet => !string.IsNullOrEmpty(PetOwner);

        /// <summary>
        /// A gathering node or map fixture rather than a monster: AI 4 is Chestnut Tree, Blood
        /// Stone, Dark Stone, Life Stone and Enshrinement Box.
        ///
        /// These are monsters in the data model the same way guards are, and they are just as
        /// wrong to attack. A level 23 wizard found the Chestnut Tree and could not be talked out
        /// of it: trees take no damage from spells, so it threw Cyclone at one from five tiles,
        /// stepped in, threw again, backed off to its preferred range, threw again - twelve mana a
        /// cast, indefinitely, until the mana potions it had finally been routed to a shop for
        /// were gone.
        ///
        /// The no-damage watchdog should have caught it and could not: see NoDamageTo, which was
        /// only ever asked on the MELEE path. Both ends are fixed, but a tree should not be a
        /// target in the first place.
        /// </summary>
        public bool IsSceneryNode => AI == 4;

        /// <summary>
        /// The Arachnid Gazer's Larva (AI 18, MonsterFlag.Larva): 5 health, no experience, kills
        /// itself when it has no target and explodes on everything adjacent when it dies. Hitting
        /// one is paying for the explosion. Not Wedge Moth Larva (AI 44), a real monster and a
        /// quest target.
        /// </summary>
        public bool IsLarva => AI == 18;

        /// <summary>
        /// Attackable at all. Pets are excluded outright: under the ordinary attack mode the server
        /// silently refuses attacks on an owned monster, so swinging at one is a decision that can
        /// never succeed and produces no error to learn from.
        /// </summary>
        /// <summary>
        /// Lesser Wedge Moth (MonsterFlag.LesserWedgeMoth): 3 HP, no experience, no drops, and a
        /// Wedge Moth Larva keeps spawning them until the larva itself dies. Banner spent Carved
        /// Stone Tomb killing the endless stream instead of the larva. Not a target - except when
        /// they have us boxed in (ScriptedBrain.BoxedInBySpawnling).
        /// </summary>
        public bool IsSpawnling;

        public bool IsValidTarget => IsLiveMonster && !IsGuard && !IsPet &&
                                     !IsSceneryNode && !IsSummonedPuppet && !IsLarva && !IsSpawnling;

        public override string ToString() => $"{Kind}:{Name}#{ObjectID}@{Location.X},{Location.Y}" +
                                             (Dead ? " (dead)" : "");
    }

    /// <summary>
    /// Everything the bot believes about the world, rebuilt from packets.
    ///
    /// This holds OBSERVED facts only. Anything derived or guessed belongs somewhere else - when
    /// step 3 adds model-driven decisions, mixing an inference back in here would let a wrong guess
    /// feed itself on the next tick.
    ///
    /// Version increments on every mutation so a decision can record the world it was computed
    /// from and be discarded when that world has moved on.
    /// </summary>
    public sealed class WorldModel
    {
        public uint SelfID;
        public string Name = "";
        public Point Location;
        public MirDirection Direction;
        private int _mapIndex;
        private string _mapName = "";

        /// <summary>
        /// Setting the index resolves the name once. The lookup is a scan of MapInfoList, so it is
        /// done on the map change rather than every time something wants to print where we are.
        /// </summary>
        public int MapIndex
        {
            get => _mapIndex;
            set
            {
                if (_mapIndex == value) return;

                _mapIndex = value;
                _mapName = Globals.MapInfoList?.Binding?
                    .FirstOrDefault(x => x.Index == value)?.Description ?? $"map {value}";
            }
        }

        /// <summary>The current map's description, for logs and the learned memory banks.</summary>
        public string MapName => _mapName;
        public MirClass Class;
        public MirGender Gender;
        public int Level;

        public int Health;
        public int Mana;
        public int MaxHealth;
        public int MaxMana;
        public bool Dead;

        public int BagWeight;
        public int MaxBagWeight;

        /// <summary>Storage is only reachable from a safe zone (PlayerObject.cs:7421).</summary>
        public bool InSafeZone;

        /// <summary>Our own stats, needed to test an item's RequiredType/RequiredAmount.</summary>
        public Stats PlayerStats = new Stats();

        /// <summary>
        /// PK points, which several NPC dialogues gate on (NPCObject.cs:573). Zero for every bot we
        /// run - they never attack players - but read rather than assumed, because the whole Hexa
        /// Holy Stone network hangs off this check and an assumption here would be invisible.
        /// </summary>
        public int PKPoints => PlayerStats[Stat.PKPoint];

        /// <summary>
        /// Our own buffs, keyed by Index because that is what the server identifies them by.
        ///
        /// S.BuffRemove, S.BuffChanged and S.BuffTime all carry an Index and nothing else, so a
        /// dictionary keyed by BuffType cannot service them. Type lookup goes over this.
        ///
        /// Nothing here is ever assumed: S.BuffAdd carries RemainingTime, expiry arrives as an
        /// ordinary S.BuffRemove, and the full list comes down in StartInformation at login. There
        /// is no re-cast timer anywhere in this bot for exactly that reason.
        /// </summary>
        private readonly Dictionary<int, ClientBuffInfo> _buffs = new Dictionary<int, ClientBuffInfo>();

        /// <summary>
        /// When each buff's RemainingTime was last told to us. The server counts it down itself and
        /// only sends S.BuffTime when it changes by other means, so the time left NOW is the
        /// stored value minus what has elapsed - unless the buff is paused (safe zone).
        /// </summary>
        private readonly Dictionary<int, DateTime> _buffSyncedAt = new Dictionary<int, DateTime>();

        /// <summary>Time left on a buff now; null for a permanent one.</summary>
        public TimeSpan? BuffRemaining(ClientBuffInfo buff)
        {
            if (buff == null || buff.RemainingTime == TimeSpan.MaxValue) return null;
            if (buff.Pause || !_buffSyncedAt.TryGetValue(buff.Index, out DateTime at))
                return buff.RemainingTime;
            TimeSpan left = buff.RemainingTime - (DateTime.UtcNow - at);
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }

        /// <summary>An item buff from this item is running (paused counts).</summary>
        public bool HasItemBuff(int itemIndex)
        {
            foreach (ClientBuffInfo buff in _buffs.Values)
                if (buff.Type == BuffType.ItemBuff && buff.ItemIndex == itemIndex) return true;
            return false;
        }

        private void SyncBuff(ClientBuffInfo buff) => _buffSyncedAt[buff.Index] = DateTime.UtcNow;

        public int BuffCount => _buffs.Count;

        public IEnumerable<ClientBuffInfo> Buffs => _buffs.Values;

        public bool HasBuff(BuffType type)
        {
            foreach (ClientBuffInfo buff in _buffs.Values)
                if (buff.Type == type) return true;

            return false;
        }

        public void ResetBuffs(IEnumerable<ClientBuffInfo> buffs)
        {
            _buffs.Clear();
            _buffSyncedAt.Clear();

            if (buffs != null)
                foreach (ClientBuffInfo buff in buffs)
                    if (buff != null)
                    {
                        _buffs[buff.Index] = buff;
                        SyncBuff(buff);
                    }

            Touch();
        }

        public void AddBuff(ClientBuffInfo buff)
        {
            if (buff == null) return;

            _buffs[buff.Index] = buff;
            SyncBuff(buff);
            Touch();
        }

        public void RemoveBuff(int index)
        {
            _buffSyncedAt.Remove(index);
            if (_buffs.Remove(index)) Touch();
        }

        public void ChangeBuff(int index, Stats stats)
        {
            if (!_buffs.TryGetValue(index, out ClientBuffInfo buff)) return;

            buff.Stats = stats;
            Touch();
        }

        public void SetBuffTime(int index, TimeSpan time)
        {
            if (!_buffs.TryGetValue(index, out ClientBuffInfo buff)) return;

            buff.RemainingTime = time;
            SyncBuff(buff);
            Touch();
        }

        public void SetBuffPaused(int index, bool paused)
        {
            if (!_buffs.TryGetValue(index, out ClientBuffInfo buff)) return;

            // Bank the time spent running before the pause flips, so the countdown stays right.
            TimeSpan? left = BuffRemaining(buff);
            if (left.HasValue) buff.RemainingTime = left.Value;
            SyncBuff(buff);
            buff.Pause = paused;
            Touch();
        }

        // ---- quests ---------------------------------------------------------------------------

        /// <summary>
        /// The character's quest log, keyed by the USER quest's index - the one S.QuestCancelled
        /// names - not by the QuestInfo definition. Seeded at login from StartInformation.Quests
        /// and kept current by S.QuestChanged (accept, every counted kill, completion) and
        /// S.QuestCancelled (the daily reset).
        /// </summary>
        private readonly Dictionary<int, ClientUserQuest> _quests = new Dictionary<int, ClientUserQuest>();

        public IEnumerable<ClientUserQuest> Quests => _quests.Values;

        public void ResetQuests(IEnumerable<ClientUserQuest> quests)
        {
            _quests.Clear();
            if (quests != null)
                foreach (ClientUserQuest quest in quests)
                    if (quest?.Quest != null) _quests[quest.Index] = quest;
            Touch();
        }

        /// <summary>Apply S.QuestChanged and say what kind of change it was.</summary>
        public QuestTransition ApplyQuestChanged(ClientUserQuest quest)
        {
            if (quest?.Quest == null) return null;

            _quests.TryGetValue(quest.Index, out ClientUserQuest before);
            _quests[quest.Index] = quest;
            Touch();

            return new QuestTransition
            {
                Quest = quest,
                Accepted = before == null,
                Completed = quest.Completed && (before == null || !before.Completed),
                Progressed = before != null && !before.Completed && !quest.Completed
            };
        }

        public ClientUserQuest RemoveQuest(int userQuestIndex)
        {
            if (!_quests.TryGetValue(userQuestIndex, out ClientUserQuest quest)) return null;
            _quests.Remove(userQuestIndex);
            Touch();
            return quest;
        }

        /// <summary>The log entry for a quest definition, or null.</summary>
        public ClientUserQuest FindQuest(int questInfoIndex)
        {
            foreach (ClientUserQuest quest in _quests.Values)
                if (quest.QuestIndex == questInfoIndex) return quest;
            return null;
        }

        /// <summary>
        /// How our pets are told to behave. ONE setting for all of them - the server has no
        /// per-pet command, only C.ChangePetMode (SConnection.cs:983).
        /// </summary>
        public PetMode PetMode = PetMode.Both;

        public void ApplyPetMode(PetMode mode)
        {
            PetMode = mode;
            Touch();
        }

        public decimal Experience;
        public decimal MaxExperience;

        /// <summary>
        /// StartInformation carries no MaxExperience, so it is 0 for the first seconds of every
        /// session - indistinguishable from "max level" on the value alone. Only treat 0 as max
        /// level once the server has actually told us a maximum.
        /// </summary>
        public bool MaxExperienceKnown;

        /// <summary>Every point of experience gained this session, never reset by a level-up.</summary>
        public decimal TotalExperienceGained;
        /// <summary>Positive XP packets, a useful but imperfect proxy for credited kills.</summary>
        public long PositiveExperienceAwards;
        /// <summary>Signed packet total for real throughput; unlike hunting XP it includes losses.</summary>
        public decimal TotalExperienceNet;

        /// <summary>
        /// When experience last arrived. MinValue until the first gain of the session.
        ///
        /// Read as "productive combat", NOT strictly "last kill" - the server also grants
        /// experience from items and other rewards (PlayerObject.GainExperience has several
        /// callers). For a bot that only fights it is a kill in practice, and it is the cheapest
        /// honest signal that the character is achieving anything at all: a bot can look busy for
        /// an hour, deciding and moving and walking to vendors, while this does not move once.
        /// </summary>
        public DateTime LastExperienceGainUtc = DateTime.MinValue;

        /// <summary>
        /// When the current map was entered. Productivity is map-local: an old drought must not
        /// make the bot abandon a fresh destination before that destination has had its own fair
        /// observation window.
        /// </summary>
        public DateTime MapEnteredUtc = DateTime.MinValue;

        public bool AtMaxLevel => MaxExperienceKnown && MaxExperience == 0;

        public long Gold;
        private int _goldCurrencyIndex = -1;

        /// <summary>Account Hunt Gold: earned while hunting, spent in the game store.</summary>
        public long HuntGold;
        private int _huntGoldCurrencyIndex = -1;

        /// <summary>Fame Points: earned from quests, spent on fame ranks (FameErrand).</summary>
        public long FamePoints;
        private int _fameCurrencyIndex = -1;

        /// <summary>
        /// The character's fame rank as a FameInfo.INDEX (0 = none), from Stats[Stat.Fame], which
        /// RefreshStats sets to Character.Fame. Progression is by FameInfo.Order, not Index.
        /// </summary>
        public int FameIndex => PlayerStats?[Stat.Fame] ?? 0;

        /// <summary>
        /// When the server last told us combat time changed. S.CombatTime is empty - its arrival
        /// IS the information. The server refuses a logout within 10s of this.
        /// </summary>
        public DateTime LastCombat = DateTime.MinValue;

        public bool InCombat => DateTime.UtcNow < LastCombat.AddSeconds(10);

        public void ApplyCombat()
        {
            LastCombat = DateTime.UtcNow;
        }

        public void ApplyExperienceGain(decimal amount)
        {
            Experience += amount;       // a delta, not an absolute
            TotalExperienceNet += amount;

            // Separate running total. Experience itself is overwritten by LevelChanged, so it
            // cannot be differenced across a level-up - and a sampling window that happens to
            // contain one is exactly the window worth measuring.
            if (amount > 0)
            {
                TotalExperienceGained += amount;
                PositiveExperienceAwards++;
                LastExperienceGainUtc = DateTime.UtcNow;
            }

            Touch();
        }

        public void ApplyMaxExperience(decimal max)
        {
            MaxExperience = max;
            MaxExperienceKnown = true;
            Touch();
        }

        public void ApplyLevelChanged(int level, decimal experience, decimal max)
        {
            Level = level;
            Experience = experience;
            MaxExperience = max;
            MaxExperienceKnown = true;
            Touch();
        }

        /// <summary>Resolve which currency is gold once; do not assume it is the first.</summary>
        public void ApplyCurrencies(IEnumerable<ClientUserCurrency> currencies)
        {
            if (currencies == null) return;

            foreach (ClientUserCurrency currency in currencies)
            {
                if (currency?.Info == null) continue;

                if (currency.Info.Type == CurrencyType.Gold && _goldCurrencyIndex < 0)
                {
                    _goldCurrencyIndex = currency.Info.Index;
                    Gold = currency.Amount;
                    Touch();
                }
                else if (currency.Info.Type == CurrencyType.HuntGold && _huntGoldCurrencyIndex < 0)
                {
                    _huntGoldCurrencyIndex = currency.Info.Index;
                    HuntGold = currency.Amount;
                    Touch();
                }
                else if (currency.Info.Type == CurrencyType.FP && _fameCurrencyIndex < 0)
                {
                    _fameCurrencyIndex = currency.Info.Index;
                    FamePoints = currency.Amount;
                    Touch();
                }
            }
        }

        public void ApplyCurrency(int currencyIndex, long amount)
        {
            if (currencyIndex == _fameCurrencyIndex)
            {
                FamePoints = amount;
                Touch();
                return;
            }

            if (currencyIndex == _huntGoldCurrencyIndex)
            {
                HuntGold = amount;      // absolute, not a delta
                Touch();
                return;
            }

            if (currencyIndex != _goldCurrencyIndex) return;

            Gold = amount;              // absolute, not a delta
            Touch();
        }

        /// <summary>Learned skills, keyed by MagicInfo.Index.</summary>
        private readonly Dictionary<int, ClientUserMagic> _magics = new Dictionary<int, ClientUserMagic>();

        public int KnownMagicCount => _magics.Count;

        public bool Knows(int magicInfoIndex) => _magics.ContainsKey(magicInfoIndex);

        /// <summary>Book pages banked towards the next level (S.MagicLeveled Experience).</summary>
        public long MagicExperience(int magicInfoIndex) =>
            _magics.TryGetValue(magicInfoIndex, out ClientUserMagic known) ? known.Experience : 0;

        /// <summary>
        /// A known skill a DROPPED copy of its book would train: the server takes a further book
        /// only at skill level 3 and below Globals.MagicMaxLevel (4). See MagicBooks.Judge.
        /// </summary>
        public bool Trainable(int magicInfoIndex) =>
            _magics.TryGetValue(magicInfoIndex, out ClientUserMagic known) &&
            known.Level >= 3 && known.Level < Globals.MagicMaxLevel;

        /// <summary>Every learned skill. Read-only use on the bot thread only.</summary>
        public IEnumerable<ClientUserMagic> Magics => _magics.Values;

        /// <summary>
        /// A learned skill by its MagicType. The dictionary is keyed by MagicInfo.Index, so callers
        /// wanting a named spell would otherwise each write their own scan.
        /// </summary>
        public bool TryGetMagic(MagicType magic, out ClientUserMagic found)
        {
            foreach (ClientUserMagic known in _magics.Values)
            {
                if (known.Info == null || known.Info.Magic != magic) continue;

                found = known;
                return true;
            }

            found = null;
            return false;
        }

        /// <summary>
        /// Do we know this skill and is our level high enough to use it?
        ///
        /// NeedLevel1 is the level at which the skill becomes castable, which is a different gate
        /// from the level requirement on the book that teaches it - see WhyNotBook in TownTrip.
        /// </summary>
        public bool CanUseMagic(MagicType magic)
        {
            if (magic == MagicType.None) return false;

            foreach (ClientUserMagic known in _magics.Values)
            {
                if (known.Info == null || known.Info.Magic != magic) continue;

                return Level >= known.Info.NeedLevel1;
            }

            return false;
        }

        /// <summary>Is any object standing on this cell, other than the one named?</summary>
        /// <summary>
        /// Cells held by something that will NEVER move: scenery - trees, stones, boxes.
        ///
        /// The pathfinder works from the map's walkability, which describes terrain and knows
        /// nothing about objects standing on it. A monster on a walkable cell is a temporary
        /// obstruction and routing through it is right, because it will wander off. Scenery will
        /// not. Lost Paradise town has a tree between two vendors and the bots walked into it,
        /// were refused, re-planned the identical route and walked into it again, back and forth,
        /// for as long as the trip lasted.
        ///
        /// NPCs count too: they never move either. Lost Paradise's Companion Manager stands in the
        /// one-cell alley on the short way from Hardy to Melisa. Seen only as a creature, it was
        /// avoided after a refused move and forgotten after the next good one, so every bot swung
        /// between the alley and the long way round until the town trip gave up.
        /// </summary>
        public HashSet<Point> SceneryCells()
        {
            HashSet<Point> cells = new HashSet<Point>();

            foreach (WorldObject ob in _objects.Values)
                if (ob.Kind == ObjectKind.Monster && ob.IsSceneryNode && !ob.Dead ||
                    ob.Kind == ObjectKind.NPC)
                    cells.Add(ob.Location);

            return cells;
        }

        /// <summary>
        /// Is something that CAN move standing here?
        ///
        /// The question LearnRefusal actually needs. Asking "is anything here" answered yes for a
        /// tree and so refused to learn the cell, on the reasoning that the obstruction was
        /// temporary - which is exactly backwards for the one kind of obstruction that is not.
        /// </summary>
        public bool MovableThingAt(Point location, uint except)
        {
            foreach (WorldObject ob in _objects.Values)
            {
                if (ob.ObjectID == except || ob.ObjectID == SelfID) continue;
                if (ob.Kind == ObjectKind.Item) continue;
                if (ob.Location != location) continue;
                if (ob.Kind == ObjectKind.Monster && ob.IsSceneryNode) continue;

                return true;
            }

            return false;
        }

        public bool SomethingAt(Point location, uint except)
        {
            foreach (WorldObject ob in _objects.Values)
            {
                if (ob.ObjectID == except || ob.ObjectID == SelfID) continue;
                if (ob.Kind == ObjectKind.Item) continue;
                if (ob.Location != location) continue;

                return true;
            }

            return false;
        }

        /// <summary>Skill level, or -1 when the skill is not known.</summary>
        public int MagicLevel(int magicInfoIndex) =>
            _magics.TryGetValue(magicInfoIndex, out ClientUserMagic magic) ? magic.Level : -1;

        public void ApplyMagics(IEnumerable<ClientUserMagic> magics)
        {
            _magics.Clear();

            if (magics == null) return;

            foreach (ClientUserMagic magic in magics) ApplyMagic(magic);
        }

        /// <summary>
        /// Upsert. S.NewMagic also arrives when the server merely clears ItemRequired on a
        /// skill that is already known, so this must not assume the magic is new.
        /// </summary>
        public void ApplyMagic(ClientUserMagic magic)
        {
            if (magic == null) return;

            _magics[magic.InfoIndex] = magic;
            Touch();
        }

        public void ApplyMagicLevel(int magicInfoIndex, int level, long experience)
        {
            if (!_magics.TryGetValue(magicInfoIndex, out ClientUserMagic magic)) return;

            magic.Level = level;
            magic.Experience = experience;
            Touch();
        }

        /// <summary>0-100. Returns 0 while the maximum is unknown, so weight gates stay open.</summary>
        public int WeightPercent => MaxBagWeight > 0 ? BagWeight * 100 / MaxBagWeight : 0;

        public long Version { get; private set; }

        private readonly Dictionary<uint, WorldObject> _objects = new Dictionary<uint, WorldObject>();

        public int HealthPercent => MaxHealth > 0 ? Health * 100 / MaxHealth : 100;
        public int ManaPercent => MaxMana > 0 ? Mana * 100 / MaxMana : 100;

        public IEnumerable<WorldObject> Objects => _objects.Values;

        /// <summary>
        /// Cells currently held by something solid, for pathing around them.
        ///
        /// Returns a fresh set rather than exposing the object dictionary: a caller holding
        /// that would break the moment the next packet added an object.
        /// </summary>
        public HashSet<Point> OccupiedCells(uint except)
        {
            HashSet<Point> cells = new HashSet<Point>();

            foreach (WorldObject ob in _objects.Values)
            {
                if (ob.ObjectID == except || ob.ObjectID == SelfID) continue;

                // Items lie on the floor and are walked over, not around.
                //
                // Pets are deliberately NOT excluded here. They are not targets and not threats,
                // but they are bodies: a summon standing in a doorway blocks it exactly as any
                // other monster does, and pathing that pretended otherwise would walk into it.
                if (ob.Kind == ObjectKind.Item) continue;
                if (ob.Kind == ObjectKind.Monster && ob.Dead) continue;

                cells.Add(ob.Location);
            }

            return cells;
        }
        public int ObjectCount => _objects.Count;

        private void Touch() => Version++;

        /// <summary>Chebyshev distance - movement and melee range are 8-directional.</summary>
        public static int Distance(Point a, Point b) =>
            Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        public int DistanceTo(Point p) => Distance(Location, p);

        public static MirDirection DirectionTo(Point from, Point to)
        {
            int dx = Math.Sign(to.X - from.X);
            int dy = Math.Sign(to.Y - from.Y);

            if (dx == 0 && dy < 0) return MirDirection.Up;
            if (dx > 0 && dy < 0) return MirDirection.UpRight;
            if (dx > 0 && dy == 0) return MirDirection.Right;
            if (dx > 0 && dy > 0) return MirDirection.DownRight;
            if (dx == 0 && dy > 0) return MirDirection.Down;
            if (dx < 0 && dy > 0) return MirDirection.DownLeft;
            if (dx < 0 && dy == 0) return MirDirection.Left;
            if (dx < 0 && dy < 0) return MirDirection.UpLeft;

            return MirDirection.Up;
        }

        public static Point Step(Point from, MirDirection direction)
        {
            switch (direction)
            {
                case MirDirection.Up: return new Point(from.X, from.Y - 1);
                case MirDirection.UpRight: return new Point(from.X + 1, from.Y - 1);
                case MirDirection.Right: return new Point(from.X + 1, from.Y);
                case MirDirection.DownRight: return new Point(from.X + 1, from.Y + 1);
                case MirDirection.Down: return new Point(from.X, from.Y + 1);
                case MirDirection.DownLeft: return new Point(from.X - 1, from.Y + 1);
                case MirDirection.Left: return new Point(from.X - 1, from.Y);
                case MirDirection.UpLeft: return new Point(from.X - 1, from.Y - 1);
                default: return from;
            }
        }

        public static MirDirection Opposite(MirDirection direction) =>
            (MirDirection)(((int)direction + 4) % 8);

        #region Queries

        /// <summary>The object with this id, or null.</summary>
        public WorldObject Find(uint objectID) =>
            _objects.TryGetValue(objectID, out WorldObject ob) ? ob : null;

        /// <summary>
        /// The nearest live monster that would actually fight back, ignoring the passive animals.
        ///
        /// Used by the fight-through rule: "something is in contact" should mean something
        /// dangerous, not a pig standing in the corridor.
        /// </summary>
        public WorldObject NearestThreat(int maxDistance, ICollection<int> harmlessAI,
            ICollection<uint> exclude = null)
        {
            WorldObject best = null;
            int bestDistance = int.MaxValue;

            foreach (WorldObject ob in _objects.Values)
            {
                if (!ob.IsValidTarget) continue;
                if (exclude != null && exclude.Contains(ob.ObjectID)) continue;
                if (harmlessAI != null && harmlessAI.Contains(ob.AI)) continue;

                int distance = DistanceTo(ob.Location);

                if (distance > maxDistance || distance >= bestDistance) continue;

                best = ob;
                bestDistance = distance;
            }

            return best;
        }

        public WorldObject NearestLiveMonster(int maxDistance, ICollection<uint> exclude = null)
        {
            WorldObject best = null;
            int bestDistance = int.MaxValue;

            foreach (WorldObject ob in _objects.Values)
            {
                if (!ob.IsValidTarget) continue;

                if (exclude != null && exclude.Contains(ob.ObjectID)) continue;

                int distance = DistanceTo(ob.Location);
                if (distance > maxDistance || distance >= bestDistance) continue;

                best = ob;
                bestDistance = distance;
            }

            return best;
        }

        public int LiveMonstersWithin(int maxDistance) =>
            _objects.Values.Count(x => x.IsValidTarget && DistanceTo(x.Location) <= maxDistance);

        #endregion

        #region Packet application

        public void ApplyStart(StartInformation start)
        {
            SelfID = start.ObjectID;
            Name = start.Name;
            Location = start.Location;
            Direction = start.Direction;
            MapIndex = start.MapIndex;
            MapEnteredUtc = DateTime.UtcNow;
            Class = start.Class;
            Gender = start.Gender;
            Level = start.Level;
            InSafeZone = start.InSafeZone;
            SelfPoison = start.Poison;
            if (start.InSafeZone) BindMapIndex = MapIndex;
            ApplyMagics(start.Magics);

            // Authoritative seeds for the two things the server owns and we must never guess:
            // the buffs already running, and how our pets are currently told to behave.
            ResetBuffs(start.Buffs);
            ResetQuests(start.Quests);
            PetMode = start.PetMode;

            Experience = start.Experience;
            MaxExperience = 0;
            MaxExperienceKnown = false;
            Gold = 0;
            _goldCurrencyIndex = -1;
            HuntGold = 0;
            _huntGoldCurrencyIndex = -1;
            FamePoints = 0;
            _fameCurrencyIndex = -1;
            LastCombat = DateTime.MinValue;
            ApplyCurrencies(start.Currencies);
            Health = start.CurrentHP;
            Mana = start.CurrentMP;
            Dead = false;

            // MaxHealth is not in StartInformation - it arrives via DataObjectMaxHealthMana.
            // Until then HealthPercent reports 100 rather than dividing by zero.
            _objects.Clear();
            Touch();
        }

        public void ApplyMapChanged(int mapIndex)
        {
            if (MapIndex != mapIndex) MapEnteredUtc = DateTime.UtcNow;
            MapIndex = mapIndex;

            // Objects belong to the map we left.
            _objects.Clear();
            Touch();
        }

        /// <summary>The server's authoritative correction. Always wins over dead reckoning.</summary>
        public void ApplyUserLocation(Point location, MirDirection direction)
        {
            Location = location;
            Direction = direction;
            Touch();
        }

        private WorldObject GetOrAdd(uint objectID, ObjectKind kind)
        {
            if (!_objects.TryGetValue(objectID, out WorldObject ob))
                _objects[objectID] = ob = new WorldObject { ObjectID = objectID, Kind = kind };

            ob.Kind = kind;
            ob.LastSeen = DateTime.Now;
            return ob;
        }

        public void AddMonster(uint objectID, string name, int ai, Point location, MirDirection direction,
            bool dead, string petOwner = null, int monsterIndex = -1, PoisonType? poison = null)
        {
            if (objectID == SelfID) return;

            WorldObject ob = GetOrAdd(objectID, ObjectKind.Monster);
            ob.Name = name;
            ob.AI = ai;
            ob.Location = location;
            ob.Direction = direction;
            ob.Dead = dead;
            ob.PetOwner = petOwner;
            // Zircon's Puppet sometimes arrives without PetOwner even though it belongs to an
            // assassin. It is a five-second decoy and the server refuses its owner's attack;
            // do not let a newly summoned puppet displace the hostile we were fighting.
            MonsterInfo known = monsterIndex >= 0
                ? Globals.MonsterInfoList?.Binding?.FirstOrDefault(info => info.Index == monsterIndex)
                : null;

            ob.IsSummonedPuppet = string.Equals(name, "SummonPuppet",
                StringComparison.OrdinalIgnoreCase) ||
                known?.Flag == MonsterFlag.SummonPuppet;

            if (known != null || !string.IsNullOrEmpty(name))
                ob.IsSpawnling = known?.Flag == MonsterFlag.LesserWedgeMoth ||
                                 string.Equals(name, "Lesser Wedge Moth", StringComparison.OrdinalIgnoreCase);

            // Null means "this packet does not carry poison" - S.DataObjectMonster has no such
            // field. Writing None for it would erase a state the server has already told us about
            // and only tells us about again when it CHANGES.
            if (poison.HasValue) ob.Poison = poison.Value;

            if (monsterIndex >= 0) ob.MonsterIndex = monsterIndex;

            Touch();
        }

        /// <summary>A monster was tamed, released, or its summon expired.</summary>
        public void ApplyPetOwner(uint objectID, string petOwner)
        {
            if (!_objects.TryGetValue(objectID, out WorldObject ob)) return;

            ob.PetOwner = petOwner;
            Touch();
        }

        /// <summary>
        /// Poison on US. The player is never in _objects, so S.ObjectPoison for SelfID used to be
        /// dropped - and Neutralize doubles the server's swing delay (PlayerObject.Attack).
        /// </summary>
        public PoisonType SelfPoison;

        public void ApplyPoison(uint objectID, PoisonType poison)
        {
            if (objectID != 0 && objectID == SelfID)
            {
                SelfPoison = poison;
                Touch();
                return;
            }

            if (!_objects.TryGetValue(objectID, out WorldObject ob)) return;

            ob.Poison = poison;
            Touch();
        }

        /// <summary>Our own pets, by the only signal the protocol gives: the owner's name.</summary>
        public IEnumerable<WorldObject> OwnPets
        {
            get
            {
                foreach (WorldObject ob in _objects.Values)
                    if (ob.Kind == ObjectKind.Monster && !ob.Dead &&
                        !string.IsNullOrEmpty(ob.PetOwner) &&
                        string.Equals(ob.PetOwner, Name, StringComparison.Ordinal))
                        yield return ob;
            }
        }

        public bool IsOwnPet(WorldObject ob) =>
            ob != null && !string.IsNullOrEmpty(ob.PetOwner) &&
            string.Equals(ob.PetOwner, Name, StringComparison.Ordinal);

        public void AddPlayer(uint objectID, string name, Point location, MirDirection direction)
        {
            if (objectID == SelfID) return;

            WorldObject ob = GetOrAdd(objectID, ObjectKind.Player);
            ob.Name = name;
            ob.Location = location;
            ob.Direction = direction;
            Touch();
        }

        public void AddNPC(uint objectID, Point location, MirDirection direction)
        {
            WorldObject ob = GetOrAdd(objectID, ObjectKind.NPC);
            ob.Location = location;
            ob.Direction = direction;
            Touch();
        }

        public void AddItem(uint objectID, string name, ItemInfo info, ClientUserItem instance, Point location)
        {
            WorldObject ob = GetOrAdd(objectID, ObjectKind.Item);
            ob.Name = name;
            ob.ItemInfo = info;
            if (instance != null) ob.Item = instance;
            ob.Location = location;
            Touch();
        }

        public void ApplyWeight(int bagWeight)
        {
            BagWeight = bagWeight;
            Touch();
        }

        public void ApplySafeZone(bool inSafeZone)
        {
            InSafeZone = inSafeZone;

            // The server re-binds the character to whatever safe zone it is standing in
            // (PlayerObject.cs:1454, UpdateBindPoint(CurrentCell.SafeZone)), and a town scroll goes
            // to that bind point - NOT to a town of the bot's choosing. So walking through Sabuk
            // Keep's safe zone on the way somewhere re-binds there, and the next scroll returns the
            // bot to Sabuk Keep, which has a safe zone and no vendors at all. One wasted scroll and
            // an abandoned trip. Watching the flag is enough to know where a scroll would land.
            if (inSafeZone) BindMapIndex = MapIndex;

            Touch();
        }

        /// <summary>Where a town scroll would put us, learned by watching safe zones. -1 until
        /// the character has stood in one since logging in.</summary>
        public int BindMapIndex = -1;

        public void ApplyStats(Stats stats)
        {
            if (stats == null) return;

            PlayerStats = stats;
            PlayerStatsKnown = true;
            ApplyMaxWeight(stats[Stat.BagWeight]);
        }

        /// <summary>A S.StatsUpdate has arrived. PlayerStats starts as an empty Stats, never null.</summary>
        public bool PlayerStatsKnown;

        /// <summary>
        /// The server's swing gate (PlayerObject.Attack): 1500 - 47 x AttackSpeed ms, never below
        /// 800, doubled while over the bag limit or under Neutralize. AttackSpeed is the server's
        /// full figure, including the +min(3, Level/15) every character gets - the bot used a flat
        /// 1500 and lost a swing in nine at level 45.
        /// </summary>
        public TimeSpan SwingDelay()
        {
            int delay = Globals.AttackDelay;

            if (PlayerStatsKnown)
                delay = Math.Max(800, Globals.AttackDelay - PlayerStats[Stat.AttackSpeed] * Globals.ASpeedRate);

            if (MaxBagWeight > 0 && BagWeight > MaxBagWeight ||
                (SelfPoison & PoisonType.Neutralize) == PoisonType.Neutralize)
                delay *= 2;

            return TimeSpan.FromMilliseconds(delay);
        }

        public void ApplyMaxWeight(int maxBagWeight)
        {
            if (maxBagWeight <= 0) return;

            MaxBagWeight = maxBagWeight;
            Touch();
        }

        public void ApplyLocation(uint objectID, Point location)
        {
            if (objectID == SelfID)
            {
                Location = location;
                Touch();
                return;
            }

            if (_objects.TryGetValue(objectID, out WorldObject ob))
            {
                ob.Location = location;
                ob.LastSeen = DateTime.Now;
                Touch();
            }
        }

        public void ApplyMove(uint objectID, Point location, MirDirection direction)
        {
            if (objectID == SelfID)
            {
                Location = location;
                Direction = direction;
                Touch();
                return;
            }

            if (_objects.TryGetValue(objectID, out WorldObject ob))
            {
                ob.Location = location;
                ob.Direction = direction;
                ob.LastSeen = DateTime.Now;
                Touch();
            }
        }

        public void ApplyTurn(uint objectID, MirDirection direction, Point location)
        {
            if (objectID == SelfID)
            {
                Direction = direction;
                Location = location;
                Touch();
                return;
            }

            if (_objects.TryGetValue(objectID, out WorldObject ob))
            {
                ob.Direction = direction;
                ob.Location = location;
                ob.LastSeen = DateTime.Now;
                Touch();
            }
        }

        public void ApplyHealthMana(uint objectID, int health, int mana, bool dead)
        {
            if (objectID == SelfID)
            {
                Health = health;
                Mana = mana;
                Dead = dead;
                Touch();
                return;
            }

            if (_objects.TryGetValue(objectID, out WorldObject ob))
            {
                ob.Health = health;
                ob.Dead = dead;
                Touch();
            }
        }

        public void ApplyMaxHealthMana(uint objectID, int maxHealth, int maxMana)
        {
            if (objectID == SelfID)
            {
                MaxHealth = maxHealth;
                MaxMana = maxMana;
                Touch();
                return;
            }

            if (_objects.TryGetValue(objectID, out WorldObject ob))
            {
                ob.MaxHealth = maxHealth;
                Touch();
            }
        }

        /// <summary>
        /// Incremental damage/heal. This is the packet that actually tracks HP moment to moment -
        /// DataObjectHealthMana arrives far less often. Dropping these leaves the bot's idea of its
        /// own health minutes stale, which silently disables every HP threshold it has.
        /// </summary>
        public void ApplyHealthDelta(uint objectID, int change)
        {
            if (objectID == SelfID)
            {
                Health = Math.Max(0, MaxHealth > 0 ? Math.Min(MaxHealth, Health + change) : Health + change);
                Touch();
                return;
            }

            if (_objects.TryGetValue(objectID, out WorldObject ob))
            {
                ob.Health += change;
                Touch();
            }
        }

        public void ApplyManaDelta(uint objectID, int change)
        {
            if (objectID != SelfID) return;

            Mana = Math.Max(0, MaxMana > 0 ? Math.Min(MaxMana, Mana + change) : Mana + change);
            Touch();
        }

        public WorldObject NearestItem(int maxDistance, ICollection<uint> exclude = null)
        {
            WorldObject best = null;
            int bestDistance = int.MaxValue;

            foreach (WorldObject ob in _objects.Values)
            {
                if (ob.Kind != ObjectKind.Item) continue;
                if (exclude != null && exclude.Contains(ob.ObjectID)) continue;

                int distance = DistanceTo(ob.Location);
                if (distance > maxDistance || distance >= bestDistance) continue;

                best = ob;
                bestDistance = distance;
            }

            return best;
        }

        /// <summary>S.ObjectRemove. True when the object is now gone entirely.</summary>
        public bool ApplyRemove(uint objectID)
        {
            if (!_objects.TryGetValue(objectID, out WorldObject ob)) return true;

            // Still shown through the data channel (grouped, or a tracked boss): keep it.
            if (ob.SeenAsData)
            {
                ob.SeenNormally = false;
                return false;
            }

            _objects.Remove(objectID);
            Touch();
            return true;
        }

        /// <summary>
        /// S.DataObjectRemove. Returns the object when this removal made it disappear entirely,
        /// so a tracked boss's last position can be kept; null otherwise.
        /// </summary>
        public WorldObject ApplyDataRemove(uint objectID)
        {
            if (!_objects.TryGetValue(objectID, out WorldObject ob)) return null;

            if (ob.SeenNormally)
            {
                ob.SeenAsData = false;
                return null;
            }

            _objects.Remove(objectID);
            Touch();
            return ob;
        }

        /// <summary>Record which channel just showed us an object.</summary>
        public void MarkSeen(uint objectID, bool asData)
        {
            if (!_objects.TryGetValue(objectID, out WorldObject ob)) return;
            if (asData) ob.SeenAsData = true;
            else ob.SeenNormally = true;
        }

        public void MarkDead(uint objectID, bool dead)
        {
            if (objectID == SelfID)
            {
                Dead = dead;
                Touch();
                return;
            }

            if (_objects.TryGetValue(objectID, out WorldObject ob))
            {
                ob.Dead = dead;
                Touch();
            }
        }

        #endregion

        public string Describe()
        {
            int monsters = _objects.Values.Count(x => x.IsValidTarget);
            int players = _objects.Values.Count(x => x.Kind == ObjectKind.Player);
            int pets = _objects.Values.Count(x => x.IsLiveMonster && x.IsPet);

            return $"{Name} L{Level} {Class} @ {Location.X},{Location.Y} map {MapIndex} | " +
                   $"HP {Health}/{MaxHealth} ({HealthPercent}%) MP {Mana}/{MaxMana} | " +
                   $"bag {BagWeight}/{MaxBagWeight} ({WeightPercent}%) | " +
                   $"{monsters} live monsters, {players} players, " +
                   (pets > 0 ? $"{pets} pets, " : "") + $"{_objects.Count} objects | " +
                   $"{KnownMagicCount} magics";
        }
    }

    /// <summary>What an S.QuestChanged meant for one quest.</summary>
    public sealed class QuestTransition
    {
        public ClientUserQuest Quest;

        /// <summary>The quest was not in our log before: an accept succeeded.</summary>
        public bool Accepted;

        /// <summary>Completed went false to true: a hand-in succeeded.</summary>
        public bool Completed;

        /// <summary>An in-progress quest changed (a counted kill).</summary>
        public bool Progressed;
    }
}
