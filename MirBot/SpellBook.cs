using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Library;

namespace MirBot
{
    /// <summary>
    /// Casting learned spells at things.
    ///
    /// Until now the bot could BUY a skill book, LEARN it, and then never use it: a wizard would
    /// walk to town, spend its gold on Fire Ball, read it, and go back to punching chickens. Attack
    /// skills were handled - they ride along on an ordinary swing through C.Attack.AttackMagic, and
    /// SkillSet covers that - but a cast is a different packet and a different set of rules.
    ///
    /// The shape of the decision is taken from the Mir 2 agents' GetBestMagic: work out what each
    /// known spell would cost at its current level, discard what is unaffordable, and prefer the
    /// one with the highest level requirement, on the assumption that a spell gated behind a higher
    /// level is the better spell. The rules it is checked against are Zircon's own, read from the
    /// server:
    ///
    /// - MANA. UserMagic.Cost is BaseCost + Level * LevelCost / 3, and PlayerObject.Magic refuses
    ///   the cast outright when it exceeds current mana (MagicObject.CheckCost).
    /// - RANGE. Globals.MagicRange is 10. Beyond it the server drops the target and the spell is
    ///   spent on empty ground.
    /// - PACE. Globals.MagicDelay is 2000ms between casts, against 1500ms between swings, so a
    ///   cast has to be worth roughly a swing and a third to be the right move at all.
    /// - COOLDOWN. Per spell, announced by S.MagicCooldown and carried on ClientUserMagic.Cooldown.
    ///
    /// NO LINE-OF-SIGHT CHECK, deliberately. The Mir 2 agents walk a line to the target before
    /// casting, and copying that would have been the obvious thing to do - but Zircon's server does
    /// not ask for it. FireBall.MagicCast tests CanAttackTarget and range and nothing else, so a
    /// spell through a wall lands, and refusing to cast one would be us inventing a restriction the
    /// game does not have.
    /// </summary>
    public sealed class SpellBook
    {
        public readonly record struct AreaAim(ClientUserMagic Magic, Point Point,
            MirDirection Direction, int Covered);

        private readonly record struct AreaReservation(int MapIndex, HashSet<Point> Cells,
            DateTime ExpiresUtc);
        private readonly List<AreaReservation> _areaReservations = new List<AreaReservation>();
        private int _areaMapIndex = -1;

        private static bool DamageSpell(MagicType type) =>
            Castable.Contains(type) || AoeGeometry.Shapes.ContainsKey(type);
        private bool EnabledAttackType(MagicType type) =>
            Castable.Contains(type) || _config.AoeEnabled && AoeGeometry.Shapes.ContainsKey(type);
        /// <summary>
        /// Spells that take a single hostile target and hurt it.
        ///
        /// A whitelist, for the same reason SkillSet keeps one: the client's own cast handler is a
        /// two-hundred-line switch over MagicType where each group fills the packet differently -
        /// some take a target, some a direction, some a ground location, some a corpse. Everything
        /// here is from the group that sends Target = the monster's ObjectID, so one packet shape
        /// covers all of it. Utility spells in that same group (Neutralize, Parasite, taming) are
        /// left out because they are not damage and the bot has no plan for them.
        /// </summary>
        private static readonly HashSet<MagicType> Castable = new HashSet<MagicType>
        {
            // Wizard
            MagicType.FireBall, MagicType.LightningBall, MagicType.IceBolt, MagicType.GustBlast,
            MagicType.AdamantineFireBall, MagicType.ThunderBolt, MagicType.IceBlades,
            MagicType.Cyclone, MagicType.ChainLightning, MagicType.FireBounce,
            MagicType.LightningStrike, MagicType.IceRain, MagicType.IceDragon,

            // Taoist
            MagicType.ExplosiveTalisman, MagicType.ImprovedExplosiveTalisman,
            MagicType.EvilSlayer, MagicType.GreaterEvilSlayer,
            MagicType.SearingLight, MagicType.BrainStorm,
            // PoisonDust is NOT here. It has its own path (ChoosePoison) because it needs the
            // applied/confirmed bookkeeping, and casting it as an ordinary damage spell would
            // bypass that and re-poison an already-poisoned target every other turn.

            // Assassin
            MagicType.Hemorrhage, MagicType.FlamingDaggers, MagicType.Shredding,
            MagicType.HellFire
        };

        /// <summary>
        /// Can the bot cast this type of spell AT ALL?
        ///
        /// Asked by the status page so a skill the character has learnt but the bot cannot use is
        /// visible as such. Wizzler learnt Fire Wall and cast Cyclone 8,685 times instead, and
        /// nothing in the UI could show why: ground-targeted spells need a location in the packet
        /// and this cast path only sends a target. That is a real limitation and it should be on
        /// the screen rather than in a comment.
        /// </summary>
        public static bool IsCastable(MagicType type) => DamageSpell(type);

