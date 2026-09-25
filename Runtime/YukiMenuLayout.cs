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
    ///
    /// A submenu made in the window is the exception: it is ours, so it has an
    /// id of its own and is matched by that alone, which is what lets it be
    /// renamed without losing its place.
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
        // Set only for a submenu made in the window (MenuFolder.id).
        public string folder = "";
    }

    /// <summary>A recorded order for the controls of one menu.</summary>
    [Serializable]
    public class MenuOrderGroup
    {
        // Which menu this orders, as a container id (see MenuContainer): for a
        // menu the tools built, its labels from the root down, "/" separated,
        // empty being the root wheel; for a submenu made in the window, its id.
        public string menuPath = "";
        public List<MenuItemKey> items = new List<MenuItemKey>();
    }

    /// <summary>
    /// A submenu the user made in the window, which no tool knows about.
    /// </summary>
    [Serializable]
    public class MenuFolder
    {
        public string id = "";
        public string name = "";
        public Texture2D icon;
        // The container it sits in (see MenuContainer).
        public string parent = "";

        public string Container => MenuContainer.OfFolder(id);

        public MenuItemKey Key() =>
            new MenuItemKey { name = name ?? "", type = MenuContainer.SubMenuType, folder = id ?? "" };
    }

    /// <summary>
    /// One control taken out of the menu a tool put it in and placed in
    /// another.
    ///
    /// The source is recorded as the menu the tools built it into, not as
    /// wherever it is now, so the record means the same thing however many
    /// times it is moved afterwards — moving it again only changes
    /// <see cref="to"/>, and moving it home deletes the record.
    /// </summary>
    [Serializable]
    public class MenuMove
    {
        // The container the tools put it in.
        public string from = "";
        public MenuItemKey item = new MenuItemKey();
        // The container it goes to instead.
        public string to = "";
    }

    /// <summary>
    /// Names a menu so a change aimed at it can find it again in the next build.
    ///
    /// A menu a tool built is named by its path through the tree the tools
    /// built — the labels from the root down — and keeps that name when it is
    /// moved somewhere else, so everything recorded against its contents stays
    /// valid. A submenu made in the window is named by its id.
    /// </summary>
    public static class MenuContainer
    {
        public const string Root = "";
        public const string FolderPrefix = "folder:";
        // VRCExpressionsMenu.Control.ControlType.SubMenu.
        public const int SubMenuType = 103;

        public static string OfFolder(string id) => FolderPrefix + (id ?? "");

        public static bool IsFolder(string container) =>
            container != null && container.StartsWith(FolderPrefix, StringComparison.Ordinal);

        public static string FolderId(string container) =>
            IsFolder(container) ? container.Substring(FolderPrefix.Length) : null;
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
        /// Off leaves the menu exactly as the other tools built it — paging,
        /// order and structure alike — the way to see what they did on their own.
        /// </summary>
        public bool repage = true;

        /// <summary>Orders set in the Yuki Menu window. Menus not listed here
        /// keep the order the tools that built them produced.</summary>
        public List<MenuOrderGroup> order = new List<MenuOrderGroup>();

        /// <summary>Submenus made in the window.</summary>
        public List<MenuFolder> folders = new List<MenuFolder>();

        /// <summary>Controls moved out of the menu their tool put them in.</summary>
        public List<MenuMove> moves = new List<MenuMove>();

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

        public MenuFolder FindFolder(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var folder in folders)
                if (folder != null && folder.id == id) return folder;
            return null;
        }

        public bool HasStructure => folders.Count > 0 || moves.Count > 0;
    }
}
