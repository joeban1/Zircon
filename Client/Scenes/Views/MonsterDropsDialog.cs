using Client.Controls;
using Client.Envir;
using Client.UserModels;
using Library;
using Library.SystemModels;
using System;
using System.Collections.Generic;
using System.Drawing;

namespace Client.Scenes.Views
{
    public sealed class MonsterDropsDialog : DXWindow
    {
        private const int RowHeight = 38;
        private const int MaximumVisibleRows = 10;
        private const int ListInset = 4;
        private const int ScrollBarWidth = 19;
        private const int ScrollBarGap = 2;
        private const int ClientWidth = 300;
        private const int NoteHeight = 18;

        public MonsterInfo Monster { get; private set; }

        public MonsterDropRow[] Rows;
        public DXVScrollBar ScrollBar;
        public DXLabel NoteLabel;

        private readonly List<MonsterDropEntry> _Entries = new List<MonsterDropEntry>();
        private readonly ClientUserItem[] _DisplayItems = new ClientUserItem[MaximumVisibleRows];
        private bool _Positioned;

        public override WindowType Type => WindowType.None;
        public override bool CustomSize => false;
        public override bool AutomaticVisibility => false;

        public MonsterDropsDialog()
        {
            Visible = false;
            DropShadow = true;
            AllowResize = false;
            HasFooter = false;

            Rows = new MonsterDropRow[MaximumVisibleRows];

            for (int i = 0; i < Rows.Length; i++)
            {
                Rows[i] = new MonsterDropRow(_DisplayItems, i)
                {
                    Parent = this,
                    Visible = false,
                };
            }

            ScrollBar = new DXVScrollBar
            {
                Parent = this,
                Change = 1,
                MinValue = 0,
                VisibleSize = 1,
                HideWhenNoScroll = true,
                BackColour = Color.Empty,
                Border = false,
                UpButton = { Index = 61, LibraryFile = LibraryFile.Interface },
                DownButton = { Index = 62, LibraryFile = LibraryFile.Interface },
                PositionBar = { Index = 60, LibraryFile = LibraryFile.Interface },
                ShowBackgroundSlider = true,
            };
            ScrollBar.ValueChanged += (o, e) => RefreshRows();

            foreach (MonsterDropRow row in Rows)
                row.BindMouseWheel(ScrollBar);

            NoteLabel = new DXLabel
            {
                Parent = this,
                IsControl = false,
                ForeColour = Color.Gray,
                Text = CEnvir.Language.BigMapDropsBaseRateNote,
            };

            SetClientSize(new Size(ClientWidth, ListInset * 2 + MaximumVisibleRows * RowHeight + NoteHeight));
        }

        public void Show(MonsterInfo monster, IEnumerable<RespawnInfo> respawns)
        {
            if (monster == null) return;

            Monster = monster;

            TitleLabel.Text = string.Format(CEnvir.Language.BigMapDropsTitle, monster.MonsterName);

            _Entries.Clear();
            _Entries.AddRange(MonsterDropHelper.GetDrops(monster, respawns));

            ScrollBar.Value = 0;
            ScrollBar.MaxValue = _Entries.Count;

            LayoutRows();
            RefreshRows();

            Visible = true;
            BringToFront();
            ClampToScene();
        }

        public void Close()
        {
            Visible = false;
        }

        // The inherited close button just sets Visible, so clear state here to catch both paths.
        public override void OnIsVisibleChanged(bool oValue, bool nValue)
        {
            base.OnIsVisibleChanged(oValue, nValue);

            if (IsVisible || IsDisposed) return;

            Monster = null;
            _Entries.Clear();

            for (int i = 0; i < _DisplayItems.Length; i++)
                _DisplayItems[i] = null;

            if (Rows == null) return;

            foreach (MonsterDropRow row in Rows)
            {
                if (row == null || row.IsDisposed) continue;

                row.SetEntry(null);
                row.Visible = false;
            }
        }

        // WindowType.None means no stored settings, so placement is our responsibility.
        private void ClampToScene()
        {
            if (GameScene.Game == null) return;

            Size scene = GameScene.Game.UISize;

            if (!_Positioned)
            {
                Location = new Point((scene.Width - Size.Width) / 2, (scene.Height - Size.Height) / 2);
                _Positioned = true;
            }

            int x = Math.Max(0, Math.Min(Math.Max(0, scene.Width - Size.Width), Location.X));
            int y = Math.Max(0, Math.Min(Math.Max(0, scene.Height - Size.Height), Location.Y));

            Location = new Point(x, y);
        }

        public override void OnClientAreaChanged(Rectangle oValue, Rectangle nValue)
        {
            base.OnClientAreaChanged(oValue, nValue);

            LayoutRows();
        }

        private void LayoutRows()
        {
            if (Rows == null || ScrollBar == null) return;

            bool needsScrollBar = _Entries.Count > MaximumVisibleRows;
            int rowWidth = ClientArea.Width - ListInset * 2 - (needsScrollBar ? ScrollBarWidth + ScrollBarGap : 0);

            for (int i = 0; i < Rows.Length; i++)
            {
                Rows[i].Location = new Point(ClientArea.X + ListInset, ClientArea.Y + ListInset + i * RowHeight);
                Rows[i].Size = new Size(Math.Max(0, rowWidth), RowHeight - 1);
            }

            ScrollBar.Location = new Point(ClientArea.X + ClientArea.Width - ListInset - ScrollBarWidth, ClientArea.Y + ListInset);
            ScrollBar.Size = new Size(ScrollBarWidth, MaximumVisibleRows * RowHeight);
            ScrollBar.VisibleSize = MaximumVisibleRows;

            if (NoteLabel != null)
                NoteLabel.Location = new Point(ClientArea.X + ListInset, ClientArea.Y + ListInset + MaximumVisibleRows * RowHeight + 2);
        }