        /// <summary>
        /// Does the bot have ANY path that uses this magic, and if not, why not?
        ///
        /// Broader than IsCastable because damage is not the only thing a skill can be for: the
        /// self-buffs, the poison path and the two spells the brain drives directly (Heal and
        /// Summon Skeleton) all live outside Castable and are all genuinely used.
        ///
        /// The reason string is the useful half. "Not castable" on a learnt skill invites the
        /// conclusion that something is broken, when the honest answer is usually that this
        /// particular spell shape was never implemented.
        /// </summary>
        /// <summary>
        /// Always-on skills that need no action from anybody.
        ///
        /// Kept separate from "the bot drives it" because the honest answer for these is neither
        /// yes nor no: there is nothing to drive. Swordsmanship applies on every swing whether or
        /// not it is named (SkillSet's own comment says so), and Potion Mastery changes what a
        /// potion restores when it is drunk. Reporting either as "unused" would be wrong, and
        /// reporting them as driven would be equally wrong.
        /// </summary>
        private static readonly HashSet<MagicType> Passive = new HashSet<MagicType>
        {
            MagicType.Swordsmanship, MagicType.PotionMastery,
            // Spirit Sword is the Taoist's Swordsmanship: an accuracy passive the server applies
            // on every swing (SpiritSword.cs AttackCast/GetPassiveStats). Not "no path".
            MagicType.SpiritSword,
            MagicType.WillowDance, MagicType.BloodyFlower,
            MagicType.PledgeOfBlood, MagicType.GhostWalk,
            MagicType.TouchOfTheDeparted
        };

        public static bool IsSupported(MagicType type, out string why)
        {
            why = "";

            if (DamageSpell(type)) return true;
            if (SkillSet.Drives(type)) return true;

            foreach ((MagicType magic, BuffType? _, bool __) in SelfBuffs)
                if (magic == type) return true;

            switch (type)
            {
                case MagicType.Heal:
                case MagicType.PoisonDust:
                case MagicType.SummonSkeleton:
                case MagicType.SummonShinsu:
                case MagicType.SummonJinSkeleton:
                case MagicType.SummonPuppet:
                case MagicType.WraithGrip:
                case MagicType.ExpelUndead:
                    return true;
            }

            if (Passive.Contains(type))
            {
                why = type == MagicType.PledgeOfBlood || type == MagicType.GhostWalk
                    ? "passive - augments Cloak or Summon Puppet"
                    : type == MagicType.TouchOfTheDeparted
                    ? "passive - augments Wraith Grip"
                    : "passive - always on, nothing to cast";
                return false;
            }

            why = "the bot has no path for this spell shape";
            return false;
        }

        /// <summary>Is this an always-on skill rather than something the bot failed to use?</summary>
        public static bool IsPassive(MagicType type) => Passive.Contains(type);

        /// <summary>
        /// Self-buffs worth keeping up, and the BuffType each one grants.
        ///
        /// The pairing has to be explicit because nothing in the data says which magics are buffs:
        /// MagicInfo.School is elemental, MagicProperty is display-only and nothing branches on it,
        /// and the behaviour lives in per-class C# subclasses on the server. So this is a list, and
        /// the BuffType is what makes "do I already have it" answerable.
        ///
        /// Deliberately short:
        ///   - Renounce is NOT here. It trades health percentage for MC (Renounce.cs:51), which is
        ///     a stance to take deliberately, not a buff to hold permanently.
        ///   - Tornado is NOT here. The enum comment calls it uncoded and the comment is stale -
        ///     there is a real implementation - but it spawns a tornado monster rather than
        ///     buffing the caster.
        ///   - The Taoist entries are ground-targeted radius-3 AoE rather than self-casts. They
        ///     still land on us, because CanHelpTarget always returns true for the caster, so
        ///     casting at our own feet is the correct way to self-buff with them.
        /// </summary>
        /// <summary>
        /// Buff is null for effects that grant no BuffType and so cannot be confirmed with
        /// HasBuff - see PoisonousCloud below. Those are re-cast on a timer instead.
        /// </summary>
        private static readonly (MagicType Magic, BuffType? Buff, bool AtOwnFeet)[] SelfBuffs =
        {
            // Wizard
            (MagicType.MagicShield, BuffType.MagicShield, false),
            (MagicType.SuperiorMagicShield, BuffType.SuperiorMagicShield, false),

            // Taoist - ground-targeted, cast at our own location
            (MagicType.MagicResistance, BuffType.MagicResistance, true),
            (MagicType.Resilience, BuffType.Resilience, true),
            (MagicType.StrengthOfFaith, BuffType.StrengthOfFaith, false),

            // Assassin - and the only entry here that is NOT a buff.
            //
            // PoisonousCloud spawns SpellObjects across a radius-2 area centred on the caster for
            // Magic.GetPower() seconds (PoisonousCloud.cs:35-58). It grants no BuffType, so there
            // is nothing for HasBuff to answer and no confirmation to wait on - hence the null.
            //
            // The server refuses a re-cast SILENTLY while a cloud is already on our cell, so the
            // timer is not an optimisation but the whole of the bookkeeping: the bot cannot see
            // spell objects at all (S.ObjectSpell is unhandled) and would otherwise ask forever
            // and never know it was being ignored.
            (MagicType.PoisonousCloud, null, true)
        };

        /// <summary>When a timer-tracked self effect may be cast again, by MagicInfo.Index.</summary>
        private readonly Dictionary<int, DateTime> _timedRecast = new Dictionary<int, DateTime>();

