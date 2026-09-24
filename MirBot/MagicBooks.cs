using System;
using System.Collections.Generic;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    public enum BookVerdict
    {
        NotABook,
        Junk,           // no magic, or a school the bot cannot use
        WrongClass,     // another class's skill - sell
        AlreadyKnown,   // learnt; a further copy is refused below skill level 3 - sell
        TooEarly,       // right class, level requirement not met yet - bank it
        Wanted          // learn it now
    }

    /// <summary>
    /// What to do with a skill book.
    ///
    /// A book's ItemInfo.Shape is the MagicInfo.Index (PlayerObject.cs:7116). Two gates matter and
    /// they live in different places:
    ///
    /// - CLASS. ItemInfo.RequiredClass defaults to RequiredClass.All (ItemInfo.cs:469) and content
    ///   authors often leave it there, so it does NOT reliably identify whose book this is. The
    ///   semantically correct check is MagicInfo.MatchesClass, which is what the real client's own
    ///   skill list uses (MagicDialog.cs:150). Both are tested here: the server enforces the former,
    ///   the latter is what is actually true.
    ///
    /// - LEVEL. The gate is on the ITEM, via RequiredType/RequiredAmount, checked for every item
    ///   type by CanUseItem (PlayerObject.cs:7313) which ItemUse calls before the book branch. So a
    ///   book requiring level 7 genuinely cannot be learnt before level 7. MagicInfo.NeedLevel1 is a
    ///   different thing that gates CASTING, and is used here only to avoid buying a skill that
    ///   could be learnt but not yet cast.
    /// </summary>
    public sealed class MagicBooks
    {
        private readonly Dictionary<int, MagicInfo> _byShape = new Dictionary<int, MagicInfo>();

        public int Count => _byShape.Count;

        /// <summary>Built once at startup; DisposableSlots runs this on every trip decision.</summary>
        public void Build()
        {
            _byShape.Clear();

            try
            {
                foreach (MagicInfo magic in Globals.MagicInfoList?.Binding ?? Enumerable.Empty<MagicInfo>())
                    _byShape[magic.Index] = magic;
            }
            catch
            {
                // Pre-login, or a database that did not load. Judge() degrades to Junk, which is
                // safe: books are sold rather than hoarded.
            }
        }

        public MagicInfo For(ItemInfo info)
        {
            if (info == null || info.ItemType != ItemType.Book) return null;

            return _byShape.TryGetValue(info.Shape, out MagicInfo magic) ? magic : null;
        }

        public BookVerdict Judge(ClientUserItem item, MirClass mirClass, int level, Stats stats,
            WorldModel world)
        {
            if (item?.Info == null || item.Info.ItemType != ItemType.Book) return BookVerdict.NotABook;

            MagicInfo magic = For(item.Info);

            if (magic == null ||
                magic.School == MagicSchool.None ||
                magic.School == MagicSchool.Discipline)
                return BookVerdict.Junk;

            // Both class gates - see the class note above.
            if (!magic.MatchesClass(mirClass)) return BookVerdict.WrongClass;
            if (!Backpack.CanClassUseInfo(item.Info, mirClass)) return BookVerdict.WrongClass;

            // Known already. A further copy is how a skill reaches level 4: at skill level 3 each
            // read rolls the book's learn chance and a success adds its learn % as pages, 500 pages
            // for level 4 (PlayerObject ItemType.Book). The server refuses the book below level 3,
            // at level 4, and at ANY level for a vendor copy - bought books are NonRefinable
            // (CanUseItem) - so only a dropped copy of a level 3 skill is worth reading.
            if (world != null && world.Knows(magic.Index))
            {
                bool bought = (item.Flags & UserItemFlags.NonRefinable) == UserItemFlags.NonRefinable;
                return !bought && world.Trainable(magic.Index) &&
                       Backpack.MeetsRequirement(item.Info, level, stats)
                    ? BookVerdict.Wanted
                    : BookVerdict.AlreadyKnown;
            }

            // The item's own level/stat requirement - the real "can I learn this" gate.
            if (!Backpack.MeetsRequirement(item.Info, level, stats)) return BookVerdict.TooEarly;

            return BookVerdict.Wanted;
        }

        /// <summary>
        /// Magic indexes worth buying a book for: right class, castable at this level, not known.
        /// Mirrors the server's own admin GiveSkills predicate, which is the closest thing to an
        /// authoritative "should this character have this skill" test.
        /// </summary>
        public IEnumerable<int> WantedMagicIndexes(MirClass mirClass, int level, WorldModel world)
        {
            foreach (KeyValuePair<int, MagicInfo> pair in _byShape)
            {
                MagicInfo magic = pair.Value;

                if (magic.School == MagicSchool.None || magic.School == MagicSchool.Discipline) continue;
                if (!magic.MatchesClass(mirClass)) continue;
                if (magic.NeedLevel1 > level) continue;
                if (world != null && world.Knows(magic.Index)) continue;

                yield return magic.Index;
            }
        }
    }
}
