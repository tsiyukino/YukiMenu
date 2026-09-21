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
                EditorGUILayout.HelpBox(L["ui.not_in_avatar"], MessageType.Warning);

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("scope"),
                new GUIContent(L["ui.scope"], L["ui.scope.tip"]));
            EditorGUILayout.IntSlider(serializedObject.FindProperty("itemsPerPage"),
                2, MenuPaginator.VrcLimit, new GUIContent(L["ui.per_page"], L["ui.per_page.tip"]));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("repage"),
                new GUIContent(L["ui.repage"], L["ui.repage.tip"]));

            EditorGUILayout.PropertyField(serializedObject.FindProperty("overflowName"),
                new GUIContent(L["ui.overflow_name"], L["ui.overflow_name.tip"]));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("overflowIcon"),
                new GUIContent(L["ui.overflow_icon"]));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("overflowAt"),
                new GUIContent(L["ui.overflow_at"], L["ui.overflow_at.tip"]));
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(6);
            var count = layout.order.Count;
            EditorGUILayout.LabelField(count == 0 ? L["ui.no_order"] : L.Tr("ui.order_count", count.ToString()),
                                       YukiGUI.WrapMini);

            EditorGUILayout.Space(4);
            if (GUILayout.Button(L["ui.open_window"], GUILayout.Height(24)))
                MenuWindow.Open(layout);
        }
    }
}
