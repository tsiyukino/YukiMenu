using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using Control = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control;

namespace TsiYuki.Menus.Editor
{
    /// <summary>
    /// Rebuilds a finished menu tree: undoes the paging the other tools added,
    /// gives it the structure and order the user set, and pages the result at
    /// the user's own limit.
    ///
    /// It runs on the build copy, after every tool that installs menu items has
    /// had its turn, so what it sees is what the avatar would have shipped
    /// with. Menus it does not need to change are handed back untouched, and a
    /// menu that is a real asset in the project is never written to — a changed
    /// menu is always a fresh one.
    /// </summary>
    public static class MenuPaginator
    {
        // VRCExpressionsMenu.MAX_CONTROLS, named here so the arithmetic below reads.
        public const int VrcLimit = VRCExpressionsMenu.MAX_CONTROLS;

        public const string DefaultOverflowName = "More";

        // What the other tools call the control that leads to the next page:
        // Modular Avatar's is "More", VRCFury's is "Next".
        static readonly string[] KnownNames = { "More", "Next" };

        // And what they call the page behind it: "Menu (Page 2)" from VRCFury,
        // "Menu (2)" from us. A tool with a renamed link still names its pages.
        static readonly Regex PageName = new Regex(@"\((?:Page )?\d+\)$");

        // Modular Avatar puts its own icon on every link it makes, which is the
        // one unmistakable mark any of them leave.
        const string MoreIconPath = "Packages/nadena.dev.modular-avatar/Runtime/Icons/Icon_More_A.png";
        static Texture2D _moreIcon;
        static bool _moreIconLoaded;
        static Texture2D MoreIcon
        {
            get
            {
                if (_moreIconLoaded) return _moreIcon;
                _moreIconLoaded = true;
                return _moreIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(MoreIconPath);
            }
        }

        public class Options
        {
            public int ItemsPerPage = VrcLimit;
            public bool Everywhere;
            public string OverflowName = DefaultOverflowName;
            public Texture2D OverflowIcon;
            public bool OverflowFirst;
            public List<MenuOrderGroup> Order;
            public List<MenuFolder> Folders;
            public List<MenuMove> Moves;

            /// <summary>Which menus came from this build rather than the project.
            /// Only those are safe to take apart.</summary>
            public Func<VRCExpressionsMenu, bool> IsGenerated = _ => false;

            /// <summary>Hands a newly made menu to the build so it survives.</summary>
            public Action<VRCExpressionsMenu> Save = _ => { };
        }

        public static VRCExpressionsMenu Run(VRCExpressionsMenu root, Options options)
        {
            return Run(root, options, out _);
        }

        /// <param name="problems">The structure changes that could not be
        /// carried out, and were skipped.</param>
        public static VRCExpressionsMenu Run(VRCExpressionsMenu root, Options options,
                                             out List<StructureProblem> problems)
        {
            problems = new List<StructureProblem>();
            if (root == null) return null;

            var boxes = new Dictionary<string, LayoutBox>();
            var rootBox = Load(root, MenuContainer.Root, options, boxes,
                               new Dictionary<VRCExpressionsMenu, LayoutBox>(), new HashSet<VRCExpressionsMenu>());
            if (rootBox == null) return root;

            problems = MenuStructure.Apply(rootBox, boxes, new MenuStructure.Input
            {
                Folders = options.Folders,
                Moves = options.Moves,
                Order = options.Order,
            }, (folder, box) => FolderControl(folder));

            return Emit(rootBox, rootBox, options, new Dictionary<LayoutBox, VRCExpressionsMenu>(),
                        new HashSet<LayoutBox>());
        }

        // --- reading the tree the tools built ---------------------------------------

