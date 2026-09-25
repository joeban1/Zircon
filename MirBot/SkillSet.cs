using System;
using System.Collections.Generic;
using Library;

namespace MirBot
{
    /// <summary>
    /// Which attack skills are known, which are armed right now, and which sustained toggles still
    /// need switching on.
    ///
    /// Attack skills in Zircon are not cast: they ride along on an ordinary attack, through the
    /// AttackMagic field of C.Attack. The server decides when one is available and says so with
    /// S.MagicToggle, which the real client uses to light up the icon. Before this the bot ignored
    /// that packet entirely and always sent MagicType.None, so every Slaying proc the server rolled
    /// was armed and then thrown away.
    ///
    /// Getting it wrong is not a silent miss. PlayerObject.Attack compares the requested skill with
    /// the one it independently decided was valid, and on a mismatch REFUSES THE WHOLE ATTACK, snaps
    /// the bot back with S.UserLocation, and writes an [ERROR] line into the server log
    /// (PlayerObject.cs:14731). So this only ever names a skill the server has explicitly armed, and
    /// falls back to None the moment anything is uncertain.
    /// </summary>
    public sealed class SkillSet
    {
        /// <summary>
        /// Attack skills whose AttackCast sets Cast = true, i.e. the ones the server will accept as
        /// an AttackMagic. Swordsmanship is deliberately absent: it is an AttackSkill and it does
        /// apply on every swing, but its AttackCast never sets Cast, so naming it would be rejected.
        /// </summary>
        private static readonly HashSet<MagicType> Nameable = new HashSet<MagicType>
        {
            // Warrior
            MagicType.Slaying, MagicType.Thrusting, MagicType.HalfMoon, MagicType.DestructiveSurge,
            MagicType.FlamingSword, MagicType.DragonRise, MagicType.BladeStorm,
            MagicType.DefensiveBlow, MagicType.OffensiveBlow,

            // Assassin. Absent until now, which is why an assassin swung a bare weapon for its
            // whole life: every list in this bot was written for the warrior and the caster, and
            // "no castable spells among 5 skills" in its own login line was the symptom nobody
            // read. Both of these are AttackSkill on the server and both add their own Type in
            // AttackCast, so the server will accept them as an AttackMagic - the test that rules
            // Swordsmanship out above.
            MagicType.VineTreeDance, MagicType.Discipline,
            MagicType.CalamityOfFullMoon, MagicType.FullBloom, MagicType.WhiteLotus,
            MagicType.RedLotus, MagicType.SweetBrier
        };

        // The client chooses these as AttackMagic on its next ordinary swing. They do not
        // send a MagicToggle(true), so waiting for one would leave them unused forever.
        private static readonly MagicType[] ChosenAssassinAttacks =
        {
            MagicType.FullBloom, MagicType.WhiteLotus, MagicType.RedLotus,
            MagicType.SweetBrier
        };

        /// <summary>
        /// Toggles that stay on once set (C.MagicToggle with CanUse = true). The server then arms
        /// them per swing.
        /// </summary>
        private static readonly MagicType[] Sustained =
        {
            MagicType.Thrusting, MagicType.HalfMoon, MagicType.DestructiveSurge, MagicType.FlameSplash,
            MagicType.VineTreeDance, MagicType.Discipline
        };

        /// <summary>
        /// One-shot charges, strongest first. Each C.MagicToggle pays the mana, starts the skill's
        /// cooldown and arms the NEXT swing for 12 seconds (FlamingSword.cs Toggle/AttackCast);
        /// charging one also pushes the other two back by two seconds.
        ///
        /// These were "left alone for now", which is why Mirbot learnt Dragon Rise and Banner learnt
        /// Blade Storm from the Lv 3 bosses and both still sat at skill level 0. Shoulder Dash is
        /// deliberately absent - it is a charge across the map, not a hit.
        /// </summary>
        private static readonly MagicType[] Charges =
        {
            MagicType.BladeStorm, MagicType.DragonRise, MagicType.FlamingSword
        };

