using System.Collections.Generic;
using System.Linq;
using TsiYuki.Core.Editor;
using UnityEditor;
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
    /// lines drawn between the items rather than as "More" submenus to click
    /// through, because the pages follow from the order and the order is the
    /// thing being edited.
    ///
    /// Submenus start closed. A menu worth opening this window for has more
    /// items than fit on a screen, and the root wheel is what the question is
    /// usually about.
    /// </summary>
    public class MenuWindow : EditorWindow
    {
        static YukiLocalizer L => MenuText.L;

        [SerializeField] VRCAvatarDescriptor avatar;

        Vector2 scroll;
        MenuNode tree;
        string error;
        bool stale = true;
        bool ranFull;
        long buildMs;

        // Only these are open; everything else is closed.
        readonly HashSet<string> expanded = new HashSet<string>();

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
            EditorApplication.hierarchyChanged += MarkStale;
            if (avatar == null) avatar = Guess();
        }

        void OnDisable() => EditorApplication.hierarchyChanged -= MarkStale;

        void MarkStale() { stale = true; Repaint(); }

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
            YukiGUI.Header(L["ui.title"], Version);
            EditorGUILayout.LabelField(L["ui.intro"], YukiGUI.WrapMini);

            using (var change = new EditorGUI.ChangeCheckScope())
            {
                avatar = (VRCAvatarDescriptor)EditorGUILayout.ObjectField(
                    L["ui.avatar"], avatar, typeof(VRCAvatarDescriptor), true);
                if (change.changed) { tree = null; stale = true; expanded.Clear(); }
            }

            if (avatar == null)
            {
                EditorGUILayout.HelpBox(L["ui.pick_avatar"], MessageType.Info);
                return;
            }

            var layout = Layout;
            DrawSettings(layout);
            DrawRefresh();

            if (!string.IsNullOrEmpty(error))
                EditorGUILayout.HelpBox(L.Tr("ui.build_failed", error), MessageType.Error);

            if (tree == null) return;

            DrawTreeToolbar();

            using (var scope = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = scope.scrollPosition;
                DrawMenu(tree, layout, 0, true);
            }

            HandleDragEnd();
            ApplyQueued();
        }

        // --- settings ---------------------------------------------------------------

        void DrawSettings(YukiMenuLayout layout)
        {
            YukiGUI.Section(L["ui.settings"]);

            if (layout == null)
            {
                EditorGUILayout.HelpBox(L["ui.no_component"], MessageType.None);
                if (GUILayout.Button(L["ui.add_component"]))
                {
                    var host = new GameObject("Yuki Menu");
                    host.transform.SetParent(avatar.transform, false);
                    host.AddComponent<YukiMenuLayout>();
                    Undo.RegisterCreatedObjectUndo(host, L["undo.add_component"]);
                    Selection.activeGameObject = host;
                    stale = true;
                }
                return;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            using (var change = new EditorGUI.ChangeCheckScope())
            {
                var scope = (PagingScope)EditorGUILayout.EnumPopup(
                    new GUIContent(L["ui.scope"], L["ui.scope.tip"]), layout.scope);
                var perPage = EditorGUILayout.IntSlider(
                    new GUIContent(L["ui.per_page"], L["ui.per_page.tip"]),
                    layout.itemsPerPage, 2, MenuPaginator.VrcLimit);
                var repage = EditorGUILayout.Toggle(
                    new GUIContent(L["ui.repage"], L["ui.repage.tip"]), layout.repage);

                var name = layout.overflowName;
                var icon = layout.overflowIcon;
                var at = layout.overflowAt;

                using (new EditorGUI.IndentLevelScope())
                {
                    name = EditorGUILayout.TextField(
                        new GUIContent(L["ui.overflow_name"], L["ui.overflow_name.tip"]), layout.overflowName);
                    icon = (Texture2D)EditorGUILayout.ObjectField(
                        L["ui.overflow_icon"], layout.overflowIcon, typeof(Texture2D), false);
                    at = (OverflowPlacement)EditorGUILayout.EnumPopup(
                        new GUIContent(L["ui.overflow_at"], L["ui.overflow_at.tip"]), layout.overflowAt);
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

                if (perPage == MenuPaginator.VrcLimit)
                    EditorGUILayout.LabelField(L["ui.at_limit"], YukiGUI.WrapMini);
            }
        }

        void DrawRefresh()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(L[tree == null ? "ui.build" : "ui.refresh"], GUILayout.Height(24)))
                    Refresh();
                GUILayout.FlexibleSpace();
                if (tree != null && stale)
                    GUILayout.Label(L["ui.stale"], YukiGUI.WrapMini);
            }

            if (tree != null && ranFull)
                EditorGUILayout.LabelField(L.Tr("ui.with_vrcfury", (buildMs / 1000f).ToString("0.0")), YukiGUI.WrapMini);
            else
                EditorGUILayout.LabelField(L["ui.build.tip"], YukiGUI.WrapMini);
        }

        void Refresh()
        {
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
        }

        // --- the tree ---------------------------------------------------------------

        void DrawTreeToolbar()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(L["ui.drag_hint"], YukiGUI.WrapMini);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(L["ui.expand_all"], EditorStyles.miniButtonLeft, GUILayout.Width(70)))
                    ExpandAll(tree);
                if (GUILayout.Button(L["ui.collapse_all"], EditorStyles.miniButtonRight, GUILayout.Width(70)))
                    expanded.Clear();
            }
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
            var paged = layout != null && layout.repage &&
                        (isRoot || layout.scope == PagingScope.Everywhere);
            var limit = paged ? Mathf.Clamp(layout.itemsPerPage, 2, MenuPaginator.VrcLimit)
                              : MenuPaginator.VrcLimit;
            var breaks = MenuPaginator.PageBreaks(menu.Children.Count, limit);

            if (isRoot)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(L.Tr("ui.root_menu", menu.Children.Count.ToString()),
                                    YukiGUI.SectionHeaderStyle);
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(breaks.Count == 0 ? L["ui.one_page"] : L.Tr("ui.pages", (breaks.Count + 1).ToString()),
                                    EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                    DrawResetOrder(layout);
                }
            }

            if (menu.Children.Count == 0)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(depth * 14 + 14);
                    GUILayout.Label(L["ui.empty"], YukiGUI.WrapMini);
                }
                return;
            }

            for (var i = 0; i < menu.Children.Count; i++)
            {
                if (breaks.Contains(i)) DrawPageBreak(depth, breaks.IndexOf(i) + 2);
                DrawRow(menu, i, layout, depth);
            }
        }

        void DrawRow(MenuNode parent, int index, YukiMenuLayout layout, int depth)
        {
            var node = parent.Children[index];
            var open = node.IsSubMenu && expanded.Contains(node.Path);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(depth * 14);

                if (node.IsSubMenu)
                {
                    if (GUILayout.Button(open ? "▾" : "▸", EditorStyles.label, GUILayout.Width(14)))
                    {
                        if (open) expanded.Remove(node.Path); else expanded.Add(node.Path);
                        open = !open;
                    }
                }
                else GUILayout.Space(14);

                GUILayout.Label("≡", EditorStyles.centeredGreyMiniLabel, GUILayout.Width(12));

                if (node.Icon != null) GUILayout.Label(node.Icon, GUILayout.Width(18), GUILayout.Height(18));
                else GUILayout.Space(18);

                GUILayout.Label(string.IsNullOrEmpty(node.Name) ? L["ui.unnamed"] : node.Name,
                                GUILayout.MinWidth(60));
                GUILayout.FlexibleSpace();

                GUILayout.Label(Describe(node), EditorStyles.miniLabel, GUILayout.ExpandWidth(false));

                using (new EditorGUI.DisabledScope(index == 0))
                    if (GUILayout.Button("▲", EditorStyles.miniButtonLeft, GUILayout.Width(22)))
                        MoveTo(parent, index, index - 1, layout);

                using (new EditorGUI.DisabledScope(index == parent.Children.Count - 1))
                    if (GUILayout.Button("▼", EditorStyles.miniButtonRight, GUILayout.Width(22)))
                        MoveTo(parent, index, index + 1, layout);
            }

            var row = GUILayoutUtility.GetLastRect();
            HandleRow(parent, index, row, layout);
            DrawDropLine(parent, index, row);

            if (node.IsSubMenu && open) DrawMenu(node, layout, depth + 1, false);
        }

        string Describe(MenuNode node)
        {
            var type = L.Has("type." + node.Type) ? L["type." + node.Type] : node.Type.ToString();
            if (string.IsNullOrEmpty(node.Parameter)) return type;
            return $"{type}  ·  {node.Parameter} = {node.Value:0.##}";
        }

        void DrawPageBreak(int depth, int page)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(depth * 14 + 14);
                var rect = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true));
                EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.5f));
                GUILayout.Label(L.Tr("ui.page", page.ToString()), EditorStyles.miniLabel,
                                GUILayout.ExpandWidth(false));
            }
        }

        void DrawResetOrder(YukiMenuLayout layout)
        {
            if (layout == null || layout.order.Count == 0) return;
            if (!GUILayout.Button(L["ui.reset_order"], EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                return;
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
            if (dropIndex == index) y = row.yMin;
            else if (dropIndex == index + 1 && last) y = row.yMax - 2f;
            else return;

            EditorGUI.DrawRect(new Rect(row.x, y, row.width, 2f), new Color(0.3f, 0.65f, 1f, 0.9f));
        }

        void HandleDragEnd()
        {
            var e = Event.current;
            if (e.type == EventType.DragExited || e.type == EventType.MouseUp)
            {
                grabbed = null;
                if (dropIndex >= 0) { dropIndex = -1; dropMenu = null; Repaint(); }
            }
        }

        void MoveTo(MenuNode parent, int from, int to, YukiMenuLayout layout)
        {
            if (from == to || from < 0 || from >= parent.Children.Count) return;
            queued = new PendingMove { Parent = parent, From = from, To = to, Layout = layout };
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
    }
}
