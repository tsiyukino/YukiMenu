using TsiYuki.Core.Editor;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace TsiYuki.Menus.Editor
{
    /// <summary>
    /// The settings, plus the way into the window — the shape of a menu is only
    /// worth judging as a whole, and the inspector cannot show it.
    /// </summary>
    [CustomEditor(typeof(YukiMenuLayout))]
    public class YukiMenuLayoutEditor : UnityEditor.Editor
    {
        static YukiLocalizer L => MenuText.L;

        public override void OnInspectorGUI()
        {
            var layout = (YukiMenuLayout)target;

            YukiGUI.Header(L["ui.title"], "");
            EditorGUILayout.LabelField(L["ui.intro"], YukiGUI.WrapMini);

            if (layout.GetComponentInParent<VRCAvatarDescriptor>() == null)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(L["ui.not_in_avatar"], MessageType.Warning);
            }

            // The window is where the menu is actually seen, so the way there
            // comes first rather than under the settings.
            EditorGUILayout.Space(8);
            var previous = GUI.backgroundColor;
            GUI.backgroundColor = MenuWindow.Styles.ButtonTint;
            if (GUILayout.Button(L["ui.open_window"], GUILayout.Height(28)))
                MenuWindow.Open(layout);
            GUI.backgroundColor = previous;

            serializedObject.Update();

            EditorGUILayout.Space(8);
            GUILayout.Label(L["ui.settings"], YukiGUI.SectionHeaderStyle);
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(MenuWindow.Styles.Card))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("repage"),
                    new GUIContent(L["ui.repage"], L["ui.repage.tip"]));

                EnumPopup(serializedObject.FindProperty("scope"),
                    new GUIContent(L["ui.scope"], L["ui.scope.tip"]), MenuWindow.ScopeOptions);
                EditorGUILayout.IntSlider(serializedObject.FindProperty("itemsPerPage"),
                    2, MenuPaginator.VrcLimit, new GUIContent(L["ui.per_page"], L["ui.per_page.tip"]));

                EditorGUILayout.Space(8);
                GUILayout.Label(L["ui.next_page_group"], EditorStyles.miniBoldLabel);
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("overflowName"),
                        new GUIContent(L["ui.overflow_name"], L["ui.overflow_name.tip"]));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("overflowIcon"),
                        new GUIContent(L["ui.overflow_icon"]));
                    EnumPopup(serializedObject.FindProperty("overflowAt"),
                        new GUIContent(L["ui.overflow_at"], L["ui.overflow_at.tip"]), MenuWindow.PlacementOptions);
                }
            }
            serializedObject.ApplyModifiedProperties();

            var note = !layout.repage ? L["ui.repage_off"]
                     : layout.itemsPerPage == MenuPaginator.VrcLimit ? L["ui.at_limit"]
                     : null;
            if (note != null)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField(note, YukiGUI.WrapMini);
            }

            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                var count = layout.order.Count;
                GUILayout.Label(count == 0 ? L["ui.no_order"] : L.Tr("ui.order_count", count), YukiGUI.WrapMini);
                if (count > 0 && GUILayout.Button(new GUIContent(L["ui.reset_order"], L["ui.reset_order.tip"]),
                                                  EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                {
                    Undo.RecordObject(layout, L["undo.reset_order"]);
                    layout.order.Clear();
                    EditorUtility.SetDirty(layout);
                }
            }
        }

        /// <summary>An enum field with translated option names instead of the
        /// identifiers from the code.</summary>
        static void EnumPopup(SerializedProperty property, GUIContent label, GUIContent[] options)
        {
            var rect = EditorGUILayout.GetControlRect();
            using (new EditorGUI.PropertyScope(rect, label, property))
            using (var change = new EditorGUI.ChangeCheckScope())
            {
                EditorGUI.showMixedValue = property.hasMultipleDifferentValues;
                var index = EditorGUI.Popup(rect, label, property.enumValueIndex, options);
                EditorGUI.showMixedValue = false;
                if (change.changed) property.enumValueIndex = index;
            }
        }
    }
}
