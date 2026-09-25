using TsiYuki.Core.Editor;

namespace TsiYuki.Menus.Editor
{
    /// <summary>
    /// UI strings (Localization/&lt;code&gt;.txt) for both the editor UI and NDMF's
    /// error report window.
    /// </summary>
    public static class MenuText
    {
        public const string Package = "moe.tsiyuki.menu";
        public static readonly YukiLocalizer L = new YukiLocalizer(Package);

        public static readonly YukiNdmfReport Errors = new YukiNdmfReport(L);
    }
}