        /// <summary>
        /// When a buff attempt may next be made, keyed by MagicInfo.Index, and how many attempts
        /// have gone unanswered.
        ///
        /// This is the starvation guard. A buff branch sitting above damage repeats for ever if the
        /// server refuses silently - no reagent, wrong shape, a precondition we cannot see - and
        /// this server refuses a great deal silently. Confirmation is S.BuffAdd arriving and
        /// HasBuff going true; until then each attempt pushes the next one further out.
        /// </summary>
        private readonly Dictionary<int, DateTime> _buffRetry = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, int> _buffTries = new Dictionary<int, int>();

        public int BuffsCast;
        public int PoisonsCast;

        /// <summary>
        /// Targets we have thrown poison at but have not yet seen poisoned.
        ///
        /// Strictly NON-AUTHORITATIVE, and never reported as an applied effect. The authoritative
        /// answer is WorldObject.Poison, fed by S.ObjectPoison - but that is broadcast only when
        /// the mask CHANGES, so there is a gap between our cast going out and the flag coming back
        /// during which we would otherwise poison the same monster over and over.
        ///
        /// An entry is deleted the moment the server confirms, because from then on the flag does
        /// the suppressing and a second record could only drift out of step with it.
        /// </summary>
        private readonly Dictionary<uint, DateTime> _poisonPending = new Dictionary<uint, DateTime>();
        private readonly Dictionary<uint, DateTime> _wraithPending = new Dictionary<uint, DateTime>();
        private DateTime _nextPuppet = DateTime.MinValue;

        private static readonly TimeSpan PoisonConfirmWindow = TimeSpan.FromSeconds(8);

        /// <summary>
        /// Heal is a short heal-over-time buff. Pet buffs are not represented in WorldModel, so a
        /// local target gate prevents paying for another cast while the previous one is ticking.
        /// Health packets remain authoritative about whether another heal is needed afterwards.
        /// </summary>
        private readonly Dictionary<uint, DateTime> _petHealRetry = new Dictionary<uint, DateTime>();
        private static readonly TimeSpan PetHealWindow = TimeSpan.FromSeconds(8);

        /// <summary>Why the last buff attempt was skipped, for the log.</summary>
        public string BuffDiagnostic = "";

        private readonly BotConfig _config;

        /// <summary>When each spell may next be cast, keyed by MagicInfo.Index.</summary>
        private readonly Dictionary<int, DateTime> _cooldowns = new Dictionary<int, DateTime>();

        /// <summary>The global between-casts gate - Globals.MagicDelay, shared by every spell.</summary>
        private DateTime _nextCast = DateTime.MinValue;

        public int CastsIssued;

