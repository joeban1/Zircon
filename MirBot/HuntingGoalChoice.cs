using System;

namespace MirBot
{
    /// <summary>Choose the hunting goal before weighted map ranking can erase alternatives.</summary>
    public static class HuntingGoalChoice
    {
        public static bool PursueBook(bool hasBookMap, bool hasOrdinaryMap, bool lossWatch,
            int consecutiveBookHunts, int normalChancePercent, int lossChancePercent,
            int maxConsecutiveBookHunts, Random random, out int chancePercent)
        {
            if (!hasBookMap) { chancePercent = 0; return false; }
            if (!hasOrdinaryMap) { chancePercent = 100; return true; }

            chancePercent = consecutiveBookHunts >= Math.Max(1, maxConsecutiveBookHunts)
                ? 0
                : Math.Clamp(lossWatch ? lossChancePercent : normalChancePercent, 0, 100);
            return chancePercent > 0 && random.Next(100) < chancePercent;
        }
    }
}