        /// <summary>
        /// One box per menu, with its pages folded back in, named by its path.
        ///
        /// Every menu is flattened, not only the ones being paged: a change can
        /// name a control that happens to sit on a later page, and it has to be
        /// found there. A menu that nothing ends up changing is still handed
        /// back as it was, pages and all.
        /// </summary>
        static LayoutBox Load(VRCExpressionsMenu menu, string path, Options options,
                              Dictionary<string, LayoutBox> boxes,
                              Dictionary<VRCExpressionsMenu, LayoutBox> loaded,
                              HashSet<VRCExpressionsMenu> open)
        {
            if (menu == null) return null;
            LayoutBox already;
            if (loaded.TryGetValue(menu, out already)) return already;
            // A menu that contains itself would never finish; that control is
            // left pointing where it did.
            if (!open.Add(menu)) return null;

            var flattened = false;
            var controls = Flatten(menu, options, ref flattened);
            var box = new LayoutBox { Id = path, Name = menu.name, Source = menu, Flattened = flattened };
            // Two menus with one label in one parent share a path; the first is
            // the one a recorded change means.
            if (!boxes.ContainsKey(path)) boxes[path] = box;

            foreach (var control in controls)
            {
                var entry = new LayoutEntry { Key = KeyOf(control), Origin = path, Payload = control };
                if (control.type == Control.ControlType.SubMenu && control.subMenu != null)
                    entry.Child = Load(control.subMenu, Join(path, control.name), options, boxes, loaded, open);
                box.Entries.Add(entry);
            }

            open.Remove(menu);
            loaded[menu] = box;
            return box;
        }

        static Control FolderControl(MenuFolder folder)
        {
            return new Control
            {
                name = folder.name ?? "",
                icon = folder.icon,
                type = Control.ControlType.SubMenu,
                parameter = new Control.Parameter { name = "" },
                subParameters = new Control.Parameter[0],
                labels = new Control.Label[0],
            };
        }

        // --- writing it back --------------------------------------------------------

        static VRCExpressionsMenu Emit(LayoutBox box, LayoutBox root, Options options,
                                       Dictionary<LayoutBox, VRCExpressionsMenu> done, HashSet<LayoutBox> open)
        {
            VRCExpressionsMenu already;
            if (done.TryGetValue(box, out already)) return already;
            var source = box.Source as VRCExpressionsMenu;
            // Cannot happen — moves that would nest a menu in itself are refused —
            // but a loop here would hang the build.
            if (!open.Add(box)) return source;

            var controls = new List<Control>();
            var childChanged = false;
            foreach (var entry in box.Entries)
            {
                var control = (Control)entry.Payload;
                if (entry.Child != null)
                {
                    var sub = Emit(entry.Child, root, options, done, open);
                    if (sub != control.subMenu)
                    {
                        control.subMenu = sub;
                        childChanged = true;
                    }
                }
                controls.Add(control);
            }

            // A menu outside the paging scope still has to obey VRChat's own
            // ceiling, which taking its pages apart or moving things in may
            // have just broken.
            var paging = options.Everywhere || box == root;
            var limit = paging ? Mathf.Clamp(options.ItemsPerPage, 2, VrcLimit) : VrcLimit;
            var changed = source == null || box.Dirty || childChanged || (paging && box.Flattened) ||
                          controls.Count > limit;

            VRCExpressionsMenu result = source;
            if (changed)
            {
                Paginate(controls, limit, box.Name, options);
                result = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                result.name = string.IsNullOrEmpty(box.Name) ? "Menu" : box.Name;
                result.controls = controls;
                options.Save(result);
            }

            open.Remove(box);
            done[box] = result;
            return result;
        }

        // --- taking the pages apart -------------------------------------------------

        /// <summary>
        /// Every control of a menu and of the pages hanging off it, as one list,
        /// the links themselves dropped.
        ///
        /// A link is spliced where it sits rather than only at the end, because
        /// by the time this runs it often is not at the end any more: Modular
        /// Avatar pages a menu, and then VRCFury appends its own items after
        /// the link Modular Avatar left, stranding it in the middle. Splicing in
        /// place is what puts those items back in the order they were meant to
        /// be read in.
        /// </summary>
        public static List<Control> FlattenPages(VRCExpressionsMenu menu, string overflowName, int itemsPerPage,
                                                 Func<VRCExpressionsMenu, bool> isGenerated)
        {
            var result = new List<Control>();
            var changed = false;
            Collect(menu, overflowName, itemsPerPage, isGenerated ?? (_ => true), result,
                    new HashSet<VRCExpressionsMenu>(), ref changed);
            return result;
        }

