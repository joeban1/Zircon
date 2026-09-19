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
            MagicType.Slaying, MagicType.Thrusting, MagicType.HalfMoon, MagicType.DestructiveSurge,
            MagicType.FlamingSword, MagicType.DragonRise, MagicType.BladeStorm,
            MagicType.DefensiveBlow, MagicType.OffensiveBlow
        };

        /// <summary>
        /// Toggles that stay on once set (C.MagicToggle with CanUse = true). The server then arms
        /// them per swing. The one-shot kind - FlamingSword, DragonRise, BladeStorm, DefensiveBlow,
        /// OffensiveBlow - cost mana per arm and are left alone for now.
        /// </summary>
        private static readonly MagicType[] Sustained =
        {
            MagicType.Thrusting, MagicType.HalfMoon, MagicType.DestructiveSurge, MagicType.FlameSplash
        };

        private readonly HashSet<MagicType> _armed = new HashSet<MagicType>();
        private readonly HashSet<MagicType> _enabled = new HashSet<MagicType>();

        public int ArmedCount => _armed.Count;

        /// <summary>S.MagicToggle: the server arming or spending a skill.</summary>
        public void Toggled(MagicType magic, bool canUse)
        {
            if (canUse) _armed.Add(magic);
            else _armed.Remove(magic);
        }

        public bool IsArmed(MagicType magic) => _armed.Contains(magic);

        /// <summary>Everything resets on a reconnect - the server's arming state does not survive.</summary>
        public void Reset()
        {
            _armed.Clear();
            _enabled.Clear();
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

            foreach (MagicType magic in _armed)
            {
                if (magic == MagicType.Thrusting) continue;   // handled above, on its own terms
                if (!Usable(world, magic)) continue;

                return magic;
            }

            return MagicType.None;
        }

        private static bool Usable(WorldModel world, MagicType magic) =>
            Nameable.Contains(magic) && world.CanUseMagic(magic);
    }
}
