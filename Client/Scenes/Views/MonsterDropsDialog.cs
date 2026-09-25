using Client.Controls;
using Client.Envir;
using Client.UserModels;
using Library;
using Library.SystemModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace Client.Scenes.Views
{
    public sealed class MonsterDropsDialog : DXWindow
    {
        private const int ItemRowHeight = 38;
        private const int SourceRowHeight = 22;
        private const int ListInset = 4;
        private const int ScrollBarWidth = 19;
        private const int ScrollBarGap = 2;
        private const int FilterRowHeight = 24;
        private const int NoteHeight = 18;
        private const int RowPoolSize = 26;

        private const int PreferredClientWidth = 660;
        private const int MinimumClientWidth = 360;
        private const int PreferredClientHeight = 430;
        private const int MinimumClientHeight = 200;

        public MonsterInfo Monster { get; private set; }

        public DXTabControl TabControl;
        public DXTab DropsTab, BrowserTab;

        // Drops tab
        public MonsterDropRow[] DropRows;
        public DXVScrollBar DropScrollBar;

        // Browser tab
        public DXComboBox TypeBox, ClassBox;
        public DXTextBox SearchBox;
        public DXLabel FindLabel;
        public MonsterDropRow[] ItemRows;
        public DXVScrollBar ItemScrollBar;
        public DropSourceRow[] SourceRows;
        public DXVScrollBar SourceScrollBar;

        public DXLabel NoteLabel;

        private readonly List<MonsterDropEntry> _Entries = new List<MonsterDropEntry>();
        private readonly ClientUserItem[] _DropDisplay = new ClientUserItem[RowPoolSize];

        private readonly List<BrowsableItem> _FilteredItems = new List<BrowsableItem>();
        private readonly ClientUserItem[] _ItemDisplay = new ClientUserItem[RowPoolSize];
        private readonly List<MonsterDropSource> _Sources = new List<MonsterDropSource>();
        private ItemInfo _SelectedItem;

        private bool _Positioned;
        private int _DropVisibleRows = 1, _ItemVisibleRows = 1, _SourceVisibleRows = 1;

        public override WindowType Type => WindowType.None;
        public override bool CustomSize => false;
        public override bool AutomaticVisibility => false;

        public MonsterDropsDialog()
        {
            Visible = false;
            DropShadow = true;
            AllowResize = false;
            HasFooter = false;

            TabControl = new DXTabControl
            {
                Parent = this,
                MarginLeft = 0,
                Padding = 0,
                BackColour = Color.Empty,
            };

            DropsTab = new DXTab
            {
                Parent = TabControl,
                MinimumTabWidth = 110,
                BackColour = Color.Empty,
                TabButton = { Label = { Text = CEnvir.Language.BigMapDropsTabLabel } },
            };

            BrowserTab = new DXTab
            {
                Parent = TabControl,
                MinimumTabWidth = 110,
                BackColour = Color.Empty,
                TabButton = { Label = { Text = CEnvir.Language.BigMapBrowserTabLabel } },
            };

            CreateDropsTab();
            CreateBrowserTab();

            NoteLabel = CreateColumnLabel(this, Color.Gray);
            NoteLabel.Text = CEnvir.Language.BigMapDropsBaseRateNote;

            TabControl.SelectedTabChanged += (o, e) =>
            {
                UpdateTitle();
                LayoutContent();
            };

            TabControl.SelectedTab = DropsTab;

            ApplyClientSize();
        }

        private void UpdateTitle()
        {
            if (TitleLabel == null) return;

            if (TabControl?.SelectedTab == BrowserTab)
                TitleLabel.Text = CEnvir.Language.BigMapBrowserTitle;
            else if (Monster != null)
                TitleLabel.Text = string.Format(CEnvir.Language.BigMapDropsTitle, Monster.MonsterName);
            else
                TitleLabel.Text = CEnvir.Language.BigMapDropsTabLabel;
        }

        // The tab may not have been resized by DXTabControl yet when layout first runs.
        private Size GetTabSize(DXTab tab)
        {
            if (tab == null) return Size.Empty;

            if (tab.Size.Width > 0 && tab.Size.Height > 0) return tab.Size;

            if (TabControl == null) return Size.Empty;

            return new Size(Math.Max(0, TabControl.Size.Width - tab.Location.X),
                            Math.Max(0, TabControl.Size.Height - tab.Location.Y));
        }

        #region Construction

        private void CreateDropsTab()
        {
            DropRows = new MonsterDropRow[RowPoolSize];

            for (int i = 0; i < DropRows.Length; i++)
            {
                DropRows[i] = new MonsterDropRow(_DropDisplay, i)
                {
                    Parent = DropsTab,
                    Visible = false,
                };
            }

            DropScrollBar = CreateScrollBar(DropsTab);
            DropScrollBar.ValueChanged += (o, e) => RefreshDropRows();

            foreach (MonsterDropRow row in DropRows)
                row.BindMouseWheel(DropScrollBar);
        }

        private void CreateBrowserTab()
        {
            TypeBox = new DXComboBox
            {
                Parent = BrowserTab,
                Size = new Size(110, DXComboBox.DefaultNormalHeight),
                Border = false,
            };
            PopulateTypeBox();
            TypeBox.SelectedItemChanged += (o, e) => RefreshFilter();

            ClassBox = new DXComboBox
            {
                Parent = BrowserTab,
                Size = new Size(110, DXComboBox.DefaultNormalHeight),
                Border = false,
            };
            PopulateClassBox();
            ClassBox.SelectedItemChanged += (o, e) => RefreshFilter();

            FindLabel = CreateColumnLabel(BrowserTab, Constants.PrimaryColour);
            FindLabel.Text = CEnvir.Language.BigMapBrowserSearchLabel;

            SearchBox = new DXTextBox
            {
                Parent = BrowserTab,
                Size = new Size(110, DXComboBox.DefaultNormalHeight),
                Border = false,
            };
            SearchBox.TextBox.TextChanged += (o, e) => RefreshFilter();

            ItemRows = new MonsterDropRow[RowPoolSize];

            for (int i = 0; i < ItemRows.Length; i++)
            {
                ItemRows[i] = new MonsterDropRow(_ItemDisplay, i)
                {
                    Parent = BrowserTab,
                    Visible = false,
                };
                ItemRows[i].MouseClick += ItemRow_MouseClick;
            }

            ItemScrollBar = CreateScrollBar(BrowserTab);
            ItemScrollBar.ValueChanged += (o, e) => RefreshItemRows();

            foreach (MonsterDropRow row in ItemRows)
                row.BindMouseWheel(ItemScrollBar);

            SourceRows = new DropSourceRow[RowPoolSize];

            for (int i = 0; i < SourceRows.Length; i++)
            {
                SourceRows[i] = new DropSourceRow
                {
                    Parent = BrowserTab,
                    Visible = false,
                };
            }

            SourceScrollBar = CreateScrollBar(BrowserTab);
            SourceScrollBar.ValueChanged += (o, e) => RefreshSourceRows();

            foreach (DropSourceRow row in SourceRows)
                row.BindMouseWheel(SourceScrollBar);
        }

        private void PopulateTypeBox()
        {
            new DXListBoxItem
            {
                Parent = TypeBox.ListBox,
                Label = { Text = CEnvir.Language.BigMapBrowserAnyType },
                Item = null,
            };

            // ItemType is a contiguous enum carrying [Description] for its display names.
            Type type = typeof(ItemType);

            for (ItemType value = ItemType.Consumable; value <= ItemType.SocketGem; value++)
            {
                MemberInfo[] members = type.GetMember(value.ToString());

                // Guards against a gap being introduced into the enum later.
                if (members.Length == 0) continue;

                DescriptionAttribute description = members[0].GetCustomAttribute<DescriptionAttribute>();

                new DXListBoxItem
                {
                    Parent = TypeBox.ListBox,
                    Label = { Text = description?.Description ?? value.ToString() },
                    Item = value,
                };
            }

            TypeBox.SelectedLabel.Text = CEnvir.Language.BigMapBrowserAnyType;
        }

        private void PopulateClassBox()
        {
            // RequiredClass is a [Flags] enum with composite members, so it cannot be walked
            // contiguously. MirClass is the plain per-class enum.
            new DXListBoxItem
            {
                Parent = ClassBox.ListBox,
                Label = { Text = CEnvir.Language.BigMapBrowserAnyClass },
                Item = null,
            };

            for (MirClass value = MirClass.Warrior; value <= MirClass.Assassin; value++)
            {
                new DXListBoxItem
                {
                    Parent = ClassBox.ListBox,
                    Label = { Text = value.ToString() },
                    Item = value,
                };
            }

            ClassBox.SelectedLabel.Text = CEnvir.Language.BigMapBrowserAnyClass;
        }

        private static DXVScrollBar CreateScrollBar(DXControl parent)
        {
            return new DXVScrollBar
            {
                Parent = parent,
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
        }

        // AutoSize must be off or CreateSize overwrites the width; WordEllipsis needs SingleLine,
        // and the format has to replace the WordBreak default rather than OR into it.
        internal static DXLabel CreateColumnLabel(DXControl parent, Color colour)
        {
            return new DXLabel
            {
                Parent = parent,
                IsControl = false,
                AutoSize = false,
                ForeColour = colour,
                DrawFormat = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                             TextFormatFlags.WordEllipsis | TextFormatFlags.NoPrefix,
            };
        }

        #endregion

        #region Show / hide

        public void Show(MonsterInfo monster, IEnumerable<RespawnInfo> respawns)
        {
            if (monster == null) return;

            Monster = monster;

            UpdateTitle();

            _Entries.Clear();
            _Entries.AddRange(MonsterDropHelper.GetDrops(monster, respawns));

            DropScrollBar.Value = 0;
            DropScrollBar.MaxValue = _Entries.Count;

            TabControl.SelectedTab = DropsTab;

            LayoutContent();
            RefreshDropRows();

            Reveal();
        }

        public void ShowBrowser()
        {
            UpdateTitle();

            if (_FilteredItems.Count == 0)
                RefreshFilter();

            TabControl.SelectedTab = BrowserTab;

            LayoutContent();
            RefreshItemRows();
            RefreshSourceRows();

            Reveal();
        }

        private void Reveal()
        {
            Visible = true;
            BringToFront();
            ClampToScene();
        }

        /// <summary>Clears the map-specific drops list, leaving the browser's filters intact.</summary>
        public void CloseDrops()
        {
            Monster = null;
            _Entries.Clear();
            DropScrollBar.MaxValue = 0;

            RefreshDropRows();

            if (TabControl.SelectedTab == DropsTab)
                Visible = false;
        }

        public void Close()
        {
            Visible = false;
        }

        public override void OnIsVisibleChanged(bool oValue, bool nValue)
        {
            base.OnIsVisibleChanged(oValue, nValue);

            if (IsVisible || IsDisposed) return;

            // These list boxes are parented to the scene, so they linger unless closed explicitly.
            if (TypeBox != null) TypeBox.Showing = false;
            if (ClassBox != null) ClassBox.Showing = false;

            Monster = null;
            _Entries.Clear();

            for (int i = 0; i < _DropDisplay.Length; i++)
                _DropDisplay[i] = null;

            RefreshDropRows();
        }

        #endregion

        #region Layout

        private void ApplyClientSize()
        {
            Size ui = GameScene.Game?.UISize ?? new Size(PreferredClientWidth + 40, PreferredClientHeight + 80);

            int width = Math.Max(MinimumClientWidth, Math.Min(PreferredClientWidth, ui.Width - 40));
            int height = Math.Max(MinimumClientHeight, Math.Min(PreferredClientHeight, ui.Height - 80));

            SetClientSize(new Size(width, height));
        }

        /// <summary>
        /// Called from GameScene.UIScaleChanged and GameScene.OnSizeChanged so the window re-derives
        /// its size, not just its position. ResolutionChanged is never invoked anywhere in the client
        /// and DXWindow.LoadSettings is not virtual, so this explicit call is the only reliable hook.
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

            if (TabControl == null) return;

            TabControl.Location = ClientArea.Location;
            TabControl.Size = new Size(ClientArea.Width, Math.Max(0, ClientArea.Height - NoteHeight));

            if (NoteLabel != null)
            {
                NoteLabel.Location = new Point(ClientArea.X + ListInset, ClientArea.Y + ClientArea.Height - NoteHeight + 2);
                NoteLabel.Size = new Size(Math.Max(0, ClientArea.Width - ListInset * 2), NoteHeight - 2);
            }

            LayoutContent();
        }

        private void LayoutContent()
        {
            LayoutDropsTab();
            LayoutBrowserTab();
        }

        private void LayoutDropsTab()
        {
            if (DropsTab == null || DropRows == null || DropScrollBar == null) return;

            Size tab = GetTabSize(DropsTab);
            int listHeight = Math.Max(0, tab.Height - ListInset * 2);

            _DropVisibleRows = Math.Max(1, Math.Min(DropRows.Length, listHeight / ItemRowHeight));

            bool scroll = _Entries.Count > _DropVisibleRows;
            int rowWidth = tab.Width - ListInset * 2 - (scroll ? ScrollBarWidth + ScrollBarGap : 0);

            for (int i = 0; i < DropRows.Length; i++)
            {
                DropRows[i].Location = new Point(ListInset, ListInset + i * ItemRowHeight);
                DropRows[i].Size = new Size(Math.Max(0, rowWidth), ItemRowHeight - 1);
            }

            DropScrollBar.Location = new Point(tab.Width - ListInset - ScrollBarWidth, ListInset);
            DropScrollBar.Size = new Size(ScrollBarWidth, _DropVisibleRows * ItemRowHeight);
            DropScrollBar.VisibleSize = _DropVisibleRows;
        }

        private void LayoutBrowserTab()
        {
            if (BrowserTab == null || ItemRows == null || SourceRows == null) return;

            Size tab = GetTabSize(BrowserTab);

            // Filter row: two combos and a captioned search box, all sized from what is available.
            int available = Math.Max(0, tab.Width - ListInset * 2);
            int gap = 6;
            int findWidth = Math.Min(34, available / 6);
            int boxWidth = Math.Max(48, (available - gap * 2 - findWidth) / 3);
            int filterY = ListInset;

            TypeBox.Location = new Point(ListInset, filterY);
            TypeBox.Size = new Size(boxWidth, DXComboBox.DefaultNormalHeight);

            ClassBox.Location = new Point(TypeBox.Location.X + boxWidth + gap, filterY);
            ClassBox.Size = new Size(boxWidth, DXComboBox.DefaultNormalHeight);

            FindLabel.Location = new Point(ClassBox.Location.X + boxWidth + gap, filterY);
            FindLabel.Size = new Size(findWidth, DXComboBox.DefaultNormalHeight);

            SearchBox.Location = new Point(FindLabel.Location.X + findWidth, filterY);
            SearchBox.Size = new Size(boxWidth, DXComboBox.DefaultNormalHeight);

            int listTop = filterY + FilterRowHeight;
            int listHeight = Math.Max(0, tab.Height - listTop - ListInset);

            _ItemVisibleRows = Math.Max(1, Math.Min(ItemRows.Length, listHeight / ItemRowHeight));
            _SourceVisibleRows = Math.Max(1, Math.Min(SourceRows.Length, listHeight / SourceRowHeight));

            int leftWidth = Math.Max(120, (tab.Width - ListInset * 3) * 2 / 5);
            int rightWidth = Math.Max(120, tab.Width - ListInset * 3 - leftWidth);

            bool itemScroll = _FilteredItems.Count > _ItemVisibleRows;
            int itemRowWidth = leftWidth - (itemScroll ? ScrollBarWidth + ScrollBarGap : 0);

            for (int i = 0; i < ItemRows.Length; i++)
            {
                ItemRows[i].Location = new Point(ListInset, listTop + i * ItemRowHeight);
                ItemRows[i].Size = new Size(Math.Max(0, itemRowWidth), ItemRowHeight - 1);
            }

            ItemScrollBar.Location = new Point(ListInset + leftWidth - ScrollBarWidth, listTop);
            ItemScrollBar.Size = new Size(ScrollBarWidth, _ItemVisibleRows * ItemRowHeight);
            ItemScrollBar.VisibleSize = _ItemVisibleRows;

            int rightX = ListInset * 2 + leftWidth;
            bool sourceScroll = _Sources.Count > _SourceVisibleRows;
            int sourceRowWidth = rightWidth - (sourceScroll ? ScrollBarWidth + ScrollBarGap : 0);

            for (int i = 0; i < SourceRows.Length; i++)
            {
                SourceRows[i].Location = new Point(rightX, listTop + i * SourceRowHeight);
                SourceRows[i].Size = new Size(Math.Max(0, sourceRowWidth), SourceRowHeight - 1);
            }

            SourceScrollBar.Location = new Point(rightX + rightWidth - ScrollBarWidth, listTop);
            SourceScrollBar.Size = new Size(ScrollBarWidth, _SourceVisibleRows * SourceRowHeight);
            SourceScrollBar.VisibleSize = _SourceVisibleRows;
        }

        #endregion

        #region Refresh

        private void RefreshDropRows()
        {
            if (DropRows == null || DropScrollBar == null) return;

            bool empty = _Entries.Count == 0;

            for (int i = 0; i < DropRows.Length; i++)
            {
                int index = DropScrollBar.Value + i;
                MonsterDropEntry entry = index < _Entries.Count ? _Entries[index] : null;

                // The cell reads straight out of this array, so update it before refreshing the row.
                _DropDisplay[i] = entry == null ? null : new ClientUserItem(entry.Item, Math.Max(1, entry.Amount));

                DropRows[i].SetEntry(entry);
                DropRows[i].Visible = i < _DropVisibleRows && (entry != null || (empty && i == 0));

                if (empty && i == 0)
                    DropRows[i].ShowPlaceholder(CEnvir.Language.BigMapDropsNone);
            }
        }

        private void RefreshFilter()
        {
            if (_FilteredItems == null || ItemScrollBar == null) return;

            ItemType? type = TypeBox?.SelectedItem as ItemType?;
            MirClass? mirClass = ClassBox?.SelectedItem as MirClass?;
            string search = (SearchBox?.TextBox?.Text ?? string.Empty).Trim().ToLowerInvariant();

            _FilteredItems.Clear();

            foreach (BrowsableItem entry in MonsterDropHelper.GetBrowsableItems())
            {
                if (type.HasValue && entry.Item.ItemType != type.Value) continue;
                if (mirClass.HasValue && !MonsterDropHelper.MatchesClass(entry.Item, mirClass.Value)) continue;
                if (search.Length > 0 && entry.SearchName.IndexOf(search, StringComparison.Ordinal) < 0) continue;

                _FilteredItems.Add(entry);
            }

            ItemScrollBar.Value = 0;
            ItemScrollBar.MaxValue = _FilteredItems.Count;

            SelectItem(null);

            LayoutBrowserTab();
            RefreshItemRows();
            RefreshSourceRows();
        }

        private void RefreshItemRows()
        {
            if (ItemRows == null || ItemScrollBar == null) return;

            bool empty = _FilteredItems.Count == 0;

            for (int i = 0; i < ItemRows.Length; i++)
            {
                int index = ItemScrollBar.Value + i;
                BrowsableItem entry = index < _FilteredItems.Count ? _FilteredItems[index] : null;

                _ItemDisplay[i] = entry == null ? null : new ClientUserItem(entry.Item, 1);

                ItemRows[i].SetItem(entry?.Item);
                ItemRows[i].Selected = entry != null && entry.Item == _SelectedItem;
                ItemRows[i].Visible = i < _ItemVisibleRows && (entry != null || (empty && i == 0));

                if (empty && i == 0)
                    ItemRows[i].ShowPlaceholder(CEnvir.Language.BigMapBrowserNoItems);
            }
        }

        private void RefreshSourceRows()
        {
            if (SourceRows == null || SourceScrollBar == null) return;

            bool empty = _Sources.Count == 0;

            for (int i = 0; i < SourceRows.Length; i++)
            {
                int index = SourceScrollBar.Value + i;
                MonsterDropSource source = index < _Sources.Count ? _Sources[index] : null;

                SourceRows[i].SetSource(source);
                SourceRows[i].Visible = i < _SourceVisibleRows && (source != null || (empty && i == 0));

                if (empty && i == 0)
                {
                    SourceRows[i].ShowPlaceholder(_SelectedItem == null
                        ? CEnvir.Language.BigMapBrowserSelectItem
                        : CEnvir.Language.BigMapBrowserNoSources);
                }
            }
        }

        private void ItemRow_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || sender is not MonsterDropRow row || row.Item == null) return;

            SelectItem(row.Item);

            LayoutBrowserTab();
            RefreshItemRows();
            RefreshSourceRows();
        }

        private void SelectItem(ItemInfo item)
        {
            _SelectedItem = item;

            _Sources.Clear();

            if (item != null)
            {
                _Sources.AddRange(MonsterDropHelper.GetDropSources(item));
                _Sources.AddRange(MonsterDropHelper.GetCraftSources(item));
            }

            if (SourceScrollBar != null)
            {
                SourceScrollBar.Value = 0;
                SourceScrollBar.MaxValue = _Sources.Count;
            }
        }

        #endregion

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (!disposing) return;

            Monster = null;
            _SelectedItem = null;
            _Entries.Clear();
            _FilteredItems.Clear();
            _Sources.Clear();

            for (int i = 0; i < _DropDisplay.Length; i++)
                _DropDisplay[i] = null;

            for (int i = 0; i < _ItemDisplay.Length; i++)
                _ItemDisplay[i] = null;

            DisposeRows(ref DropRows);
            DisposeRows(ref ItemRows);

            if (SourceRows != null)
            {
                foreach (DropSourceRow row in SourceRows)
                {
                    if (row != null && !row.IsDisposed)
                        row.Dispose();
                }

                SourceRows = null;
            }

            DisposeControl(ref DropScrollBar);
            DisposeControl(ref ItemScrollBar);
            DisposeControl(ref SourceScrollBar);
            DisposeControl(ref TypeBox);
            DisposeControl(ref ClassBox);
            DisposeControl(ref SearchBox);
            DisposeControl(ref FindLabel);
            DisposeControl(ref NoteLabel);
            DisposeControl(ref DropsTab);
            DisposeControl(ref BrowserTab);
            DisposeControl(ref TabControl);
        }

        private void DisposeRows(ref MonsterDropRow[] rows)
        {
            if (rows == null) return;

            foreach (MonsterDropRow row in rows)
            {
                if (row == null) continue;

                row.MouseClick -= ItemRow_MouseClick;

                if (!row.IsDisposed)
                    row.Dispose();
            }

            rows = null;
        }

        private static void DisposeControl<T>(ref T control) where T : DXControl
        {
            if (control == null) return;

            if (!control.IsDisposed)
                control.Dispose();

            control = null;
        }
    }

    /// <summary>Icon + name row, used both for a monster's drops and for the item browser list.</summary>
    public sealed class MonsterDropRow : DXControl
    {
        private const int ChanceWidth = 66;

        public DXItemCell Cell;
        public DXLabel NameLabel, ChanceLabel, PartLabel;

        public ItemInfo Item { get; private set; }

        public bool Selected
        {
            get => _Selected;
            set
            {
                if (_Selected == value) return;

                _Selected = value;
                BackColour = _Selected ? Constants.SelectedRowBackColour : Constants.RowBackColour;
            }
        }
        private bool _Selected;

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
                ItemGrid = displayItems,   // must be assigned before Slot
            };
            Cell.Slot = slot;

            NameLabel = MonsterDropsDialog.CreateColumnLabel(this, Constants.PrimaryColour);
            PartLabel = MonsterDropsDialog.CreateColumnLabel(this, Color.MediumPurple);
            PartLabel.Text = CEnvir.Language.BigMapDropsPartLabel;
            PartLabel.Visible = false;
            ChanceLabel = MonsterDropsDialog.CreateColumnLabel(this, Color.White);
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
            Item = entry?.Item;

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

            UpdateLayout();
        }

        public void SetItem(ItemInfo item)
        {
            Item = item;

            NameLabel.Text = item?.ItemName ?? string.Empty;

            if (item != null)
                NameLabel.ForeColour = GameScene.GetItemLabelRarityColour(item.Rarity);

            ChanceLabel.Text = string.Empty;
            PartLabel.Visible = false;
            Cell.Visible = item != null;

            Cell.RefreshItem();

            UpdateLayout();
        }

        public void ShowPlaceholder(string text)
        {
            Item = null;
            Cell.Visible = false;
            PartLabel.Visible = false;
            ChanceLabel.Text = string.Empty;
            NameLabel.Text = text;
            NameLabel.ForeColour = Color.Gray;

            UpdateLayout();
        }

        private void UpdateLayout()
        {
            if (NameLabel == null) return;

            int left = (Cell != null && Cell.Visible ? DXItemCell.CellWidth : 0) + 8;
            bool hasChance = !string.IsNullOrEmpty(ChanceLabel?.Text);
            int chanceWidth = hasChance ? Math.Min(ChanceWidth, Math.Max(0, Size.Width - left - 8)) : 0;
            int textWidth = Math.Max(0, Size.Width - left - chanceWidth - 8);

            NameLabel.Location = new Point(left, PartLabel.Visible ? 3 : 0);
            NameLabel.Size = new Size(textWidth, PartLabel.Visible ? 16 : Size.Height);

            PartLabel.Location = new Point(left, 19);
            PartLabel.Size = new Size(textWidth, 14);

            if (ChanceLabel != null)
            {
                ChanceLabel.Location = new Point(Math.Max(left, Size.Width - chanceWidth - 4), 0);
                ChanceLabel.Size = new Size(chanceWidth, Size.Height);
            }
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

            Item = null;
            _Selected = false;

            if (_ScrollBar != null)
            {
                MouseWheel -= _ScrollBar.DoMouseWheel;

                if (Cell != null && !Cell.IsDisposed)
                    Cell.MouseWheel -= _ScrollBar.DoMouseWheel;

                _ScrollBar = null;
            }

            DisposeChild(ref Cell);
            DisposeChild(ref NameLabel);
            DisposeChild(ref PartLabel);
            DisposeChild(ref ChanceLabel);
        }

        private static void DisposeChild<T>(ref T control) where T : DXControl
        {
            if (control == null) return;

            if (!control.IsDisposed)
                control.Dispose();

            control = null;
        }
    }

    /// <summary>One monster + map that drops the selected item.</summary>
    public sealed class DropSourceRow : DXControl
    {
        private const int ChanceWidth = 62;
        private const int LevelWidth = 34;

        public DXLabel MonsterLabel, LevelLabel, MapLabel, ChanceLabel;

        private DXVScrollBar _ScrollBar;

        public DropSourceRow()
        {
            DrawTexture = true;
            BackColour = Constants.RowBackColour;

            MonsterLabel = MonsterDropsDialog.CreateColumnLabel(this, Constants.PrimaryColour);
            LevelLabel = MonsterDropsDialog.CreateColumnLabel(this, Color.Silver);
            MapLabel = MonsterDropsDialog.CreateColumnLabel(this, Color.LightSteelBlue);
            ChanceLabel = MonsterDropsDialog.CreateColumnLabel(this, Color.White);
        }

        public void BindMouseWheel(DXVScrollBar scrollBar)
        {
            _ScrollBar = scrollBar;
            MouseWheel += scrollBar.DoMouseWheel;
        }

        public void SetSource(MonsterDropSource source)
        {
            if (source == null)
            {
                MonsterLabel.Text = string.Empty;
                LevelLabel.Text = string.Empty;
                MapLabel.Text = string.Empty;
                ChanceLabel.Text = string.Empty;
                MonsterLabel.Hint = null;
                MapLabel.Hint = null;
            }
            else if (source.IsCraft)
            {
                // An NPC combination: who makes it, where, the recipe, and the real odds.
                string where = source.Map?.PlayerDescription ?? CEnvir.Language.BigMapBrowserNotSpawned;
                string full = string.Format(CEnvir.Language.BigMapBrowserCraftHint, source.Npc.NPCName, where, source.Recipe);

                MonsterLabel.Text = string.Format(CEnvir.Language.BigMapBrowserCraftFormat, source.Npc.NPCName);
                MonsterLabel.ForeColour = Color.LightGreen;
                MonsterLabel.Hint = full;
                LevelLabel.Text = string.Empty;
                MapLabel.Text = source.Recipe;
                MapLabel.ForeColour = Color.LightSteelBlue;
                MapLabel.Hint = full;
                ChanceLabel.Text = MonsterDropHelper.FormatChance(source.ChancePercent);
            }
            else
            {
                MonsterLabel.Hint = null;
                MapLabel.Hint = null;
                MonsterLabel.Text = source.Monster.MonsterName;
                MonsterLabel.ForeColour = source.Monster.IsBoss ? Color.OrangeRed : Constants.PrimaryColour;
                LevelLabel.Text = string.Format(CEnvir.Language.BigMapBrowserLevelFormat, source.Monster.Level);
                MapLabel.Text = source.Map?.PlayerDescription ?? CEnvir.Language.BigMapBrowserNotSpawned;
                MapLabel.ForeColour = source.Map == null ? Color.Gray : Color.LightSteelBlue;
                ChanceLabel.Text = MonsterDropHelper.FormatChance(source.ChancePercent);
            }

            UpdateLayout();
        }

        public void ShowPlaceholder(string text)
        {
            MonsterLabel.Text = text;
            MonsterLabel.ForeColour = Color.Gray;
            LevelLabel.Text = string.Empty;
            MapLabel.Text = string.Empty;
            ChanceLabel.Text = string.Empty;

            UpdateLayout();
        }

        private void UpdateLayout()
        {
            if (MonsterLabel == null) return;

            bool placeholder = string.IsNullOrEmpty(MapLabel?.Text) && string.IsNullOrEmpty(ChanceLabel?.Text);

            int width = Size.Width;
            int chanceWidth = placeholder ? 0 : Math.Min(ChanceWidth, Math.Max(0, width - 40));
            int levelWidth = placeholder ? 0 : Math.Min(LevelWidth, Math.Max(0, width - chanceWidth - 40));
            int remaining = Math.Max(0, width - chanceWidth - levelWidth - 12);

            int monsterWidth = placeholder ? Math.Max(0, width - 8) : remaining / 2;
            int mapWidth = placeholder ? 0 : remaining - monsterWidth;

            MonsterLabel.Location = new Point(4, 0);
            MonsterLabel.Size = new Size(monsterWidth, Size.Height);

            LevelLabel.Location = new Point(4 + monsterWidth + 2, 0);
            LevelLabel.Size = new Size(levelWidth, Size.Height);

            MapLabel.Location = new Point(LevelLabel.Location.X + levelWidth + 2, 0);
            MapLabel.Size = new Size(mapWidth, Size.Height);

            ChanceLabel.Location = new Point(Math.Max(0, width - chanceWidth - 4), 0);
            ChanceLabel.Size = new Size(chanceWidth, Size.Height);
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

            DisposeChild(ref MonsterLabel);
            DisposeChild(ref LevelLabel);
            DisposeChild(ref MapLabel);
            DisposeChild(ref ChanceLabel);
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