        public SpellBook(BotConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// What this character can actually throw, named. Logged once when the skill list changes,
        /// which is the only form of this question worth answering out loud: the moment-to-moment
        /// reasons for not casting - mana, cooldown, range - are already legible from the MP
        /// figures on every log line.
        /// </summary>
        public string Describe(WorldModel world)
        {
            List<string> names = new List<string>();

            foreach (ClientUserMagic magic in world.Magics)
            {
                if (magic?.Info == null || !EnabledAttackType(magic.Info.Magic)) continue;
                if (magic.ItemRequired) continue;

                names.Add(world.Level >= magic.Info.NeedLevel1
                    ? $"{magic.Info.Name} ({magic.Cost} mana" +
                      (AoeGeometry.Shapes.ContainsKey(magic.Info.Magic) &&
                       !Castable.Contains(magic.Info.Magic) ? ", area" : "") + ")"
                    : $"{magic.Info.Name} (needs level {magic.Info.NeedLevel1})");
            }

            if (names.Count > 0) return "can cast " + string.Join(", ", names);

            // NAME WHAT IT DOES KNOW.
            //
            // "no castable spells among 5 skills" is a dead end: it says a character cannot cast
            // without saying what it is holding or why none of it qualifies, and an assassin has
            // now sat at 7% mana swinging a bare weapon through two separate investigations with
            // this line as the only evidence. The list is short and the answer is always in it.
            List<string> held = new List<string>();

            foreach (ClientUserMagic magic in world.Magics)
            {
                if (magic?.Info == null) continue;

                string why = magic.ItemRequired ? "needs an item"
                    : !EnabledAttackType(magic.Info.Magic) ? "not an enabled castable type"
                    : world.Level < magic.Info.NeedLevel1 ? $"needs level {magic.Info.NeedLevel1}"
                    : "castable";

                held.Add($"{magic.Info.Name} [{magic.Info.Magic}] cost {magic.Cost} - {why}");
            }

            return $"no castable spells among {world.KnownMagicCount} skills: " +
                   string.Join("; ", held);
        }

        /// <summary>
        /// Does this character have anything to spend mana ON?
        ///
        /// Asked before drinking a mana potion. A warrior's mana pays for nothing the bot uses, so
        /// pouring potions into it would be burning gold and bag weight for no effect.
        /// </summary>
        /// <summary>
        /// Does this character have ANY spell it will actually cast that costs mana?
        ///
        /// Broader than HasCastable on purpose. HasCastable answers "can I throw something at a
        /// target", and the mana-drinking gate was built out of it - so a character whose only
        /// costed magic is a self-centred area spell was judged to have no use for mana at all.
        ///
        /// An assassin sat at 9 of 125 mana swinging a bare weapon, holding two mana potions and a
        /// Poisonous Cloud costing 11. Cloud is cast through the SelfBuffs path, which is neither
        /// Castable nor anything the gate consulted, so the bot never drank, never had the 11, and
        /// could never cast the one spell it owned. Its own skill line read "no castable spells"
        /// while listing a spell with a price on it.
        ///
        /// Anything this book can issue and has to pay for counts.
        /// </summary>
        public bool UsesMana(WorldModel world)
        {
            foreach (ClientUserMagic magic in world.Magics)
            {
                if (magic?.Info == null || magic.ItemRequired) continue;
                if (magic.Cost <= 0) continue;
                if (world.Level < magic.Info.NeedLevel1) continue;

                if (EnabledAttackType(magic.Info.Magic) ||
                    magic.Info.Magic == MagicType.WraithGrip ||
                    magic.Info.Magic == MagicType.ExpelUndead ||
                    magic.Info.Magic == MagicType.SummonPuppet) return true;

                foreach ((MagicType Magic, BuffType? Buff, bool AtOwnFeet) self in SelfBuffs)
                    if (self.Magic == magic.Info.Magic) return true;
            }

            return false;
        }

        public bool HasCastable(WorldModel world)
        {
            foreach (ClientUserMagic magic in world.Magics)
            {
                if (magic?.Info == null || magic.ItemRequired) continue;
                if (!EnabledAttackType(magic.Info.Magic)) continue;
                if (world.Level < magic.Info.NeedLevel1) continue;

                return true;
            }

            return false;
        }

        /// <summary>
        /// Is there an attack spell we could cast RIGHT NOW - mana in the pool, off cooldown?
        ///
        /// Distinct from HasCastable, which answers the much weaker "does this character know any
        /// attack spell at all". The two were being used interchangeably and the difference is not
        /// academic: the kiting branch asked HasCastable and so kept backing away to spell range on
        /// behalf of a spell it had no mana to cast.
        ///
        /// Measured on the running bots before this was fixed: a level 14 Taoist backed off to
        /// "cast from 7" forty-one times at 0 of 74 mana, a level 19 Wizard seventy-eight times at
        /// 0 of 293. Both then walked back in, and out again, until the no-progress watchdog gave
        /// up on the target - 182 and 105 abandoned chases respectively, against zero for either
        /// melee bot. A caster with an empty pool is a melee character and has to fight like one.
        ///
        /// The gates are deliberately the same ones ChooseAttack applies, including the mana floor,
        /// so this cannot say yes to something that would then be refused. The global _nextCast
        /// throttle is NOT included: it is a sub-second gap between casts, and treating it as
        /// "cannot cast" would make the bot lurch in and out of range between every spell.
        /// </summary>
        public bool CanCastNow(WorldModel world)
        {
            int floor = world.MaxMana * Math.Clamp(_config.SpellManaFloorPercent, 0, 90) / 100;

            foreach (ClientUserMagic magic in world.Magics)
            {
                if (magic?.Info == null || magic.ItemRequired) continue;
                if (!EnabledAttackType(magic.Info.Magic)) continue;
                if (world.Level < magic.Info.NeedLevel1) continue;

                if (_cooldowns.TryGetValue(magic.InfoIndex, out DateTime until) &&
                    DateTime.UtcNow < until)
                    continue;

                if (world.Mana - magic.Cost < floor) continue;

                return true;
            }

            return false;
        }

        /// <summary>Unlike CanCastNow, area-only magic does not count without a viable cluster.</summary>
        public bool CanCastSingleNow(WorldModel world)
        {
            int floor = world.MaxMana * Math.Clamp(_config.SpellManaFloorPercent, 0, 90) / 100;
            DateTime now = DateTime.UtcNow;
            foreach (ClientUserMagic magic in world.Magics)
                if (magic?.Info != null && Castable.Contains(magic.Info.Magic) &&
                    EligibleDamage(world, magic, floor, now)) return true;
            return false;
        }

        /// <summary>S.MagicCooldown: the server putting one spell on ice for Delay milliseconds.</summary>
        public void Cooldown(int magicInfoIndex, int delayMilliseconds)
        {
            _cooldowns[magicInfoIndex] = DateTime.UtcNow.AddMilliseconds(Math.Max(0, delayMilliseconds));
        }

        /// <summary>Nothing survives a reconnect - the server's timers are its own.</summary>
        public void Reset()
        {
            _cooldowns.Clear();
            _buffRetry.Clear();
            _buffTries.Clear();
            _poisonPending.Clear();
            _wraithPending.Clear();
            _nextPuppet = DateTime.MinValue;
            _areaReservations.Clear();
            _areaMapIndex = -1;
            _nextCast = DateTime.MinValue;
        }

        private bool EligibleDamage(WorldModel world, ClientUserMagic magic, int floor,
            DateTime now)
        {
            if (magic?.Info == null || magic.ItemRequired ||
                world.Level < magic.Info.NeedLevel1 ||
                world.Mana - magic.Cost < floor) return false;
            return !_cooldowns.TryGetValue(magic.InfoIndex, out DateTime until) || now >= until;
        }

        /// <summary>Best legal area opportunity; a geometric hit count, not guaranteed damage.</summary>
        public AreaAim? ChooseArea(WorldModel world, WorldObject committed, MapGrid grid,
            bool ignoreGlobalDelay = false)
        {
            if (!_config.AoeEnabled || !_config.CastSpells || committed == null || grid == null ||
                world.KnownMagicCount == 0) return null;
            DateTime now = DateTime.UtcNow;
            if (!ignoreGlobalDelay && now < _nextCast) return null;
            if (_areaMapIndex != world.MapIndex)
            {
                _areaReservations.Clear();
                _areaMapIndex = world.MapIndex;
            }
            _areaReservations.RemoveAll(r => r.ExpiresUtc <= now);

            WorldObject[] hostiles = world.Objects.Where(x => x.IsValidTarget).ToArray();
            if (hostiles.Length < Math.Clamp(_config.AoeMinimumTargets, 1, 10)) return null;
            int floor = world.MaxMana * Math.Clamp(_config.SpellManaFloorPercent, 0, 90) / 100;
            int range = Math.Min(_config.CastRange, 10);
            AreaAim? best = null;
            int bestCost = int.MaxValue;

            foreach (ClientUserMagic magic in world.Magics.OrderBy(m => m?.Info?.Magic))
            {
                if (magic?.Info == null || !AoeGeometry.Shapes.TryGetValue(magic.Info.Magic,
                    out AoeShape shape) || !EligibleDamage(world, magic, floor, now)) continue;

                // Rays ignore the supplied location; the server advances from the caster.
                var centres = new HashSet<Point>();
                if (AoeGeometry.Directional(shape)) centres.Add(world.Location);
                else
                    foreach (WorldObject ob in hostiles)
                        for (int dx = -3; dx <= 3; dx++)
                            for (int dy = -3; dy <= 3; dy++)
                            {
                                Point p = new Point(ob.Location.X + dx, ob.Location.Y + dy);
                                // The server accepts an aim on an unwalkable centre if its
                                // surrounding cells exist; only footprint cells need to exist.
                                if (p.X >= 0 && p.Y >= 0 && p.X < grid.Width && p.Y < grid.Height &&
                                    WorldModel.Distance(world.Location, p) <= range)
                                    centres.Add(p);
                            }

                foreach (Point centre in centres)
                {
                    int directions = AoeGeometry.Directional(shape) ? 8 : 1;
                    for (int d = 0; d < directions; d++)
                    {
                        MirDirection direction = AoeGeometry.Directional(shape)
                            ? (MirDirection)d : WorldModel.DirectionTo(world.Location, centre);
                        Point aim = AoeGeometry.Directional(shape)
                            ? WorldModel.Step(world.Location, direction) : centre;
                        HashSet<Point> cells = AoeGeometry.Footprint(shape, world.Location, aim,
                            direction, grid.Width, grid.Height, grid.Walkable);
                        if (!cells.Contains(committed.Location)) continue;
                        if (AoeGeometry.Persistent(magic.Info.Magic) &&
                            _areaReservations.Any(r => r.MapIndex == world.MapIndex &&
                                AoeGeometry.SubstantiallyOverlaps(cells, r.Cells)))
                            continue;

                        int covered = hostiles.Count(x => cells.Contains(x.Location) &&
                            (shape != AoeShape.Meteor || WorldModel.Distance(world.Location, x.Location) <= 10));
                        if (shape == AoeShape.Meteor) covered = Math.Min(covered, 6 + magic.Level);
                        if (covered < Math.Clamp(_config.AoeMinimumTargets, 1, 10)) continue;
                        if (best != null && (covered < best.Value.Covered ||
                            covered == best.Value.Covered && magic.Cost >= bestCost)) continue;
                        best = new AreaAim(magic, aim, direction, covered);
                        bestCost = magic.Cost;
                    }
                }
            }
            return best;
        }

        /// <summary>Predictive protection: no spell-object confirmation is available to this bot.</summary>
        public void ReserveIssued(MagicType type, WorldModel world, Point aim,
            MirDirection direction, MapGrid grid)
        {
            if (!AoeGeometry.Persistent(type) || grid == null ||
                !AoeGeometry.Shapes.TryGetValue(type, out AoeShape shape)) return;
            if (_areaMapIndex != world.MapIndex)
            {
                _areaReservations.Clear();
                _areaMapIndex = world.MapIndex;
            }
            ClientUserMagic magic = world.Magics.FirstOrDefault(m => m?.Info?.Magic == type);
            int ticks = ((magic?.Level ?? 0) + 2) * 5;
            HashSet<Point> cells = AoeGeometry.Footprint(shape, world.Location, aim, direction,
                grid.Width, grid.Height, grid.Walkable);
            _areaReservations.Add(new AreaReservation(world.MapIndex, cells,
                DateTime.UtcNow.AddSeconds(ticks * 2 + 2)));
        }

        /// <summary>
        /// A self-buff we are missing and can afford, or null.
        ///
        /// "Missing" is the server's answer, not ours: S.BuffAdd, S.BuffRemove and the login dump
        /// keep WorldModel.HasBuff authoritative, and expiry arrives as an ordinary remove. There
        /// is no duration tracking and no re-cast timer here, and there should never be one - the
        /// Mir 2 agents need a two-minute guess only because their server will not tell them about
        /// other players' buffs, which is a problem we do not have.
        /// </summary>
        public ClientUserMagic ChooseBuff(WorldModel world, out bool atOwnFeet)
        {
            atOwnFeet = false;

            if (!_config.CastSpells || !_config.MaintainBuffs) return null;
            if (world.KnownMagicCount == 0) return null;
            if (DateTime.UtcNow < _nextCast) return null;

            int floor = world.MaxMana * Math.Clamp(_config.SpellManaFloorPercent, 0, 90) / 100;

            foreach ((MagicType magic, BuffType? buff, bool feet) in SelfBuffs)
            {
                if (buff.HasValue)
                {
                    if (world.HasBuff(buff.Value)) continue;
                }
                else
                {
                    // A timed area effect is only worth laying when there is something to lay it
                    // on. A real buff is free to hold indefinitely, so the branch above has never
                    // needed a combat test - but PoisonousCloud expires, costs mana each time and
                    // does nothing at all in an empty street. Sindo was seen casting it in town
                    // between vendors.
                    if (world.NearestLiveMonster(_config.AggroRange) == null) continue;

                    if (_timedRecast.TryGetValue(MagicIndex(world, magic), out DateTime again) &&
                        DateTime.UtcNow < again)
                        continue;
                }
                if (!world.TryGetMagic(magic, out ClientUserMagic known)) continue;
                if (known.Info == null) continue;
                if (world.Level < known.Info.NeedLevel1) continue;
                if (known.ItemRequired) continue;

                if (_cooldowns.TryGetValue(known.Info.Index, out DateTime ready) &&
                    DateTime.UtcNow < ready)
                    continue;

                if (_buffRetry.TryGetValue(known.Info.Index, out DateTime retry) &&
                    DateTime.UtcNow < retry)
                    continue;

                if (world.Mana - known.Cost < floor)
                {
                    BuffDiagnostic = $"{known.Info.Name} needs {known.Cost} mana, have {world.Mana}";
                    continue;
                }

                atOwnFeet = feet;
                return known;
            }

            return null;
        }

        /// <summary>
        /// A buff cast went out. Back the next attempt off, because the only proof it worked is
        /// HasBuff turning true - and if it does, ChooseBuff stops selecting this spell anyway, so
        /// the backoff never gets in the way of a buff that is actually landing.
        /// </summary>
        private static int MagicIndex(WorldModel world, MagicType magic) =>
            world.TryGetMagic(magic, out ClientUserMagic known) && known.Info != null
                ? known.Info.Index : -1;

        /// <summary>Note a timer-tracked self effect as freshly cast.</summary>
        public void TimedSelfEffectIssued(ClientUserMagic magic)
        {
            if (magic?.Info == null) return;

            foreach ((MagicType m, BuffType? buff, bool _) in SelfBuffs)
                if (buff == null && m == magic.Info.Magic)
                    _timedRecast[magic.Info.Index] =
                        DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(5, _config.SelfAoeRecastSeconds));
        }

