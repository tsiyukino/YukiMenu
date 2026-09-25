using System.Linq;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

[assembly: ExportsPlugin(typeof(TsiYuki.Menus.Editor.MenuPlugin))]

namespace TsiYuki.Menus.Editor
{
    /// <summary>
    /// Gives the finished menu the shape the user asked for.
    ///
    /// Every tool installs its menu items by its own rules, and none of them
    /// can see the result, because the result does not exist until they have
    /// all run. So this runs last and works on the finished thing.
    ///
    /// Last means the Optimizing phase rather than Transforming, and the reason
    /// is VRCFury. VRCFury is not an NDMF plugin: it is a VRChat SDK
    /// preprocessor at order -10000, which puts it after NDMF's Transforming
    /// phase (-11000) and before its Optimizing phase (-1025). It merges the
    /// whole menu into one of its own, adds its items, and splits every menu at
    /// eight again — so anything laid out in Transforming is undone before the
    /// avatar is uploaded. Optimizing is on the far side of it, where what we
    /// see includes VRCFury's items and is what ships.
    /// </summary>
    public class MenuPlugin : Plugin<MenuPlugin>
    {
        public override string QualifiedName => "moe.tsiyuki.menu";
        public override string DisplayName => "Yuki Menu";

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                // Avatar Optimizer can drop controls whose parameters it has
                // removed; the pages should be counted after that.
                .AfterPlugin("com.anatawa12.avatar-optimizer")
                .Run("Lay out the avatar menu", Execute);
        }

        static void Execute(BuildContext ctx)
        {
            var components = ctx.AvatarRootObject.GetComponentsInChildren<YukiMenuLayout>(true);
            if (components.Length == 0) return;

            var layout = components[0];
            if (components.Length > 1)
                MenuText.Errors.Report(ErrorSeverity.NonFatal, "warn.multiple", layout,
                       new object[] { components.Length.ToString(), Path(ctx, layout) });

            var descriptor = ctx.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();
            var menu = descriptor != null ? descriptor.expressionsMenu : null;

            if (menu == null)
            {
                MenuText.Errors.Report(ErrorSeverity.Information, "warn.no_menu", layout, new object[0]);
            }
            else if (layout.repage)
            {
                var options = new MenuPaginator.Options
                {
                    ItemsPerPage = layout.itemsPerPage,
                    Everywhere = layout.scope == PagingScope.Everywhere,
                    OverflowName = string.IsNullOrEmpty(layout.overflowName)
                        ? MenuPaginator.DefaultOverflowName
                        : layout.overflowName,
                    OverflowIcon = layout.overflowIcon,
                    OverflowFirst = layout.overflowAt == OverflowPlacement.Start,
                    Order = layout.order,
                    Folders = layout.folders,
                    Moves = layout.moves,
                    IsGenerated = Generated(ctx),
                    Save = m => ctx.AssetSaver.SaveAsset(m),
                };

                var result = MenuPaginator.Run(menu, options, out var problems);
                foreach (var problem in problems) ReportProblem(problem, layout);

                if (result != menu)
                {
                    descriptor.expressionsMenu = result;
                    Debug.Log($"[Yuki Menu] Laid out the menu at {layout.itemsPerPage} per page" +
                              (options.Everywhere ? " (every submenu)" : " (root wheel only)") +
                              (layout.HasStructure
                                  ? $", with {layout.folders.Count} submenu(s) made and {layout.moves.Count - problems.Count(p => p.Move != null)} move(s)."
                                  : "."));
                }
            }

            foreach (var component in components)
                if (component != null) Object.DestroyImmediate(component);
        }

        /// <summary>
        /// A change that could not be made is skipped, and the control stays
        /// where its tool put it; the report says which, so it can be tidied up
        /// in the window.
        /// </summary>
        static void ReportProblem(StructureProblem problem, YukiMenuLayout layout)
        {
            var root = MenuText.L["ui.root_short"];
            if (problem.Folder != null)
            {
                MenuText.Errors.Report(ErrorSeverity.NonFatal, "warn.folder_parent_missing", layout, new object[]
                {
                    MenuPreview.Plain(problem.Folder.name),
                });
                return;
            }

            var move = problem.Move;
            var item = MenuPreview.Plain(move.item != null ? move.item.name : "");
            var from = MenuPreview.Describe(move.from, layout, root);
            var to = MenuPreview.Describe(move.to, layout, root);
            switch (problem.Kind)
            {
                case StructureProblemKind.MissingItem:
                    MenuText.Errors.Report(ErrorSeverity.NonFatal, "warn.move_item_missing", layout, new object[] { item, from });
                    break;
                case StructureProblemKind.MissingTarget:
                    MenuText.Errors.Report(ErrorSeverity.NonFatal, "warn.move_target_missing", layout, new object[] { item, to });
                    break;
                case StructureProblemKind.WouldContainItself:
                    MenuText.Errors.Report(ErrorSeverity.NonFatal, "warn.move_into_itself", layout, new object[] { item, to });
                    break;
            }
        }

        /// <summary>
        /// The three places a menu made during this build can be: NDMF's own
        /// temporary folder, nowhere at all (held only in memory), and the
        /// scratch package VRCFury writes its build into. A menu anywhere else
        /// is a file in the project and belongs to the user.
        /// </summary>
        const string VrcFuryBuilds = "Packages/com.vrcfury.temp/";

        static System.Func<VRCExpressionsMenu, bool> Generated(BuildContext ctx)
        {
            return menu =>
            {
                if (menu == null) return false;
                if (ctx.IsTemporaryAsset(menu)) return true;
                var path = AssetDatabase.GetAssetPath(menu);
                return string.IsNullOrEmpty(path) || path.StartsWith(VrcFuryBuilds);
            };
        }

        static string Path(BuildContext ctx, Component component)
        {
            var parts = new System.Collections.Generic.List<string>();
            for (var at = component.transform; at != null && at != ctx.AvatarRootTransform; at = at.parent)
                parts.Add(at.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
