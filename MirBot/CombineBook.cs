using System;
using System.Collections.Generic;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    /// <summary>One NPC combination: pieces + gold in, a chance at one item out.</summary>
    public sealed class CombineRecipe
    {
        public NPCInfo Npc { get; init; }
        public MapInfo Map => Npc?.Region?.Map;

        /// <summary>The page whose checks and actions take the pieces and gold.</summary>
        public NPCPage RequestPage { get; init; }

        /// <summary>Buttons to press from the NPC's entry page to reach RequestPage, in order.</summary>
        public IReadOnlyList<int> ButtonPath { get; init; }

        /// <summary>The page each press of ButtonPath leads to (the last is RequestPage).</summary>
        public IReadOnlyList<int> PagePath { get; init; }

        public IReadOnlyList<(ItemInfo Item, long Count)> Inputs { get; init; }
        public long Gold { get; init; }
        public ItemInfo Output { get; init; }

        /// <summary>1 in this many succeed; 1 = always.</summary>
        public int ChanceOneIn { get; init; } = 1;

        public string Name => Output?.ItemName ?? "?";

        public bool Uses(ItemInfo info) => info != null && Inputs.Any(i => i.Item?.Index == info.Index);

        public override string ToString() =>
            $"{Name}: {string.Join(" + ", Inputs.Select(i => (i.Count > 1 ? $"{i.Count} x " : "") + i.Item?.ItemName))}" +
            (Gold > 0 ? $" + {Gold:N0} gold" : "") + (ChanceOneIn > 1 ? $", 1 in {ChanceOneIn}" : "");
    }

    /// <summary>
    /// NPC combinations the bots take pieces to - built once from System.db.
    ///
    /// Payton (Numa Village) turns Rusty accessories into the real thing (1 Rusty + 1,000,000 gold)
    /// and a Rusty + Cracked + Worn (+ Scratched for bracelets) set into a lair accessory
    /// (+ 2,000,000). The request page's checks and TakeItem/TakeGold run first; its SuccessPage
    /// then rolls Random(10) Equal 0 and runs GiveItemExperience - so the pieces and gold are gone
    /// either way, and one press in ten yields the item. Found from the page data, not hard-coded.
    /// </summary>
    public sealed class CombineBook
    {
        private readonly List<CombineRecipe> _recipes = new List<CombineRecipe>();
        private readonly HashSet<int> _pieces = new HashSet<int>();
        private readonly HashSet<int> _outputs = new HashSet<int>();

        public IReadOnlyList<CombineRecipe> Recipes => _recipes;
        public List<string> Report { get; } = new List<string>();

        public bool IsPiece(ItemInfo info) => info != null && _pieces.Contains(info.Index);
        public bool IsOutput(ItemInfo info) => info != null && _outputs.Contains(info.Index);

        /// <param name="npcNames">Comma-separated NPC names to read recipes from (CombineNPCs).</param>
        public void Build(string npcNames) =>
            Build(npcNames, Globals.NPCInfoList?.Binding ?? Enumerable.Empty<NPCInfo>());

        public void Build(string npcNames, IEnumerable<NPCInfo> npcs)
        {
            _recipes.Clear();
            _pieces.Clear();
            _outputs.Clear();
            Report.Clear();

            HashSet<string> wanted = new HashSet<string>(
                (npcNames ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (NPCInfo npc in npcs)
                {
                    if (npc?.EntryPage == null || !wanted.Contains(npc.NPCName ?? "")) continue;
                    Walk(npc);
                }

                foreach (CombineRecipe recipe in _recipes)
                {
                    foreach ((ItemInfo item, long _) in recipe.Inputs) _pieces.Add(item.Index);
                    _outputs.Add(recipe.Output.Index);
                }

                foreach (IGrouping<NPCInfo, CombineRecipe> npc in _recipes.GroupBy(r => r.Npc))
                    Report.Add($"{npc.Count()} recipe(s) at {npc.Key.NPCName} ({npc.Key.Region?.Map?.Description ?? "?"})");
                foreach (CombineRecipe recipe in _recipes)
                    Report.Add($"  {recipe} - buttons {string.Join(" ", recipe.ButtonPath)}");
                if (_recipes.Count == 0)
                    Report.Add(wanted.Count == 0 ? "no CombineNPCs configured" : "no recipes found at " + string.Join(", ", wanted));
            }
            catch (Exception ex)
            {
                _recipes.Clear();
                _pieces.Clear();
                _outputs.Clear();
                Report.Add("combination data did not load: " + ex.Message);
            }
        }

        /// <summary>Breadth-first from the entry page, following buttons, remembering the path.</summary>
        private void Walk(NPCInfo npc)
        {
            var queue = new Queue<(NPCPage Page, List<int> Buttons, List<int> Pages)>();
            var seen = new HashSet<int> { npc.EntryPage.Index };
            queue.Enqueue((npc.EntryPage, new List<int>(), new List<int>()));

            while (queue.Count > 0)
            {
                (NPCPage page, List<int> buttons, List<int> pages) = queue.Dequeue();

                CombineRecipe recipe = AsRecipe(npc, page, buttons, pages);
                if (recipe != null)
                {
                    if (_recipes.All(r => r.RequestPage.Index != page.Index)) _recipes.Add(recipe);
                    continue;
                }

                if (buttons.Count >= 6 || page.Buttons == null) continue;

                foreach (NPCButton button in page.Buttons)
                {
                    NPCPage next = button?.DestinationPage;
                    if (next == null || !seen.Add(next.Index)) continue;
                    queue.Enqueue((next, new List<int>(buttons) { button.ButtonID },
                        new List<int>(pages) { next.Index }));
                }
            }
        }

        private static CombineRecipe AsRecipe(NPCInfo npc, NPCPage page, List<int> buttons, List<int> pages)
        {
            if (buttons.Count == 0 || page?.Actions == null) return null;

            List<(ItemInfo, long)> inputs = page.Actions
                .Where(a => a?.ActionType == NPCActionType.TakeItem && a.ItemParameter1 != null)
                .Select(a => (a.ItemParameter1, (long)Math.Max(1, a.IntParameter1)))
                .ToList();
            if (inputs.Count == 0) return null;

            NPCPage success = page.SuccessPage;
            NPCAction give = success?.Actions?.FirstOrDefault(a =>
                (a?.ActionType == NPCActionType.GiveItem || a?.ActionType == NPCActionType.GiveItemExperience) &&
                a.ItemParameter1 != null)
                ?? page.Actions.FirstOrDefault(a =>
                (a?.ActionType == NPCActionType.GiveItem || a?.ActionType == NPCActionType.GiveItemExperience) &&
                a.ItemParameter1 != null);
            if (give == null) return null;

            NPCCheck roll = success?.Checks?.FirstOrDefault(c =>
                c?.CheckType == NPCCheckType.Random && c.Operator == Operator.Equal && c.IntParameter1 > 1 &&
                c.IntParameter2 >= 0 && c.IntParameter2 < c.IntParameter1);

            long gold = page.Actions.Where(a => a?.ActionType == NPCActionType.TakeGold)
                .Sum(a => (long)a.IntParameter1);

            return new CombineRecipe
            {
                Npc = npc,
                RequestPage = page,
                ButtonPath = buttons,
                PagePath = pages,
                Inputs = inputs,
                Gold = gold,
                Output = give.ItemParameter1,
                ChanceOneIn = roll?.IntParameter1 ?? 1
            };
        }

        /// <summary>How many of this item we hold: the bag, and optionally the bank too.</summary>
        public static long Held(Backpack bag, ItemInfo info, bool includeBank)
        {
            if (bag == null || info == null) return 0;

            long count = bag.Carried.Where(p => p.Value?.Info?.Index == info.Index).Sum(p => Math.Max(1, p.Value.Count));
            if (includeBank)
                count += bag.Stored.Where(p => p.Value?.Info?.Index == info.Index).Sum(p => Math.Max(1, p.Value.Count));
            return count;
        }

        /// <summary>
        /// The first recipe whose every piece we hold (bag + bank, or the bag alone) and whose fee
        /// we can pay on top of the reserve. Null when none.
        /// </summary>
        public CombineRecipe Ready(Backpack bag, long gold, long reserve, bool bagOnly)
        {
            foreach (CombineRecipe recipe in _recipes)
            {
                if (gold < recipe.Gold + reserve) continue;
                if (recipe.Inputs.All(i => Held(bag, i.Item, !bagOnly) >= i.Count)) return recipe;
            }

            return null;
        }

        /// <summary>"Seal Of Overlord - Rusty ✓ Cracked ✓ Worn ✗" for every recipe with a piece held.</summary>
        public string Progress(Backpack bag)
        {
            List<string> lines = new List<string>();

            foreach (CombineRecipe recipe in _recipes)
            {
                var have = recipe.Inputs.Select(i => (i.Item, Got: Held(bag, i.Item, true) >= i.Count)).ToList();
                if (!have.Any(h => h.Got)) continue;

                lines.Add($"{recipe.Name} - " + string.Join(" ", have.Select(h =>
                    $"{Short(h.Item, recipe.Output)} {(h.Got ? "✓" : "✗")}")));
            }

            return string.Join("; ", lines);
        }

        /// <summary>"Rusty" from "Rusty Seal Of Overlord" when the rest is the output's name.</summary>
        private static string Short(ItemInfo piece, ItemInfo output)
        {
            string name = piece?.ItemName ?? "?";
            string tail = output?.ItemName ?? "";
            int at = tail.Length > 0 ? name.IndexOf(tail.Split(' ').Last(), StringComparison.Ordinal) : -1;
            return at > 0 ? name.Split(' ')[0] : name;
        }
    }
}