        static List<Control> Flatten(VRCExpressionsMenu menu, Options options, ref bool changed)
        {
            var result = new List<Control>();
            Collect(menu, options.OverflowName, options.ItemsPerPage, options.IsGenerated, result,
                    new HashSet<VRCExpressionsMenu>(), ref changed);
            return result;
        }

        static void Collect(VRCExpressionsMenu menu, string overflowName, int itemsPerPage,
                            Func<VRCExpressionsMenu, bool> isGenerated, List<Control> into,
                            HashSet<VRCExpressionsMenu> seen, ref bool changed)
        {
            if (menu == null || !seen.Add(menu)) return;

            var list = menu.controls;
            for (var i = 0; i < list.Count; i++)
            {
                var control = list[i];
                // A link only counts as paging if the menu behind it was made
                // during this build. A submenu of the user's own, however it is
                // named, is one of their menu items and stays one.
                // A mark that only a paging link carries is proof on its own.
                // A link recognised by its shape alone has to be backed up by
                // the menu behind it having been made during this build.
                if (IsMarkedPageLink(control) ||
                    (IsShapedLikePageLink(control, overflowName, list.Count, itemsPerPage, i == list.Count - 1)
                     && isGenerated(control.subMenu)))
                {
                    changed = true;
                    Collect(control.subMenu, overflowName, itemsPerPage, isGenerated, into, seen, ref changed);
                    continue;
                }
                into.Add(Clone(control));
            }
        }

        /// <summary>
        /// Recognises an overflow link.
        ///
        /// Being sure matters, because a link is not kept: the page behind it is
        /// spliced into the page in front. Mistaking one of the user's own
        /// submenus for a link would tip its contents out into its parent.
        /// </summary>
        /// <param name="parentCount">How many controls sit on the page holding this one.</param>
        /// <param name="ourLimit">The page size we ourselves would use, since a
        /// page we made earlier is as much paging as anyone else's.</param>
        public static bool IsPageLink(Control control, string overflowName, int parentCount, int ourLimit, bool isLast)
        {
            return IsMarkedPageLink(control)
                   || IsShapedLikePageLink(control, overflowName, parentCount, ourLimit, isLast);
        }

        /// <summary>
        /// A link carrying a mark no ordinary menu item carries: the icon
        /// Modular Avatar puts on every one of its links, or a page named the
        /// way VRCFury and we name ours. Either settles it wherever the control
        /// happens to sit.
        /// </summary>
        static bool IsMarkedPageLink(Control control)
        {
            if (!CouldBeLink(control)) return false;
            if (MoreIcon != null && control.icon == MoreIcon) return true;
            return PageName.IsMatch(control.subMenu.name ?? "");
        }

        /// <summary>
        /// No mark, so only the shape is left, and then the control has to be in
        /// the shape's place: named the way a link is named and sitting last on a
        /// page that is exactly full, since a menu with room to spare was never
        /// split.
        /// </summary>
        static bool IsShapedLikePageLink(Control control, string overflowName, int parentCount, int ourLimit, bool isLast)
        {
            if (!CouldBeLink(control) || !isLast) return false;
            if (parentCount != VrcLimit && parentCount != Mathf.Clamp(ourLimit, 2, VrcLimit)) return false;
            return KnownNames.Contains(control.name)
                   || (!string.IsNullOrEmpty(overflowName) && control.name == overflowName);
        }

