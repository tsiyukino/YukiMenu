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
    /// What the other tools build is read back from a real build, so it is the
    /// answer rather than an estimate — which is also why it is not live: it is
    /// taken when asked for, and goes stale as soon as anything changes. The
    /// order and structure set here are laid over it by the same code the build
    /// uses, so those show the moment they are made. The paging is shown as
    /// bands drawn between the items, with the "next page" link drawn where the
    /// build will put it, rather than as "More" submenus to click through,
    /// because the pages follow from the order and the order is the thing being
    /// edited.
    ///
    /// Changing which menu something is in is kept behind a switch. Reordering
    /// is what a drag does most of the time, and a drag that strays a row too
    /// far should not quietly carry an item off into a submenu. Moves made
    /// deliberately from the context menu need no switch.
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
        const string ChangesOpenKey = "moe.tsiyuki.menu.changesOpen";

        // A press has to travel this far before it is a drag, so a click that
        // wobbles does not pick the row up.
        const float DragThreshold = 6f;
        // How long a dragged row has to rest on a closed submenu to open it.
        const double SpringDelay = 0.6;
        // Dragging this close to the top or bottom of the list scrolls it.
        const float ScrollZone = 28f;
        const float ScrollSpeed = 12f;

        [SerializeField] VRCAvatarDescriptor avatar;

        Vector2 scroll;
        Rect listRect;

        // What the other tools built, read back from the build.
        MenuNode raw;
        // What the build will make of it: raw with the order and structure
        // laid over. Only recomputed between frames (see Compose).
        MenuNode tree;
        List<StructureProblem> problems = new List<StructureProblem>();
        MenuNode composedRaw;
        YukiMenuLayout composedLayout;
        int composedStamp = -1;
        bool recompose;
        readonly Dictionary<string, MenuNode> folderRows = new Dictionary<string, MenuNode>();

        string error;
        bool stale = true;
        bool ranFull;
        long buildMs;

        // A build makes and throws away a copy of the avatar, which itself
        // raises hierarchyChanged; that echo must not mark the result stale.
        double quietUntil;

        // Only these are open; everything else is closed. Keyed by container,
        // which a submenu keeps wherever it is moved.
        readonly HashSet<string> expanded = new HashSet<string>();

        // --- search ---
        string filter = "";
        SearchField searchField;
        // The rows to show while searching; null when not searching.
        HashSet<MenuNode> visible;

        int zebra;
        bool settingsOpen = true;
        bool changesOpen;

        // Off whenever the window opens: moving things between menus is the
        // exception, and the switch is how it stays one.
        bool editStructure;

        // Widths of the type and detail columns, measured over the whole tree
        // so the tags and details line up down the list, and do not jump
        // about as submenus open and close.
        float pillColumn;
        float detailColumn;

        // --- dragging ---
        class DragRow { public MenuNode Node; }
        const string DragKey = "moe.tsiyuki.menu.row";
        MenuNode grabbed;
        Vector2 grabbedAt;
        MenuNode dragging;

        // Every row drawn in this pass, for working out where a drag would land.
        class RowInfo
        {
            public MenuNode Menu;   // the menu the row is in (for a note: the empty menu)
            public int Index;
            public Rect Rect;
            public int Depth;
            public bool IsNote;
        }
        readonly List<RowInfo> rows = new List<RowInfo>();

        /// <summary>Where a dragged row would go: before
        /// <see cref="Index"/> in <see cref="Menu"/>.</summary>
        class Drop
        {
            public MenuNode Menu;
            public int Index;
            public float Y;
            public int Depth;
            public Rect Note;       // set when dropping into an empty menu
            public bool Valid;
            public bool Cross;      // into a different menu
            public bool NoOp;       // back where it already is
            public string Reason;   // why not, when not valid
        }
        Drop drop;

        string springContainer;
        double springSince;
        float autoScroll;

        // A change is queued rather than applied where it is asked for:
        // changing the rows in the middle of drawing them would leave IMGUI's
        // layout pass and its paint pass disagreeing about what is on screen.
        System.Action queued;

        // Where the pointer was when a context menu opened, in screen space,
        // for placing what that menu opens.
        Vector2 contextAt;

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
            changesOpen = EditorPrefs.GetBool(ChangesOpenKey, false);
            EditorApplication.hierarchyChanged += MarkStale;
            EditorApplication.update += Tick;
            Undo.undoRedoEvent += OnUndoRedo;
            if (avatar == null) avatar = Guess();
        }

        void OnDisable()
        {
            EditorApplication.hierarchyChanged -= MarkStale;
            EditorApplication.update -= Tick;
            Undo.undoRedoEvent -= OnUndoRedo;
        }

        void MarkStale()
        {
            if (EditorApplication.timeSinceStartup < quietUntil) return;
            stale = true;
            Repaint();
        }

        // What this window and the folder editor record, none of which
        // changes what the other tools build.
        static readonly string[] OwnUndo =
        {
            "undo.settings", "undo.reorder", "undo.reset_order", "undo.move", "undo.move_home",
            "undo.new_folder", "undo.edit_folder", "undo.dissolve", "undo.clean", "undo.clear_structure",
        };

        void OnUndoRedo(in UndoRedoInfo info)
        {
            // The lists were read back in as new objects; the rows must stop
            // pointing at the old ones.
            recompose = true;
            var name = info.undoName;
            if (OwnUndo.Any(key => L[key] == name)) Quiet();
            Repaint();
        }

        /// <summary>Our own edits are not the scene changing under the
        /// snapshot, so the echo they raise is let pass.</summary>
        internal void Quiet() => quietUntil = EditorApplication.timeSinceStartup + 0.5;

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
            var e = Event.current;
            if (e.type == EventType.MouseMove) Repaint();

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
                    if (change.changed)
                    {
                        raw = null;
                        tree = null;
                        error = null;
                        stale = true;
                        expanded.Clear();
                    }
                }

                if (avatar == null)
                {
                    EditorGUILayout.Space(Gap);
                    EditorGUILayout.HelpBox(L["ui.pick_avatar"], MessageType.Info);
                    return;
                }

                layout = Layout;
                if (layout == null) editStructure = false;

                EditorGUILayout.Space(Block);
                DrawSettings(layout);
                EditorGUILayout.Space(Block);
                DrawRefresh();

                if (!string.IsNullOrEmpty(error))
                {
                    EditorGUILayout.Space(Gap);
                    EditorGUILayout.HelpBox(L.Tr("ui.build_failed", error), MessageType.Error);
                }

                Compose(layout);

                if (layout != null && layout.HasStructure)
                {
                    EditorGUILayout.Space(Block);
                    DrawChanges(layout);
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

            rows.Clear();
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

                HandleDrag(layout);
                PaintDrop();
            }
            if (e.type == EventType.Repaint) listRect = GUILayoutUtility.GetLastRect();

            DrawFooter();
            HandleDragEnd();
            ApplyQueued();
        }

        /// <summary>
        /// Lays the order and structure over what the tools built, whenever
        /// either has changed. Only at the start of a frame: the layout pass
        /// and the paint pass have to see the same rows.
        /// </summary>
        void Compose(YukiMenuLayout layout)
        {
            if (raw == null)
            {
                tree = null;
                return;
            }

            var stamp = layout != null ? EditorUtility.GetDirtyCount(layout) : -1;
            var current = tree != null && !recompose && composedRaw == raw && composedLayout == layout &&
                          composedStamp == stamp;
            if (current) return;
            if (tree != null && Event.current.type != EventType.Layout) return;

            tree = MenuPreview.Compose(raw, layout, out problems);
            composedRaw = raw;
            composedLayout = layout;
            composedStamp = stamp;
            recompose = false;

            folderRows.Clear();
            IndexFolders(tree);

            // What was being dragged, or waited on, belongs to the old rows.
            grabbed = null;
            drop = null;
        }

        void IndexFolders(MenuNode menu)
        {
            foreach (var child in menu.Children)
            {
                if (child.IsFolder && !folderRows.ContainsKey(child.Folder.id)) folderRows[child.Folder.id] = child;
                if (child.IsSubMenu) IndexFolders(child);
            }
        }

        void Tick()
        {
            var active = dragging != null;
            if (springContainer != null)
            {
                if (!active) springContainer = null;
                else if (EditorApplication.timeSinceStartup - springSince >= SpringDelay)
                {
                    expanded.Add(springContainer);
                    springContainer = null;
                }
                Repaint();
            }
            if (active && autoScroll != 0f)
            {
                scroll.y = Mathf.Max(0f, scroll.y + autoScroll);
                Repaint();
            }
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

            var content = new GUIContent(" " + L[raw == null ? "ui.build" : "ui.refresh"],
                                         Styles.RefreshIcon, L["ui.build.tip"]);
            var width = Mathf.Max(130f, GUI.skin.button.CalcSize(content).x + Block * 2);
            var button = new Rect(row.x, row.y, Mathf.Min(width, row.width), height);

            var previous = GUI.backgroundColor;
            if (raw == null || stale) GUI.backgroundColor = Styles.ButtonTint;
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

            if (raw != null && ranFull)
            {
                EditorGUILayout.Space(Gap);
                EditorGUILayout.LabelField(L.Tr("ui.with_vrcfury", Seconds), YukiGUI.WrapMini);
            }
        }

        string Seconds => (buildMs / 1000f).ToString("0.0");

        YukiStatus Status(out string text)
        {
            if (!string.IsNullOrEmpty(error)) { text = L["ui.status.failed"]; return YukiStatus.Problem; }
            if (raw == null) { text = L["ui.status.unread"]; return YukiStatus.None; }
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
                raw = MenuSnapshot.Capture(avatar.gameObject, overflow, perPage, out error);
                ranFull = MenuSnapshot.LastRunWasFull;
                buildMs = MenuSnapshot.LastMilliseconds;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            tree = null;
            recompose = true;
            stale = false;
            quietUntil = EditorApplication.timeSinceStartup + 0.5;
            Repaint();
        }

        // --- structure changes --------------------------------------------------------

        string RootName => L["ui.root_short"];

        string Describe(string container, YukiMenuLayout layout) =>
            MenuPreview.Describe(container, layout, RootName);

        string MenuName(MenuNode menu)
        {
            if (menu == null || menu == tree) return RootName;
            var name = MenuPreview.Plain(menu.Name);
            return name.Length > 0 ? name : L["ui.unnamed"];
        }

        StructureProblem ProblemOf(MenuMove move) =>
            composedLayout != null ? problems.FirstOrDefault(p => p.Move == move) : null;

        StructureProblem ProblemOf(MenuFolder folder) =>
            composedLayout != null ? problems.FirstOrDefault(p => p.Folder == folder) : null;

        /// <summary>
        /// Every submenu made and every move, each with a way back. The list is
        /// the answer to "what did I change", and it is where a change that no
        /// longer finds its item shows up rather than failing silently.
        /// </summary>
        void DrawChanges(YukiMenuLayout layout)
        {
            var known = tree != null && composedLayout == layout;
            var broken = known ? problems.Count : 0;

            using (new EditorGUILayout.VerticalScope(Styles.Card))
            {
                var title = new GUIContent(L["ui.changes"]);
                var head = GUILayoutUtility.GetRect(10, 20, GUILayout.ExpandWidth(true));
                var open = EditorGUI.Foldout(head, changesOpen, title, true, Styles.BoldFoldout);
                if (open != changesOpen)
                {
                    changesOpen = open;
                    EditorPrefs.SetBool(ChangesOpenKey, open);
                }

                var titleWidth = Styles.BoldFoldout.CalcSize(title).x + Block * 2;
                var summary = L.Tr("ui.changes.summary", layout.folders.Count, layout.moves.Count);
                if (broken > 0) summary += "  ·  " + L.Tr("ui.changes.broken", broken);
                GUI.Label(new Rect(head.x + titleWidth, head.y, Mathf.Max(0, head.width - titleWidth), head.height),
                          summary, broken > 0 ? Styles.WarningRight : Styles.SummaryRight);

                if (!changesOpen) return;

                if (!layout.repage)
                {
                    EditorGUILayout.Space(Gap);
                    EditorGUILayout.LabelField(L["ui.changes.off"], Styles.WarningWrap);
                }
                if (!known)
                {
                    EditorGUILayout.Space(Gap);
                    EditorGUILayout.LabelField(L["ui.changes.unread"], YukiGUI.WrapMini);
                }

                EditorGUILayout.Space(Gap);

                foreach (var folder in layout.folders.ToList())
                {
                    if (folder == null) continue;
                    MenuNode row;
                    folderRows.TryGetValue(folder.id ?? "", out row);
                    var count = row != null ? L.Tr("ui.items", row.Children.Count) : "";
                    var text = L.Tr("ui.changes.folder", MenuPreview.Plain(folder.name), Describe(folder.parent, layout), count);
                    var problem = known ? ProblemOf(folder) : null;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(Styles.FolderIcon, GUILayout.Width(16), GUILayout.Height(16));
                        GUILayout.Label(new GUIContent(text, problem != null ? Reason(problem) : text),
                                        problem != null ? Styles.WarningLine : Styles.ChangeLine);
                        using (new EditorGUI.DisabledScope(row == null || !editStructure))
                        {
                            var tip = editStructure ? "" : L["ui.folder.needs_edit"];
                            if (GUILayout.Button(new GUIContent(L["ui.changes.dissolve"], tip), EditorStyles.miniButton,
                                                 GUILayout.Width(56)))
                                Queue(() => Dissolve(row, layout));
                        }
                    }
                    if (problem != null) EditorGUILayout.LabelField(Reason(problem), Styles.WarningWrap);
                }

                foreach (var move in layout.moves.ToList())
                {
                    if (move == null) continue;
                    var name = MenuPreview.Plain(move.item != null ? move.item.name : "");
                    if (name.Length == 0) name = L["ui.unnamed"];
                    var text = L.Tr("ui.changes.move", name, Describe(move.from, layout), Describe(move.to, layout));
                    var problem = known ? ProblemOf(move) : null;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(new GUIContent("↷"), Styles.MovedMark, GUILayout.Width(16));
                        GUILayout.Label(new GUIContent(text, problem != null ? Reason(problem) : text),
                                        problem != null ? Styles.WarningLine : Styles.ChangeLine);
                        var label = problem != null ? L["ui.changes.remove"] : L["ui.changes.undo_move"];
                        if (GUILayout.Button(new GUIContent(label, L["ui.changes.undo_move.tip"]), EditorStyles.miniButton,
                                             GUILayout.Width(56)))
                        {
                            var target = move;
                            Queue(() => ForgetMove(target, layout));
                        }
                    }
                    if (problem != null) EditorGUILayout.LabelField(Reason(problem), Styles.WarningWrap);
                }

                EditorGUILayout.Space(Gap);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (broken > 0 && GUILayout.Button(L.Tr("ui.changes.clean", broken), EditorStyles.miniButton,
                                                       GUILayout.ExpandWidth(false)))
                        Queue(() => CleanBroken(layout));
                    if (GUILayout.Button(L["ui.changes.clear_all"], EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                        Queue(() => ClearStructure(layout));
                }
            }
        }

        static string Reason(StructureProblem problem)
        {
            if (problem.Folder != null) return L["problem.folder_parent"];
            switch (problem.Kind)
            {
                case StructureProblemKind.MissingItem: return L["problem.missing_item"];
                case StructureProblemKind.MissingTarget: return L["problem.missing_target"];
                default: return L["problem.itself"];
            }
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

        bool IsOpen(MenuNode node) =>
            node.IsSubMenu && (expanded.Contains(node.Container) || (Searching && node.Children.Any(visible.Contains)));

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

                GUILayout.Space(Gap);

                if (editStructure && layout != null)
                {
                    if (GUILayout.Button(new GUIContent(L["ui.new_folder"], L["ui.new_folder.tip"]),
                                         EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
                    {
                        var at = GUILayoutUtility.GetLastRect();
                        var screen = GUIUtility.GUIToScreenPoint(new Vector2(at.x, at.yMax));
                        Queue(() => NewFolder(tree, int.MaxValue, layout, screen));
                    }
                }

                using (new EditorGUI.DisabledScope(layout == null))
                {
                    var content = new GUIContent(" " + L["ui.edit_structure"],
                                                 editStructure ? Styles.UnlockedIcon : Styles.LockedIcon,
                                                 layout == null ? L["ui.order_needs_component"] : L["ui.edit_structure.tip"]);
                    var previous = GUI.backgroundColor;
                    if (editStructure) GUI.backgroundColor = Styles.MoveTint;
                    var next = GUILayout.Toggle(editStructure, content, EditorStyles.toolbarButton,
                                                GUILayout.ExpandWidth(false));
                    GUI.backgroundColor = previous;
                    if (next != editStructure)
                    {
                        editStructure = next;
                        drop = null;
                    }
                }
            }
        }

        void DrawFooter()
        {
            // A hairline rather than a gap: the list above scrolls, so empty
            // space alone would not say where it stops.
            var line = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(line, editStructure ? Styles.MoveColor : Styles.Divider);

            var hint = Searching ? L["ui.filter_no_reorder"]
                     : editStructure ? L["ui.drag_hint_edit"]
                     : L["ui.drag_hint"];
            using (new EditorGUILayout.HorizontalScope(Styles.Footer))
                GUILayout.Label(hint, YukiGUI.WrapMini);
        }

        void ExpandAll(MenuNode menu)
        {
            foreach (var child in menu.Children)
            {
                if (!child.IsSubMenu) continue;
                expanded.Add(child.Container);
                ExpandAll(child);
            }
        }

        void DrawMenu(MenuNode menu, YukiMenuLayout layout, int depth, bool isRoot)
        {
            if (menu.Children.Count == 0)
            {
                DrawNote(menu, depth, L["ui.empty"]);
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
            var hover = rect.Contains(e.mousePosition) && GUIUtility.hotControl == 0 && dragging == null;
            var open = IsOpen(node);

            if (canReorder) rows.Add(new RowInfo { Menu = parent, Index = index, Rect = rect, Depth = depth });

            if (e.type == EventType.Repaint)
            {
                if ((zebra & 1) == 1) EditorGUI.DrawRect(rect, Styles.Zebra);
                if (dragging != null && dragging == node)
                    EditorGUI.DrawRect(rect, Styles.WithAlpha(Styles.Accent, 0.18f));
                else if (hover)
                    EditorGUI.DrawRect(rect, Styles.Hover);

                // A closed submenu filling up while a row rests on it, so the
                // wait before it opens is seen rather than guessed at.
                if (springContainer != null && !open && node.IsSubMenu && node.Container == springContainer)
                {
                    var t = Mathf.Clamp01((float)((EditorApplication.timeSinceStartup - springSince) / SpringDelay));
                    EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width * t, rect.height),
                                       Styles.WithAlpha(Styles.MoveColor, 0.16f));
                }
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
                    if (next) expanded.Add(node.Container); else expanded.Remove(node.Container);
                    open = next;
                }
            }

            if (canReorder) GUI.Label(new Rect(left + GripX, rect.y, 12, rect.height), "≡", Styles.Grip);

            var icon = node.Icon != null ? node.Icon : node.IsFolder ? Styles.FolderIcon : null;
            if (icon != null) GUI.DrawTexture(IconRect(rect, left), icon, ScaleMode.ScaleToFit);

            var x = left + NameX;

            // Fixed columns from the right: details, then the type tag; the
            // label takes what is left. Every gap between them is one Block.
            var detailRect = DetailRect(rect);
            var pill = new Rect(detailRect.x - Block - pillColumn, rect.y + (rect.height - 16) / 2, pillColumn, 16);

            var detail = Detail(node, layout);
            if (!string.IsNullOrEmpty(detail))
                GUI.Label(detailRect, new GUIContent(detail, detail), Styles.Detail);

            if (e.type == EventType.Repaint)
                EditorGUI.DrawRect(pill, node.IsFolder ? Styles.FolderColor : Styles.TypeColor(node.Type));
            GUI.Label(pill, node.IsFolder ? L["type.folder"] : TypeName(node.Type), Styles.Pill);

            var unnamed = string.IsNullOrEmpty(node.Name);
            var style = unnamed ? Styles.Unnamed : Styles.Name;
            var content = new GUIContent(unnamed ? L["ui.unnamed"] : node.Name, node.Path);
            var room = Mathf.Max(0, pill.x - Block - x);
            if (node.Moved)
            {
                // The mark follows the label, so it reads as part of the item.
                const float mark = 14f;
                var width = Mathf.Min(style.CalcSize(content).x, Mathf.Max(0, room - mark - Gap));
                GUI.Label(new Rect(x, rect.y, width, rect.height), content, style);
                GUI.Label(new Rect(x + width + Gap, rect.y, mark, rect.height),
                          new GUIContent("↷", L.Tr("ui.moved.tip", Describe(node.Origin, layout))), Styles.MovedMark);
            }
            else GUI.Label(new Rect(x, rect.y, room, rect.height), content, style);

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
                contextAt = GUIUtility.GUIToScreenPoint(e.mousePosition);
                ShowContextMenu(parent, index, layout, canReorder);
                e.Use();
            }

            if (canReorder) HandleRowMouse(node, rect, layout);

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

        /// <summary>The line an empty menu shows, which is also where something
        /// dragged into it is dropped.</summary>
        void DrawNote(MenuNode menu, int depth, string text)
        {
            var rect = GUILayoutUtility.GetRect(10, RowHeight, GUILayout.ExpandWidth(true));
            if (!Searching) rows.Add(new RowInfo { Menu = menu, Index = 0, Rect = rect, Depth = depth, IsNote = true });
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
                var tag = node.IsFolder ? L["type.folder"] : TypeName(node.Type);
                pill = Mathf.Max(pill, Styles.Pill.CalcSize(new GUIContent(tag)).x);
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

        // --- context menu -----------------------------------------------------------

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

            // Moving by name is deliberate enough not to need the switch.
            if (layout != null) AddMoveTargets(menu, node, layout);
            else Item(menu, L["ui.move_to"], false, null);
            if (node.Moved && layout != null)
            {
                var home = Describe(node.Origin, layout);
                Item(menu, L.Tr("ui.move_home", home), true, () => Queue(() => ReturnHome(node, layout)));
            }

            if (layout != null)
            {
                var screen = contextAt;
                menu.AddSeparator("");
                var hint = editStructure ? "" : "  " + L["ui.folder.needs_edit"];
                Item(menu, L["ui.new_folder_here"] + hint, editStructure && !Searching,
                     () => Queue(() => NewFolder(parent, index + 1, layout, screen)));
                if (node.IsSubMenu)
                    Item(menu, L.Tr("ui.new_folder_inside", MenuName(node)) + hint, editStructure,
                         () => Queue(() => NewFolder(node, int.MaxValue, layout, screen)));

                if (node.IsFolder)
                {
                    menu.AddSeparator("");
                    Item(menu, L["ui.folder.edit"], true, () => MenuFolderEditor.Open(layout, node.Folder, screen));
                    var dissolve = node.Children.Count > 0 ? L["ui.folder.dissolve"] : L["ui.folder.delete"];
                    Item(menu, dissolve + hint, editStructure, () => Queue(() => Dissolve(node, layout)));
                }
            }

            menu.AddSeparator("");
            if (node.IsSubMenu)
            {
                Item(menu, L["ui.expand_here"], true, () =>
                {
                    expanded.Add(node.Container);
                    ExpandAll(node);
                    Repaint();
                });
            }
            Item(menu, L["ui.copy_name"], !string.IsNullOrEmpty(node.Name),
                 () => EditorGUIUtility.systemCopyBuffer = node.Name);
            Item(menu, L["ui.copy_param"], !string.IsNullOrEmpty(node.Parameter),
                 () => EditorGUIUtility.systemCopyBuffer = node.Parameter);
            menu.ShowAsContext();
        }

        /// <summary>
        /// "Move to", with every menu the row could go into as a cascade that
        /// follows the tree. A menu that has submenus of its own opens onto
        /// them, and its own entry comes first in that cascade.
        /// </summary>
        void AddMoveTargets(GenericMenu menu, MenuNode node, YukiMenuLayout layout)
        {
            var prefix = Escape(L["ui.move_to"]) + "/";
            AddTarget(menu, prefix + Escape(RootName), tree, node, layout);
            menu.AddSeparator(prefix);
            AddTargets(menu, tree, prefix, node, layout);
        }

        void AddTargets(GenericMenu menu, MenuNode at, string prefix, MenuNode moving, YukiMenuLayout layout)
        {
            var used = new HashSet<string>();
            foreach (var child in at.Children)
            {
                if (!child.IsSubMenu || child == moving) continue;
                var label = Escape(MenuName(child));
                // GenericMenu merges entries with the same path; keep both.
                var unique = label;
                for (var n = 2; !used.Add(unique); n++) unique = label + " (" + n + ")";

                if (child.Children.Any(c => c.IsSubMenu && c != moving))
                {
                    AddTarget(menu, prefix + unique + "/" + Escape(L.Tr("ui.move_here", MenuName(child))), child, moving, layout);
                    menu.AddSeparator(prefix + unique + "/");
                    AddTargets(menu, child, prefix + unique + "/", moving, layout);
                }
                else AddTarget(menu, prefix + unique, child, moving, layout);
            }
        }

        void AddTarget(GenericMenu menu, string path, MenuNode target, MenuNode moving, YukiMenuLayout layout)
        {
            var here = target == moving.Parent;
            var ok = !here && !(moving.IsSubMenu && IsInside(target, moving));
            var content = new GUIContent(path);
            if (ok) menu.AddItem(content, false, () => Queue(() => MoveAcross(moving, target, int.MaxValue, layout)));
            else menu.AddDisabledItem(content, here);
        }

        /// <summary>GenericMenu reads "/" as a cascade and some characters as
        /// shortcut keys; labels come from the tools and from translations.</summary>
        static string Escape(string label) =>
            (label ?? "").Replace('/', '∕').Replace('%', '％').Replace('#', '＃').Replace('&', '＆');

        static void Item(GenericMenu menu, string label, bool enabled, GenericMenu.MenuFunction action)
        {
            var content = new GUIContent(Escape(label));
            if (enabled && action != null) menu.AddItem(content, false, action);
            else menu.AddDisabledItem(content);
        }

        /// <summary>Whether <paramref name="menu"/> is <paramref name="ancestor"/>
        /// or somewhere inside it.</summary>
        static bool IsInside(MenuNode menu, MenuNode ancestor)
        {
            for (var at = menu; at != null; at = at.Parent)
                if (at == ancestor) return true;
            return false;
        }

        // --- changing things ----------------------------------------------------------

        void Queue(System.Action action)
        {
            queued += action;
            Repaint();
        }

        void ApplyQueued()
        {
            if (queued == null) return;
            var actions = queued;
            queued = null;
            actions();
            Repaint();
        }

        void Touched(YukiMenuLayout layout)
        {
            EditorUtility.SetDirty(layout);
            recompose = true;
            Quiet();
            Repaint();
        }

        void ResetOrder(YukiMenuLayout layout)
        {
            Undo.RecordObject(layout, L["undo.reset_order"]);
            layout.order.Clear();
            Touched(layout);
        }

        void MoveTo(MenuNode parent, int from, int to, YukiMenuLayout layout)
        {
            if (from == to || from < 0 || from >= parent.Children.Count) return;
            Queue(() => Reorder(parent, from, to, layout));
        }

        /// <summary>
        /// Moving a row within its menu rewrites the whole recorded order for
        /// that menu, so the recording always describes a complete arrangement
        /// rather than a list of nudges that later builds would have to
        /// reconcile.
        /// </summary>
        void Reorder(MenuNode parent, int from, int to, YukiMenuLayout layout)
        {
            if (layout == null)
            {
                EditorUtility.DisplayDialog(L["ui.title"], L["ui.order_needs_component"], L["ui.ok"]);
                return;
            }
            var keys = parent.Children.Select(c => c.Key()).ToList();
            var key = keys[from];
            keys.RemoveAt(from);
            keys.Insert(Mathf.Clamp(to, 0, keys.Count), key);

            Undo.RecordObject(layout, L["undo.reorder"]);
            layout.Ensure(parent.Container).items = keys;
            Touched(layout);
        }

        /// <summary>
        /// Records that a row belongs in another container. A folder simply
        /// changes parent. Anything else is recorded against where its tool
        /// put it, so moving it again edits the one record, and moving it back
        /// home removes it.
        /// </summary>
        static void Place(MenuNode node, string container, YukiMenuLayout layout)
        {
            container = container ?? "";
            if (node.IsFolder)
            {
                node.Folder.parent = container;
                return;
            }
            if (node.Move != null)
            {
                if ((node.Move.from ?? "") == container) layout.moves.Remove(node.Move);
                else node.Move.to = container;
                return;
            }
            if ((node.Origin ?? "") == container) return;
            layout.moves.Add(new MenuMove { from = node.Origin ?? "", item = node.Key(), to = container });
        }

        /// <summary>
        /// Puts a row into another menu at a position. The position is written
        /// into that menu's order unless it is the end of a menu the tools
        /// alone filled, where anything the order does not mention goes anyway;
        /// once things have been moved in, "the end" has to be said.
        /// </summary>
        void MoveAcross(MenuNode node, MenuNode target, int index, YukiMenuLayout layout)
        {
            if (layout == null || node == null || target == null) return;
            if (node.IsSubMenu && IsInside(target, node)) return;

            Undo.RecordObject(layout, L["undo.move"]);
            Place(node, target.Container, layout);
            if (NeedsOrder(target, index))
            {
                var keys = target.Children.Select(c => c.Key()).ToList();
                keys.Insert(Mathf.Clamp(index, 0, keys.Count), node.Key());
                layout.Ensure(target.Container).items = keys;
            }
            Touched(layout);
            ShowNotification(new GUIContent(L.Tr("ui.moved_into", MenuName(target))), 1.5);
        }

        void ReturnHome(MenuNode node, YukiMenuLayout layout)
        {
            if (node.Move == null) return;
            Undo.RecordObject(layout, L["undo.move_home"]);
            layout.moves.Remove(node.Move);
            Touched(layout);
            ShowNotification(new GUIContent(L.Tr("ui.moved_home", Describe(node.Origin, layout))), 1.5);
        }

        void ForgetMove(MenuMove move, YukiMenuLayout layout)
        {
            Undo.RecordObject(layout, L["undo.move_home"]);
            layout.moves.Remove(move);
            Touched(layout);
        }

        void NewFolder(MenuNode menu, int index, YukiMenuLayout layout, Vector2 screen)
        {
            if (layout == null || menu == null) return;
            Undo.RecordObject(layout, L["undo.new_folder"]);
            var folder = new MenuFolder
            {
                id = System.Guid.NewGuid().ToString("N"),
                name = UniqueName(menu, L["ui.folder.default"]),
                parent = menu.Container,
            };
            layout.folders.Add(folder);
            if (NeedsOrder(menu, index))
            {
                var keys = menu.Children.Select(c => c.Key()).ToList();
                keys.Insert(Mathf.Clamp(index, 0, keys.Count), folder.Key());
                layout.Ensure(menu.Container).items = keys;
            }
            if (menu != tree) expanded.Add(menu.Container);
            Touched(layout);
            MenuFolderEditor.Open(layout, folder, screen);
        }

        /// <summary>Folders are placed before moved items when the structure is
        /// laid out, so only a menu nothing was put into ends where it seems to.</summary>
        static bool NeedsOrder(MenuNode menu, int index) =>
            index < menu.Children.Count || menu.Children.Any(c => c.IsFolder || c.Moved);

        static string UniqueName(MenuNode menu, string wanted)
        {
            var taken = new HashSet<string>(menu.Children.Select(c => c.Name));
            if (!taken.Contains(wanted)) return wanted;
            for (var n = 2; ; n++)
                if (!taken.Contains(wanted + " " + n)) return wanted + " " + n;
        }

        /// <summary>
        /// Removes a folder and puts what was in it where the folder was, in
        /// the folder's order. Nothing inside is lost or sent elsewhere.
        /// </summary>
        void Dissolve(MenuNode node, YukiMenuLayout layout)
        {
            if (node == null || !node.IsFolder || layout == null) return;
            var parent = node.Parent;
            if (parent == null) return;
            var into = parent.Container;
            var inside = node.Children.ToList();

            if (inside.Count > 0 && !EditorUtility.DisplayDialog(L["ui.title"],
                    L.Tr("ui.dissolve.confirm", MenuName(node), inside.Count, MenuName(parent)),
                    L["ui.dissolve.ok"], L["ui.cancel"]))
                return;

            Undo.RecordObject(layout, L["undo.dissolve"]);
            foreach (var child in inside) Place(child, into, layout);

            // Anything else still aimed at it: moves that did not find their
            // item this time, folders that did not find their way in.
            var id = node.Folder.Container;
            foreach (var move in layout.moves.ToList())
            {
                if (move.to != id) continue;
                if (move.from == into) layout.moves.Remove(move);
                else move.to = into;
            }
            foreach (var folder in layout.folders)
                if (folder != null && folder.parent == id) folder.parent = into;

            if (inside.Count > 0)
            {
                var keys = new List<MenuItemKey>();
                foreach (var child in parent.Children)
                {
                    if (child == node) keys.AddRange(inside.Select(c => c.Key()));
                    else keys.Add(child.Key());
                }
                layout.Ensure(into).items = keys;
            }

            layout.Forget(id);
            layout.folders.Remove(node.Folder);
            Touched(layout);
        }

        void CleanBroken(YukiMenuLayout layout)
        {
            if (problems.Count == 0) return;
            Undo.RecordObject(layout, L["undo.clean"]);
            foreach (var problem in problems)
            {
                if (problem.Move != null) layout.moves.Remove(problem.Move);
                // A folder whose menu has gone is shown at the root; saying so
                // makes it stay there.
                else if (problem.Folder != null) problem.Folder.parent = MenuContainer.Root;
            }
            Touched(layout);
        }

        void ClearStructure(YukiMenuLayout layout)
        {
            if (!EditorUtility.DisplayDialog(L["ui.title"],
                    L.Tr("ui.changes.clear_confirm", layout.folders.Count, layout.moves.Count),
                    L["ui.changes.clear_ok"], L["ui.cancel"]))
                return;
            Undo.RecordObject(layout, L["undo.clear_structure"]);
            layout.folders.Clear();
            layout.moves.Clear();
            layout.order.RemoveAll(g => g == null || MenuContainer.IsFolder(g.menuPath));
            Touched(layout);
        }

        // --- dragging ---------------------------------------------------------------

        /// <summary>
        /// A row is picked up from anywhere on it: by the time this runs, any
        /// button on the row that was clicked has already taken the event for
        /// itself, so whatever is left is a grab. It only becomes a drag once
        /// the pointer has moved (see HandleDrag).
        /// </summary>
        void HandleRowMouse(MenuNode node, Rect row, YukiMenuLayout layout)
        {
            var e = Event.current;
            EditorGUIUtility.AddCursorRect(row, MouseCursor.Pan);
            if (e.type != EventType.MouseDown || e.button != 0 || !row.Contains(e.mousePosition)) return;

            if (e.clickCount == 2)
            {
                grabbed = null;
                if (node.IsFolder && layout != null)
                    MenuFolderEditor.Open(layout, node.Folder, GUIUtility.GUIToScreenPoint(e.mousePosition));
                else if (node.IsSubMenu)
                {
                    if (!expanded.Remove(node.Container)) expanded.Add(node.Container);
                }
                e.Use();
                return;
            }

            grabbed = node;
            grabbedAt = e.mousePosition;
            e.Use();
        }

        /// <summary>Starting a drag, and working out where it lands. Runs once
        /// per event after every row is drawn, so it sees the whole list.</summary>
        void HandleDrag(YukiMenuLayout layout)
        {
            var e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDrag:
                    if (grabbed == null) break;
                    if ((e.mousePosition - grabbedAt).sqrMagnitude < DragThreshold * DragThreshold)
                    {
                        e.Use();
                        break;
                    }
                    DragAndDrop.PrepareStartDrag();
                    DragAndDrop.objectReferences = new Object[0];
                    DragAndDrop.SetGenericData(DragKey, new DragRow { Node = grabbed });
                    DragAndDrop.StartDrag(MenuPreview.Plain(grabbed.Name));
                    dragging = grabbed;
                    grabbed = null;
                    e.Use();
                    break;

                case EventType.DragUpdated:
                case EventType.DragPerform:
                    var payload = DragAndDrop.GetGenericData(DragKey) as DragRow;
                    if (payload == null || payload.Node == null) break;
                    dragging = payload.Node;

                    AutoScroll(e.mousePosition);
                    drop = FindDrop(e.mousePosition, payload.Node, layout);
                    Spring(e.mousePosition, payload.Node);
                    DragAndDrop.visualMode = drop != null && drop.Valid
                        ? DragAndDropVisualMode.Move
                        : DragAndDropVisualMode.Rejected;

                    if (e.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        var landed = drop;
                        var node = payload.Node;
                        if (landed != null && landed.Valid && !landed.NoOp)
                            Queue(() => Perform(node, landed, layout));
                        EndDrag();
                    }
                    e.Use();
                    Repaint();
                    break;
            }
        }

        Rect VisibleList => new Rect(scroll.x, scroll.y, listRect.width, listRect.height);

        void AutoScroll(Vector2 mouse)
        {
            autoScroll = 0f;
            if (listRect.height <= ScrollZone * 3) return;
            var y = mouse.y - scroll.y;
            if (y < ScrollZone) autoScroll = -ScrollSpeed * (1f - Mathf.Clamp01(y / ScrollZone));
            else if (y > listRect.height - ScrollZone)
                autoScroll = ScrollSpeed * (1f - Mathf.Clamp01((listRect.height - y) / ScrollZone));
        }

        /// <summary>
        /// Where a row dropped here would go.
        ///
        /// There is no dropping onto a row, only between rows: the line shows
        /// exactly where it will go, and a row that meant to land beside a
        /// submenu cannot end up inside it by being a few pixels out. The
        /// candidates for a spot are listed best first, and the first the
        /// current mode allows is the one used — so with the switch off, the
        /// same spot resolves to the nearest place in the row's own menu.
        /// </summary>
        Drop FindDrop(Vector2 mouse, MenuNode dragged, YukiMenuLayout layout)
        {
            if (rows.Count == 0 || Searching) return null;
            if (listRect.height > 0 && !VisibleList.Contains(mouse)) return null;

            RowInfo hit = null;
            var best = float.MaxValue;
            foreach (var row in rows)
            {
                var d = mouse.y < row.Rect.yMin ? row.Rect.yMin - mouse.y
                      : mouse.y > row.Rect.yMax ? mouse.y - row.Rect.yMax
                      : 0f;
                if (d < best) { best = d; hit = row; }
                if (d == 0f) break;
            }
            if (hit == null) return null;

            var options = new List<Drop>();
            if (hit.IsNote)
            {
                options.Add(new Drop { Menu = hit.Menu, Index = 0, Y = hit.Rect.center.y, Depth = hit.Depth, Note = hit.Rect });
                var owner = hit.Menu.Parent;
                if (owner != null)
                    options.Add(new Drop
                    {
                        Menu = owner, Index = owner.Children.IndexOf(hit.Menu) + 1, Y = hit.Rect.yMax, Depth = hit.Depth - 1,
                    });
            }
            else
            {
                var node = hit.Menu.Children[hit.Index];
                var below = mouse.y > hit.Rect.center.y;
                if (!below)
                {
                    options.Add(new Drop { Menu = hit.Menu, Index = hit.Index, Y = hit.Rect.yMin, Depth = hit.Depth });
                }
                else if (node.IsSubMenu && IsOpen(node))
                {
                    // Just under an open submenu's header is its first place.
                    options.Add(new Drop { Menu = node, Index = 0, Y = hit.Rect.yMax, Depth = hit.Depth + 1 });
                    options.Add(new Drop { Menu = hit.Menu, Index = hit.Index + 1, Y = hit.Rect.yMax, Depth = hit.Depth });
                }
                else if (hit.Index < hit.Menu.Children.Count - 1)
                {
                    options.Add(new Drop { Menu = hit.Menu, Index = hit.Index + 1, Y = hit.Rect.yMax, Depth = hit.Depth });
                }
                else
                {
                    // Under the last row of a menu is also the end of every
                    // menu that closes there. Which is meant is read from how
                    // far left the pointer is, as in the Hierarchy.
                    var chain = new List<Drop>
                    {
                        new Drop { Menu = hit.Menu, Index = hit.Menu.Children.Count, Y = hit.Rect.yMax, Depth = hit.Depth },
                    };
                    var at = hit.Menu;
                    var depth = hit.Depth;
                    while (at.Parent != null)
                    {
                        var i = at.Parent.Children.IndexOf(at);
                        chain.Add(new Drop { Menu = at.Parent, Index = i + 1, Y = hit.Rect.yMax, Depth = depth - 1 });
                        if (i < at.Parent.Children.Count - 1) break;
                        at = at.Parent;
                        depth--;
                    }
                    var pick = chain.FindIndex(c => mouse.x >= Left(hit.Rect, c.Depth) + GripX);
                    if (pick < 0) pick = chain.Count - 1;
                    options.Add(chain[pick]);
                    options.AddRange(chain.Where((c, k) => k != pick));
                }
            }

            foreach (var option in options) Judge(option, dragged, layout);
            return options.FirstOrDefault(o => o.Valid) ?? options[0];
        }

        void Judge(Drop drop, MenuNode dragged, YukiMenuLayout layout)
        {
            var home = dragged.Parent;
            drop.Cross = drop.Menu != home;
            if (!drop.Cross)
            {
                var from = home.Children.IndexOf(dragged);
                drop.NoOp = drop.Index == from || drop.Index == from + 1;
                drop.Valid = true;
                return;
            }
            if (layout == null) { drop.Reason = L["ui.order_needs_component"]; return; }
            if (!editStructure) { drop.Reason = L["ui.drop_locked"]; return; }
            if (dragged.IsSubMenu && IsInside(drop.Menu, dragged)) { drop.Reason = L["ui.drop_itself"]; return; }
            drop.Valid = true;
        }

        /// <summary>A closed submenu the row rests on opens after a moment, so
        /// a drag can go deeper without being let go of.</summary>
        void Spring(Vector2 mouse, MenuNode dragged)
        {
            if (!editStructure) { springContainer = null; return; }
            var hit = rows.FirstOrDefault(r => !r.IsNote && r.Rect.Contains(mouse));
            var node = hit != null ? hit.Menu.Children[hit.Index] : null;
            if (node == null || !node.IsSubMenu || IsOpen(node) || IsInside(node, dragged))
            {
                springContainer = null;
                return;
            }
            if (springContainer == node.Container) return;
            springContainer = node.Container;
            springSince = EditorApplication.timeSinceStartup;
        }

        void Perform(MenuNode node, Drop landed, YukiMenuLayout layout)
        {
            if (!landed.Cross)
            {
                var home = node.Parent;
                var from = home.Children.IndexOf(node);
                if (from < 0) return;
                var to = landed.Index > from ? landed.Index - 1 : landed.Index;
                Reorder(home, from, to, layout);
                return;
            }
            MoveAcross(node, landed.Menu, landed.Index, layout);
        }

        /// <summary>The line where the row will land, in blue within its own
        /// menu and orange into another, which is named beside it. A place it
        /// cannot go says why.</summary>
        void PaintDrop()
        {
            if (Event.current.type != EventType.Repaint) return;
            if (drop == null || dragging == null || drop.NoOp || rows.Count == 0) return;

            var width = rows[0].Rect;
            var color = !drop.Valid ? Styles.Rejected : drop.Cross ? Styles.MoveColor : Styles.Accent;
            if (drop.Note.height > 0 && drop.Menu != null && drop.Menu.Children.Count == 0)
            {
                EditorGUI.DrawRect(drop.Note, Styles.WithAlpha(color, 0.16f));
            }
            else
            {
                var x0 = Left(width, drop.Depth) + GripX;
                EditorGUI.DrawRect(new Rect(x0, drop.Y - 1, Mathf.Max(0, Right(width) - x0), 2), color);
                EditorGUI.DrawRect(new Rect(x0 - 3, drop.Y - 3, 6, 6), color);
            }

            var text = !drop.Valid ? drop.Reason
                     : drop.Cross ? L.Tr("ui.drop_into", MenuName(drop.Menu))
                     : null;
            if (string.IsNullOrEmpty(text)) return;
            var content = new GUIContent(text);
            var size = Styles.DropPill.CalcSize(content);
            var tag = new Rect(Right(width) - size.x - 8, drop.Y - size.y - 3, size.x + 8, size.y + 2);
            if (tag.y < scroll.y) tag.y = drop.Y + 3;
            EditorGUI.DrawRect(tag, Styles.WithAlpha(color, 0.92f));
            GUI.Label(tag, content, Styles.DropPill);
        }

        void EndDrag()
        {
            grabbed = null;
            dragging = null;
            drop = null;
            springContainer = null;
            autoScroll = 0f;
        }

        void HandleDragEnd()
        {
            var e = Event.current;
            if (e.type == EventType.DragExited || e.type == EventType.MouseUp)
            {
                EndDrag();
                Repaint();
            }
        }

        // --- look -------------------------------------------------------------------

        internal static class Styles
        {
            static bool Pro => EditorGUIUtility.isProSkin;

            public static Color Accent => Pro ? new Color(0.36f, 0.62f, 1f) : new Color(0.15f, 0.42f, 0.86f);
            // Anything that changes which menu something is in.
            public static Color MoveColor => Pro ? new Color(1f, 0.62f, 0.25f) : new Color(0.86f, 0.45f, 0.05f);
            public static Color MoveTint => Pro ? new Color(1f, 0.75f, 0.45f) : new Color(1f, 0.82f, 0.6f);
            public static Color Rejected => Pro ? new Color(0.62f, 0.62f, 0.62f) : new Color(0.45f, 0.45f, 0.45f);
            public static Color Warning => Pro ? new Color(0.95f, 0.8f, 0.35f) : new Color(0.6f, 0.42f, 0.02f);
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

            // A submenu made here rather than by a tool.
            public static Color FolderColor => WithAlpha(new Color(0.93f, 0.45f, 0.68f), Pro ? 0.32f : 0.3f);

            // The editor's own window colour, for covering what is under the
            // hover arrows.
            public static Color Background => Pro ? new Color(0.22f, 0.22f, 0.22f) : new Color(0.76f, 0.76f, 0.76f);
            public static Color Divider => Pro ? new Color(0f, 0f, 0f, 0.35f) : new Color(0f, 0f, 0f, 0.15f);

            // Text inside the rows is placed on exact columns, so the styles
            // carry no padding of their own; the spacing all comes from the
            // window's constants.
            static RectOffset None => new RectOffset(0, 0, 0, 0);

            static GUIStyle _padded, _card, _footer, _middle, _name, _unnamed, _detail, _pill, _grip, _link, _linkArrow,
                            _page, _toolbarTitle, _boldFoldout, _summaryRight, _emptyNote, _moved, _dropPill,
                            _changeLine, _warningLine, _warningWrap, _warningRight;

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
            public static GUIStyle ChangeLine => _changeLine ??= new GUIStyle(EditorStyles.miniLabel)
                { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };

            public static GUIStyle MovedMark
            {
                get
                {
                    _moved ??= new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, padding = None };
                    _moved.normal.textColor = MoveColor;
                    return _moved;
                }
            }

            public static GUIStyle DropPill
            {
                get
                {
                    _dropPill ??= new GUIStyle(EditorStyles.miniBoldLabel)
                        { alignment = TextAnchor.MiddleCenter, padding = new RectOffset(4, 4, 1, 1) };
                    _dropPill.normal.textColor = Pro ? new Color(0.1f, 0.1f, 0.1f) : Color.white;
                    return _dropPill;
                }
            }

            public static GUIStyle WarningLine
            {
                get
                {
                    _warningLine ??= new GUIStyle(EditorStyles.miniLabel)
                        { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
                    _warningLine.normal.textColor = Warning;
                    return _warningLine;
                }
            }

            public static GUIStyle WarningWrap
            {
                get
                {
                    _warningWrap ??= new GUIStyle(EditorStyles.miniLabel)
                        { wordWrap = true, padding = new RectOffset(20, 0, 0, 2) };
                    _warningWrap.normal.textColor = Warning;
                    return _warningWrap;
                }
            }

            public static GUIStyle WarningRight
            {
                get
                {
                    _warningRight ??= new GUIStyle(SummaryRight);
                    _warningRight.normal.textColor = Warning;
                    return _warningRight;
                }
            }

            public static GUIStyle PageLabel
            {
                get
                {
                    _page ??= new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleLeft, padding = None };
                    _page.normal.textColor = Accent;
                    return _page;
                }
            }

            static Texture _refresh, _folder, _locked, _unlocked;
            public static Texture RefreshIcon => _refresh ??= EditorGUIUtility.IconContent("Refresh").image;
            public static Texture FolderIcon => _folder ??= EditorGUIUtility.IconContent("Folder Icon").image;
            public static Texture LockedIcon => _locked ??= EditorGUIUtility.IconContent("IN LockButton on").image;
            public static Texture UnlockedIcon => _unlocked ??= EditorGUIUtility.IconContent("IN LockButton").image;
        }
    }
}
