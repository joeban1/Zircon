using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Library;

namespace MirBot
{
    public enum AoeShape { Square1, Square3, Plus, Ray, Cone, Meteor }

    /// <summary>Server-matched opportunity footprints. A cell in a footprint is not a promise of damage.</summary>
    internal static class AoeGeometry
    {
        internal static readonly IReadOnlyDictionary<MagicType, AoeShape> Shapes =
            new Dictionary<MagicType, AoeShape>
            {
                [MagicType.IceStorm] = AoeShape.Square1,
                [MagicType.FireStorm] = AoeShape.Square1,
                [MagicType.FireWall] = AoeShape.Plus,
                [MagicType.IceRain] = AoeShape.Square3,
                [MagicType.Asteroid] = AoeShape.Square3,
                [MagicType.Tempest] = AoeShape.Square1,
                [MagicType.FrozenEarth] = AoeShape.Ray,
                [MagicType.BlowEarth] = AoeShape.Ray,
                [MagicType.LightningBeam] = AoeShape.Ray,
                [MagicType.ScortchedEarth] = AoeShape.Ray,
                [MagicType.GreaterFrozenEarth] = AoeShape.Cone,
                [MagicType.MeteorShower] = AoeShape.Meteor
            };

        internal static bool Directional(AoeShape shape) =>
            shape == AoeShape.Ray || shape == AoeShape.Cone;

        internal static bool Persistent(MagicType type) =>
            type == MagicType.FireWall || type == MagicType.Tempest;

        internal static bool SubstantiallyOverlaps(HashSet<Point> next, HashSet<Point> existing) =>
            next.Count > 0 && existing.Count > 0 &&
            // Two cells of a five-cell Fire Wall are already a meaningful replacement.
            next.Count(p => existing.Contains(p)) * 3 >= Math.Min(next.Count, existing.Count);

        internal static HashSet<Point> Footprint(AoeShape shape, Point caster, Point aim,
            MirDirection direction, int width, int height, Func<Point, bool> exists = null)
        {
            var cells = new HashSet<Point>();
            void Add(Point p)
            {
                if (p.X >= 0 && p.Y >= 0 && p.X < width && p.Y < height &&
                    (exists == null || exists(p))) cells.Add(p);
            }

            switch (shape)
            {
                case AoeShape.Square1:
                case AoeShape.Square3:
                case AoeShape.Meteor:
                    int radius = shape == AoeShape.Square1 ? 1 : 3;
                    for (int x = aim.X - radius; x <= aim.X + radius; x++)
                        for (int y = aim.Y - radius; y <= aim.Y + radius; y++)
                            Add(new Point(x, y));
                    break;
                case AoeShape.Plus:
                    Add(aim);
                    for (int d = 0; d < 8; d += 2)
                        Add(WorldModel.Step(aim, (MirDirection)d));
                    break;
                case AoeShape.Ray:
                case AoeShape.Cone:
                    for (int shift = shape == AoeShape.Cone ? -1 : 0;
                         shift <= (shape == AoeShape.Cone ? 1 : 0); shift++)
                    {
                        MirDirection ray = (MirDirection)(((int)direction + shift + 8) % 8);
                        Point at = caster;
                        for (int i = 1; i <= 8; i++)
                        {
                            at = WorldModel.Step(at, ray);
                            Add(at);
                            int flank = (int)ray % 2 == 0 ? 2 : 1;
                            Add(WorldModel.Step(at, (MirDirection)(((int)ray + flank) % 8)));
                            Add(WorldModel.Step(at, (MirDirection)(((int)ray - flank + 8) % 8)));
                        }
                    }
                    break;
            }
            return cells;
        }
    }
}
