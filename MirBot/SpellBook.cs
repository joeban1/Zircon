using System;
using System.Collections.Generic;
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
            MagicType.EvilSlayer, MagicType.GreaterEvilSlayer, MagicType.PoisonDust,
            MagicType.SearingLight, MagicType.BrainStorm,

            // Assassin
            MagicType.Hemorrhage, MagicType.FlamingDaggers, MagicType.Shredding
        };

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
                if (magic?.Info == null || !Castable.Contains(magic.Info.Magic)) continue;
                if (magic.ItemRequired) continue;

                names.Add(world.Level >= magic.Info.NeedLevel1
                    ? $"{magic.Info.Name} ({magic.Cost} mana)"
                    : $"{magic.Info.Name} (needs level {magic.Info.NeedLevel1})");
            }

            return names.Count == 0
                ? $"no castable spells among {world.KnownMagicCount} skills"
                : "can cast " + string.Join(", ", names);
        }

        /// <summary>
        /// Does this character have anything to spend mana ON?
        ///
        /// Asked before drinking a mana potion. A warrior's mana pays for nothing the bot uses, so
        /// pouring potions into it would be burning gold and bag weight for no effect.
        /// </summary>
        public bool HasCastable(WorldModel world)
        {
            foreach (ClientUserMagic magic in world.Magics)
            {
                if (magic?.Info == null || magic.ItemRequired) continue;
                if (!Castable.Contains(magic.Info.Magic)) continue;
                if (world.Level < magic.Info.NeedLevel1) continue;

                return true;
            }

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
            _nextCast = DateTime.MinValue;
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