        public void BuffIssued(ClientUserMagic magic)
        {
            if (magic?.Info == null) return;

            _buffTries.TryGetValue(magic.Info.Index, out int tries);
            tries++;
            _buffTries[magic.Info.Index] = tries;

            // 5s, 10s, 20s, 40s, capped at a minute. A silent refusal must cost less and less
            // attention, without ever giving up completely - a missing reagent can be bought.
            int seconds = Math.Min(60, 5 * (int)Math.Pow(2, Math.Min(4, tries - 1)));
            _buffRetry[magic.Info.Index] = DateTime.UtcNow.AddSeconds(seconds);

            // NOT Issued() - the brain calls that for every BotAction.Cast, buff or damage, so
            // doing it here too would double-count the casts and apply the magic delay twice.
            BuffsCast++;
        }

        /// <summary>
        /// Poison worth applying to this target, or null.
        ///
        /// Chosen before damage because a poison ticks for the whole fight, so the earlier it lands
        /// the more it is worth - and because re-applying it is pure waste, which is what the
        /// bookkeeping below exists to prevent.
        ///
        /// Which poison lands is not our choice: PoisonDust reads the EQUIPPED poison item and
        /// takes its Shape to decide green or red (PoisonDust.cs:64). So the test is simply whether
        /// the target already carries either - we cannot aim for the one it is missing without
        /// swapping equipment mid-fight, and the single Poison slot makes that a bad trade.
        /// </summary>
        public ClientUserMagic ChoosePoison(WorldModel world, Backpack items, WorldObject target,
            int distance)
        {
            if (!_config.CastSpells || target == null) return null;
            if (distance > Math.Min(_config.CastRange, 10)) return null;
            if (DateTime.UtcNow < _nextCast) return null;

            if (!world.TryGetMagic(MagicType.PoisonDust, out ClientUserMagic magic)) return null;
            if (magic.Info == null || world.Level < magic.Info.NeedLevel1) return null;

            // No poison, no poisoning. PoisonDust takes the reagent from the EQUIPMENT slot inside
            // MagicCast and simply stops if it is not there (PoisonDust.cs:64) - no error, no
            // cooldown, nothing to learn from. Checking here makes the refusal ours and visible,
            // rather than a spell that appears to be cast and silently never happens.
            if (items == null || items.EquippedReagentCount(ItemType.Poison) <= 0) return null;

            // Confirmed by the server: stop, and drop our guess, which has served its purpose.
            if (target.Poison.HasFlag(PoisonType.Green) || target.Poison.HasFlag(PoisonType.Red))
            {
                _poisonPending.Remove(target.ObjectID);
                return null;
            }

            if (_poisonPending.TryGetValue(target.ObjectID, out DateTime until))
            {
                if (DateTime.UtcNow < until) return null;

                // The window elapsed with no confirmation. Either it missed or the poison was
                // resisted (PoisonResistance is a roll, MapObject.cs:1645), so it is worth one
                // more attempt rather than assuming it landed.
                _poisonPending.Remove(target.ObjectID);
            }

            int floor = world.MaxMana * Math.Clamp(_config.SpellManaFloorPercent, 0, 90) / 100;
            if (world.Mana - magic.Cost < floor) return null;

            if (_cooldowns.TryGetValue(magic.Info.Index, out DateTime ready) &&
                DateTime.UtcNow < ready)
                return null;

            return magic;
        }

