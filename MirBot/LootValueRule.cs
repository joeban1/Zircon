using System;

namespace MirBot
{
    /// <summary>
    /// How much a sellable drop has to be worth before it earns its place in the bag.
    ///
    /// Bag weight, not gold, is the scarce resource here: weight is what forces the walk back to
    /// town, so the question about a piece of cargo is never "is this worth anything" but "is this
    /// worth the trip". Value is therefore measured per unit of weight, never in absolute gold.
    ///
    /// The bar slides with wealth. A character with nothing needs gold more than it needs time and
    /// takes almost anything that sells; a rich one only stoops for the good stuff. Between the two
    /// the threshold interpolates, and outside them it clamps, so there is no amount of gold at
    /// which the bot either hoovers up literal junk or walks past a windfall.
    /// </summary>
    public sealed class LootValueRule
    {
        /// <summary>At or below this much gold, the bar sits at its lowest.</summary>
        public long PoorGold = 5000;

        /// <summary>At or above this much gold, the bar sits at its highest.</summary>
        public long RichGold = 200000;

        public int PerWeightWhenPoor = 5;
        public int PerWeightWhenRich = 80;

        /// <summary>
        /// Multiplier applied once the bag is heavy. Space is then contested by potions and real
        /// upgrades, so cargo has to be considerably better to justify taking it.
        /// </summary>
        public int HeavyMultiplier = 4;

        /// <summary>Gold per unit of weight a drop must reach to be worth carrying.</summary>
        public int MinimumPerWeight(long gold, bool heavy)
        {
            int bar;

            if (gold <= PoorGold)
                bar = PerWeightWhenPoor;
            else if (gold >= RichGold)
                bar = PerWeightWhenRich;
            else
            {
                long span = RichGold - PoorGold;

                // Defensive: a config with the two bounds crossed would otherwise divide by zero.
                if (span <= 0)
                    bar = PerWeightWhenRich;
                else
                {
                    double progress = (gold - PoorGold) / (double)span;
                    bar = (int)Math.Round(PerWeightWhenPoor +
                                          progress * (PerWeightWhenRich - PerWeightWhenPoor));
                }
            }

            if (heavy) bar *= Math.Max(1, HeavyMultiplier);

            return Math.Max(1, bar);
        }
    }
}
