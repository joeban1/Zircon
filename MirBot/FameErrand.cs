using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Library;
using Library.SystemModels;

namespace MirBot
{
    /// <summary>
    /// The fame ranks and where they are bought - built once from System.db.
    ///
    /// Fame Points (currency FP) come from quests. A rank is bought from the fame NPC (Chief
    /// Yonghyeon, Frost Village) by pressing the button that leads to the page running
    /// NPCActionType.PromoteFame; the server's CheckFame on the way refuses unless FP covers the
    /// NEXT rank's Cost. Progression is by FameInfo.Order; the character's current rank is its
    /// FameInfo.Index (Stats[Stat.Fame]). One rank per press.
    /// </summary>
    public sealed class FameBook
    {
        private readonly List<FameInfo> _ranks = new List<FameInfo>();

        public IReadOnlyList<FameInfo> Ranks => _ranks;
        public NPCInfo Npc { get; private set; }
        public int ButtonId { get; private set; } = -1;
        public MapInfo Map => Npc?.Region?.Map;
        public List<string> Report { get; } = new List<string>();

        public bool Ready => Npc != null && ButtonId >= 0 && _ranks.Count > 0;

        public void Build()
        {
            _ranks.Clear();
            Npc = null;
            ButtonId = -1;
            Report.Clear();

            try
            {
                _ranks.AddRange((Globals.FameInfoList?.Binding ?? Enumerable.Empty<FameInfo>())
                    .Where(f => f != null).OrderBy(f => f.Order));

                // The NPC whose entry page reaches a PromoteFame page, and the entry-page button
                // that starts that chain. Found from the data rather than hard-coded.
                foreach (NPCInfo npc in Globals.NPCInfoList?.Binding ?? Enumerable.Empty<NPCInfo>())
                {
                    NPCPage entry = npc?.EntryPage;
                    if (entry?.Buttons == null) continue;

                    foreach (NPCButton button in entry.Buttons)
                        if (Reaches(button?.DestinationPage, 0, new HashSet<int>()))
                        {
                            Npc = npc;
                            ButtonId = button.ButtonID;
                            break;
                        }

                    if (Npc != null) break;
                }

                Report.Add(Npc == null
                    ? "no fame NPC found - fame ranks will not be bought"
                    : $"fame NPC {Npc.NPCName} #{Npc.Index} on {Map?.Description ?? "?"}, button {ButtonId}");
                foreach (FameInfo rank in _ranks)
                    Report.Add($"rank {rank.Order + 1} '{rank.Name}' (#{rank.Index}) costs {rank.Cost:N0} FP: {Buffs(rank)}");
            }
            catch (Exception ex)
            {
                _ranks.Clear();
                Npc = null;
                Report.Add("fame data did not load: " + ex.Message);
            }
        }

        private static bool Reaches(NPCPage page, int depth, HashSet<int> seen)
        {
            if (page == null || depth > 4 || !seen.Add(page.Index)) return false;
            if (page.Actions?.Any(a => a?.ActionType == NPCActionType.PromoteFame) == true) return true;
            return Reaches(page.SuccessPage, depth + 1, seen) ||
                   (page.Buttons?.Any(b => Reaches(b?.DestinationPage, depth + 1, seen)) ?? false);
        }

        public static string Buffs(FameInfo rank) =>
            rank?.BuffStats == null ? "" :
            string.Join(" ", rank.BuffStats.Where(b => b != null).Select(b => $"{b.Stat}+{b.Amount}"));

        /// <summary>The rank a character holds, by FameInfo.Index, or null for none.</summary>
        public FameInfo Current(int fameIndex) =>
            fameIndex <= 0 ? null : _ranks.FirstOrDefault(r => r.Index == fameIndex);

        /// <summary>The next rank to buy: smallest Order above the current one's, or null at the top.</summary>
        public FameInfo Next(int fameIndex)
        {
            FameInfo current = Current(fameIndex);
            int order = current?.Order ?? -1;
            return _ranks.FirstOrDefault(r => r.Order > order);
        }

        /// <summary>Its rewards, as HasRoomForRewards wants them.</summary>
        public static IEnumerable<(ItemInfo, long, bool, bool)> Rewards(FameInfo rank) =>
            (rank?.ItemRewards ?? Enumerable.Empty<FameInfoReward>())
            .Where(r => r?.Item != null).Select(r => (r.Item, (long)r.Amount, false, false));
    }

