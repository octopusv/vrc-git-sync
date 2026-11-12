using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VrcGitSync
{
    /// <summary>
    /// .gitignore と .gitattributes のうち、このツールが管理する区間を書き換える。
    /// 区間の外に手で書いた内容はそのまま残す。
    /// </summary>
    public static class RepoFiles
    {
        const string Begin = "# >>> VRC Git Sync（自動生成。この区間は手で編集しないでください）";
        const string End = "# <<< VRC Git Sync";
        const string LargeBegin = "# >>> VRC Git Sync: 大きいファイル（自動追加）";
        const string LargeEnd = "# <<< VRC Git Sync: 大きいファイル";

        public const string BackupFolderName = "VrcGitSyncBackup";

        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        /// <summary>LFS で扱う拡張子（画像・3Dモデル・音声・動画・フォントなど）。</summary>
        static readonly string[] LfsExtensions =
        {
            // 画像
            "png", "jpg", "jpeg", "tga", "tif", "tiff", "bmp", "gif", "webp", "exr", "hdr", "dds",
            // ペイントソフト
            "psd", "psb", "clip", "kra", "xcf", "sai", "sai2", "mdp",
            // 3D
            "fbx", "obj", "blend", "dae", "3ds", "max", "ma", "mb", "glb", "vrm",
            // 音声・動画
            "wav", "mp3", "ogg", "aif", "aiff", "flac", "m4a", "mp4", "mov", "webm", "avi",
            // その他
            "ttf", "otf", "zip", "7z", "rar", "dll", "bytes",
        };

        static string GitignorePath => ProjectPaths.Root + "/.gitignore";
        static string GitattributesPath => ProjectPaths.Root + "/.gitattributes";

        /// <summary>.gitignore と .gitattributes を今のバージョンの内容にする。どれかが変わったら true。</summary>
        public static bool WriteAll(string ownFolder, IEnumerable<string> extraFolders = null)
        {
            bool a = WriteBlock(GitignorePath, Begin, End, BuildGitignore(ownFolder, extraFolders));
            bool b = WriteBlock(GitattributesPath, Begin, End, BuildGitattributes());
            return a || b;
        }

        public static string BuildGitignore(string ownFolder, IEnumerable<string> extraFolders = null)
        {
            string name = EscapePattern(ownFolder.Substring(ownFolder.LastIndexOf('/') + 1));
            var extras = new List<string>();
            foreach (var f in extraFolders ?? new string[0])
            {
                var n = ProjectPaths.Normalize(f);
                if (n.StartsWith("Assets/", StringComparison.Ordinal)) extras.Add("!/" + string.Join("/", n.Split('/').Select(EscapePattern)) + "/**");
            }
            var lines = new List<string>
            {
                "# Unity が自動で作るもの",
                "/[Ll]ibrary/",
                "/[Tt]emp/",
                "/[Oo]bj/",
                "/[Bb]uild/",
                "/[Bb]uilds/",
                "/[Ll]ogs/",
                "/[Uu]ser[Ss]ettings/",
                "/[Mm]emoryCaptures/",
                "/[Rr]ecordings/",
                "",
                "# エディタ・IDE",
                ".vs/",
                ".vscode/",
                ".idea/",
                "*.csproj",
                "*.sln",
                "*.suo",
                "*.user",
                "*.pidb",
                "*.pdb",
                "*.mdb",
                "*.opendb",
                "*.VC.db",
                "",
                "# OS",
                ".DS_Store",
                "Thumbs.db",
                "Desktop.ini",
                "",
                "# Assets: 自分のフォルダは中身ごと、それ以外（配布物）は .meta（ID と設定）だけを同期する",
                "# （zip で配られた素材は PC ごとに ID が変わってしまうので、.meta をそろえて参照がつながるようにする）",
                "/Assets/**",
                "!/Assets/**/",
                "!/Assets/**/*.meta",
                "!/Assets/" + name + "/**",
            };
            if (extras.Count > 0)
            {
                lines.Add("# 自分のフォルダ以外に、中身ごと同期するフォルダ");
                lines.AddRange(extras);
            }
            lines.AddRange(new[]
            {
                "",
                "# Assets の中でも同期しないもの（ビルド時の一時ファイル、元の unitypackage、バックアップ）",
                "/Assets/ZZZ_GeneratedAssets*",
                "*.unitypackage",
                "*.unitypackage.meta",
                "*.blend1",
                "*.blend1.meta",
                "*.blend2",
                "*.blend2.meta",
                "/" + BackupFolderName + "/",
                "",
                "# Packages は一覧ファイルだけを同期する（中身は VCC / ALCOM で入れる）",
                "/Packages/*",
                "!/Packages/manifest.json",
                "!/Packages/packages-lock.json",
                "!/Packages/vpm-manifest.json",
            });
            return string.Join("\n", lines);
        }

        public static string BuildGitattributes()
        {
            var lines = new List<string>
            {
                "# テキストの改行は LF にそろえる（バイナリには影響しない）",
                "* text=auto eol=lf",
                "",
                "# 大きいバイナリは Git LFS で扱う",
            };
            foreach (var ext in LfsExtensions)
            {
                lines.Add("*." + CaseInsensitive(ext) + " filter=lfs diff=lfs merge=lfs -text");
            }
            return string.Join("\n", lines);
        }

        /// <summary>大きいファイルを個別に LFS の対象にする。</summary>
        public static void AddLargeFilePatterns(IEnumerable<string> paths)
        {
            var existing = ReadBlock(GitattributesPath, LargeBegin, LargeEnd);
            var set = new List<string>(existing);
            foreach (var p in paths)
            {
                var line = "/" + EscapePattern(p) + " filter=lfs diff=lfs merge=lfs -text";
                if (!set.Contains(line)) set.Add(line);
            }
            WriteBlock(GitattributesPath, LargeBegin, LargeEnd, string.Join("\n", set));
        }

        /// <summary>"png" → "[pP][nN][gG]"（大文字の拡張子にも一致させる）。</summary>
        static string CaseInsensitive(string ext)
        {
            var sb = new StringBuilder();
            foreach (char c in ext)
            {
                if (char.IsLetter(c)) sb.Append('[').Append(char.ToLowerInvariant(c)).Append(char.ToUpperInvariant(c)).Append(']');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>gitignore / gitattributes のパターンとして文字通りに一致させるためのエスケープ。</summary>
        public static string EscapePattern(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == ' ' || c == '\t') sb.Append('?'); // 空白はパターンの区切りになるので任意の1文字で代用
                else if ("\\*?[]!#".IndexOf(c) >= 0) sb.Append('\\').Append(c);
                else sb.Append(c);
            }
            return sb.ToString();
        }

        static List<string> ReadBlock(string file, string begin, string end)
        {
            var result = new List<string>();
            if (!File.Exists(file)) return result;
            bool inside = false;
            foreach (var raw in File.ReadAllLines(file, Encoding.UTF8))
            {
                var line = raw.TrimEnd('\r');
                if (line == begin) { inside = true; continue; }
                if (line == end) { inside = false; continue; }
                if (inside && line.Length > 0) result.Add(line);
            }
            return result;
        }

        /// <summary>管理する区間を書き換える。内容が変わらなければファイルに触らない。変わったら true。</summary>
        static bool WriteBlock(string file, string begin, string end, string body)
        {
            string before = File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : null;
            var lines = before != null
                ? before.Replace("\r\n", "\n").Split('\n').ToList()
                : new List<string>();
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);

            int b = lines.IndexOf(begin);
            int e = lines.IndexOf(end);
            var block = new List<string> { begin };
            block.AddRange(body.Split('\n'));
            block.Add(end);

            if (b >= 0 && e > b)
            {
                lines.RemoveRange(b, e - b + 1);
                lines.InsertRange(b, block);
            }
            else
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Length > 0) lines.Add("");
                lines.AddRange(block);
            }
            var after = string.Join("\n", lines) + "\n";
            if (after == before) return false;
            File.WriteAllText(file, after, Utf8);
            return true;
        }
    }
}
