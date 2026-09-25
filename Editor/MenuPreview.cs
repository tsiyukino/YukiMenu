using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TsiYuki.Menus.Editor
{
    /// <summary>
    /// Turns the menu the tools built into the menu the build will make of it,
    /// for the window to show.
    ///
    /// The snapshot is taken without Yuki Menu's own pass, so the structure set
    /// in the window can be laid over it here — by the same code the build
    /// uses — without building again. That is what lets a change show the
    /// moment it is made.
    /// </summary>
    public static class MenuPreview
    {
        public static MenuNode Compose(MenuNode raw, YukiMenuLayout layout, out List<StructureProblem> problems)
        {
            problems = new List<StructureProblem>();
            if (raw == null) return null;

            var boxes = new Dictionary<string, LayoutBox>();
            var rootBox = Load(raw, MenuContainer.Root, boxes);

            if (layout != null)
                problems = MenuStructure.Apply(rootBox, boxes, new MenuStructure.Input
                {
                    Folders = layout.folders,
                    Moves = layout.moves,
                    Order = layout.order,
                }, (folder, box) => null);

            var root = Show(rootBox, "", new HashSet<LayoutBox>());
            root.Name = raw.Name;
            return root;
        }

        static LayoutBox Load(MenuNode node, string id, Dictionary<string, LayoutBox> boxes)
        {
            var box = new LayoutBox { Id = id, Name = node.Name, Source = node };
            if (!boxes.ContainsKey(id)) boxes[id] = box;
            foreach (var child in node.Children)
            {
                var entry = new LayoutEntry { Key = child.Key(), Origin = id, Payload = child };
                if (child.IsSubMenu) entry.Child = Load(child, child.Path, boxes);
                box.Entries.Add(entry);
            }
            return box;
        }

        static MenuNode Show(LayoutBox box, string path, HashSet<LayoutBox> open)
        {
            var node = new MenuNode { Container = box.Id, IsSubMenu = true, Path = path, Name = box.Name };
            if (!open.Add(box)) return node;

            foreach (var entry in box.Entries)
            {
                var source = entry.Payload as MenuNode;
                var folder = entry.Folder;
                var name = folder != null ? folder.name ?? "" : source?.Name ?? "";
                var row = new MenuNode
                {
                    Name = name,
                    Path = MenuPaginator.Join(path, name),
                    Icon = folder != null ? folder.icon : source?.Icon,
                    Type = folder != null ? MenuContainer.SubMenuType : source?.Type ?? 0,
                    Parameter = source?.Parameter ?? "",
                    Value = source?.Value ?? 0,
                    IsSubMenu = folder != null || (source != null && source.IsSubMenu),
                    Container = source?.Container ?? "",
                    Origin = entry.Origin,
                    Folder = folder,
                    Move = entry.Move,
                    ItemKey = entry.Key,
                    Parent = node,
                };
                if (entry.Child != null)
                {
                    var inside = Show(entry.Child, row.Path, open);
                    row.Container = entry.Child.Id;
                    row.Children = inside.Children;
                    foreach (var child in row.Children) child.Parent = row;
                }
                node.Children.Add(row);
            }

            open.Remove(box);
            return node;
        }

        // --- naming menus for people -------------------------------------------------

        static readonly Regex Tags = new Regex("<[^>]*>");

        /// <summary>A label as plain one-line text: VRChat renders rich text
        /// and line breaks in labels, which a list of names cannot.</summary>
        public static string Plain(string label)
        {
            if (string.IsNullOrEmpty(label)) return "";
            var text = Tags.Replace(label, "").Replace("\r", "").Replace('\n', ' ').Trim();
            return text.Length > 48 ? text.Substring(0, 47) + "…" : text;
        }

        /// <summary>
        /// What to call a container: the root wheel, a folder by its current
        /// name, a tool's menu by the last label of its path.
        /// </summary>
        public static string Describe(string container, YukiMenuLayout layout, string rootName)
        {
            container = container ?? "";
            if (container.Length == 0) return rootName;
            var id = MenuContainer.FolderId(container);
            if (id != null)
            {
                var folder = layout != null ? layout.FindFolder(id) : null;
                return folder != null ? Plain(folder.name) : "?";
            }
            var slash = container.LastIndexOf('/');
            return Plain(slash >= 0 ? container.Substring(slash + 1) : container);
        }
    }
}
