using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

[assembly: ExportsPlugin(typeof(TsiYuki.Menus.Editor.MenuPlugin))]

namespace TsiYuki.Menus.Editor
{
    /// <summary>
    /// Gives the finished menu the shape the user asked for.
    ///
    /// Every tool installs its menu items by its own rules, and none of them
    /// can see the result, because the result does not exist until they have
    /// all run. So this runs last — after Modular Avatar, which is what merges
    /// them all together — and works on the finished thing.
    /// </summary>
    public class MenuPlugin : Plugin<MenuPlugin>
    {
        public override string QualifiedName => "moe.tsiyuki.menu";
        public override string DisplayName => "Yuki Menu";

        protected override void Configure()
        {
            InPhase(BuildPhase.Transforming)
                // Modular Avatar merges every installer into the one real menu
                // and pages what overflows; there is nothing to lay out before
                // it has done that.
                .AfterPlugin("nadena.dev.modular-avatar")
                .Run("Lay out the avatar menu", Execute);
        }

        static void Execute(BuildContext ctx)
        {
            var components = ctx.AvatarRootObject.GetComponentsInChildren<YukiMenuLayout>(true);
            if (components.Length == 0) return;

            var layout = components[0];
            if (components.Length > 1)
                Report(ErrorSeverity.NonFatal, "warn.multiple", layout,
                       new object[] { components.Length.ToString(), Path(ctx, layout) });

            var descriptor = ctx.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();
            var menu = descriptor != null ? descriptor.expressionsMenu : null;

            if (menu == null)
            {
                Report(ErrorSeverity.Information, "warn.no_menu", layout, new object[0]);
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
                    IsGenerated = m => m != null && ctx.IsTemporaryAsset(m),
                    Save = m => ctx.AssetSaver.SaveAsset(m),
                };

                var result = MenuPaginator.Run(menu, options);
                if (result != menu)
                {
                    descriptor.expressionsMenu = result;
                    Debug.Log($"[Yuki Menu] Laid out the menu at {layout.itemsPerPage} per page" +
                              (options.Everywhere ? " (every submenu)." : " (root wheel only)."));
                }
            }

            foreach (var component in components)
                if (component != null) Object.DestroyImmediate(component);
        }

        static string Path(BuildContext ctx, Component component)
        {
            var parts = new System.Collections.Generic.List<string>();
            for (var at = component.transform; at != null && at != ctx.AvatarRootTransform; at = at.parent)
                parts.Add(at.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        static void Report(ErrorSeverity severity, string key, Object context, object[] args)
        {
            var all = (args ?? new object[0]).Select(a => (object)(a?.ToString() ?? "")).ToList();
            if (context != null) all.Add(context);
            ErrorReport.ReportError(MenuText.Ndmf, severity, key, all.ToArray());
        }
    }
}