        /// <summary>
        /// Which armed skill to name when several are armed. A charge expires unused after twelve
        /// seconds, so it goes first; Destructive Surge hits every side where Half Moon hits three.
        /// </summary>
        private static readonly MagicType[] SwingPriority =
        {
            MagicType.BladeStorm, MagicType.DragonRise, MagicType.FlamingSword,
            MagicType.DestructiveSurge, MagicType.HalfMoon
        };

        /// <summary>A charge request is answered by S.MagicToggle; do not repeat it meanwhile.</summary>
        private DateTime _nextCharge = DateTime.MinValue;

        /// <summary>
        /// Does the bot actively drive this melee skill?
        ///
        /// Nameable and Sustained between them are every magic this class acts on: one names the
        /// skill on a swing, the other arms it and leaves it armed. Asked by the status page, which
        /// otherwise reported a warrior's entire kit as unused - Mirbot has four skills, all four
        /// are melee, and it had cast Half Moon 3,727 times while the page called it unusable.
        /// </summary>
        public static bool Drives(MagicType magic) =>
            Nameable.Contains(magic) || Array.IndexOf(Sustained, magic) >= 0;

        public bool HasAssassinMelee(WorldModel world)
        {
            if (world.Class != MirClass.Assassin) return false;
            foreach (MagicType magic in ChosenAssassinAttacks)
                if (world.CanUseMagic(magic)) return true;
            return world.CanUseMagic(MagicType.Discipline) ||
                   world.CanUseMagic(MagicType.VineTreeDance) ||
                   world.CanUseMagic(MagicType.CalamityOfFullMoon);
        }

        private readonly HashSet<MagicType> _armed = new HashSet<MagicType>();
        private readonly HashSet<MagicType> _enabled = new HashSet<MagicType>();
        private readonly Dictionary<int, DateTime> _cooldowns = new Dictionary<int, DateTime>();

        public int ArmedCount => _armed.Count;

        /// <summary>S.MagicToggle: the server arming or spending a skill.</summary>
        public void Toggled(MagicType magic, bool canUse)
        {
            if (canUse) _armed.Add(magic);
            else _armed.Remove(magic);

            if (magic == _awaiting) _awaiting = MagicType.None;
        }

        /// <summary>
        /// One clock for every C.MagicToggle - sustained, charge before a swing, charge during the
        /// swing cooldown. The toggle used to hold the whole action gate for a second, which cost
        /// warriors a second on every swing; the server never needed it (FlamingSword.Toggle does
        /// not touch AttackTime), only the client's own one-second anti-spam, which this keeps.
        /// </summary>
        private DateTime _nextToggle = DateTime.MinValue;

        public bool ToggleReady => DateTime.UtcNow >= _nextToggle;

        public void ToggleIssued() => _nextToggle = DateTime.UtcNow.AddSeconds(1);

        /// <summary>A charge asked for and not yet confirmed armed by S.MagicToggle.</summary>
        private MagicType _awaiting = MagicType.None;
        private DateTime _awaitingUntil = DateTime.MinValue;

        /// <summary>How long a swing waits for a requested charge to be confirmed.</summary>
        public static readonly TimeSpan ChargeAckWait = TimeSpan.FromMilliseconds(400);

        /// <summary>
        /// Hold the swing: a charge was just requested and the server has not armed it yet. A
        /// swing sent now would go out plain and the charge would be wasted on the one after.
        /// </summary>
        public bool AwaitingCharge =>
            _awaiting != MagicType.None && DateTime.UtcNow < _awaitingUntil &&
            !_armed.Contains(_awaiting);

        public bool IsArmed(MagicType magic) => _armed.Contains(magic);

        public void Cooldown(int infoIndex, int delayMilliseconds) =>
            _cooldowns[infoIndex] = DateTime.UtcNow.AddMilliseconds(Math.Max(0, delayMilliseconds));

