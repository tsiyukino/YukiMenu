using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace TsiYuki.Menus
{
    /// <summary>Which menus the paging rules apply to.</summary>
    public enum PagingScope
    {
        // The outermost wheel only; everything deeper is left as it was.
        RootOnly = 0,
        // Every menu, however deep.
        Everywhere = 1,
    }

    /// <summary>Where the link to the next page sits on a page.</summary>
    public enum OverflowPlacement
    {
        End = 0,
        Start = 1,
    }

    /// <summary>
    /// Identifies one control in the finished menu well enough to find it again
    /// in the next build.
    ///
    /// Nothing in the finished menu survives between builds — it is all made
    /// from scratch each time — so an order recorded now has to be matched
    /// against controls that are different objects entirely. A parameter name
    /// and value pin down a control exactly and rarely change; a label is the
    /// fallback for controls that drive nothing, such as submenus. Both are
    /// recorded and tried in that order, so renaming a label or moving a
    /// toggle to another parameter loses the position of that one item rather
    /// than scrambling the menu.
    /// </summary>
    [Serializable]
    public class MenuItemKey
    {
        public string name = "";
        public string parameter = "";
        public float value;
        // VRCExpressionsMenu.Control.ControlType, as a number so this assembly
        // needs nothing from the avatars SDK.
        public int type;
    }

    /// <summary>A recorded order for the controls of one menu.</summary>
    [Serializable]
    public class MenuOrderGroup
    {
        // Labels from the root down, "/" separated. Empty is the root wheel.
        public string menuPath = "";
        public List<MenuItemKey> items = new List<MenuItemKey>();
    }

    /// <summary>
    /// Controls the shape of the menu the avatar ends up with.
    ///
    /// VRChat allows eight controls on a wheel, and Modular Avatar fills all
    /// eight before spilling into a "More" submenu. Eight is also the point at
    /// which each wedge is at its smallest and hardest to hit, so a lower limit
    /// set here is often the more usable one. The existing paging is undone
    /// first, so this decides the result rather than layering on top of it.
    /// </summary>
    [AddComponentMenu("TsiYuki/Yuki Menu")]
    [DisallowMultipleComponent]
    public class YukiMenuLayout : MonoBehaviour, IEditorOnly
    {
        public PagingScope scope = PagingScope.RootOnly;

        // VRChat's own ceiling is 8. Fewer means bigger, easier wedges.
        [Range(2, 8)] public int itemsPerPage = 8;

        // Empty uses "More".
        public string overflowName = "";
        public Texture2D overflowIcon;
        public OverflowPlacement overflowAt = OverflowPlacement.End;

        /// <summary>
        /// Off leaves the menu exactly as the other tools built it, paging and
        /// order alike — the way to see what they did on their own.
        /// </summary>
        public bool repage = true;

        /// <summary>Orders set in the Yuki Menu window. Menus not listed here
        /// keep the order the tools that built them produced.</summary>
        public List<MenuOrderGroup> order = new List<MenuOrderGroup>();

        public MenuOrderGroup Find(string menuPath)
        {
            var wanted = menuPath ?? "";
            foreach (var group in order)
                if ((group.menuPath ?? "") == wanted) return group;
            return null;
        }

        public MenuOrderGroup Ensure(string menuPath)
        {
            var group = Find(menuPath);
            if (group == null) order.Add(group = new MenuOrderGroup { menuPath = menuPath ?? "" });
            return group;
        }

        public void Forget(string menuPath)
        {
            var group = Find(menuPath);
            if (group != null) order.Remove(group);
        }
    }
}
