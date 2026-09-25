using System;
using System.Collections.Generic;
using UnityEngine;

namespace TsiYuki.Menus.Editor
{
    /// <summary>One menu while its structure is being worked out.</summary>
    public sealed class LayoutBox
    {
        /// <summary>Its container id (see <see cref="MenuContainer"/>).</summary>
        public string Id = "";
        public string Name = "";
        /// <summary>Set for a submenu made in the window.</summary>
        public MenuFolder Folder;
        public List<LayoutEntry> Entries = new List<LayoutEntry>();
        /// <summary>What it was read from: the menu asset in a build, the
        /// snapshot node in the window. Null for a folder.</summary>
        public object Source;
        /// <summary>Pages were folded back into it while reading.</summary>
        public bool Flattened;
        /// <summary>Its contents are no longer what the tools built.</summary>
        public bool Dirty;
    }

    /// <summary>One control of a <see cref="LayoutBox"/>.</summary>
    public sealed class LayoutEntry
    {
        public MenuItemKey Key;
        /// <summary>The container the tools put it in. Null for a folder, which
        /// no tool put anywhere.</summary>
        public string Origin;
        public MenuFolder Folder;
        /// <summary>The move that put it where it is, if one did.</summary>
        public MenuMove Move;
        /// <summary>The menu it opens, if it is a submenu that could be read.</summary>
        public LayoutBox Child;
        /// <summary>The control itself (build) or its snapshot node (window).</summary>
        public object Payload;
    }

    public enum StructureProblemKind
    {
        /// <summary>The control a move names is not in the menu it names —
        /// the tool renamed or dropped it, or dropped the menu.</summary>
        MissingItem,
        /// <summary>The menu a move or a folder points into is not there.</summary>
        MissingTarget,
        /// <summary>The move would put a submenu inside itself.</summary>
        WouldContainItself,
    }

    public sealed class StructureProblem
    {
        public StructureProblemKind Kind;
        public MenuMove Move;
        public MenuFolder Folder;
    }

    /// <summary>
    /// Applies the structure set in the window — its submenus, its moves and
    /// its orders — to a menu the tools built.
    ///
    /// One implementation serves both the build and the window, so what the
    /// window shows is what the build does. Both read their tree into
    /// <see cref="LayoutBox"/>es keyed by container id, hand them here, and
    /// write the boxes back out in their own terms.
    ///
    /// Nothing is ever lost. A change that cannot be carried out is reported
    /// and skipped, which leaves the control where the tools put it; a folder
    /// whose menu has gone goes to the root wheel with everything in it.
    /// </summary>
    public static class MenuStructure
    {
        public sealed class Input
        {
            public List<MenuFolder> Folders;
            public List<MenuMove> Moves;
            public List<MenuOrderGroup> Order;
        }

        /// <param name="makeFolder">Makes the payload for a folder's entry.</param>
        public static List<StructureProblem> Apply(LayoutBox root, Dictionary<string, LayoutBox> boxes, Input input,
                                                   Func<MenuFolder, LayoutBox, object> makeFolder)
        {
            var problems = new List<StructureProblem>();
            if (root == null) return problems;

            PlaceFolders(root, boxes, input.Folders, makeFolder, problems);
            ApplyMoves(boxes, input.Moves, problems);
            ApplyOrder(boxes, input.Order);
            return problems;
        }

        // --- folders --------------------------------------------------------------

        static void PlaceFolders(LayoutBox root, Dictionary<string, LayoutBox> boxes, List<MenuFolder> folders,
                                 Func<MenuFolder, LayoutBox, object> makeFolder, List<StructureProblem> problems)
        {
            if (folders == null) return;

            // Every box first, so a folder can sit in one listed after it.
            var made = new List<(MenuFolder folder, LayoutBox box)>();
            foreach (var folder in folders)
            {
                if (folder == null || string.IsNullOrEmpty(folder.id)) continue;
                var id = folder.Container;
                if (boxes.ContainsKey(id)) continue; // a duplicated id; the first one wins
                var box = new LayoutBox { Id = id, Name = folder.name ?? "", Folder = folder, Dirty = true };
                boxes[id] = box;
                made.Add((folder, box));
            }

            foreach (var (folder, box) in made)
            {
                LayoutBox parent;
                if (!boxes.TryGetValue(folder.parent ?? "", out parent) || InsideItself(folder, boxes))
                {
                    // Its menu is gone. The root wheel is somewhere it can be
                    // seen and dealt with; nowhere is not.
                    problems.Add(new StructureProblem { Kind = StructureProblemKind.MissingTarget, Folder = folder });
                    parent = root;
                }

                parent.Entries.Add(new LayoutEntry
                {
                    Key = folder.Key(),
                    Folder = folder,
                    Child = box,
                    Payload = makeFolder(folder, box),
                });
                parent.Dirty = true;
            }
        }

        /// <summary>Folders whose parents lead back round to themselves.</summary>
        static bool InsideItself(MenuFolder folder, Dictionary<string, LayoutBox> boxes)
        {
            var seen = new HashSet<string> { folder.Container };
            var at = folder.parent ?? "";
            while (MenuContainer.IsFolder(at))
            {
                if (!seen.Add(at)) return true;
                LayoutBox box;
                if (!boxes.TryGetValue(at, out box) || box.Folder == null) return false;
                at = box.Folder.parent ?? "";
            }
            return false;
        }