        /// <summary>Everything resets on a reconnect - the server's arming state does not survive.</summary>
        public void Reset()
        {
            _armed.Clear();
            _enabled.Clear();
            _cooldowns.Clear();
            _nextCharge = DateTime.MinValue;
            _nextToggle = DateTime.MinValue;
            _awaiting = MagicType.None;
        }

        /// <summary>
        /// A one-shot charge worth arming for the swing we are about to make, or None.
        ///
        /// Only while in contact - an armed charge lasts twelve seconds and is spent by the next
        /// swing - and only when nothing is already armed, since a second charge would be a
        /// "charge failed" that still costs mana.
        /// </summary>
        public MagicType PendingCharge(WorldModel world)
        {
            if (DateTime.UtcNow < _nextCharge) return MagicType.None;

            foreach (MagicType magic in Charges)
                if (_armed.Contains(magic)) return MagicType.None;

            foreach (MagicType magic in Charges)
            {
                if (!world.TryGetMagic(magic, out ClientUserMagic known) ||
                    known?.Info == null || known.ItemRequired) continue;
                if (!world.CanUseMagic(magic)) continue;
                if (world.Mana < known.Cost) continue;
                if (_cooldowns.TryGetValue(known.InfoIndex, out DateTime until) &&
                    DateTime.UtcNow < until) continue;

                return magic;
            }

            return MagicType.None;
        }

        public void ChargeSent() => _nextCharge = DateTime.UtcNow.AddMilliseconds(1500);

        public void ChargeSent(MagicType magic)
        {
            ChargeSent();
            _awaiting = magic;
            _awaitingUntil = DateTime.UtcNow + ChargeAckWait;
        }

        /// <summary>
        /// A sustained toggle we know, can use at this level, and have not switched on yet, or None.
        /// Asked once per decision; each toggle is only ever sent once per session.
        /// </summary>
        public MagicType PendingToggle(WorldModel world)
        {
            foreach (MagicType magic in Sustained)
            {
                if (_enabled.Contains(magic)) continue;
                if (!world.CanUseMagic(magic)) continue;

                return magic;
            }

            return MagicType.None;
        }

        public void ToggleSent(MagicType magic) => _enabled.Add(magic);

        /// <summary>
        /// The skill to name on this swing, or None for a plain attack.
        ///
        /// Thrusting hits the cell behind the target as well, so it is only worth naming when there
        /// is something there to hit - at range 2 the target itself is the second cell. That check
        /// is lifted from the Mir 2 agents' WarriorAI, which is the only working reference for this
        /// mechanic that we have.
        /// </summary>
        public MagicType ChooseAttackMagic(WorldModel world, WorldObject target, int distance)
        {
            if (target == null) return MagicType.None;

            // Prefer the server's Lotus chain while its predecessor buff is still present.
            // Every one is a C.Attack AttackMagic, not a C.Magic cast or a toggle.
            MagicType preferred = world.HasBuff(BuffType.RedLotus) ? MagicType.SweetBrier :
                world.HasBuff(BuffType.WhiteLotus) ? MagicType.RedLotus :
                world.HasBuff(BuffType.FullBloom) ? MagicType.WhiteLotus :
                MagicType.FullBloom;
            if (ManualReady(world, preferred)) return preferred;
            foreach (MagicType magic in ChosenAssassinAttacks)
                if (magic != preferred && ManualReady(world, magic)) return magic;

            // A charge first: it is spent or wasted within twelve seconds either way.
            foreach (MagicType magic in Charges)
                if (IsArmed(magic) && Usable(world, magic)) return magic;

            if (IsArmed(MagicType.Thrusting) && Usable(world, MagicType.Thrusting))
            {
                if (distance == 2) return MagicType.Thrusting;

                if (distance == 1)
                {
                    MirDirection direction = WorldModel.DirectionTo(world.Location, target.Location);
                    System.Drawing.Point behind = Functions.Move(target.Location, direction);

                    if (world.SomethingAt(behind, target.ObjectID)) return MagicType.Thrusting;
                }
            }

            foreach (MagicType magic in SwingPriority)
                if (IsArmed(magic) && Usable(world, magic)) return magic;

            foreach (MagicType magic in _armed)
            {
                if (magic == MagicType.Thrusting) continue;   // handled above, on its own terms
                if (!Usable(world, magic)) continue;

                return magic;
            }

            return MagicType.None;
        }

