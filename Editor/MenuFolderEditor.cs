using TsiYuki.Core.Editor;
using UnityEditor;
using UnityEngine;

namespace TsiYuki.Menus.Editor
{
    /// <summary>
    /// Names a submenu made in the window and gives it an icon.
    ///
    /// A small window of its own rather than a field in the row: the icon
    /// picker opens a window, and anything that closed on losing focus would
    /// close under it.
    /// </summary>
    public class MenuFolderEditor : EditorWindow
    {
        static YukiLocalizer L => MenuText.L;
        const string NameControl = "moe.tsiyuki.menu.folderName";

        YukiMenuLayout layout;
        string folderId;
        string label;
        Texture2D icon;
        bool focusName = true;

        static GUIStyle _padded;
        static GUIStyle Padded => _padded ??= new GUIStyle { padding = new RectOffset(8, 8, 8, 8) };

        public static void Open(YukiMenuLayout layout, MenuFolder folder, Vector2 screenPoint)
        {
            if (layout == null || folder == null) return;
            foreach (var open in Resources.FindObjectsOfTypeAll<MenuFolderEditor>()) open.Close();

            var window = CreateInstance<MenuFolderEditor>();
            window.layout = layout;
            window.folderId = folder.id;
            window.label = folder.name;
            window.icon = folder.icon;
            window.titleContent = new GUIContent(L["ui.folder.title"]);
            var size = new Vector2(340, 104);
            window.minSize = size;
            window.maxSize = size;
            window.position = new Rect(screenPoint.x, screenPoint.y, size.x, size.y);
            window.ShowUtility();
            window.Focus();
        }

        void OnGUI()
        {
            var e = Event.current;
            if (e.type == EventType.KeyDown)
            {
                // Enter while an input method is still composing is that
                // method's, not ours.
                var composing = !string.IsNullOrEmpty(Input.compositionString);
                if (!composing && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
                {
                    e.Use();
                    Apply();
                    return;
                }
                if (e.keyCode == KeyCode.Escape)
                {
                    e.Use();
                    Close();
                    return;
                }
            }

            if (layout == null || layout.FindFolder(folderId) == null)
            {
                // Undone out from under us.
                Close();
                return;
            }

            using (new EditorGUILayout.VerticalScope(Padded))
            {
                GUI.SetNextControlName(NameControl);
                label = EditorGUILayout.TextField(L["ui.folder.name"], label);
                icon = (Texture2D)EditorGUILayout.ObjectField(L["ui.folder.icon"], icon, typeof(Texture2D), false,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));

                GUILayout.FlexibleSpace();
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(L["ui.cancel"], GUILayout.Width(80))) Close();
                    if (GUILayout.Button(L["ui.ok"], GUILayout.Width(80))) Apply();
                }
            }

            if (focusName && e.type == EventType.Repaint)
            {
                EditorGUI.FocusTextInControl(NameControl);
                focusName = false;
                Repaint();
            }
        }

        void Apply()
        {
            var folder = layout != null ? layout.FindFolder(folderId) : null;
            if (folder != null)
            {
                var name = (label ?? "").Trim();
                if (name.Length == 0) name = folder.name;
                if (name != folder.name || icon != folder.icon)
                {
                    Undo.RecordObject(layout, L["undo.edit_folder"]);
                    folder.name = name;
                    folder.icon = icon;
                    EditorUtility.SetDirty(layout);
                }
            }
            foreach (var window in Resources.FindObjectsOfTypeAll<MenuWindow>())
            {
                window.Quiet();
                window.Repaint();
            }
            Close();
        }
    }
}
