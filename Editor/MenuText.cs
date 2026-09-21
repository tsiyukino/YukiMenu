using System;
using System.Collections.Generic;
using System.IO;
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

        static nadena.dev.ndmf.localization.Localizer _ndmf;

        // NDMF shows errors in its own language setting; we feed it our tables.
        public static nadena.dev.ndmf.localization.Localizer Ndmf => _ndmf ?? (_ndmf =
            new nadena.dev.ndmf.localization.Localizer("en-us", () => new List<(string, Func<string, string>)>
            {
                ("en-us", Table("en")),
                ("zh-hans", Table("zh-Hans")),
                ("ja-jp", Table("ja")),
            }));

        static Func<string, string> Table(string code)
        {
            var table = new Dictionary<string, string>();
            var path = Path.GetFullPath($"Packages/{Package}/Localization/{code}.txt");
            if (File.Exists(path))
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    table[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim().Replace("\\n", "\n");
                }
            return key => table.TryGetValue(key, out var v) ? v : null;
        }
    }
}