        /// <summary>A poison cast went out. Optimistic, short-lived, and explicitly a guess.</summary>
        public void PoisonIssued(uint targetID)
        {
            _poisonPending[targetID] = DateTime.UtcNow + PoisonConfirmWindow;
            PoisonsCast++;
        }

        /// <summary>Apply the assassin's damage-over-time/paralysis skill once per target.</summary>
        public ClientUserMagic ChooseWraithGrip(WorldModel world, WorldObject target, int distance)
        {
            if (!_config.CastSpells || target == null ||
                distance > Math.Min(_config.CastRange, 10) || DateTime.UtcNow < _nextCast)
                return null;
            if (target.Poison.HasFlag(PoisonType.WraithGrip))
            {
                _wraithPending.Remove(target.ObjectID);
                return null;
            }
            if (target.Level > world.Level + 15) return null; // server refuses over-level mobs
            if (_wraithPending.TryGetValue(target.ObjectID, out DateTime pending) &&
                DateTime.UtcNow < pending) return null;
            if (!world.TryGetMagic(MagicType.WraithGrip, out ClientUserMagic magic) ||
                magic.Info == null || magic.ItemRequired || world.Level < magic.Info.NeedLevel1)
                return null;
            if (_cooldowns.TryGetValue(magic.InfoIndex, out DateTime ready) &&
                DateTime.UtcNow < ready) return null;
            int floor = world.MaxMana * Math.Clamp(_config.SpellManaFloorPercent, 0, 90) / 100;
            return world.Mana - magic.Cost >= floor ? magic : null;
        }