        // --- moves ----------------------------------------------------------------

        static void ApplyMoves(Dictionary<string, LayoutBox> boxes, List<MenuMove> moves, List<StructureProblem> problems)
        {
            if (moves == null) return;

            foreach (var move in moves)
            {
                if (move == null || move.item == null) continue;
                var from = move.from ?? "";
                var to = move.to ?? "";
                if (from == to) continue;

                LayoutBox source, target;
                var index = -1;
                if (boxes.TryGetValue(from, out source))
                    // Only what the tools put there: something moved in by an
                    // earlier record is that record's, not this one's.
                    index = Match(source.Entries, move.item, e => e.Origin == from && e.Move == null && e.Folder == null);

                if (index < 0)
                {
                    problems.Add(new StructureProblem { Kind = StructureProblemKind.MissingItem, Move = move });
                    continue;
                }
                if (!boxes.TryGetValue(to, out target))
                {
                    problems.Add(new StructureProblem { Kind = StructureProblemKind.MissingTarget, Move = move });
                    continue;
                }

                var entry = source.Entries[index];
                if (entry.Child != null && Reaches(entry.Child, target))
                {
                    problems.Add(new StructureProblem { Kind = StructureProblemKind.WouldContainItself, Move = move });
                    continue;
                }

                source.Entries.RemoveAt(index);
                entry.Move = move;
                target.Entries.Add(entry);
                source.Dirty = true;
                target.Dirty = true;
            }
        }

        /// <summary>Whether <paramref name="target"/> is <paramref name="from"/>
        /// or anywhere inside it.</summary>
        public static bool Reaches(LayoutBox from, LayoutBox target)
        {
            var seen = new HashSet<LayoutBox>();
            var stack = new Stack<LayoutBox>();
            stack.Push(from);
            while (stack.Count > 0)
            {
                var box = stack.Pop();
                if (box == target) return true;
                if (!seen.Add(box)) continue;
                foreach (var entry in box.Entries)
                    if (entry.Child != null) stack.Push(entry.Child);
            }
            return false;
        }

        // --- order ----------------------------------------------------------------

        static void ApplyOrder(Dictionary<string, LayoutBox> boxes, List<MenuOrderGroup> order)
        {
            if (order == null) return;
            foreach (var group in order)
            {
                if (group == null || group.items == null || group.items.Count == 0) continue;
                LayoutBox box;
                if (!boxes.TryGetValue(group.menuPath ?? "", out box)) continue;
                if (Reorder(box.Entries, group)) box.Dirty = true;
            }
        }

        /// <summary>
        /// Sorts in place to match the recorded order. Anything the recording
        /// does not mention was added by a tool, or moved in, since; it keeps
        /// its place at the end rather than disappearing.
        /// </summary>
        static bool Reorder(List<LayoutEntry> entries, MenuOrderGroup group)
        {
            var before = new List<LayoutEntry>(entries);
            var remaining = new List<LayoutEntry>(entries);
            var sorted = new List<LayoutEntry>();

            foreach (var key in group.items)
            {
                var index = Match(remaining, key, null);
                if (index < 0) continue;
                sorted.Add(remaining[index]);
                remaining.RemoveAt(index);
            }
            sorted.AddRange(remaining);

            entries.Clear();
            entries.AddRange(sorted);
            for (var i = 0; i < before.Count; i++)
                if (before[i] != entries[i]) return true;
            return false;
        }

        /// <summary>
        /// A folder by its id and by nothing else. Anything else by parameter
        /// and value first, because they survive renaming; the label is the
        /// fallback for controls that drive nothing.
        /// </summary>
        public static int Match(List<LayoutEntry> entries, MenuItemKey key, Func<LayoutEntry, bool> eligible)
        {
            if (key == null) return -1;

            if (!string.IsNullOrEmpty(key.folder))
                return entries.FindIndex(e => e.Folder != null && e.Folder.id == key.folder &&
                                              (eligible == null || eligible(e)));

            Func<LayoutEntry, bool> ok = e => e.Folder == null && (eligible == null || eligible(e));

            if (!string.IsNullOrEmpty(key.parameter))
            {
                var exact = entries.FindIndex(e => ok(e) && e.Key.parameter == key.parameter &&
                                                   Mathf.Approximately(e.Key.value, key.value));
                if (exact >= 0) return exact;
            }

            var labelled = entries.FindIndex(e => ok(e) && e.Key.name == key.name && e.Key.type == key.type);
            if (labelled >= 0) return labelled;

            return entries.FindIndex(e => ok(e) && e.Key.name == key.name);
        }

        /// <summary>Whether two keys name the same control.</summary>
        public static bool SameKey(MenuItemKey a, MenuItemKey b)
        {
            if (a == null || b == null) return false;
            if (!string.IsNullOrEmpty(a.folder) || !string.IsNullOrEmpty(b.folder)) return a.folder == b.folder;
            return a.name == b.name && a.type == b.type && a.parameter == b.parameter &&
                   Mathf.Approximately(a.value, b.value);
        }

        public static MenuItemKey Copy(MenuItemKey key) => new MenuItemKey
        {
            name = key.name ?? "", parameter = key.parameter ?? "", value = key.value, type = key.type,
            folder = key.folder ?? "",
        };
    }
}