        private void RefreshRows()
        {
            if (Rows == null || ScrollBar == null) return;

            bool empty = _Entries.Count == 0;

            for (int i = 0; i < Rows.Length; i++)
            {
                int index = ScrollBar.Value + i;
                MonsterDropEntry entry = index < _Entries.Count ? _Entries[index] : null;

                // The cell reads straight out of this array, so update it before refreshing the row.
                _DisplayItems[i] = entry == null ? null : new ClientUserItem(entry.Item, Math.Max(1, entry.Amount));

                Rows[i].SetEntry(entry);
                Rows[i].Visible = entry != null || (empty && i == 0);

                if (empty && i == 0)
                    Rows[i].ShowPlaceholder(CEnvir.Language.BigMapDropsNone);
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (!disposing) return;

            Monster = null;
            _Entries.Clear();

            for (int i = 0; i < _DisplayItems.Length; i++)
                _DisplayItems[i] = null;

            if (Rows != null)
            {
                foreach (MonsterDropRow row in Rows)
                {
                    if (row != null && !row.IsDisposed)
                        row.Dispose();
                }

                Rows = null;
            }

            if (ScrollBar != null)
            {
                if (!ScrollBar.IsDisposed)
                    ScrollBar.Dispose();

                ScrollBar = null;
            }

            if (NoteLabel != null)
            {
                if (!NoteLabel.IsDisposed)
                    NoteLabel.Dispose();

                NoteLabel = null;
            }
        }
    }

    public sealed class MonsterDropRow : DXControl
    {
        public DXItemCell Cell;
        public DXLabel NameLabel, ChanceLabel, PartLabel;

        private DXVScrollBar _ScrollBar;

        public MonsterDropRow(ClientUserItem[] displayItems, int slot)
        {
            DrawTexture = true;
            BackColour = Constants.RowBackColour;

            Cell = new DXItemCell
            {
                Parent = this,
                Location = new Point(1, 1),
                // GridType.Inspect short-circuits DXItemCell.OnMouseClick, and ReadOnly blocks
                // double-click/key actions, so the cell is display-only but keeps its hover tooltip.
                GridType = GridType.Inspect,
                ReadOnly = true,
                AllowLink = false,
                ShowCountLabel = false,
                ItemGrid = displayItems,
            };
            Cell.Slot = slot;

            NameLabel = new DXLabel
            {
                Parent = this,
                IsControl = false,
                Location = new Point(DXItemCell.CellWidth + 8, 4),
                ForeColour = Constants.PrimaryColour,
            };

            PartLabel = new DXLabel
            {
                Parent = this,
                IsControl = false,
                Location = new Point(DXItemCell.CellWidth + 8, 20),
                ForeColour = Color.MediumPurple,
                Visible = false,
                Text = CEnvir.Language.BigMapDropsPartLabel,
            };

            ChanceLabel = new DXLabel
            {
                Parent = this,
                IsControl = false,
                ForeColour = Color.White,
            };
            ChanceLabel.SizeChanged += (o, e) => UpdateChanceLocation();
        }

        public void BindMouseWheel(DXVScrollBar scrollBar)
        {
            _ScrollBar = scrollBar;

            // HandleMouseWheel does not bubble, so the row and the cell both need wiring.
            MouseWheel += scrollBar.DoMouseWheel;
            Cell.MouseWheel += scrollBar.DoMouseWheel;
        }

        public void SetEntry(MonsterDropEntry entry)
        {
            if (entry == null)
            {
                NameLabel.Text = string.Empty;
                ChanceLabel.Text = string.Empty;
                PartLabel.Visible = false;
                Cell.Visible = false;
            }
            else
            {
                NameLabel.Text = entry.Item.ItemName;
                NameLabel.ForeColour = GameScene.GetItemLabelRarityColour(entry.Item.Rarity);
                ChanceLabel.Text = MonsterDropHelper.FormatChance(entry.ChancePercent);
                PartLabel.Visible = entry.PartOnly;
                Cell.Visible = true;
            }

            // Picks up the new backing-array entry, invalidates the cache and refreshes a hovered tooltip.
            Cell.RefreshItem();

            UpdateChanceLocation();
        }

        public void ShowPlaceholder(string text)
        {
            Cell.Visible = false;
            PartLabel.Visible = false;
            ChanceLabel.Text = string.Empty;
            NameLabel.Text = text;
            NameLabel.ForeColour = Color.Gray;
        }

        private void UpdateChanceLocation()
        {
            if (ChanceLabel == null) return;

            ChanceLabel.Location = new Point(Math.Max(0, Size.Width - ChanceLabel.Size.Width - 6), 11);
        }

        public override void OnSizeChanged(Size oValue, Size nValue)
        {
            base.OnSizeChanged(oValue, nValue);

            UpdateChanceLocation();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (!disposing) return;

            if (_ScrollBar != null)
            {
                MouseWheel -= _ScrollBar.DoMouseWheel;

                if (Cell != null && !Cell.IsDisposed)
                    Cell.MouseWheel -= _ScrollBar.DoMouseWheel;

                _ScrollBar = null;
            }

            if (Cell != null)
            {
                if (!Cell.IsDisposed)
                    Cell.Dispose();

                Cell = null;
            }

            if (NameLabel != null)
            {
                if (!NameLabel.IsDisposed)
                    NameLabel.Dispose();

                NameLabel = null;
            }

            if (PartLabel != null)
            {
                if (!PartLabel.IsDisposed)
                    PartLabel.Dispose();

                PartLabel = null;
            }

            if (ChanceLabel != null)
            {
                if (!ChanceLabel.IsDisposed)
                    ChanceLabel.Dispose();

                ChanceLabel = null;
            }
        }
    }
}