        static bool CouldBeLink(Control control)
        {
            if (control == null) return false;
            if (control.type != Control.ControlType.SubMenu || control.subMenu == null) return false;
            // A link exists to be walked through, so it drives nothing.
            return control.parameter == null || string.IsNullOrEmpty(control.parameter.name);
        }

        public static MenuItemKey KeyOf(Control control)
        {
            return new MenuItemKey
            {
                name = control.name ?? "",
                parameter = control.parameter != null ? control.parameter.name ?? "" : "",
                value = control.value,
                type = (int)control.type,
            };
        }

        // --- paging it again --------------------------------------------------------

        /// <summary>
        /// Leaves <paramref name="controls"/> holding the first page, with each
        /// page after it saved as its own menu and linked from the one before.
        /// </summary>
        static bool Paginate(List<Control> controls, int limit, string menuName, Options options)
        {
            if (controls.Count <= limit) return false;
            if (string.IsNullOrEmpty(menuName)) menuName = "Menu";

            // Every page but the last gives up one slot to the link onwards.
            var perPage = limit - 1;
            var pages = new List<List<Control>>();
            var at = 0;
            while (controls.Count - at > limit)
            {
                pages.Add(controls.GetRange(at, perPage));
                at += perPage;
            }
            pages.Add(controls.GetRange(at, controls.Count - at));

            var label = string.IsNullOrEmpty(options.OverflowName) ? DefaultOverflowName : options.OverflowName;

            // Back to front, so each page is finished — link included — before
            // the page that points at it is made.
            for (var page = pages.Count - 1; page > 0; page--)
            {
                var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                menu.name = $"{menuName} ({page + 1})";
                menu.controls = pages[page];
                options.Save(menu);

                var link = new Control
                {
                    name = label,
                    type = Control.ControlType.SubMenu,
                    subMenu = menu,
                    icon = options.OverflowIcon,
                    parameter = new Control.Parameter { name = "" },
                    subParameters = new Control.Parameter[0],
                    labels = new Control.Label[0],
                };
                if (options.OverflowFirst) pages[page - 1].Insert(0, link);
                else pages[page - 1].Add(link);
            }

            controls.Clear();
            controls.AddRange(pages[0]);
            return true;
        }

        /// <summary>How many pages a list of that length needs at that limit.</summary>
        public static int PageCount(int count, int limit)
        {
            limit = Mathf.Clamp(limit, 2, VrcLimit);
            if (count <= limit) return 1;
            // The last page holds `limit`; every earlier one holds `limit - 1`.
            return 1 + Mathf.CeilToInt((count - limit) / (float)(limit - 1));
        }

        /// <summary>Where the page breaks fall, as indices into a flat list.</summary>
        public static List<int> PageBreaks(int count, int limit)
        {
            var breaks = new List<int>();
            limit = Mathf.Clamp(limit, 2, VrcLimit);
            var at = 0;
            while (count - at > limit)
            {
                at += limit - 1;
                breaks.Add(at);
            }
            return breaks;
        }

        // --- odds and ends ----------------------------------------------------------

        /// <summary>
        /// A control is copied before anything is changed on it: the list we
        /// were handed belongs to a menu we may well decide to leave alone.
        /// </summary>
        static Control Clone(Control source)
        {
            return new Control
            {
                name = source.name,
                icon = source.icon,
                type = source.type,
                parameter = source.parameter == null
                    ? null
                    : new Control.Parameter { name = source.parameter.name },
                value = source.value,
                style = source.style,
                subMenu = source.subMenu,
                subParameters = source.subParameters == null
                    ? new Control.Parameter[0]
                    : source.subParameters.Select(p => p == null ? null : new Control.Parameter { name = p.name }).ToArray(),
                labels = source.labels == null
                    ? new Control.Label[0]
                    : source.labels.Select(l => new Control.Label { name = l.name, icon = l.icon }).ToArray(),
            };
        }

        public static string Join(string path, string name)
        {
            return path.Length == 0 ? (name ?? "") : path + "/" + (name ?? "");
        }
    }
}
