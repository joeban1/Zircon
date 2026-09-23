using System;
using System.Collections.Generic;

namespace MirBot
{
    public static class WeightedChoice
    {
        /// <summary>Draw from nonnegative scores after max-normalisation and exponentiation.</summary>
        public static int Pick(IReadOnlyList<double> weights, double power, Random random,
            out double chance)
        {
            chance = 0;
            if (weights == null || weights.Count == 0) return -1;
            if (weights.Count == 1) { chance = 1; return 0; }
            if (random == null) throw new ArgumentNullException(nameof(random));

            double max = 0;
            foreach (double weight in weights)
                if (double.IsFinite(weight) && weight > max) max = weight;

            if (power <= 0 || !double.IsFinite(power) || max <= 0)
            {
                chance = 1.0 / weights.Count;
                return random.Next(weights.Count);
            }

            double[] scaled = new double[weights.Count];
            double total = 0;
            for (int i = 0; i < weights.Count; i++)
            {
                double weight = weights[i];
                if (!double.IsFinite(weight) || weight <= 0) continue;
                scaled[i] = Math.Pow(weight / max, power);
                total += scaled[i];
            }
            if (total <= 0)
            {
                chance = 1.0 / weights.Count;
                return random.Next(weights.Count);
            }

            double draw = random.NextDouble() * total;
            for (int i = 0; i < scaled.Length; i++)
            {
                draw -= scaled[i];
                if (draw < 0 && scaled[i] > 0)
                { chance = scaled[i] / total; return i; }
            }
            for (int i = scaled.Length - 1; i >= 0; i--)
                if (scaled[i] > 0) { chance = scaled[i] / total; return i; }
            chance = 1.0 / weights.Count;
            return random.Next(weights.Count);
        }
    }
}
