using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using Control = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control;

namespace TsiYuki.Menus.Editor
{
    /// <summary>
    /// Rebuilds a finished menu tree: undoes the paging the other tools added,
    /// puts the controls in the order the user set, and pages the result at the
    /// user's own limit.
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

        public class Options
        {
            public int ItemsPerPage = VrcLimit;
            public bool Everywhere;
            public string OverflowName = DefaultOverflowName;
            public Texture2D OverflowIcon;
            public bool OverflowFirst;
            public List<MenuOrderGroup> Order;

            /// <summary>Which menus came from this build rather than the project.
            /// Only those are safe to take apart.</summary>
            public Func<VRCExpressionsMenu, bool> IsGenerated = _ => false;

            /// <summary>Hands a newly made menu to the build so it survives.</summary>
            public Action<VRCExpressionsMenu> Save = _ => { };
        }

        public static VRCExpressionsMenu Run(VRCExpressionsMenu root, Options options)
        {
            if (root == null) return null;
            return Visit(root, "", options, new Dictionary<VRCExpressionsMenu, VRCExpressionsMenu>(),
                         new HashSet<VRCExpressionsMenu>());
        }

        static VRCExpressionsMenu Visit(VRCExpressionsMenu menu, string path, Options options,
                                        Dictionary<VRCExpressionsMenu, VRCExpressionsMenu> done,
                                        HashSet<VRCExpressionsMenu> open)
        {
            if (menu == null) return null;
            VRCExpressionsMenu already;
            if (done.TryGetValue(menu, out already)) return already;
            // A menu that contains itself would never finish; leave that whole
            // branch exactly as it is.
            if (!open.Add(menu)) return menu;

            var paging = options.Everywhere || path.Length == 0;
            var group = Group(options, path);
            var changed = false;

            // Undoing the paging is what makes the controls of a menu one list
            // again, so it is needed for ordering just as much as for re-paging.
            var controls = paging || group != null
                ? Flatten(menu, options, ref changed)
                : menu.controls.Select(Clone).ToList();

            foreach (var control in controls)
            {
                if (control.type != Control.ControlType.SubMenu || control.subMenu == null) continue;
                var child = Visit(control.subMenu, Join(path, control.name), options, done, open);
                if (child == control.subMenu) continue;
                control.subMenu = child;
                changed = true;
            }

            if (group != null) changed |= Reorder(controls, group);

            // A menu outside the paging scope still has to obey VRChat's own
            // ceiling, which taking its pages apart may have just broken.
            var limit = paging ? Mathf.Clamp(options.ItemsPerPage, 2, VrcLimit) : VrcLimit;
            changed |= Paginate(controls, limit, menu.name, options);

            var result = changed ? Rebuild(menu, controls, options) : menu;
            open.Remove(menu);
            done[menu] = result;
            return result;
        }

        // --- taking the pages apart -------------------------------------------------

        /// <summary>
        /// Walks the chain of overflow submenus and returns every control on it
        /// as one list, the links themselves dropped.
        /// </summary>
        static List<Control> Flatten(VRCExpressionsMenu menu, Options options, ref bool changed)
        {
            var result = new List<Control>();
            var seen = new HashSet<VRCExpressionsMenu>();
            var at = menu;

            while (at != null && seen.Add(at))
            {
                var list = at.controls;
                var last = list.Count > 0 ? list[list.Count - 1] : null;

                // A link only counts as paging if the menu behind it was made
                // during this build. A submenu of the user's own, however it is
                // named, is one of their menu items and stays one.
                if (list.Count > 1 && IsPageLink(last, options.OverflowName) && options.IsGenerated(last.subMenu))
                {
                    for (var i = 0; i < list.Count - 1; i++) result.Add(Clone(list[i]));
                    at = last.subMenu;
                    changed = true;
                    continue;
                }

                foreach (var control in list) result.Add(Clone(control));
                at = null;
            }

            return result;
        }

        /// <summary>
        /// Recognises an overflow link: a submenu, last on its page, driving no
        /// parameter, labelled either the way Modular Avatar labels one or the
        /// way we do.
        /// </summary>
        public static bool IsPageLink(Control control, string overflowName)
        {
            if (control == null) return false;
            if (control.type != Control.ControlType.SubMenu || control.subMenu == null) return false;
            if (control.parameter != null && !string.IsNullOrEmpty(control.parameter.name)) return false;
            if (control.name == DefaultOverflowName) return true;
            return !string.IsNullOrEmpty(overflowName) && control.name == overflowName;
        }

        // --- the order the user set -------------------------------------------------

        static MenuOrderGroup Group(Options options, string path)
        {
            if (options.Order == null) return null;
            foreach (var group in options.Order)
                if ((group.menuPath ?? "") == path && group.items != null && group.items.Count > 0)
                    return group;
            return null;
        }

        /// <summary>
        /// Sorts in place to match the recorded order. Anything the recording
        /// does not mention was added by a tool since, and keeps its place at
        /// the end rather than disappearing.
        /// </summary>
        static bool Reorder(List<Control> controls, MenuOrderGroup group)
        {
            var before = new List<Control>(controls);
            var remaining = new List<Control>(controls);
            var sorted = new List<Control>();

            foreach (var key in group.items)
            {
                var index = Match(remaining, key);
                if (index < 0) continue;
                sorted.Add(remaining[index]);
                remaining.RemoveAt(index);
            }
            sorted.AddRange(remaining);

            controls.Clear();
            controls.AddRange(sorted);
            return !before.SequenceEqual(controls);
        }

        /// <summary>Parameter and value first, because they survive renaming;
        /// the label is the fallback for controls that drive nothing.</summary>
        static int Match(List<Control> controls, MenuItemKey key)
        {
            if (key == null) return -1;

            if (!string.IsNullOrEmpty(key.parameter))
            {
                var exact = controls.FindIndex(c =>
                    c.parameter != null && c.parameter.name == key.parameter &&
                    Mathf.Approximately(c.value, key.value));
                if (exact >= 0) return exact;
            }

            var labelled = controls.FindIndex(c => c.name == key.name && (int)c.type == key.type);
            if (labelled >= 0) return labelled;

            return controls.FindIndex(c => c.name == key.name);
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

        static VRCExpressionsMenu Rebuild(VRCExpressionsMenu original, List<Control> controls, Options options)
        {
            var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            menu.name = original.name;
            menu.controls = controls;
            options.Save(menu);
            return menu;
        }

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
