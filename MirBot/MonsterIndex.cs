using System;
using System.Collections.Generic;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    /// <summary>
    /// Monsters by index, built once and shared read-only across bots.
    ///
    /// Without it, every S.ObjectMonster packet triggered a linear scan of the whole
    /// MonsterInfoList. That ran on the socket callback thread, for every monster that came into
    /// view, multiplied by the number of bots - the kind of cost that later gets blamed on the
    /// status page.
    ///
    /// Note the equivalent scan inside LibraryCore cannot be fixed from here:
    /// ClientUserItem.Complete() scans ItemInfoList per item during deserialization. That is the
    /// game's shared library, not ours.
    /// </summary>
    public sealed class MonsterIndex
    {
        private readonly Dictionary<int, MonsterInfo> _byIndex = new Dictionary<int, MonsterInfo>();

        public int Count => _byIndex.Count;

        public void Build()
        {
            _byIndex.Clear();

            try
            {
                foreach (MonsterInfo monster in Globals.MonsterInfoList?.Binding
                                                ?? Enumerable.Empty<MonsterInfo>())
                    _byIndex[monster.Index] = monster;
            }
            catch
            {
                // Pre-login or a database that did not load. Find() then returns null, which the
                // callers already handle by falling back to the name "monster".
            }
        }

        public MonsterInfo Find(int index) =>
            _byIndex.TryGetValue(index, out MonsterInfo monster) ? monster : null;
    }
}
