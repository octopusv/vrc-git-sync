using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace VrcGitSync
{
    /// <summary>
    /// GitHub に記録されている VPM パッケージのバージョン（Packages/vpm-manifest.json の locked）と、このPCに入っているバージョンを比べる。
    /// 2台でバージョンが違うと、シーンなどに書き込まれる項目が変わって、何もしていなくても変更が出ることがあるため。
    /// </summary>
    public static class PackageCheck
    {
        public struct Mismatch
        {
            public string Name;
            /// <summary>vpm-manifest.json に記録されているバージョン。</summary>
            public string Recorded;
            /// <summary>このPCに入っているバージョン。入っていなければ null。</summary>
            public string Installed;
        }

        // "locked" の中の  "パッケージ名": { "version": "1.2.3", … }
        static readonly Regex LockedEntry = new Regex("\"([^\"]+)\"\\s*:\\s*\\{\\s*\"version\"\\s*:\\s*\"([^\"]+)\"");
        static readonly Regex VersionField = new Regex("\"version\"\\s*:\\s*\"([^\"]+)\"");

        public static List<Mismatch> Find(string projectRoot)
        {
            var list = new List<Mismatch>();
            try
            {
                var manifest = Path.Combine(projectRoot, "Packages", "vpm-manifest.json");
                if (!File.Exists(manifest)) return list;
                var text = File.ReadAllText(manifest);
                int locked = text.IndexOf("\"locked\"", StringComparison.Ordinal);
                if (locked < 0) return list;
                foreach (Match m in LockedEntry.Matches(text, locked + "\"locked\"".Length))
                {
                    var name = m.Groups[1].Value;
                    var recorded = m.Groups[2].Value;
                    string installed = null;
                    var packageJson = Path.Combine(projectRoot, "Packages", name, "package.json");
                    if (File.Exists(packageJson))
                    {
                        var v = VersionField.Match(File.ReadAllText(packageJson));
                        if (v.Success) installed = v.Groups[1].Value;
                    }
                    if (installed != recorded) list.Add(new Mismatch { Name = name, Recorded = recorded, Installed = installed });
                }
            }
            catch (IOException)
            {
                // 読めなければ比べない
            }
            catch (UnauthorizedAccessException)
            {
            }
            return list;
        }
    }
}
