using Client.Controls;
using Client.Envir;
using Client.UserModels;
using Library;
using Library.SystemModels;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Client.Scenes.Views
{
    public sealed class GuidesDialog : DXWindow
    {
        private const int RowHeight = 22;
        private const int ListInset = 4;
        private const int ScrollBarWidth = 19;
        private const int ScrollBarGap = 2;
        private const int HeaderHeight = 20;
        private const int RowPoolSize = 32;
        private const int MenuWidth = 150;

        private const int PreferredClientWidth = 720;
        private const int MinimumClientWidth = 420;
        private const int PreferredClientHeight = 460;
        private const int MinimumClientHeight = 220;

        public GuideMenuRow[] MenuRows;
        public GuideContentRow[] ContentRows;
        public DXVScrollBar ContentScrollBar;
        public GuideContentRow HeaderRow;

        private readonly List<GuideRow> _Rows = new List<GuideRow>();
        private GuideEntry _Selected;
        private int _VisibleRows = 1;

        public override WindowType Type => WindowType.GuidesBox;
        public override bool CustomSize => false;          // persisted Size would fight UISize sizing
        public override bool AutomaticVisibility => false; // must not reopen itself on login

        public GuidesDialog()
        {
            Visible = false;
            DropShadow = true;
            AllowResize = false;
            HasFooter = false;

            TitleLabel.Text = CEnvir.Language.GuidesTitle;

            IReadOnlyList<GuideEntry> guides = ItemGuideHelper.Guides;

            MenuRows = new GuideMenuRow[guides.Count];

            for (int i = 0; i < guides.Count; i++)
            {
                MenuRows[i] = new GuideMenuRow(guides[i])
                {
                    Parent = this,
                };
                MenuRows[i].MouseClick += MenuRow_MouseClick;
            }

            HeaderRow = new GuideContentRow(true)
            {
                Parent = this,
            };

            ContentRows = new GuideContentRow[RowPoolSize];

            for (int i = 0; i < ContentRows.Length; i++)
            {
                ContentRows[i] = new GuideContentRow(false)
                {
                    Parent = this,
                    Visible = false,
                };
            }

            ContentScrollBar = new DXVScrollBar
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
            ContentScrollBar.ValueChanged += (o, e) => RefreshRows();

            foreach (GuideContentRow row in ContentRows)
                row.BindMouseWheel(ContentScrollBar);

            ApplyClientSize();

            if (guides.Count > 0)
                Select(guides[0]);
        }

        #region Sizing

        private void ApplyClientSize()
        {
            Size ui = GameScene.Game?.UISize ?? new Size(PreferredClientWidth + 40, PreferredClientHeight + 80);

            int width = Math.Max(MinimumClientWidth, Math.Min(PreferredClientWidth, ui.Width - 40));
            int height = Math.Max(MinimumClientHeight, Math.Min(PreferredClientHeight, ui.Height - 80));

            SetClientSize(new Size(width, height));
        }

        /// <summary>
        /// Called from GameScene.UIScaleChanged and GameScene.OnSizeChanged. ResolutionChanged is
        /// never invoked anywhere in the client, and DXWindow.LoadSettings is not virtual, so this
        /// explicit call is the only reliable hook.
        /// </summary>
        public void RefreshForScale()
        {
            ApplyClientSize();
            ClampToScene();
        }

        private void ClampToScene()
        {
            if (GameScene.Game == null) return;

            Size scene = GameScene.Game.UISize;

            int x = Math.Max(0, Math.Min(Math.Max(0, scene.Width - Size.Width), Location.X));
            int y = Math.Max(0, Math.Min(Math.Max(0, scene.Height - Size.Height), Location.Y));

            Location = new Point(x, y);
        }

        public override void OnClientAreaChanged(Rectangle oValue, Rectangle nValue)
        {
            base.OnClientAreaChanged(oValue, nValue);

            Layout();
        }

        private void Layout()
        {
            if (MenuRows == null || ContentRows == null || ContentScrollBar == null) return;

            int menuWidth = Math.Min(MenuWidth, Math.Max(90, ClientArea.Width / 3));

            for (int i = 0; i < MenuRows.Length; i++)
            {
                MenuRows[i].Location = new Point(ClientArea.X + ListInset, ClientArea.Y + ListInset + i * RowHeight);
                MenuRows[i].Size = new Size(Math.Max(0, menuWidth - ListInset), RowHeight - 1);
            }

            int contentX = ClientArea.X + menuWidth + ListInset;
            int contentWidth = Math.Max(0, ClientArea.Width - menuWidth - ListInset * 2);

            HeaderRow.Location = new Point(contentX, ClientArea.Y + ListInset);
            HeaderRow.Size = new Size(contentWidth, HeaderHeight - 1);

            int listTop = ClientArea.Y + ListInset + HeaderHeight;
            int listHeight = Math.Max(0, ClientArea.Height - ListInset * 2 - HeaderHeight);

            _VisibleRows = Math.Max(1, Math.Min(ContentRows.Length, listHeight / RowHeight));

            bool scroll = _Rows.Count > _VisibleRows;
            int rowWidth = contentWidth - (scroll ? ScrollBarWidth + ScrollBarGap : 0);

            for (int i = 0; i < ContentRows.Length; i++)
            {
                ContentRows[i].Location = new Point(contentX, listTop + i * RowHeight);
                ContentRows[i].Size = new Size(Math.Max(0, rowWidth), RowHeight - 1);
            }

            ContentScrollBar.Location = new Point(contentX + contentWidth - ScrollBarWidth, listTop);
            ContentScrollBar.Size = new Size(ScrollBarWidth, _VisibleRows * RowHeight);
            ContentScrollBar.VisibleSize = _VisibleRows;
        }

        #endregion

        #region Selection

        private void MenuRow_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || sender is not GuideMenuRow row) return;

            Select(row.Entry);
        }

        private void Select(GuideEntry entry)
        {
            if (entry == null) return;

            _Selected = entry;

            _Rows.Clear();
            _Rows.AddRange(entry.Build());

            if (ContentScrollBar != null)
            {
                ContentScrollBar.Value = 0;
                ContentScrollBar.MaxValue = _Rows.Count;
            }

            if (MenuRows != null)
            {
                foreach (GuideMenuRow row in MenuRows)
                    row.Selected = row.Entry == entry;
            }

            HeaderRow?.SetHeader(entry.StatLabel);

            Layout();
            RefreshRows();
        }

        private void RefreshRows()
        {
            if (ContentRows == null || ContentScrollBar == null) return;

            for (int i = 0; i < ContentRows.Length; i++)
            {
                int index = ContentScrollBar.Value + i;
                GuideRow row = index < _Rows.Count ? _Rows[index] : null;

                ContentRows[i].SetRow(row);
                ContentRows[i].Visible = i < _VisibleRows && row != null;
            }
        }

        #endregion

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (!disposing) return;

            _Selected = null;
            _Rows.Clear();

            if (MenuRows != null)
            {
                foreach (GuideMenuRow row in MenuRows)
                {
                    if (row == null) continue;

                    row.MouseClick -= MenuRow_MouseClick;

                    if (!row.IsDisposed)
                        row.Dispose();
                }

                MenuRows = null;
            }

            if (ContentRows != null)
            {
                foreach (GuideContentRow row in ContentRows)
                {
                    if (row != null && !row.IsDisposed)
                        row.Dispose();
                }

                ContentRows = null;
            }

            if (HeaderRow != null)
            {
                if (!HeaderRow.IsDisposed)
                    HeaderRow.Dispose();

                HeaderRow = null;
            }

            if (ContentScrollBar != null)
            {
                if (!ContentScrollBar.IsDisposed)
                    ContentScrollBar.Dispose();

                ContentScrollBar = null;
            }
        }
    }

    /// <summary>One guide in the left-hand list.</summary>
    public sealed class GuideMenuRow : DXControl
    {
        public GuideEntry Entry { get; }
        public DXLabel NameLabel;

        public bool Selected
        {
            get => _Selected;
            set
            {
                if (_Selected == value) return;

                _Selected = value;
                BackColour = _Selected ? Constants.SelectedRowBackColour : Constants.RowBackColour;
                NameLabel.ForeColour = _Selected ? Color.White : Constants.PrimaryColour;
            }
        }
        private bool _Selected;

        public GuideMenuRow(GuideEntry entry)
        {
            Entry = entry;

            DrawTexture = true;
            BackColour = Constants.RowBackColour;

            NameLabel = MonsterDropsDialog.CreateColumnLabel(this, Constants.PrimaryColour);
            NameLabel.Text = entry.Title;
        }

        public override void OnSizeChanged(Size oValue, Size nValue)
        {
            base.OnSizeChanged(oValue, nValue);

            if (NameLabel == null) return;

            NameLabel.Location = new Point(5, 0);
            NameLabel.Size = new Size(Math.Max(0, Size.Width - 8), Size.Height);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (!disposing) return;

            _Selected = false;

            if (NameLabel != null)
            {
                if (!NameLabel.IsDisposed)
                    NameLabel.Dispose();

                NameLabel = null;
            }
        }
    }

    /// <summary>
    /// One row of the right-hand guide. Headings are the SAME height as item rows and differ only
    /// in colour and indent - the virtualisation counts rows, not pixels, so mixed heights would
    /// break scrolling and hit-testing.
    /// </summary>
    public sealed class GuideContentRow : DXControl
    {
        private const int StatWidth = 66;
        private const int DefenceWidth = 54;
        private const int CritWidth = 52;

        private readonly bool _IsHeaderRow;
        private bool _ShowColumns;

        public DXLabel NameLabel, StatLabel, ACLabel, MRLabel, CritChanceLabel, CritDamageLabel;

        private DXVScrollBar _ScrollBar;

        public GuideContentRow(bool headerRow)
        {
            _IsHeaderRow = headerRow;

            DrawTexture = true;
            BackColour = headerRow ? Constants.WindowBackColour : Constants.RowBackColour;

            Color colour = headerRow ? Color.White : Constants.PrimaryColour;

            NameLabel = MonsterDropsDialog.CreateColumnLabel(this, colour);
            StatLabel = MonsterDropsDialog.CreateColumnLabel(this, colour);
            ACLabel = MonsterDropsDialog.CreateColumnLabel(this, headerRow ? Color.White : Color.Silver);
            MRLabel = MonsterDropsDialog.CreateColumnLabel(this, headerRow ? Color.White : Color.Silver);
            CritChanceLabel = MonsterDropsDialog.CreateColumnLabel(this, headerRow ? Color.White : Color.Gold);
            CritDamageLabel = MonsterDropsDialog.CreateColumnLabel(this, headerRow ? Color.White : Color.Gold);

            if (headerRow)
                SetHeader("DC");
        }

        public void BindMouseWheel(DXVScrollBar scrollBar)
        {
            _ScrollBar = scrollBar;
            MouseWheel += scrollBar.DoMouseWheel;
        }

        public void SetHeader(string statLabel)
        {
            _ShowColumns = true;

            NameLabel.Text = CEnvir.Language.GuidesColumnItem;
            StatLabel.Text = statLabel;
            ACLabel.Text = "AC";
            MRLabel.Text = "MR";
            CritChanceLabel.Text = CEnvir.Language.GuidesColumnCritChance;
            CritDamageLabel.Text = CEnvir.Language.GuidesColumnCritDamage;

            UpdateLayout();
        }

        public void SetRow(GuideRow row)
        {
            if (row == null)
            {
                _ShowColumns = false;
                Clear();
                UpdateLayout();
                return;
            }

            if (row.IsHeading || !row.IsItem)
            {
                _ShowColumns = false;
                Clear();

                NameLabel.Text = row.Text ?? string.Empty;
                NameLabel.ForeColour = row.IsHeading ? Color.White : Color.Gray;
                BackColour = row.IsHeading ? Constants.WindowBackColour : Constants.RowBackColour;

                UpdateLayout();
                return;
            }

            ItemInfo item = row.Item;

            _ShowColumns = true;
            BackColour = Constants.RowBackColour;

            NameLabel.Text = item.ItemName;
            NameLabel.ForeColour = GameScene.GetItemLabelRarityColour(item.Rarity);
            StatLabel.Text = Range(row.PrimaryMin, row.PrimaryMax);
            ACLabel.Text = Range(item.Stats[Stat.MinAC], item.Stats[Stat.MaxAC]);
            MRLabel.Text = Range(item.Stats[Stat.MinMR], item.Stats[Stat.MaxMR]);
            CritChanceLabel.Text = Percent(item.Stats[Stat.CriticalChance]);
            CritDamageLabel.Text = Percent(item.Stats[Stat.CriticalDamage]);

            UpdateLayout();
        }

        private void Clear()
        {
            NameLabel.Text = string.Empty;
            StatLabel.Text = string.Empty;
            ACLabel.Text = string.Empty;
            MRLabel.Text = string.Empty;
            CritChanceLabel.Text = string.Empty;
            CritDamageLabel.Text = string.Empty;
        }

        private static string Range(int min, int max)
        {
            if (min == 0 && max == 0) return "-";

            return min == 0 || min == max ? max.ToString() : min + "-" + max;
        }

        private static string Percent(int value)
        {
            return value == 0 ? "-" : "+" + value + "%";
        }

        private void UpdateLayout()
        {
            if (NameLabel == null) return;

            bool columns = _ShowColumns;

            int width = Size.Width;
            int indent = columns ? 5 : 8;

            if (!columns)
            {
                // Heading or placeholder - the whole row is text.
                NameLabel.Location = new Point(indent, 0);
                NameLabel.Size = new Size(Math.Max(0, width - indent - 4), Size.Height);

                foreach (DXLabel label in new[] { StatLabel, ACLabel, MRLabel, CritChanceLabel, CritDamageLabel })
                {
                    label.Location = new Point(width, 0);
                    label.Size = new Size(0, Size.Height);
                }

                return;
            }

            int stat = Math.Min(StatWidth, Math.Max(0, width / 6));
            int defence = Math.Min(DefenceWidth, Math.Max(0, width / 8));
            int crit = Math.Min(CritWidth, Math.Max(0, width / 8));

            int used = stat + defence * 2 + crit * 2 + 10;
            int name = Math.Max(0, width - used);

            int x = indent;

            NameLabel.Location = new Point(x, 0);
            NameLabel.Size = new Size(name, Size.Height);
            x += name + 4;

            StatLabel.Location = new Point(x, 0);
            StatLabel.Size = new Size(stat, Size.Height);
            x += stat;

            ACLabel.Location = new Point(x, 0);
            ACLabel.Size = new Size(defence, Size.Height);
            x += defence;

            MRLabel.Location = new Point(x, 0);
            MRLabel.Size = new Size(defence, Size.Height);
            x += defence;

            CritChanceLabel.Location = new Point(x, 0);
            CritChanceLabel.Size = new Size(crit, Size.Height);
            x += crit;

            CritDamageLabel.Location = new Point(x, 0);
            CritDamageLabel.Size = new Size(crit, Size.Height);
        }

        public override void OnSizeChanged(Size oValue, Size nValue)
        {
            base.OnSizeChanged(oValue, nValue);

            UpdateLayout();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (!disposing) return;

            if (_ScrollBar != null)
            {
                MouseWheel -= _ScrollBar.DoMouseWheel;
                _ScrollBar = null;
            }

            DisposeChild(ref NameLabel);
            DisposeChild(ref StatLabel);
            DisposeChild(ref ACLabel);
            DisposeChild(ref MRLabel);
            DisposeChild(ref CritChanceLabel);
            DisposeChild(ref CritDamageLabel);
        }

        private static void DisposeChild<T>(ref T control) where T : DXControl
        {
            if (control == null) return;

            if (!control.IsDisposed)
                control.Dispose();

            control = null;
        }
    }
}
