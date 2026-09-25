using System;
using System.Collections.Generic;
using System.Linq;
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
        /// <summary>Labels from the root down. Only for showing: what a change
        /// is recorded against is <see cref="Container"/> and <see cref="Origin"/>.</summary>
        public string Path = "";
        public Texture2D Icon;
        public int Type;
        public string Parameter = "";
        public float Value;
        public bool IsSubMenu;
        public List<MenuNode> Children = new List<MenuNode>();

        /// <summary>For a submenu, the container its children are recorded
        /// against. It does not change when the submenu is moved.</summary>
        public string Container = "";

        // Set by MenuPreview on the tree the window shows.

        /// <summary>The container the tools put this control in; null for a
        /// folder, which no tool put anywhere.</summary>
        public string Origin;
        /// <summary>Set for a submenu made in the window.</summary>
        public MenuFolder Folder;
        /// <summary>The recorded move that put it here, if one did.</summary>
        public MenuMove Move;
        public MenuItemKey ItemKey;
        /// <summary>The submenu row (or the root) this row sits in.</summary>
        public MenuNode Parent;

        public bool IsFolder => Folder != null;
        public bool Moved => Move != null;

        public MenuItemKey Key()
        {
            if (ItemKey != null) return MenuStructure.Copy(ItemKey);
            return new MenuItemKey { name = Name, parameter = Parameter, value = Value, type = Type };
        }
    }

    /// <summary>
    /// Works out what the tools make of the avatar's menu, by building the
    /// avatar and reading the result.
    ///
    /// There is no shortcut: a tool's menu items exist only as instructions
    /// until a build turns them into a menu, and the tools disagree about when
    /// and in what order they do it. Guessing would be wrong exactly when it
    /// matters. So a copy of the avatar is built for real, the finished menu is
    /// copied out as plain data, and the copy is thrown away — the scene is left
    /// as it was found, dirty flag included.
    ///
    /// Yuki Menu's own pass is left out of that build: what comes back is what
    /// the other tools made, which is what the changes set in the window are
    /// recorded against. The window lays those changes over it itself.
    ///
    /// The paging is undone in the copy, so what comes back is the list of menu
    /// items rather than the pages they were split across. The pages are a
    /// consequence of the list, and the window draws them from it.
    /// </summary>
    public static class MenuSnapshot
    {
        /// <summary>True when the last capture had to run the whole VRChat
        /// preprocessor chain because VRCFury is on the avatar.</summary>
        public static bool LastRunWasFull { get; private set; }

        public static long LastMilliseconds { get; private set; }

        public static MenuNode Capture(GameObject avatarRoot, string overflowName, int itemsPerPage, out string error)
        {
            error = null;
            if (avatarRoot == null) { error = "no avatar"; return null; }

            var scene = avatarRoot.scene;
            var wasDirty = scene.IsValid() && scene.isDirty;

            var wanted = HasVRCFury(avatarRoot);
            var full = false;
            var clock = System.Diagnostics.Stopwatch.StartNew();

            GameObject clone = null;
            try
            {
                clone = UnityEngine.Object.Instantiate(avatarRoot);
                clone.name = avatarRoot.name;
                clone.transform.position = avatarRoot.transform.position + Vector3.right * 1000f;

                // Without the component our pass does nothing, so the result is
                // the other tools' work alone.
                foreach (var own in clone.GetComponentsInChildren<YukiMenuLayout>(true))
                    UnityEngine.Object.DestroyImmediate(own);

                full = wanted && ProcessEverything(clone);
                LastRunWasFull = full;
                if (!full) Process(clone);

                var descriptor = clone.GetComponent<VRCAvatarDescriptor>();
                var menu = descriptor != null ? descriptor.expressionsMenu : null;
                if (menu == null) return new MenuNode { Name = avatarRoot.name, IsSubMenu = true };

                var root = Build(menu, "", overflowName, itemsPerPage, new HashSet<VRCExpressionsMenu>());
                root.Name = avatarRoot.name;
                return root;
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
                // The full chain ends at upload, so nothing cleans up after it
                // here; the assets the build wrote are ours to remove.
                if (full) TryCleanTemporaryAssets();
                // Looking at the menu is not editing the scene.
                if (scene.IsValid() && !wasDirty) ClearDirtiness(scene);
                clock.Stop();
                LastMilliseconds = clock.ElapsedMilliseconds;
            }
        }

        /// <summary>
        /// VRCFury components, recognised by their namespace rather than by a
        /// reference, so this package does not depend on VRCFury being there.
        /// </summary>
        public static bool HasVRCFury(GameObject avatarRoot)
        {
            foreach (var component in avatarRoot.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                var name = component.GetType().FullName;
                if (name != null && name.StartsWith("VF.")) return true;
            }
            return false;
        }

        /// <summary>
        /// Runs the whole VRChat preprocessor chain, which is the only way to
        /// see VRCFury's menu items: VRCFury is not an NDMF plugin, it is an SDK
        /// preprocessor, and it runs between NDMF's two halves. The chain is the
        /// same one an upload runs, on a copy that is thrown away.
        ///
        /// Reached by name rather than by a reference, so that a change in the
        /// SDK costs the VRCFury items rather than the whole window.
        /// </summary>
        static bool ProcessEverything(GameObject clone)
        {
            try
            {
                // Found by walking the loaded assemblies: the SDK's editor
                // assembly is named VRCSDKBase-Editor, which is not something to
                // rely on staying that way.
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("VRC.SDKBase.Editor.BuildPipeline.VRCBuildPipelineCallbacks"))
                    .FirstOrDefault(x => x != null);
                if (type == null) return false;
                var method = type.GetMethod("OnPreprocessAvatar",
                    BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(GameObject) }, null);
                if (method == null) return false;
                method.Invoke(null, new object[] { clone });
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Yuki Menu] The full build failed, falling back to the NDMF-only one. " +
                                 e.GetBaseException().Message);
                return false;
            }
        }

        static void TryCleanTemporaryAssets()
        {
            try { AvatarProcessor.CleanTemporaryAssets(); }
            catch (Exception e) { Debug.LogWarning("[Yuki Menu] " + e.Message); }
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

        static MenuNode Build(VRCExpressionsMenu menu, string path, string overflowName, int itemsPerPage,
                              HashSet<VRCExpressionsMenu> open)
        {
            var node = new MenuNode { Path = path, Container = path, IsSubMenu = true };
            if (menu == null || !open.Add(menu)) return node;

            foreach (var control in Flatten(menu, overflowName, itemsPerPage))
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
                child.Container = child.Path;
                if (child.IsSubMenu && control.subMenu != null)
                    child.Children = Build(control.subMenu, child.Path, overflowName, itemsPerPage, open).Children;
                node.Children.Add(child);
            }

            open.Remove(menu);
            return node;
        }

        /// <summary>
        /// The menu's controls with its pages folded back in. Everything here
        /// came out of a build, so a link that looks like paging is paging.
        /// </summary>
        static List<Control> Flatten(VRCExpressionsMenu menu, string overflowName, int itemsPerPage)
        {
            return MenuPaginator.FlattenPages(menu, overflowName, itemsPerPage, _ => true);
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
