using System;
using System.Collections.Generic;
using System.Reflection;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using Control = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control;

namespace TsiYuki.Menus.Editor
{
    /// <summary>One control of the finished menu, as plain data.</summary>
    public class MenuNode
    {
        public string Name = "";
        public string Path = "";
        public Texture2D Icon;
        public int Type;
        public string Parameter = "";
        public float Value;
        public bool IsSubMenu;
        public List<MenuNode> Children = new List<MenuNode>();

        public MenuItemKey Key()
        {
            return new MenuItemKey { name = Name, parameter = Parameter, value = Value, type = Type };
        }
    }

    /// <summary>
    /// Works out what the avatar's menu actually ends up as, by building the
    /// avatar and reading the result.
    ///
    /// There is no shortcut: a tool's menu items exist only as instructions
    /// until a build turns them into a menu, and the tools disagree about when
    /// and in what order they do it. Guessing would be wrong exactly when it
    /// matters. So a copy of the avatar is built for real, the finished menu is
    /// copied out as plain data, and the copy is thrown away — the scene is left
    /// as it was found, dirty flag included.
    ///
    /// The paging is undone in the copy, so what comes back is the list of menu
    /// items rather than the pages they were split across. The pages are a
    /// consequence of the list, and the window draws them from it.
    /// </summary>
    public static class MenuSnapshot
    {
        public static MenuNode Capture(GameObject avatarRoot, string overflowName, out string error)
        {
            error = null;
            if (avatarRoot == null) { error = "no avatar"; return null; }

            var scene = avatarRoot.scene;
            var wasDirty = scene.IsValid() && scene.isDirty;

            GameObject clone = null;
            try
            {
                clone = UnityEngine.Object.Instantiate(avatarRoot);
                clone.name = avatarRoot.name;
                clone.transform.position = avatarRoot.transform.position + Vector3.right * 1000f;

                Process(clone);

                var descriptor = clone.GetComponent<VRCAvatarDescriptor>();
                var menu = descriptor != null ? descriptor.expressionsMenu : null;
                if (menu == null) return new MenuNode { Name = avatarRoot.name };

                return Build(menu, "", overflowName, new HashSet<VRCExpressionsMenu>());
            }
            catch (Exception e)
            {
                error = e.Message;
                Debug.LogException(e);
                return null;
            }
            finally
            {
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                // Looking at the menu is not editing the scene.
                if (scene.IsValid() && !wasDirty) ClearDirtiness(scene);
            }
        }

        /// <summary>
        /// Everything that shapes the menu is done by the end of Transforming,
        /// and the phases after it are the expensive ones — meshes, textures,
        /// optimisation. Stopping there is the same answer in a fraction of the
        /// time, so it is worth asking NDMF for it even though the shorter build
        /// is not part of its public surface. If that entry point ever goes
        /// away, the full build still gives the right result.
        /// </summary>
        static void Process(GameObject clone)
        {
            var method = typeof(AvatarProcessor).GetMethod(
                "ProcessAvatar",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(GameObject), typeof(BuildPhase) },
                null);

            if (method != null)
            {
                method.Invoke(null, new object[] { clone, BuildPhase.Transforming });
                return;
            }

            AvatarProcessor.ProcessAvatar(clone);
        }

        static MenuNode Build(VRCExpressionsMenu menu, string path, string overflowName,
                              HashSet<VRCExpressionsMenu> open)
        {
            var node = new MenuNode { Path = path, IsSubMenu = true };
            if (menu == null || !open.Add(menu)) return node;

            foreach (var control in Flatten(menu, overflowName))
            {
                var child = new MenuNode
                {
                    Name = control.name ?? "",
                    Path = MenuPaginator.Join(path, control.name),
                    Icon = control.icon,
                    Type = (int)control.type,
                    Parameter = control.parameter != null ? control.parameter.name ?? "" : "",
                    Value = control.value,
                    IsSubMenu = control.type == Control.ControlType.SubMenu,
                };
                if (child.IsSubMenu && control.subMenu != null)
                    child.Children = Build(control.subMenu, child.Path, overflowName, open).Children;
                node.Children.Add(child);
            }

            open.Remove(menu);
            return node;
        }

        /// <summary>The controls of a menu and of every page hanging off it, as
        /// one list. Everything here came out of a build, so a link that looks
        /// like paging is paging.</summary>
        static List<Control> Flatten(VRCExpressionsMenu menu, string overflowName)
        {
            var result = new List<Control>();
            var seen = new HashSet<VRCExpressionsMenu>();
            var at = menu;

            while (at != null && seen.Add(at))
            {
                var list = at.controls;
                var last = list.Count > 0 ? list[list.Count - 1] : null;
                if (list.Count > 1 && MenuPaginator.IsPageLink(last, overflowName))
                {
                    for (var i = 0; i < list.Count - 1; i++) result.Add(list[i]);
                    at = last.subMenu;
                    continue;
                }
                result.AddRange(list);
                at = null;
            }

            return result;
        }

        static void ClearDirtiness(UnityEngine.SceneManagement.Scene scene)
        {
            // Internal, and the only way to put the flag back.
            var method = typeof(EditorSceneManager).GetMethod(
                "ClearSceneDirtiness",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(UnityEngine.SceneManagement.Scene) },
                null);
            if (method != null) method.Invoke(null, new object[] { scene });
        }
    }
}
