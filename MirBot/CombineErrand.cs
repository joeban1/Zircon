using System;
using System.Drawing;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    /// <summary>
    /// A visit to a combination NPC with a full set in the bag: talk, press the recipe's button
    /// path page by page, and judge the answer by the bag.
    ///
    /// The last press runs the request page: HasItem/Gold/CanGainItem checks, then TakeItem and
    /// TakeGold, then the success page's Random roll and GiveItemExperience. Nothing in the reply
    /// says "combined"; the pieces leaving the bag (S.ItemChanged) mean the attempt is COMMITTED -
    /// never press again - and the output appearing means it worked. Pieces gone and no output is
    /// the nine-in-ten "too old and damaged". Nothing changing is a refusal (no room, no gold).
    /// </summary>
    public sealed class CombineErrand
    {
        public enum ErrandPhase { Idle, Talking, Pressed, Committed }

        private static readonly TimeSpan PageWait = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan RefusalWait = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan CommitWait = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan NpcSearch = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan GiveUpCooldown = TimeSpan.FromMinutes(30);
        private const int Tries = 2;

        private readonly BotConfig _config;
        private readonly CombineBook _book;
        private readonly Action<string> _log;

        public ErrandPhase Phase { get; private set; } = ErrandPhase.Idle;
        public bool Active => Phase != ErrandPhase.Idle;
        public bool OwnsMovement => Active;
        public string Status { get; private set; } = "";

        /// <summary>Set when a visit ends; BotInstance consumes it as a travel trigger.</summary>
        public bool JustFinished;

        /// <summary>An attempt finished: (recipe, succeeded).</summary>
        public Action<CombineRecipe, bool> OnCombined;

        private CombineRecipe _recipe;
        private int _mapIndex = -1;
        private DateTime _cooldownUntil = DateTime.MinValue;

        private int _step;                      // next button in the path to press
        private int _lastPage = -1;             // page index most recently shown
        private string _lastPageName = "";
        private bool _pageOpen;
        private DateTime _calledAt = DateTime.MinValue;
        private DateTime _pressedStepAt = DateTime.MinValue;
        private int _calls;
        private DateTime _atNpcSince = DateTime.MinValue;

        private long[] _piecesBefore;
        private long _outputBefore;
        private DateTime _pressedAt;
        private DateTime _committedAt;

        public CombineErrand(BotConfig config, CombineBook book, Action<string> log)
        {
            _config = config;
            _book = book;
            _log = log ?? (_ => { });
        }

        public void PageChanged(NPCPage page)
        {
            if (Phase == ErrandPhase.Idle) return;
            _lastPage = page?.Index ?? -1;
            _lastPageName = page?.Description ?? "";
            if (Phase == ErrandPhase.Talking) _pageOpen = true;
        }

        public void Suspend(string why)
        {
            if (!Active) return;
            _log($"Combine: pausing - {why}.");
            End(cooldown: false);
        }

        private void End(bool cooldown)
        {
            Phase = ErrandPhase.Idle;
            Status = "";
            _recipe = null;
            _step = 0;
            _pageOpen = false;
            _calls = 0;
            _calledAt = DateTime.MinValue;
            _atNpcSince = DateTime.MinValue;
            if (cooldown) _cooldownUntil = DateTime.UtcNow + GiveUpCooldown;
            JustFinished = true;
        }

        /// <summary>A full set in the BAG, affordable, at an NPC on this map.</summary>
        public CombineRecipe Pending(WorldModel world, Backpack bag) =>
            _book?.Ready(bag, world.Gold, 0, bagOnly: true) is CombineRecipe r &&
            r.Map?.Index == world.MapIndex ? r : null;

        public bool HasWork(WorldModel world, Backpack bag) =>
            _config.EnableCombine && _book != null && !world.Dead && DateTime.UtcNow >= _cooldownUntil &&
            Pending(world, bag) != null;

        private static Point NpcPoint(NPCInfo npc) =>
            npc?.Region?.PointRegion != null && npc.Region.PointRegion.Length > 0
                ? npc.Region.PointRegion[0] : Point.Empty;

        public Decision Next(WorldModel world, Backpack bag, DateTime? clock = null)
        {
            DateTime now = clock ?? DateTime.UtcNow;

            if (!Active)
            {
                if (!HasWork(world, bag)) return null;
                _recipe = Pending(world, bag);
                Phase = ErrandPhase.Talking;
                _mapIndex = world.MapIndex;
                _step = 0;
                _pageOpen = false;
                _calls = 0;
                _calledAt = DateTime.MinValue;
                _atNpcSince = DateTime.MinValue;
                _log($"Combine: full {_recipe.Name} set in the bag - going to {_recipe.Npc.NPCName} " +
                     $"({_recipe}).");
            }

            if (world.Dead) { _log("Combine: died - stopping."); End(cooldown: false); return null; }
            if (world.MapIndex != _mapIndex) { _log("Combine: left the map - stopping."); End(cooldown: false); return null; }

            // The last press is in flight: judge it by the bag, never by the page.
            if (Phase == ErrandPhase.Pressed || Phase == ErrandPhase.Committed)
            {
                bool outputArrived = CombineBook.Held(bag, _recipe.Output, false) > _outputBefore;
                bool piecesTaken = _recipe.Inputs.Select((input, i) => CombineBook.Held(bag, input.Item, false) < _piecesBefore[i])
                    .Any(x => x);

                if (outputArrived)
                {
                    _log($"Combine: {_recipe.Name} SUCCEEDED (1 in {_recipe.ChanceOneIn}).");
                    OnCombined?.Invoke(_recipe, true);
                    End(cooldown: false);
                    return null;
                }

                if (piecesTaken)
                {
                    // Committed: the pieces and gold are gone. Pressing again now would be a
                    // "no item" refusal at best; wait for the output or accept the break.
                    if (Phase != ErrandPhase.Committed)
                    {
                        Phase = ErrandPhase.Committed;
                        _committedAt = now;
                    }

                    if (now - _committedAt > CommitWait)
                    {
                        _log($"Combine: {_recipe.Name} failed - the pieces" +
                             (_recipe.Gold > 0 ? $" and {_recipe.Gold:N0} gold" : "") +
                             $" were lost ({_lastPageName}).");
                        OnCombined?.Invoke(_recipe, false);
                        End(cooldown: false);
                        return null;
                    }

                    return new Decision { Action = BotAction.Idle, Reason = "waiting for the combination result" };
                }

                if (now - _pressedAt > RefusalWait)
                {
                    _log($"Combine: {_recipe.Name} was refused ({(_lastPageName.Length > 0 ? _lastPageName : "nothing changed")}); " +
                         $"not trying again for {GiveUpCooldown.TotalMinutes:0} minutes.");
                    End(cooldown: true);
                    return null;
                }

                return new Decision { Action = BotAction.Idle, Reason = "waiting for the combination answer" };
            }

            NPCInfo npc = _recipe.Npc;
            Point spot = NpcPoint(npc);
            int range = Math.Max(1, _config.VendorTalkRange);
            int distance = WorldModel.Distance(world.Location, spot);

            if (distance > range)
            {
                _pageOpen = false;
                _step = 0;
                _atNpcSince = DateTime.MinValue;
                Status = $"combine: walking to {npc.NPCName}";
                return new Decision
                {
                    Action = BotAction.WalkTo,
                    Destination = spot,
                    Reason = $"walking to {npc.NPCName} to combine {_recipe.Name} ({distance} tiles)",
                    Subject = npc.NPCName
                };
            }

            if (_atNpcSince == DateTime.MinValue) _atNpcSince = now;

            WorldObject ob = world.Objects
                .Where(x => x.Kind == ObjectKind.NPC && WorldModel.Distance(x.Location, spot) <= 2)
                .OrderBy(x => WorldModel.Distance(x.Location, spot)).FirstOrDefault();

            if (ob == null)
            {
                if (now - _atNpcSince > NpcSearch)
                {
                    _log($"Combine: {npc.NPCName} is not where the database says - giving up for now.");
                    End(cooldown: true);
                }
                return null;
            }

            // Talk (again) from the entry page whenever the conversation is not where the path
            // expects it: first contact, or an unexpected page came back mid-path.
            int expected = _step == 0 ? npc.EntryPage.Index : _recipe.PagePath[_step - 1];
            if (!_pageOpen || _lastPage != expected)
            {
                if (_pageOpen && _step > 0 && now - _pressedStepAt < PageWait)
                    return new Decision { Action = BotAction.Idle, Reason = $"waiting for {npc.NPCName}" };

                if (_calledAt != DateTime.MinValue && now - _calledAt < PageWait && !_pageOpen)
                    return new Decision { Action = BotAction.Idle, Reason = $"waiting for {npc.NPCName}" };

                if (_calls >= Tries + _recipe.ButtonPath.Count)
                {
                    _log($"Combine: {npc.NPCName} did not show the expected pages - giving up for now.");
                    End(cooldown: true);
                    return null;
                }

                _calls++;
                _step = 0;
                _pageOpen = false;
                _lastPage = -1;
                _calledAt = now;
                Status = $"combine: talking to {npc.NPCName}";
                return new Decision
                {
                    Action = BotAction.NPCCall,
                    TargetID = ob.ObjectID,
                    Reason = $"talking to {npc.NPCName} about {_recipe.Name}",
                    Subject = npc.NPCName
                };
            }

            bool last = _step == _recipe.ButtonPath.Count - 1;
            int button = _recipe.ButtonPath[_step];

            if (last)
            {
                // One free slot for the result, or the server's CanGainItem check refuses.
                if (!bag.HasRoomForRewards(new[] { (_recipe.Output, 1L, false, false) }))
                {
                    _log($"Combine: no bag room for {_recipe.Name} - trying again later.");
                    End(cooldown: true);
                    return null;
                }

                _piecesBefore = _recipe.Inputs.Select(i => CombineBook.Held(bag, i.Item, false)).ToArray();
                _outputBefore = CombineBook.Held(bag, _recipe.Output, false);
                _pressedAt = now;
                Phase = ErrandPhase.Pressed;
                Status = $"combine: combining {_recipe.Name}";
                return new Decision
                {
                    Action = BotAction.NPCButton,
                    ButtonID = button,
                    Reason = $"combining {_recipe.Name} ({_recipe.Gold:N0} gold, 1 in {_recipe.ChanceOneIn})",
                    Subject = _recipe.Name
                };
            }

            _step++;
            _pressedStepAt = now;
            return new Decision
            {
                Action = BotAction.NPCButton,
                ButtonID = button,
                Reason = $"opening {npc.NPCName}'s {_recipe.Name} page",
                Subject = npc.NPCName
            };
        }
    }
}