        public void WraithIssued(uint targetID) =>
            _wraithPending[targetID] = DateTime.UtcNow.AddSeconds(10);

        /// <summary>Expel Undead attempts per target, so a resisted one is not tried for ever.</summary>
        private readonly Dictionary<uint, int> _expelTries = new Dictionary<uint, int>();

        /// <summary>
        /// Expel Undead: an instant kill, not damage (ExpelUndead.cs MagicComplete). The server
        /// ignores it on anything not Undead, on a boss, at monster level 70+, and whenever the
        /// monster's level reaches ours minus one plus a 0-3 roll - so it is only offered where it
        /// can work: an undead, non-boss target at least two levels below us with most of its
        /// health left (a kill is worth least on something nearly dead). Success is then
        /// 35% + 9% per skill level + 5% per level of difference, so two tries per target.
        ///
        /// Jane learnt it and it sat at skill level 0: it was in no list, so nothing ever cast it.
        /// </summary>
        public ClientUserMagic ChooseExpel(WorldModel world, WorldObject target, int distance)
        {
            if (!_config.CastSpells || target == null || target.MonsterIndex < 0 ||
                distance > Math.Min(_config.CastRange, 10) || DateTime.UtcNow < _nextCast)
                return null;
            if (!world.TryGetMagic(MagicType.ExpelUndead, out ClientUserMagic magic) ||
                magic.Info == null || magic.ItemRequired || world.Level < magic.Info.NeedLevel1)
                return null;

            Library.SystemModels.MonsterInfo info = BotConnection.Monsters?.Find(target.MonsterIndex);
            if (info == null || !info.Undead || info.IsBoss || info.Level >= 70) return null;
            if (info.Level > world.Level - 2) return null;
            if (target.MaxHealth > 0 && target.Health * 100 < target.MaxHealth * 50) return null;
            if (_expelTries.TryGetValue(target.ObjectID, out int tries) && tries >= 2) return null;

            if (_cooldowns.TryGetValue(magic.InfoIndex, out DateTime ready) &&
                DateTime.UtcNow < ready) return null;
            int floor = world.MaxMana * Math.Clamp(_config.SpellManaFloorPercent, 0, 90) / 100;
            return world.Mana - magic.Cost >= floor ? magic : null;
        }

        public void ExpelIssued(uint targetID)
        {
            _expelTries.TryGetValue(targetID, out int tries);
            _expelTries[targetID] = tries + 1;
        }

        /// <summary>
        /// Puppet is a five-second explosive decoy, not a persistent pet. The server also moves
        /// the caster a few cells and applies Cloak, so use it only in close combat and throttle
        /// it independently of the server's spell cooldown.
        /// </summary>
        public ClientUserMagic ChoosePuppet(WorldModel world, WorldObject target, int distance)
        {
            if (!_config.CastSpells || target == null || distance > 2 ||
                DateTime.UtcNow < _nextCast || DateTime.UtcNow < _nextPuppet) return null;
            if (!world.TryGetMagic(MagicType.SummonPuppet, out ClientUserMagic magic) ||
                magic.Info == null || magic.ItemRequired || world.Level < magic.Info.NeedLevel1)
                return null;
            if (_cooldowns.TryGetValue(magic.InfoIndex, out DateTime ready) &&
                DateTime.UtcNow < ready) return null;
            int floor = world.MaxMana * Math.Clamp(_config.SpellManaFloorPercent, 0, 90) / 100;
            return world.Mana - magic.Cost >= floor ? magic : null;
        }