    /// <summary>
    /// A visit to the fame NPC: buy every rank Fame Points cover, one press at a time.
    ///
    /// The server's answer is not a packet about fame. PromoteFame spends the FP (S.CurrencyChanged
    /// FIRST), then sets the rank and refreshes stats (S.StatsUpdate with Stat.Fame LATER), and the
    /// page that comes back is only whatever the NPC shows next. So a press is judged by the
    /// currency and the stat: FP falling means the promotion is COMMITTED - never press again until
    /// the new rank shows - and nothing changing means it was refused (no rank affordable after
    /// all, or no room for the rewards: the "FameNeedSpace" chat line).
    /// </summary>
    public sealed class FameErrand
    {
        public enum ErrandPhase { Idle, Talking, Pressed, Committed }

        private static readonly TimeSpan PageWait = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan RefusalWait = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan CommitWait = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan NpcSearch = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan GiveUpCooldown = TimeSpan.FromMinutes(30);
        private const int Tries = 2;

        private readonly BotConfig _config;
        private readonly FameBook _book;
        private readonly Action<string> _log;

        public ErrandPhase Phase { get; private set; } = ErrandPhase.Idle;
        public bool Active => Phase != ErrandPhase.Idle;
        public bool OwnsMovement => Active;
        public string Status { get; private set; } = "";

        /// <summary>Set when a visit ends; BotInstance consumes it as a travel trigger.</summary>
        public bool JustFinished;

        /// <summary>A rank was reached: (the new rank, FP left).</summary>
        public Action<FameInfo, long> OnPromoted;

        private int _mapIndex = -1;
        private DateTime _cooldownUntil = DateTime.MinValue;

        private bool _pageOpen;
        private DateTime _calledAt = DateTime.MinValue;
        private int _calls;
        private DateTime _atNpcSince = DateTime.MinValue;

        private long _fpBefore;
        private int _indexBefore;
        private FameInfo _buying;
        private DateTime _pressedAt;
        private string _lastServerLine;
        private DateTime _lastServerLineAt = DateTime.MinValue;

        public FameErrand(BotConfig config, FameBook book, Action<string> log)
        {
            _config = config;
            _book = book;
            _log = log ?? (_ => { });
        }

        public void PageChanged(NPCPage page)
        {
            if (Phase == ErrandPhase.Talking) _pageOpen = true;
        }

        public void NoteServerLine(string text, DateTime? at = null)
        {
            _lastServerLine = text;
            _lastServerLineAt = at ?? DateTime.UtcNow;
        }

        public void Suspend(string why)
        {
            if (!Active) return;
            _log($"Fame: pausing - {why}.");
            End(cooldown: false);
        }

        private void End(bool cooldown)
        {
            Phase = ErrandPhase.Idle;
            Status = "";
            _pageOpen = false;
            _calls = 0;
            _calledAt = DateTime.MinValue;
            _atNpcSince = DateTime.MinValue;
            _buying = null;
            if (cooldown) _cooldownUntil = DateTime.UtcNow + GiveUpCooldown;
            JustFinished = true;
        }

        /// <summary>The next rank, if FP covers it now.</summary>
        public FameInfo Affordable(WorldModel world)
        {
            FameInfo next = _book?.Next(world.FameIndex);
            return next != null && world.FamePoints >= next.Cost ? next : null;
        }

        /// <summary>Anything to do on this map right now?</summary>
        public bool HasWork(WorldModel world) =>
            _config.EnableFame && _book != null && _book.Ready && !world.Dead &&
            world.MapIndex == _book.Map?.Index && DateTime.UtcNow >= _cooldownUntil &&
            Affordable(world) != null;

        private static Point NpcPoint(NPCInfo npc) =>
            npc?.Region?.PointRegion != null && npc.Region.PointRegion.Length > 0
                ? npc.Region.PointRegion[0] : Point.Empty;

