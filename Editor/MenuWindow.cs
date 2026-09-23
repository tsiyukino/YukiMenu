using System.Collections.Generic;
using System.Linq;
using TsiYuki.Core.Editor;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace TsiYuki.Menus.Editor
{
    /// <summary>
    /// The menu the avatar will actually have, and the controls for changing
    /// its shape.
    ///
    /// The tree is read back from a real build, so it is the answer rather than
    /// an estimate — which is also why it is not live: it is taken when asked
    /// for, and goes stale as soon as anything changes. The paging is shown as
    /// bands drawn between the items, with the "next page" link drawn where the
    /// build will put it, rather than as "More" submenus to click through,
    /// because the pages follow from the order and the order is the thing being
    /// edited.
    ///
    /// Submenus start closed. A menu worth opening this window for has more
    /// items than fit on a screen, and the root wheel is what the question is
    /// usually about.
    /// </summary>
    public class MenuWindow : EditorWindow
    {
        static YukiLocalizer L => MenuText.L;

        // One spacing scale for the whole window, on a 4 px grid: Gap between
        // things inside a block, Block between blocks, Gutter at the edges.
        const float Gap = 4f;
        const float Block = 8f;
        const float Gutter = 8f;

        const float RowHeight = 22f;
        const float Indent = 16f;

        // Columns of a row, measured from its indent. Page labels, the
        // next-page link and notes line up on these too.
        const float GripX = 16f;             // after the foldout arrow
        const float IconX = GripX + 12 + Gap; // after the grip
        const float NameX = IconX + 16 + 6f;  // after the icon
        const float ArrowsWidth = 42f;
        const string SettingsOpenKey = "moe.tsiyuki.menu.settingsOpen";

        [SerializeField] VRCAvatarDescriptor avatar;

        Vector2 scroll;
        MenuNode tree;
        string error;
        bool stale = true;
        bool ranFull;
        long buildMs;

        // A build makes and throws away a copy of the avatar, which itself
        // raises hierarchyChanged; that echo must not mark the result stale.
        double quietUntil;

        // Only these are open; everything else is closed.
        readonly HashSet<string> expanded = new HashSet<string>();

        // --- search ---
        string filter = "";
        SearchField searchField;
        // The rows to show while searching; null when not searching.
        HashSet<MenuNode> visible;

        int zebra;
        bool settingsOpen = true;

        // Widths of the type and detail columns, measured over the whole tree
        // so the tags and details line up down the list, and do not jump
        // about as submenus open and close.
        float pillColumn;
        float detailColumn;

        // --- dragging ---
        class DragRow { public MenuNode Parent; public int Index; }
        const string DragKey = "moe.tsiyuki.menu.row";
        DragRow grabbed;
        string dropMenu;
        int dropIndex = -1;

        // A move is queued rather than applied where it is asked for: reordering
        // the rows in the middle of drawing them would leave IMGUI's layout pass
        // and its paint pass disagreeing about what is on screen.
        class PendingMove { public MenuNode Parent; public int From; public int To; public YukiMenuLayout Layout; }
        PendingMove queued;

        static string _version;
        static string Version
        {
            get
            {
                if (_version != null) return _version;
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MenuWindow).Assembly);
                return _version = info != null ? "v" + info.version : "";
            }
        }

        [MenuItem(YukiMenu.Root + "Menu Layout")]
        public static void ShowWindow() => Open(null);

        public static MenuWindow Open(YukiMenuLayout layout)
        {
            var window = GetWindow<MenuWindow>();
            window.titleContent = new GUIContent("Yuki Menu");
            window.minSize = new Vector2(360, 320);
            if (layout != null)
            {
                var found = layout.GetComponentInParent<VRCAvatarDescriptor>();
                if (found != null) window.avatar = found;
            }
            window.stale = true;
            window.Show();
            return window;
        }

        void OnEnable()
        {
            wantsMouseMove = true;
            searchField = new SearchField();
            settingsOpen = EditorPrefs.GetBool(SettingsOpenKey, true);
            EditorApplication.hierarchyChanged += MarkStale;
            if (avatar == null) avatar = Guess();
        }

        void OnDisable() => EditorApplication.hierarchyChanged -= MarkStale;

        void MarkStale()
        {
            if (EditorApplication.timeSinceStartup < quietUntil) return;
            stale = true;
            Repaint();
        }

        static VRCAvatarDescriptor Guess()
        {
            var selected = Selection.activeGameObject;
            if (selected != null)
            {
                var found = selected.GetComponentInParent<VRCAvatarDescriptor>();
                if (found != null) return found;
            }
            var all = Object.FindObjectsOfType<VRCAvatarDescriptor>();
            return all.Length == 1 ? all[0] : null;
        }

        YukiMenuLayout Layout =>
            avatar != null ? avatar.GetComponentInChildren<YukiMenuLayout>(true) : null;

        void OnGUI()
        {
            if (Event.current.type == EventType.MouseMove) Repaint();

            YukiMenuLayout layout = null;

            using (new EditorGUILayout.VerticalScope(Styles.Padded))
            {
                YukiGUI.Header(L["ui.title"], Version);
                EditorGUILayout.LabelField(L["ui.intro"], YukiGUI.WrapMini);
                EditorGUILayout.Space(Block);

                using (var change = new EditorGUI.ChangeCheckScope())
                {
                    avatar = (VRCAvatarDescriptor)EditorGUILayout.ObjectField(
                        L["ui.avatar"], avatar, typeof(VRCAvatarDescriptor), true);
                    if (change.changed) { tree = null; error = null; stale = true; expanded.Clear(); }
                }

                if (avatar == null)
                {
                    EditorGUILayout.Space(Gap);
                    EditorGUILayout.HelpBox(L["ui.pick_avatar"], MessageType.Info);
                    return;
                }

                layout = Layout;
                EditorGUILayout.Space(Block);
                DrawSettings(layout);
                EditorGUILayout.Space(Block);
                DrawRefresh();

                if (!string.IsNullOrEmpty(error))
                {
                    EditorGUILayout.Space(Gap);
                    EditorGUILayout.HelpBox(L.Tr("ui.build_failed", error), MessageType.Error);
                }
            }

            if (tree == null)
            {
                if (string.IsNullOrEmpty(error)) DrawEmptyState();
                return;
            }

            RebuildFilter();
            MeasureColumns(layout);
            DrawTreeToolbar(layout);

            using (var scope = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = scope.scrollPosition;
                zebra = 0;
                GUILayout.Space(Gap);
                if (visible != null && visible.Count == 0)
                {
                    GUILayout.Space(Block * 2);
                    GUILayout.Label(L.Tr("ui.no_match", filter), Styles.EmptyNote);
                }
                else DrawMenu(tree, layout, 0, true);
                GUILayout.Space(Block);
            }

            DrawFooter();
            HandleDragEnd();
            ApplyQueued();
        }

        // --- settings ---------------------------------------------------------------

        internal static GUIContent[] ScopeOptions => new[]
        {
            new GUIContent(L["ui.scope.root"]),
            new GUIContent(L["ui.scope.everywhere"]),
        };

        internal static GUIContent[] PlacementOptions => new[]
        {
            new GUIContent(L["ui.at.end"]),
            new GUIContent(L["ui.at.start"]),
        };

        internal static string Summary(YukiMenuLayout layout)
        {
            if (!layout.repage) return L["ui.summary_off"];
            var scope = L[layout.scope == PagingScope.Everywhere ? "ui.scope.everywhere" : "ui.scope.root"];
            var at = L[layout.overflowAt == OverflowPlacement.Start ? "ui.at.start_short" : "ui.at.end_short"];
            return L.Tr("ui.summary", scope, layout.itemsPerPage, at);
        }

        void DrawSettings(YukiMenuLayout layout)
        {
            if (layout == null)
            {
                using (new EditorGUILayout.VerticalScope(Styles.Card))
                {
                    GUILayout.Label(L["ui.settings"], EditorStyles.boldLabel);
                    EditorGUILayout.Space(Gap);
                    EditorGUILayout.LabelField(L["ui.no_component"], YukiGUI.WrapMini);
                    EditorGUILayout.Space(Block);
                    if (GUILayout.Button(L["ui.add_component"], GUILayout.Height(24)))
                    {
                        var host = new GameObject("Yuki Menu");
                        host.transform.SetParent(avatar.transform, false);
                        host.AddComponent<YukiMenuLayout>();
                        Undo.RegisterCreatedObjectUndo(host, L["undo.add_component"]);
                        Selection.activeGameObject = host;
                        stale = true;
                    }
                }
                return;
            }

            using (new EditorGUILayout.VerticalScope(Styles.Card))
            {
                // The header doubles as a summary, so the tree can have the
                // room once the settings are decided.
                var title = new GUIContent(L["ui.settings"]);
                var head = GUILayoutUtility.GetRect(10, 20, GUILayout.ExpandWidth(true));
                var open = EditorGUI.Foldout(head, settingsOpen, title, true, Styles.BoldFoldout);
                if (open != settingsOpen)
                {
                    settingsOpen = open;
                    EditorPrefs.SetBool(SettingsOpenKey, open);
                }
                if (!settingsOpen)
                {
                    // Starts just past the title rather than at a fixed offset,
                    // so it keeps the same gap in every language.
                    var titleWidth = Styles.BoldFoldout.CalcSize(title).x + Block * 2;
                    var summary = new Rect(head.x + titleWidth, head.y, Mathf.Max(0, head.width - titleWidth), head.height);
                    GUI.Label(summary, Summary(layout), Styles.SummaryRight);
                    return;
                }

                EditorGUILayout.Space(Gap);

                using (var change = new EditorGUI.ChangeCheckScope())
                {
                    var repage = EditorGUILayout.Toggle(
                        new GUIContent(L["ui.repage"], L["ui.repage.tip"]), layout.repage);
                    var scope = (PagingScope)EditorGUILayout.Popup(
                        new GUIContent(L["ui.scope"], L["ui.scope.tip"]), (int)layout.scope, ScopeOptions);
                    var perPage = EditorGUILayout.IntSlider(
                        new GUIContent(L["ui.per_page"], L["ui.per_page.tip"]),
                        layout.itemsPerPage, 2, MenuPaginator.VrcLimit);

                    EditorGUILayout.Space(Block);
                    GUILayout.Label(L["ui.next_page_group"], EditorStyles.miniBoldLabel);

                    string name;
                    Texture2D icon;
                    OverflowPlacement at;
                    using (new EditorGUI.IndentLevelScope())
                    {
                        name = EditorGUILayout.TextField(
                            new GUIContent(L["ui.overflow_name"], L["ui.overflow_name.tip"]), layout.overflowName);
                        icon = (Texture2D)EditorGUILayout.ObjectField(
                            new GUIContent(L["ui.overflow_icon"]), layout.overflowIcon, typeof(Texture2D), false,
                            GUILayout.Height(EditorGUIUtility.singleLineHeight));
                        at = (OverflowPlacement)EditorGUILayout.Popup(
                            new GUIContent(L["ui.overflow_at"], L["ui.overflow_at.tip"]), (int)layout.overflowAt,
                            PlacementOptions);
                    }

                    if (change.changed)
                    {
                        Undo.RecordObject(layout, L["undo.settings"]);
                        layout.scope = scope;
                        layout.itemsPerPage = perPage;
                        layout.repage = repage;
                        layout.overflowName = name;
                        layout.overflowIcon = icon;
                        layout.overflowAt = at;
                        EditorUtility.SetDirty(layout);
                    }
                }

                var note = !layout.repage ? L["ui.repage_off"]
                         : layout.itemsPerPage == MenuPaginator.VrcLimit ? L["ui.at_limit"]
                         : null;
                if (note != null)
                {
                    EditorGUILayout.Space(Block);
                    EditorGUILayout.LabelField(note, YukiGUI.WrapMini);
                }
            }
        }

        void DrawRefresh()
        {
            // One fixed-height row, drawn by hand, so the status sits on the
            // button's centre line instead of wherever layout lets it fall.
            const float height = 26f;
            var row = GUILayoutUtility.GetRect(10, height, GUILayout.ExpandWidth(true));

            var content = new GUIContent(" " + L[tree == null ? "ui.build" : "ui.refresh"],
                                         Styles.RefreshIcon, L["ui.build.tip"]);
            var width = Mathf.Max(130f, GUI.skin.button.CalcSize(content).x + Block * 2);
            var button = new Rect(row.x, row.y, Mathf.Min(width, row.width), height);

            var previous = GUI.backgroundColor;
            if (tree == null || stale) GUI.backgroundColor = Styles.ButtonTint;
            var clicked = GUI.Button(button, content);
            GUI.backgroundColor = previous;

            var status = Status(out var text);
            var x = button.xMax + Block + Gap;
            var dot = new Rect(x, row.y, 12, height);
            var previousColor = GUI.contentColor;
            GUI.contentColor = YukiGUI.StatusColor(status);
            GUI.Label(dot, "●", Styles.Middle);
            GUI.contentColor = previousColor;
            GUI.Label(new Rect(dot.xMax + Gap, row.y, Mathf.Max(0, row.xMax - dot.xMax - Gap), height),
                      text, Styles.Middle);

            // Last, so the rest of this pass is drawn against the tree it
            // started with.
            if (clicked) EditorApplication.delayCall += Refresh;

            if (tree != null && ranFull)
            {
                EditorGUILayout.Space(Gap);
                EditorGUILayout.LabelField(L.Tr("ui.with_vrcfury", Seconds), YukiGUI.WrapMini);
            }
        }

        string Seconds => (buildMs / 1000f).ToString("0.0");

        YukiStatus Status(out string text)
        {
            if (!string.IsNullOrEmpty(error)) { text = L["ui.status.failed"]; return YukiStatus.Problem; }
            if (tree == null) { text = L["ui.status.unread"]; return YukiStatus.None; }
            if (stale) { text = L["ui.stale"]; return YukiStatus.Approximate; }
            text = L.Tr("ui.status.fresh", Seconds);
            return YukiStatus.Ok;
        }

        void DrawEmptyState()
        {
            GUILayout.Space(Block * 3);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label(L["ui.empty_state"], Styles.EmptyNote, GUILayout.MaxWidth(360));
                GUILayout.FlexibleSpace();
            }
            GUILayout.FlexibleSpace();
        }

        void Refresh()
        {
            if (avatar == null) return;
            var layout = Layout;
            var overflow = layout != null && !string.IsNullOrEmpty(layout.overflowName)
                ? layout.overflowName
                : MenuPaginator.DefaultOverflowName;
            var perPage = layout != null ? layout.itemsPerPage : MenuPaginator.VrcLimit;

            try
            {
                var full = MenuSnapshot.HasVRCFury(avatar.gameObject);
                EditorUtility.DisplayProgressBar(L["ui.title"], L[full ? "ui.building_full" : "ui.building"], 0.5f);
                tree = MenuSnapshot.Capture(avatar.gameObject, overflow, perPage, out error);
                ranFull = MenuSnapshot.LastRunWasFull;
                buildMs = MenuSnapshot.LastMilliseconds;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            stale = false;
            quietUntil = EditorApplication.timeSinceStartup + 0.5;
            Repaint();
        }

        // --- search -----------------------------------------------------------------

        bool Searching => visible != null;

        bool Matches(MenuNode node) =>
            Contains(node.Name, filter) || Contains(node.Parameter, filter);

        static bool Contains(string text, string part) =>
            !string.IsNullOrEmpty(text) && text.IndexOf(part, System.StringComparison.OrdinalIgnoreCase) >= 0;

        void RebuildFilter()
        {
            if (string.IsNullOrWhiteSpace(filter)) { visible = null; return; }
            visible = new HashSet<MenuNode>();
            Collect(tree);
        }

        bool Collect(MenuNode menu)
        {
            var any = false;
            foreach (var child in menu.Children)
            {
                var hit = Matches(child);
                if (child.IsSubMenu && Collect(child)) hit = true;
                if (!hit) continue;
                visible.Add(child);
                any = true;
            }
            return any;
        }

        // --- the tree ---------------------------------------------------------------

        static bool Paged(YukiMenuLayout layout, bool isRoot) =>
            layout != null && layout.repage && (isRoot || layout.scope == PagingScope.Everywhere);

        static int Limit(YukiMenuLayout layout, bool paged) =>
            paged ? Mathf.Clamp(layout.itemsPerPage, 2, MenuPaginator.VrcLimit) : MenuPaginator.VrcLimit;

        void DrawTreeToolbar(YukiMenuLayout layout)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var count = tree.Children.Count;
                var pages = MenuPaginator.PageCount(count, Limit(layout, Paged(layout, true)));
                var title = L.Tr("ui.root_menu", count) + "  ·  " +
                            (pages <= 1 ? L["ui.one_page"] : L.Tr("ui.pages", pages));
                GUILayout.Space(2);
                GUILayout.Label(title, Styles.ToolbarTitle, GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();

                filter = searchField.OnToolbarGUI(filter, GUILayout.MinWidth(60), GUILayout.MaxWidth(180));
                GUILayout.Space(Gap);

                if (GUILayout.Button(L["ui.expand_all"], EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
                    ExpandAll(tree);
                if (GUILayout.Button(L["ui.collapse_all"], EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
                    expanded.Clear();

                if (layout != null && layout.order.Count > 0 &&
                    GUILayout.Button(new GUIContent(L["ui.reset_order"], L["ui.reset_order.tip"]),
                                     EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
                    ResetOrder(layout);
            }
        }

        void DrawFooter()
        {
            // A hairline rather than a gap: the list above scrolls, so empty
            // space alone would not say where it stops.
            var line = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(line, Styles.Divider);

            using (new EditorGUILayout.HorizontalScope(Styles.Footer))
                GUILayout.Label(Searching ? L["ui.filter_no_reorder"] : L["ui.drag_hint"], YukiGUI.WrapMini);
        }

        void ExpandAll(MenuNode menu)
        {
            foreach (var child in menu.Children)
            {
                if (!child.IsSubMenu) continue;
                expanded.Add(child.Path);
                ExpandAll(child);
            }
        }

        void DrawMenu(MenuNode menu, YukiMenuLayout layout, int depth, bool isRoot)
        {
            if (menu.Children.Count == 0)
            {
                DrawNote(depth, L["ui.empty"]);
                return;
            }

            // Search results are a flat list of hits; the pages would only
            // describe rows that are not being shown.
            if (Searching)
            {
                for (var i = 0; i < menu.Children.Count; i++)
                    if (visible.Contains(menu.Children[i])) DrawRow(menu, i, layout, depth, false);
                return;
            }

            var paged = Paged(layout, isRoot);
            var breaks = MenuPaginator.PageBreaks(menu.Children.Count, Limit(layout, paged));
            var linkFirst = paged && layout.overflowAt == OverflowPlacement.Start;
            var linkName = paged && !string.IsNullOrEmpty(layout.overflowName)
                ? layout.overflowName
                : MenuPaginator.DefaultOverflowName;
            var linkIcon = paged ? layout.overflowIcon : null;

            var page = 0;
            for (var i = 0; i < menu.Children.Count; i++)
            {
                if (breaks.Count > 0 && (i == 0 || breaks.Contains(i)))
                {
                    if (i > 0)
                    {
                        if (!linkFirst) DrawLink(depth, linkName, linkIcon, page + 2);
                        page++;
                    }
                    DrawPageBand(depth, page + 1, i == 0);
                    if (linkFirst && page < breaks.Count) DrawLink(depth, linkName, linkIcon, page + 2);
                }
                DrawRow(menu, i, layout, depth, true);
            }
        }

        void DrawRow(MenuNode parent, int index, YukiMenuLayout layout, int depth, bool canReorder)
        {
            var node = parent.Children[index];
            var e = Event.current;
            var rect = GUILayoutUtility.GetRect(10, RowHeight, GUILayout.ExpandWidth(true));
            var hover = rect.Contains(e.mousePosition) && GUIUtility.hotControl == 0;
            var open = node.IsSubMenu && (expanded.Contains(node.Path) ||
                                          (Searching && node.Children.Any(visible.Contains)));

            if (e.type == EventType.Repaint)
            {
                if ((zebra & 1) == 1) EditorGUI.DrawRect(rect, Styles.Zebra);
                var payload = DragAndDrop.GetGenericData(DragKey) as DragRow;
                if (payload != null && payload.Parent == parent && payload.Index == index)
                    EditorGUI.DrawRect(rect, Styles.WithAlpha(Styles.Accent, 0.18f));
                else if (hover)
                    EditorGUI.DrawRect(rect, Styles.Hover);
                if (Searching && Matches(node))
                    EditorGUI.DrawRect(new Rect(rect.x, rect.y + 2, 2, rect.height - 4), Styles.Accent);
            }
            zebra++;

            var left = Left(rect, depth);

            if (node.IsSubMenu)
            {
                var next = EditorGUI.Foldout(new Rect(left, rect.y, GripX, rect.height), open, GUIContent.none, true);
                if (next != open)
                {
                    if (next) expanded.Add(node.Path); else expanded.Remove(node.Path);
                    open = next;
                }
            }

            if (canReorder) GUI.Label(new Rect(left + GripX, rect.y, 12, rect.height), "≡", Styles.Grip);

            if (node.Icon != null) GUI.DrawTexture(IconRect(rect, left), node.Icon, ScaleMode.ScaleToFit);

            var x = left + NameX;

            // Fixed columns from the right: details, then the type tag; the
            // label takes what is left. Every gap between them is one Block.
            var detailRect = DetailRect(rect);
            var pill = new Rect(detailRect.x - Block - pillColumn, rect.y + (rect.height - 16) / 2, pillColumn, 16);

            var detail = Detail(node, layout);
            if (!string.IsNullOrEmpty(detail))
                GUI.Label(detailRect, new GUIContent(detail, detail), Styles.Detail);

            if (e.type == EventType.Repaint) EditorGUI.DrawRect(pill, Styles.TypeColor(node.Type));
            GUI.Label(pill, TypeName(node.Type), Styles.Pill);

            var unnamed = string.IsNullOrEmpty(node.Name);
            GUI.Label(new Rect(x, rect.y, Mathf.Max(0, pill.x - Block - x), rect.height),
                      new GUIContent(unnamed ? L["ui.unnamed"] : node.Name, node.Path),
                      unnamed ? Styles.Unnamed : Styles.Name);

            // The arrows are only there while the row is hovered, so they sit
            // over the end of the details instead of keeping an empty strip
            // down every row for them.
            if (canReorder && hover)
            {
                var right = Right(rect);
                var y = rect.y + (rect.height - 16) / 2;
                var arrows = new Rect(right - ArrowsWidth, y, ArrowsWidth, 16);
                if (e.type == EventType.Repaint)
                {
                    var under = new Rect(arrows.x - Block, rect.y, arrows.width + Block, rect.height);
                    EditorGUI.DrawRect(under, Styles.Background);
                    if ((zebra & 1) == 0) EditorGUI.DrawRect(under, Styles.Zebra); // zebra already advanced
                    EditorGUI.DrawRect(under, Styles.Hover);
                }
                var last = parent.Children.Count - 1;
                var half = ArrowsWidth / 2;
                using (new EditorGUI.DisabledScope(index == 0))
                    if (GUI.Button(new Rect(arrows.x, y, half, 16), "▲", EditorStyles.miniButtonLeft))
                        MoveTo(parent, index, index - 1, layout);
                using (new EditorGUI.DisabledScope(index == last))
                    if (GUI.Button(new Rect(arrows.x + half, y, half, 16), "▼", EditorStyles.miniButtonRight))
                        MoveTo(parent, index, index + 1, layout);
            }

            if (e.type == EventType.ContextClick && rect.Contains(e.mousePosition))
            {
                ShowContextMenu(parent, index, layout, canReorder);
                e.Use();
            }

            if (canReorder)
            {
                HandleRow(parent, index, rect, layout);
                DrawDropLine(parent, index, rect);
            }

            if (node.IsSubMenu && open) DrawMenu(node, layout, depth + 1, false);
        }

        /// <summary>The control paging adds to lead to the next page, drawn
        /// where the build will put it so the pages read as the wheel will.</summary>
        void DrawLink(int depth, string name, Texture2D icon, int target)
        {
            var rect = GUILayoutUtility.GetRect(10, RowHeight, GUILayout.ExpandWidth(true));
            zebra++;
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(rect, Styles.WithAlpha(Styles.Accent, 0.06f));

            // Same columns as a real row: icon, label, and the target where a
            // row's details go.
            var left = Left(rect, depth);
            if (icon != null)
                GUI.DrawTexture(IconRect(rect, left), icon, ScaleMode.ScaleToFit);
            else
                GUI.Label(new Rect(left + IconX, rect.y, 16, rect.height), "↪", Styles.LinkArrow);

            var x = left + NameX;
            var detail = DetailRect(rect);
            GUI.Label(detail, L.Tr("ui.next_page", target), Styles.Detail);
            GUI.Label(new Rect(x, rect.y, Mathf.Max(0, detail.x - Block - pillColumn - Block - x), rect.height),
                      new GUIContent(name, L["ui.link.tip"]), Styles.Link);
        }

        Rect DetailRect(Rect row) =>
            new Rect(Right(row) - detailColumn, row.y, detailColumn, row.height);

        /// <summary>A page label with a rule running to the edge. The space
        /// above it is what separates the pages, so the first page, with
        /// nothing above to separate from, gets less.</summary>
        void DrawPageBand(int depth, int page, bool first)
        {
            const float labelHeight = 16f;
            var rect = GUILayoutUtility.GetRect(10, labelHeight + (first ? Gap : Block * 2), GUILayout.ExpandWidth(true));
            if (Event.current.type != EventType.Repaint) return;

            var text = new GUIContent(L.Tr("ui.page", page));
            var style = Styles.PageLabel;
            var label = new Rect(Left(rect, depth) + GripX, rect.yMax - labelHeight - 1, style.CalcSize(text).x, labelHeight);
            GUI.Label(label, text, style);

            var ruleX = label.xMax + Block;
            EditorGUI.DrawRect(new Rect(ruleX, Mathf.Round(label.center.y), Mathf.Max(0, Right(rect) - ruleX), 1),
                               Styles.WithAlpha(Styles.Accent, 0.35f));
        }

        void DrawNote(int depth, string text)
        {
            var rect = GUILayoutUtility.GetRect(10, RowHeight, GUILayout.ExpandWidth(true));
            var x = Left(rect, depth) + NameX;
            GUI.Label(new Rect(x, rect.y, Mathf.Max(0, Right(rect) - x), rect.height), text, Styles.Unnamed);
        }

        void MeasureColumns(YukiMenuLayout layout)
        {
            var pill = 0f;
            var detail = Styles.Detail.CalcSize(new GUIContent(L.Tr("ui.next_page", 9))).x;
            Measure(tree, layout, ref pill, ref detail);
            pillColumn = pill + Block * 2;
            // A long parameter name must not squeeze the labels out; past this
            // it is clipped, and the full text is in the tooltip.
            detailColumn = Mathf.Min(detail, Mathf.Max(80f, position.width * 0.36f));
        }

        void Measure(MenuNode menu, YukiMenuLayout layout, ref float pill, ref float detail)
        {
            foreach (var node in menu.Children)
            {
                pill = Mathf.Max(pill, Styles.Pill.CalcSize(new GUIContent(TypeName(node.Type))).x);
                var text = Detail(node, layout);
                if (!string.IsNullOrEmpty(text))
                    detail = Mathf.Max(detail, Styles.Detail.CalcSize(new GUIContent(text)).x);
                if (node.IsSubMenu) Measure(node, layout, ref pill, ref detail);
            }
        }

        static float Left(Rect row, int depth) => row.x + Gutter + depth * Indent;
        static float Right(Rect row) => row.xMax - Gutter;
        static Rect IconRect(Rect row, float left) =>
            new Rect(left + IconX, row.y + (row.height - 16) / 2, 16, 16);

        string Detail(MenuNode node, YukiMenuLayout layout)
        {
            if (node.IsSubMenu)
            {
                var count = node.Children.Count;
                var pages = MenuPaginator.PageCount(count, Limit(layout, Paged(layout, false)));
                return pages > 1 ? L.Tr("ui.items_pages", count, pages) : L.Tr("ui.items", count);
            }
            if (string.IsNullOrEmpty(node.Parameter)) return "";
            // Buttons and toggles set a value; puppets drive a parameter freely.
            return node.Type == 101 || node.Type == 102
                ? $"{node.Parameter} = {node.Value:0.##}"
                : node.Parameter;
        }

        static string TypeName(int type) =>
            L.Has("type." + type) ? L["type." + type] : type.ToString();

        void ShowContextMenu(MenuNode parent, int index, YukiMenuLayout layout, bool canReorder)
        {
            var node = parent.Children[index];
            var last = parent.Children.Count - 1;
            var menu = new GenericMenu();

            if (canReorder)
            {
                Item(menu, L["ui.move_top"], index > 0, () => MoveTo(parent, index, 0, layout));
                Item(menu, L["ui.move_bottom"], index < last, () => MoveTo(parent, index, last, layout));
                menu.AddSeparator("");
            }
            if (node.IsSubMenu)
            {
                Item(menu, L["ui.expand_here"], true, () =>
                {
                    expanded.Add(node.Path);
                    ExpandAll(node);
                    Repaint();
                });
                menu.AddSeparator("");
            }
            Item(menu, L["ui.copy_name"], !string.IsNullOrEmpty(node.Name),
                 () => EditorGUIUtility.systemCopyBuffer = node.Name);
            Item(menu, L["ui.copy_param"], !string.IsNullOrEmpty(node.Parameter),
                 () => EditorGUIUtility.systemCopyBuffer = node.Parameter);
            menu.ShowAsContext();
        }

        static void Item(GenericMenu menu, string label, bool enabled, GenericMenu.MenuFunction action)
        {
            // GenericMenu reads "/" as a submenu; labels come from translations.
            var content = new GUIContent(label.Replace('/', '∕'));
            if (enabled) menu.AddItem(content, false, action);
            else menu.AddDisabledItem(content);
        }

        void ResetOrder(YukiMenuLayout layout)
        {
            Undo.RecordObject(layout, L["undo.reset_order"]);
            layout.order.Clear();
            EditorUtility.SetDirty(layout);
            stale = true;
        }

        // --- dragging ---------------------------------------------------------------

        /// <summary>
        /// A row is picked up from anywhere on it: by the time this runs, any
        /// button on the row that was clicked has already taken the event for
        /// itself, so whatever is left is a grab.
        /// </summary>
        void HandleRow(MenuNode parent, int index, Rect row, YukiMenuLayout layout)
        {
            var e = Event.current;
            EditorGUIUtility.AddCursorRect(row, MouseCursor.Pan);

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (e.button != 0 || !row.Contains(e.mousePosition)) break;
                    grabbed = new DragRow { Parent = parent, Index = index };
                    e.Use();
                    break;

                case EventType.MouseDrag:
                    // Whichever row draws first sees this, so the drag is
                    // described by what was grabbed, not by what is being drawn.
                    if (grabbed == null) break;
                    DragAndDrop.PrepareStartDrag();
                    DragAndDrop.objectReferences = new Object[0];
                    DragAndDrop.SetGenericData(DragKey, grabbed);
                    DragAndDrop.StartDrag(grabbed.Parent.Children[grabbed.Index].Name);
                    grabbed = null;
                    e.Use();
                    break;

                case EventType.DragUpdated:
                case EventType.DragPerform:
                    if (!row.Contains(e.mousePosition)) break;
                    var payload = DragAndDrop.GetGenericData(DragKey) as DragRow;
                    // Only within one menu: moving an item to another wheel is
                    // a different thing entirely, and not one paging can express.
                    if (payload == null || payload.Parent != parent)
                    {
                        DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                        break;
                    }

                    var below = e.mousePosition.y > row.center.y;
                    dropMenu = parent.Path;
                    dropIndex = below ? index + 1 : index;
                    DragAndDrop.visualMode = DragAndDropVisualMode.Move;

                    if (e.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        var to = dropIndex > payload.Index ? dropIndex - 1 : dropIndex;
                        MoveTo(parent, payload.Index, to, layout);
                        dropIndex = -1;
                        dropMenu = null;
                    }
                    e.Use();
                    Repaint();
                    break;
            }
        }

        void DrawDropLine(MenuNode parent, int index, Rect row)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (dropIndex < 0 || dropMenu != parent.Path) return;

            var last = index == parent.Children.Count - 1;
            float y;
            if (dropIndex == index) y = row.yMin - 1f;
            else if (dropIndex == index + 1 && last) y = row.yMax - 1f;
            else return;

            EditorGUI.DrawRect(new Rect(row.x, y, row.width, 2f), Styles.Accent);
        }

        void HandleDragEnd()
        {
            var e = Event.current;
            if (e.type == EventType.DragExited || e.type == EventType.MouseUp)
            {
                grabbed = null;
                if (dropIndex >= 0) { dropIndex = -1; dropMenu = null; }
                Repaint();
            }
        }

        void MoveTo(MenuNode parent, int from, int to, YukiMenuLayout layout)
        {
            if (from == to || from < 0 || from >= parent.Children.Count) return;
            queued = new PendingMove { Parent = parent, From = from, To = to, Layout = layout };
            Repaint();
        }

        /// <summary>
        /// Moving a row rewrites the whole recorded order for that one menu, so
        /// the recording always describes a complete arrangement rather than a
        /// list of nudges that later builds would have to reconcile.
        /// </summary>
        void ApplyQueued()
        {
            if (queued == null) return;
            var move = queued;
            queued = null;

            var parent = move.Parent;
            var to = Mathf.Clamp(move.To, 0, parent.Children.Count - 1);
            var node = parent.Children[move.From];
            parent.Children.RemoveAt(move.From);
            parent.Children.Insert(to, node);
            Repaint();

            if (move.Layout == null)
            {
                EditorUtility.DisplayDialog(L["ui.title"], L["ui.order_needs_component"], L["ui.ok"]);
                return;
            }

            Undo.RecordObject(move.Layout, L["undo.reorder"]);
            var group = move.Layout.Ensure(parent.Path);
            group.items = parent.Children.Select(c => c.Key()).ToList();
            EditorUtility.SetDirty(move.Layout);
        }

        // --- look -------------------------------------------------------------------

        internal static class Styles
        {
            static bool Pro => EditorGUIUtility.isProSkin;

            public static Color Accent => Pro ? new Color(0.36f, 0.62f, 1f) : new Color(0.15f, 0.42f, 0.86f);
            public static Color Zebra => Pro ? new Color(1f, 1f, 1f, 0.025f) : new Color(0f, 0f, 0f, 0.035f);
            public static Color Hover => Pro ? new Color(1f, 1f, 1f, 0.06f) : new Color(0f, 0f, 0f, 0.07f);
            public static Color ButtonTint => Pro ? new Color(0.55f, 0.78f, 1f) : new Color(0.7f, 0.85f, 1f);

            public static Color WithAlpha(Color c, float a) { c.a = a; return c; }

            /// <summary>One hue per kind of control, so a wheel's make-up reads at a glance.</summary>
            public static Color TypeColor(int type)
            {
                Color c;
                switch (type)
                {
                    case 101: c = new Color(0.38f, 0.58f, 0.95f); break; // button
                    case 102: c = new Color(0.36f, 0.76f, 0.46f); break; // toggle
                    case 103: c = new Color(0.66f, 0.47f, 0.92f); break; // submenu
                    case 201:
                    case 202: c = new Color(0.95f, 0.62f, 0.3f); break;  // two / four axis
                    case 203: c = new Color(0.3f, 0.76f, 0.8f); break;   // radial
                    default: c = new Color(0.6f, 0.6f, 0.6f); break;
                }
                return WithAlpha(c, Pro ? 0.3f : 0.28f);
            }

            // The editor's own window colour, for covering what is under the
            // hover arrows.
            public static Color Background => Pro ? new Color(0.22f, 0.22f, 0.22f) : new Color(0.76f, 0.76f, 0.76f);
            public static Color Divider => Pro ? new Color(0f, 0f, 0f, 0.35f) : new Color(0f, 0f, 0f, 0.15f);

            // Text inside the rows is placed on exact columns, so the styles
            // carry no padding of their own; the spacing all comes from the
            // window's constants.
            static RectOffset None => new RectOffset(0, 0, 0, 0);

            static GUIStyle _padded, _card, _footer, _middle, _name, _unnamed, _detail, _pill, _grip, _link, _linkArrow,
                            _page, _toolbarTitle, _boldFoldout, _summaryRight, _emptyNote;

            public static GUIStyle Padded => _padded ??= new GUIStyle
                { padding = new RectOffset((int)Gutter, (int)Gutter, (int)Block, (int)Block) };
            public static GUIStyle Card => _card ??= new GUIStyle(EditorStyles.helpBox)
                { padding = new RectOffset((int)Block, (int)Block, (int)Gap + 2, (int)Block) };
            public static GUIStyle Footer => _footer ??= new GUIStyle
                { padding = new RectOffset((int)Gutter, (int)Gutter, (int)Gap + 2, (int)Block) };
            public static GUIStyle Middle => _middle ??= new GUIStyle(EditorStyles.label)
                { alignment = TextAnchor.MiddleLeft, padding = None, clipping = TextClipping.Clip };
            public static GUIStyle Name => _name ??= new GUIStyle(EditorStyles.label)
                // VRChat renders rich text in menu labels, so show them the same way.
                { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, richText = true, padding = None };
            public static GUIStyle Unnamed => _unnamed ??= new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, fontStyle = FontStyle.Italic,
                padding = None, normal = { textColor = EditorStyles.centeredGreyMiniLabel.normal.textColor },
            };
            public static GUIStyle Detail => _detail ??= new GUIStyle(EditorStyles.miniLabel)
                { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, padding = None };
            public static GUIStyle Pill => _pill ??= new GUIStyle(EditorStyles.miniLabel)
                { alignment = TextAnchor.MiddleCenter, padding = None, fontSize = 10 };
            public static GUIStyle Grip => _grip ??= new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                { alignment = TextAnchor.MiddleCenter, padding = None };
            public static GUIStyle Link => _link ??= new GUIStyle(Unnamed);
            public static GUIStyle LinkArrow => _linkArrow ??= new GUIStyle(Unnamed)
                { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Normal };
            public static GUIStyle ToolbarTitle => _toolbarTitle ??= new GUIStyle(EditorStyles.miniBoldLabel)
                { alignment = TextAnchor.MiddleLeft };
            public static GUIStyle BoldFoldout => _boldFoldout ??= new GUIStyle(EditorStyles.foldout)
                { fontStyle = FontStyle.Bold };
            public static GUIStyle SummaryRight => _summaryRight ??= new GUIStyle(EditorStyles.miniLabel)
                { alignment = TextAnchor.MiddleRight, clipping = TextClipping.Clip, padding = None };
            public static GUIStyle EmptyNote => _emptyNote ??= new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                { wordWrap = true, fontSize = 11 };

            public static GUIStyle PageLabel
            {
                get
                {
                    _page ??= new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleLeft, padding = None };
                    _page.normal.textColor = Accent;
                    return _page;
                }
            }

            static Texture _refresh;
            public static Texture RefreshIcon => _refresh ??= EditorGUIUtility.IconContent("Refresh").image;
        }
    }
}
