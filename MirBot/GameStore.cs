using System;
using System.Collections.Generic;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    /// <summary>One thing a class buys from the game store, in the order it buys them.</summary>
    public sealed class StoreWant
    {
        public StoreInfo Store;
        public ItemInfo Item => Store?.Item;
        public int StoreIndex => Store?.Index ?? 0;

        /// <summary>What the purchase costs in Hunt Gold (the server falls back to Price).</summary>
        public int Price => Store == null ? 0 : Store.HuntGoldPrice > 0 ? Store.HuntGoldPrice : Store.Price;

        public bool Permanent;
        public bool IsMark;
        public string Name => Item?.ItemName ?? "?";
    }

    /// <summary>
    /// The game store shopping list, per class. Operator-chosen order: every permanent before any
    /// temporary, bought strictly in sequence - a bot saves up for the next item rather than
    /// skipping ahead to something cheaper.
    ///
    /// Store items are bought with Hunt Gold (C.MarketPlaceStoreBuy, no NPC needed) and arrive
    /// Bound, Locked and Worthless. The tonics are account-level item buffs: a [P] never expires
    /// and cannot be used twice; a [T] counts down outside safe zones and a reuse extends it. A [P]
    /// and a [T] of the same tonic are different items, so the two buffs stack.
    /// </summary>
    public sealed class GameStore
    {
        private static readonly string[] Classes = { "Warrior", "Wizard", "Taoist", "Assassin" };

        private readonly Dictionary<MirClass, List<StoreWant>> _wants = new Dictionary<MirClass, List<StoreWant>>();
        private readonly HashSet<int> _wantedItems = new HashSet<int>();

        public List<string> Report { get; } = new List<string>();

        private static string ClassTonic(MirClass c) =>
            c == MirClass.Wizard ? "Nature" : c == MirClass.Taoist ? "Spirit" : "Destruction";

        /// <summary>The shopping list for a class, by item name, in buying order.</summary>
        public static List<(string Name, bool Permanent, bool Mark)> Plan(MirClass c)
        {
            string tonic = ClassTonic(c);
            bool melee = c == MirClass.Warrior || c == MirClass.Assassin;

            var plan = new List<(string, bool, bool)>
            {
                ("Mir Package [P]", true, false),
                ($"Tonic Of {tonic} [P]", true, false),
                ($"Mark Of {tonic} [P]", true, true),
            };

            if (melee) plan.Add(("Tonic Of Velocity [P]", true, false));
            plan.Add(("Tonic Of Life [P]", true, false));
            plan.Add(("Tonic Of Mana [P]", true, false));
            if (melee) plan.Add(("Tonic Of Dexterity [P]", true, false));
            plan.Add(("Tonic Of Experience [P]", true, false));
            plan.Add(("Tonic Of Knowledge [P]", true, false));
            plan.Add(("Tonic Of Spelunking [P]", true, false));
            plan.Add(("Tonic Of Treasure [P]", true, false));
            plan.Add(("Tonic Of Wealth [P]", true, false));

            plan.Add(("Mir Package [T]", false, false));
            plan.Add(($"Tonic Of {tonic} [T]", false, false));
            if (melee) plan.Add(("Tonic Of Velocity [T]", false, false));
            plan.Add(("Tonic Of Life [T]", false, false));
            plan.Add(("Tonic Of Mana [T]", false, false));
            plan.Add(("Tonic Of Experience [T]", false, false));

            return plan;
        }

        public void Build(IEnumerable<StoreInfo> rows)
        {
            _wants.Clear();
            _wantedItems.Clear();
            Report.Clear();

            List<StoreInfo> available = (rows ?? Enumerable.Empty<StoreInfo>())
                .Where(x => x?.Item != null && x.Available)
                .OrderBy(x => x.Index)
                .ToList();

            foreach (MirClass c in new[] { MirClass.Warrior, MirClass.Wizard, MirClass.Taoist, MirClass.Assassin })
            {
                List<StoreWant> list = new List<StoreWant>();

                foreach ((string name, bool permanent, bool mark) in Plan(c))
                {
                    StoreInfo row = available.FirstOrDefault(x =>
                        string.Equals(x.Item.ItemName, name, StringComparison.OrdinalIgnoreCase));

                    if (row == null)
                    {
                        Report.Add($"{c}: '{name}' is not for sale - skipped.");
                        continue;
                    }

                    if (!FilterAllows(row.Filter, c))
                    {
                        Report.Add($"{c}: '{name}' is not listed for {c} - skipped.");
                        continue;
                    }

                    list.Add(new StoreWant { Store = row, Permanent = permanent, IsMark = mark });
                    _wantedItems.Add(row.Item.Index);
                }

                _wants[c] = list;
                Report.Add($"{c}: {string.Join(", ", list.Select(w => $"{w.Name} ({w.Price})"))}");
            }
        }

        /// <summary>
        /// Class tags live only in the store's Filter text ("Torch, Permanent, Warrior, Assassin").
        /// A row naming no class at all is open to everyone.
        /// </summary>
        public static bool FilterAllows(string filter, MirClass c)
        {
            string[] tags = (filter ?? "").Split(',').Select(x => x.Trim()).ToArray();
            if (!tags.Any(t => Classes.Contains(t, StringComparer.OrdinalIgnoreCase))) return true;
            return tags.Contains(c.ToString(), StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<StoreWant> WantsFor(MirClass c) =>
            _wants.TryGetValue(c, out List<StoreWant> list) ? list : (IReadOnlyList<StoreWant>)Array.Empty<StoreWant>();

        /// <summary>Is this item one the store plan buys (any class)?</summary>
        public bool IsStoreItem(ItemInfo info) => info != null && _wantedItems.Contains(info.Index);

        /// <summary>The want for an item, for this class, or null.</summary>
        public StoreWant WantFor(MirClass c, ItemInfo info) =>
            info == null ? null : WantsFor(c).FirstOrDefault(w => w.Item?.Index == info.Index);

        /// <summary>
        /// The first thing on the list not yet satisfied, or null when everything is. Returned even
        /// when it cannot be afforded: the caller saves up for it rather than skipping ahead.
        /// </summary>
        public StoreWant Next(MirClass c, Func<StoreWant, bool> satisfied)
        {
            IReadOnlyList<StoreWant> list = WantsFor(c);

            foreach (StoreWant want in list.Where(w => w.Permanent))
                if (!satisfied(want)) return want;

            foreach (StoreWant want in list.Where(w => !w.Permanent))
                if (!satisfied(want)) return want;

            return null;
        }

        /// <summary>
        /// Whether a want is already covered, from the bot's own state:
        /// a permanent buff that is running, or held waiting to be used; a mark worn or held; a
        /// temporary held, or running with more than the rebuy window left.
        /// </summary>
        public static bool Satisfied(StoreWant want, WorldModel world, Backpack items, TimeSpan rebuyWindow)
        {
            int index = want.Item?.Index ?? -1;
            if (index < 0) return true;

            bool Holds(IEnumerable<KeyValuePair<int, ClientUserItem>> grid) =>
                grid.Any(p => p.Value?.Info != null && p.Value.Info.Index == index && p.Value.Count > 0);

            if (Holds(items.Carried)) return true;

            if (want.IsMark)
                return Holds(items.Worn) || Holds(items.Stored);

            if (want.Permanent)
                return world.HasItemBuff(index) || Holds(items.Stored);

            TimeSpan? left = ItemBuffRemaining(world, index, out bool running);
            if (!running) return false;
            return left == null || left.Value > rebuyWindow;
        }

        /// <summary>Time left on the item buff from this item; null = permanent. running=false when absent.</summary>
        public static TimeSpan? ItemBuffRemaining(WorldModel world, int itemIndex, out bool running)
        {
            foreach (ClientBuffInfo buff in world.Buffs)
            {
                if (buff.Type != BuffType.ItemBuff || buff.ItemIndex != itemIndex) continue;
                running = true;
                return world.BuffRemaining(buff);
            }

            running = false;
            return null;
        }
    }

    /// <summary>
    /// One bot's purchases in flight. The server's reply to a store buy is an empty packet sent
    /// BEFORE it validates anything, so it proves nothing; a refusal is only a system chat line.
    /// The purchase is confirmed by Hunt Gold dropping by the price, and presumed refused if that
    /// has not happened within a few seconds - then that item is left alone for a while.
    /// </summary>
    public sealed class StoreShopper
    {
        public static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan RefusalBackoff = TimeSpan.FromMinutes(10);

        /// <summary>After a confirmed buy, the item may land a moment after the currency change.</summary>
        public static readonly TimeSpan ArrivalGrace = TimeSpan.FromSeconds(15);

        private StoreWant _pending;
        private long _huntGoldBefore;
        private DateTime _sentAt;
        private string _lastServerLine;
        private DateTime _lastServerLineAt = DateTime.MinValue;

        private readonly Dictionary<int, DateTime> _backoff = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _boughtAt = new Dictionary<int, DateTime>();

        public Action<string> Log;

        /// <summary>A purchase confirmed: (want, Hunt Gold left).</summary>
        public Action<StoreWant, long> OnBought;

        /// <summary>What the bot is buying or saving for next, for the status page.</summary>
        public StoreWant NextWant
        {
            get => _next;
            set { _next = value; Evaluated = true; }
        }
        private StoreWant _next;

        /// <summary>The list has been checked at least once since login.</summary>
        public bool Evaluated { get; private set; }

        public bool Pending => _pending != null;

        public void NoteServerLine(string text, DateTime? at = null)
        {
            _lastServerLine = text;
            _lastServerLineAt = at ?? DateTime.UtcNow;
        }

        public void NoteSent(StoreWant want, long huntGold, DateTime now)
        {
            _pending = want;
            _huntGoldBefore = huntGold;
            _sentAt = now;
        }

        public bool InBackoff(StoreWant want, DateTime now) =>
            _backoff.TryGetValue(want.StoreIndex, out DateTime until) && now < until;

        /// <summary>Bought moments ago: the item may not be in the bag yet, so do not buy another.</summary>
        public bool JustBought(StoreWant want, DateTime now) =>
            _boughtAt.TryGetValue(want.StoreIndex, out DateTime at) && now - at < ArrivalGrace;

        /// <summary>Settle the pending purchase from the current Hunt Gold. Call every tick.</summary>
        public void Update(long huntGold, DateTime now)
        {
            if (_pending == null) return;

            if (huntGold <= _huntGoldBefore - _pending.Price)
            {
                _boughtAt[_pending.StoreIndex] = now;
                Log?.Invoke($"Store: bought {_pending.Name} for {_pending.Price:N0} Hunt Gold ({huntGold:N0} left).");
                OnBought?.Invoke(_pending, huntGold);
                _pending = null;
                return;
            }

            if (now - _sentAt < ConfirmWindow) return;

            string why = _lastServerLine != null && _lastServerLineAt >= _sentAt
                ? $" - server: {_lastServerLine}"
                : " (bag, funds or not available)";
            Log?.Invoke($"Store: {_pending.Name} was refused{why}; not trying it again for " +
                        $"{RefusalBackoff.TotalMinutes:0} minutes.");
            _backoff[_pending.StoreIndex] = now + RefusalBackoff;
            _pending = null;
        }

        public void Reset()
        {
            _pending = null;
            _next = null;
            Evaluated = false;
        }
    }
}