        public void PuppetIssued() => _nextPuppet = DateTime.UtcNow.AddSeconds(60);

        /// <summary>Choose our lowest visible pet that Heal can help right now.</summary>
        public ClientUserMagic ChoosePetHeal(WorldModel world, out WorldObject target)
        {
            target = null;

            if (!_config.CastSpells || _config.HealPetAtPercent <= 0) return null;
            if (DateTime.UtcNow < _nextCast) return null;
            if (!world.TryGetMagic(MagicType.Heal, out ClientUserMagic magic)) return null;
            if (magic.Info == null || world.Level < magic.Info.NeedLevel1) return null;

            int threshold = Math.Clamp(_config.HealPetAtPercent, 1, 99);
            int lowest = int.MaxValue;

            foreach (WorldObject pet in world.OwnPets)
            {
                if (pet.MaxHealth <= 0 || pet.Health <= 0) continue;

                int percent = pet.Health * 100 / pet.MaxHealth;
                if (percent > threshold || percent >= lowest) continue;
                if (world.DistanceTo(pet.Location) > Math.Min(_config.CastRange, 10)) continue;

                if (_petHealRetry.TryGetValue(pet.ObjectID, out DateTime retry) &&
                    DateTime.UtcNow < retry)
                    continue;

                lowest = percent;
                target = pet;
            }

            if (target == null) return null;

            int floor = world.MaxMana * Math.Clamp(_config.SpellManaFloorPercent, 0, 90) / 100;
            if (world.Mana - magic.Cost < floor)
            {
                target = null;
                return null;
            }

            if (_cooldowns.TryGetValue(magic.Info.Index, out DateTime ready) &&
                DateTime.UtcNow < ready)
            {
                target = null;
                return null;
            }

            return magic;
        }

        public void PetHealIssued(uint targetID) =>
            _petHealRetry[targetID] = DateTime.UtcNow + PetHealWindow;

        /// <summary>
        /// Forget targets that are gone. Object ids are recycled by the server, so a pending entry
        /// that outlives its monster would suppress poison on whatever inherits the id.
        /// </summary>
        public void ForgetPoison(uint targetID)
        {
            _poisonPending.Remove(targetID);
            _wraithPending.Remove(targetID);
            _petHealRetry.Remove(targetID);
            _expelTries.Remove(targetID);
        }

        public void ForgetAllPoison()
        {
            _poisonPending.Clear();
            _wraithPending.Clear();
        }

        /// <summary>The buff landed, so the backoff it accumulated is meaningless.</summary>
        public void BuffConfirmed(ClientUserMagic magic)
        {
            if (magic?.Info == null) return;

            _buffTries.Remove(magic.Info.Index);
            _buffRetry.Remove(magic.Info.Index);
        }

        /// <summary>Record that a cast went out, and hold off for the server's magic delay.</summary>
        public void Issued()
        {
            CastsIssued++;
            _nextCast = DateTime.UtcNow.AddMilliseconds(2000 + 150);
        }

        /// <summary>
        /// The spell to throw at this target, or None to fall back on hitting it.
        ///
        /// Order matters: the cheapest checks that rule out casting ENTIRELY come first, so the
        /// common case of a warrior with no spells costs a bool and a dictionary miss.
        /// </summary>
        public ClientUserMagic Choose(WorldModel world, WorldObject target, int distance)
        {
            if (!_config.CastSpells || target == null) return null;
            if (world.KnownMagicCount == 0) return null;

            if (distance > Math.Min(_config.CastRange, 10)) return null;
            if (DateTime.UtcNow < _nextCast) return null;

            // Mana floor. A caster that spends to zero has no escape and nothing left to do but
            // melee, which it is very bad at - so a slice of the pool is never available to the
            // attack spells at all.
            int floor = world.MaxMana * Math.Clamp(_config.SpellManaFloorPercent, 0, 90) / 100;

            ClientUserMagic best = null;
            int bestGate = -1;

            foreach (ClientUserMagic magic in world.Magics)
            {
                if (magic?.Info == null) continue;
                if (!Castable.Contains(magic.Info.Magic)) continue;

                // The level gate is on CASTING, separately from the one on learning the book.
                if (world.Level < magic.Info.NeedLevel1) continue;

                // ItemRequired means the skill only works while a matching magic ring is worn
                // (MagicObject.CanUseMagic). The bot does not manage those, so it does not use them.
                if (magic.ItemRequired) continue;

                if (_cooldowns.TryGetValue(magic.InfoIndex, out DateTime until) &&
                    DateTime.UtcNow < until)
                    continue;

                if (world.Mana - magic.Cost < floor) continue;

                // Highest requirement wins: a spell gated behind a higher level is the better
                // spell. This is the Mir 2 agents' rule and it holds on this server too.
                if (magic.Info.NeedLevel1 <= bestGate) continue;

                best = magic;
                bestGate = magic.Info.NeedLevel1;
            }

            return best;
        }
    }
}