        /// <summary>
        /// Can this skill ride along on a swing RIGHT NOW - including whether we can pay for it.
        ///
        /// WorldModel.CanUseMagic answers a weaker question than this one: it checks the skill is
        /// known and the level is high enough, and says nothing about mana. The server's own
        /// CanUseMagic does check the cost, and the consequence of the two disagreeing is not a
        /// wasted skill but a LOST ATTACK - PlayerObject.cs:13419 compares the requested magic
        /// against the one it found valid and, when they differ, logs
        ///
        ///     [ERROR] Mirbot requested Attack Skill 'HalfMoon' but valid magic was 'None'
        ///
        /// then returns having done nothing at all. No swing, no fallback. A level 27 warrior at
        /// 0 of 117 mana stood in a crowd of thirty monsters requesting Half Moon over and over
        /// and never hit anything.
        ///
        /// This is the same mistake as the caster one CanCastNow fixed - asking "do I know it"
        /// where the server asks "can you use it" - left in the melee path because attack skills
        /// arrive through C.Attack rather than C.Magic and did not look like casting.
        ///
        /// No mana floor here, unlike spells: an attack skill we cannot afford simply degrades to
        /// an ordinary swing, which is a perfectly good outcome, so there is nothing to reserve.
        /// </summary>
        private static bool Usable(WorldModel world, MagicType magic)
        {
            if (!Nameable.Contains(magic)) return false;
            if (!world.CanUseMagic(magic)) return false;

            return world.Mana >= CostOf(world, magic);
        }

        private bool ManualReady(WorldModel world, MagicType magic)
        {
            if (!world.TryGetMagic(magic, out ClientUserMagic known) ||
                known?.Info == null || known.ItemRequired || !Usable(world, magic)) return false;
            return !_cooldowns.TryGetValue(known.InfoIndex, out DateTime until) ||
                   DateTime.UtcNow >= until;
        }

        /// <summary>
        /// Does this character spend mana on ORDINARY SWINGS?
        ///
        /// The mana-drinking branch was gated on Spells.HasCastable, which asks whether there are
        /// spells to cast. A warrior has none, so it never drank mana - and then could not afford
        /// the attack skills that DO cost it. Mirbot sat at 2 of 117 mana holding nineteen Mana
        /// Potion (II), requesting Half Moon it could not pay for, and the server threw every
        /// attack away. The mana gate and the attack-skill gate were each half right.
        ///
        /// Asked of the known magics rather than the armed set, because the server disarms a skill
        /// once it is unaffordable - so consulting _armed would answer "no mana needed" exactly
        /// when mana is what is missing.
        /// </summary>
        public bool NeedsMana(WorldModel world)
        {
            foreach (ClientUserMagic known in world.Magics)
            {
                if (known?.Info == null) continue;
                if (!Nameable.Contains(known.Info.Magic)) continue;
                if (world.Level < known.Info.NeedLevel1) continue;

                if (known.Cost > 0) return true;
            }

            return false;
        }

        /// <summary>What one use of this skill costs, or 0 if we somehow do not know it.</summary>
        private static int CostOf(WorldModel world, MagicType magic)
        {
            foreach (ClientUserMagic known in world.Magics)
                if (known?.Info != null && known.Info.Magic == magic) return known.Cost;

            return 0;
        }
    }
}
