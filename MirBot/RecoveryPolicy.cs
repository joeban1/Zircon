using System;

namespace MirBot
{
    /// <summary>
    /// Stateful poverty recovery. Entering and leaving use different thresholds so one sale or
    /// restock cannot bounce a bot between recovery ground and ordinary hunting.
    /// </summary>
    public sealed class RecoveryPolicy
    {
        public bool Active { get; private set; }
        public bool MoneyCaveActive { get; private set; }

        public void Reset()
        {
            Active = false;
            MoneyCaveActive = false;
        }

        /// <summary>
        /// Arm immediately below the entry balance. Once armed, release only after a completed
        /// town trip leaves both the supplies and the larger recovery balance intact.
        /// </summary>
        public bool Update(long gold, long enterGold, long exitGold,
            bool tripJustFinished, bool shortOfSupplies)
        {
            if (enterGold <= 0)
            {
                Active = false;
                MoneyCaveActive = false;
                return false;
            }

            if (gold < enterGold)
            {
                Active = true;
                return true;
            }

            long exit = Math.Max(enterGold, exitGold);

            if (Active && tripJustFinished && !shortOfSupplies && gold >= exit)
                Active = false;

            return Active;
        }

        /// <summary>
        /// Promote as soon as the carried load is ready, but do not bounce back to beginner maps
        /// when one potion takes it below the line. Re-evaluate a downgrade only after shopping.
        /// </summary>
        public bool UpdateMoneyCave(bool supplied, bool tripJustFinished)
        {
            if (!Active)
            {
                MoneyCaveActive = false;
                return false;
            }

            if (supplied)
                MoneyCaveActive = true;
            else if (tripJustFinished)
                MoneyCaveActive = false;

            return MoneyCaveActive;
        }

        /// <summary>
        /// Whether a recovered character can graduate from free beginner mobs to the configured
        /// money cave. Potion load and target are both weights; do not compare either with a count.
        /// </summary>
        public static bool SuppliesReady(int level, int minimumLevel, int supplyPercent,
            int healthLoad, int healthTarget, bool spendsMana, int manaLoad, int manaTarget,
            int scrolls, int scrollReserve)
        {
            if (level < minimumLevel) return false;

            int percent = Math.Clamp(supplyPercent, 0, 100);

            if (!AtLeastPercent(healthLoad, healthTarget, percent)) return false;
            if (spendsMana && !AtLeastPercent(manaLoad, manaTarget, percent)) return false;
            if (scrolls < Math.Max(0, scrollReserve)) return false;

            return true;
        }

        private static bool AtLeastPercent(int load, int target, int percent)
        {
            if (target <= 0) return true;
            return (long)Math.Max(0, load) * 100 >= (long)target * percent;
        }
    }
}
