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
    /// </summary>
    public class MenuWindow : EditorWindow
    {
        static YukiLocalizer L => MenuText.L;

        [SerializeField] VRCAvatarDescriptor avatar;

        Vector2 scroll;
        MenuNode tree;
        string error;
        bool stale = true;
        readonly HashSet<string> collapsed = new HashSet<string>();

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
                if (change.changed) { tree = null; stale = true; }
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

            using (var scope = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = scope.scrollPosition;
                DrawMenu(tree, layout, 0, true);
            }
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
            EditorGUILayout.LabelField(L["ui.build.tip"], YukiGUI.WrapMini);
        }

        void Refresh()
        {
            var layout = Layout;
            var overflow = layout != null && !string.IsNullOrEmpty(layout.overflowName)
                ? layout.overflowName
                : MenuPaginator.DefaultOverflowName;

            try
            {
                EditorUtility.DisplayProgressBar(L["ui.title"], L["ui.building"], 0.5f);
                tree = MenuSnapshot.Capture(avatar.gameObject, overflow, out error);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            stale = false;
        }

        // --- the tree ---------------------------------------------------------------

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
                    DrawPageBadge(breaks.Count + 1);
                    DrawResetOrder(layout, menu);
                }
            }

            if (menu.Children.Count == 0)
            {
                Indented(depth, () => GUILayout.Label(L["ui.empty"], YukiGUI.WrapMini));
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
            var open = node.IsSubMenu && !collapsed.Contains(node.Path);

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(depth * 14);

                if (node.IsSubMenu)
                {
                    if (GUILayout.Button(open ? "\u25be" : "\u25b8", EditorStyles.label, GUILayout.Width(14)))
                    {
                        if (open) collapsed.Add(node.Path); else collapsed.Remove(node.Path);
                        open = !open;
                    }
                }
                else GUILayout.Space(14);

                if (node.Icon != null) GUILayout.Label(node.Icon, GUILayout.Width(18), GUILayout.Height(18));
                else GUILayout.Space(18);

                GUILayout.Label(string.IsNullOrEmpty(node.Name) ? L["ui.unnamed"] : node.Name,
                                GUILayout.MinWidth(80));
                GUILayout.FlexibleSpace();

                GUILayout.Label(Describe(node), EditorStyles.miniLabel, GUILayout.ExpandWidth(false));

                using (new EditorGUI.DisabledScope(index == 0))
                    if (GUILayout.Button("▲", EditorStyles.miniButtonLeft, GUILayout.Width(22)))
                        Move(parent, index, -1, layout);

                using (new EditorGUI.DisabledScope(index == parent.Children.Count - 1))
                    if (GUILayout.Button("▼", EditorStyles.miniButtonRight, GUILayout.Width(22)))
                        Move(parent, index, 1, layout);
            }

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

        void DrawPageBadge(int pages)
        {
            GUILayout.Label(pages <= 1 ? L["ui.one_page"] : L.Tr("ui.pages", pages.ToString()),
                            EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
        }

        void DrawResetOrder(YukiMenuLayout layout, MenuNode menu)
        {
            if (layout == null || layout.order.Count == 0) return;
            if (!GUILayout.Button(L["ui.reset_order"], EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                return;
            Undo.RecordObject(layout, L["undo.reset_order"]);
            layout.order.Clear();
            EditorUtility.SetDirty(layout);
            stale = true;
        }

        /// <summary>
        /// Moving a row rewrites the whole recorded order for that one menu, so
        /// the recording always describes a complete arrangement rather than a
        /// list of nudges that later builds would have to reconcile.
        /// </summary>
        void Move(MenuNode parent, int index, int delta, YukiMenuLayout layout)
        {
            var to = index + delta;
            if (to < 0 || to >= parent.Children.Count) return;

            var node = parent.Children[index];
            parent.Children.RemoveAt(index);
            parent.Children.Insert(to, node);

            if (layout == null)
            {
                EditorUtility.DisplayDialog(L["ui.title"], L["ui.order_needs_component"], L["ui.ok"]);
                return;
            }

            Undo.RecordObject(layout, L["undo.reorder"]);
            var group = layout.Ensure(parent.Path);
            group.items = parent.Children.Select(c => c.Key()).ToList();
            EditorUtility.SetDirty(layout);
            GUI.changed = true;
        }

        static void Indented(int depth, System.Action body)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(depth * 14 + 14);
                body();
            }
        }
    }
}
