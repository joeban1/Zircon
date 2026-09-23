namespace MirBot
{
    /// <summary>Keep shopping towns available for errands and recovery, not routine high-level hunting.</summary>
    public static class HuntingTownPolicy
    {
        public static bool Defer(bool shoppingTown, int medianMonsterLevel, int characterLevel,
            int levelGap, bool poor, bool recovery, bool suppliesWantedDropOnlyBook) =>
            levelGap > 0 && shoppingTown && medianMonsterLevel > 0 &&
            characterLevel >= medianMonsterLevel + levelGap &&
            !poor && !recovery && !suppliesWantedDropOnlyBook;
    }
}