        public Decision Next(WorldModel world, Backpack bag, DateTime? clock = null)
        {
            DateTime now = clock ?? DateTime.UtcNow;

            if (!Active)
            {
                if (!HasWork(world)) return null;
                Phase = ErrandPhase.Talking;
                _mapIndex = world.MapIndex;
                _pageOpen = false;
                _calls = 0;
                _calledAt = DateTime.MinValue;
                _atNpcSince = DateTime.MinValue;
                FameInfo first = Affordable(world);
                _log($"Fame: {world.FamePoints:N0} FP covers {first.Name} ({first.Cost:N0}) - " +
                     $"going to {_book.Npc.NPCName}.");
            }

            if (world.Dead) { _log("Fame: died - stopping."); End(cooldown: false); return null; }
            if (world.MapIndex != _mapIndex) { _log("Fame: left the map - stopping."); End(cooldown: false); return null; }

            // A press is in flight: judge it by FP and the fame stat, never by the page.
            if (Phase == ErrandPhase.Pressed || Phase == ErrandPhase.Committed)
            {
                if (world.FameIndex != _indexBefore)
                {
                    FameInfo reached = _book.Current(world.FameIndex);
                    if (world.FamePoints != _fpBefore - (_buying?.Cost ?? 0))
                        _log($"Fame: rank changed to {reached?.Name ?? world.FameIndex.ToString()} but FP went " +
                             $"{_fpBefore:N0} -> {world.FamePoints:N0} (expected -{_buying?.Cost ?? 0:N0}) - recalculating.");
                    _log($"Fame: reached {reached?.Name ?? "?"} - {FameBook.Buffs(reached)} " +
                         $"({world.FamePoints:N0} FP left).");
                    OnPromoted?.Invoke(reached, world.FamePoints);
                    _buying = null;
                    Phase = ErrandPhase.Talking;
                    _pageOpen = false;          // re-open the page before the next press
                    _calls = 0;
                    _calledAt = DateTime.MinValue;
                    if (Affordable(world) == null)
                    {
                        _log("Fame: nothing more affordable - done.");
                        End(cooldown: false);
                        return null;
                    }
                }
                else if (world.FamePoints < _fpBefore)
                {
                    // Committed: the server has taken the FP; the new rank arrives with the next
                    // StatsUpdate. Pressing again now would buy the rank after it.
                    if (Phase != ErrandPhase.Committed)
                    {
                        Phase = ErrandPhase.Committed;
                        _pressedAt = now;
                    }
                    if (now - _pressedAt > CommitWait)
                    {
                        _log("Fame: FP was spent but the new rank never showed - stopping to be safe.");
                        End(cooldown: true);
                    }
                    return new Decision { Action = BotAction.Idle, Reason = "waiting for the new fame rank" };
                }
                else if (now - _pressedAt > RefusalWait)
                {
                    string why = _lastServerLine != null && _lastServerLineAt >= _pressedAt
                        ? $"server: {_lastServerLine}" : "nothing changed";
                    _log($"Fame: {_buying?.Name ?? "the next rank"} was refused ({why}); " +
                         $"not trying again for {GiveUpCooldown.TotalMinutes:0} minutes.");
                    End(cooldown: true);
                    return null;
                }
                else
                    return new Decision { Action = BotAction.Idle, Reason = "waiting for the fame answer" };
            }

            NPCInfo npc = _book.Npc;
            Point spot = NpcPoint(npc);
            int range = Math.Max(1, _config.VendorTalkRange);
            int distance = WorldModel.Distance(world.Location, spot);

            if (distance > range)
            {
                _pageOpen = false;
                _atNpcSince = DateTime.MinValue;
                Status = $"fame: walking to {npc.NPCName}";
                return new Decision
                {
                    Action = BotAction.WalkTo,
                    Destination = spot,
                    Reason = $"walking to {npc.NPCName} for a fame rank ({distance} tiles)",
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
                    _log($"Fame: {npc.NPCName} is not where the database says - giving up for now.");
                    End(cooldown: true);
                }
                return null;
            }

            if (!_pageOpen)
            {
                if (_calledAt != DateTime.MinValue && now - _calledAt < PageWait)
                    return new Decision { Action = BotAction.Idle, Reason = $"waiting for {npc.NPCName}" };
                if (_calls >= Tries)
                {
                    _log($"Fame: {npc.NPCName} did not answer - giving up for now.");
                    End(cooldown: true);
                    return null;
                }
                _calls++;
                _calledAt = now;
                Status = $"fame: talking to {npc.NPCName}";
                return new Decision
                {
                    Action = BotAction.NPCCall,
                    TargetID = ob.ObjectID,
                    Reason = $"talking to {npc.NPCName} about fame",
                    Subject = npc.NPCName
                };
            }

            FameInfo next = Affordable(world);
            if (next == null)
            {
                End(cooldown: false);
                return null;
            }

            if (!bag.HasRoomForRewards(FameBook.Rewards(next)))
            {
                _log($"Fame: no bag room for the {next.Name} rewards - trying again later.");
                End(cooldown: true);
                return null;
            }

            _fpBefore = world.FamePoints;
            _indexBefore = world.FameIndex;
            _buying = next;
            _pressedAt = now;
            Phase = ErrandPhase.Pressed;
            Status = $"fame: buying {next.Name}";
            return new Decision
            {
                Action = BotAction.NPCButton,
                ButtonID = _book.ButtonId,
                Reason = $"buying fame rank {next.Name} for {next.Cost:N0} FP",
                Subject = next.Name
            };
        }
    }
}
