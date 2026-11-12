using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VrcGitSync
{
    /// <summary>
    /// プロジェクト内のパスを扱うユーティリティ。
    /// アセットパスは "Assets/..." 形式、区切りは常に "/"。
    /// </summary>
    public static class ProjectPaths
    {
        static string _root;

        [InitializeOnLoadMethod]
        static void Init()
        {
            _root = Normalize(Path.GetDirectoryName(Application.dataPath));
        }

        /// <summary>プロジェクトのルート（Assets フォルダの親）。</summary>
        public static string Root
        {
            get
            {
                if (_root == null) Init();
                return _root;
            }
        }

        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            return path.Replace('\\', '/').TrimEnd('/');
        }

        public static string ToFull(string projectRelativePath)
        {
            return Root + "/" + Normalize(projectRelativePath);
        }

        /// <summary>path が folder 自身、または folder の中にあるか。</summary>
        public static bool IsUnder(string path, string folder)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(folder)) return false;
            path = Normalize(path);
            folder = Normalize(folder);
            return path.Equals(folder, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>"Assets/X/Y/z.mat" → "Assets/X"。Assets 直下のファイルなら null。</summary>
        public static string TopLevelAssetFolder(string assetPath)
        {
            assetPath = Normalize(assetPath);
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal)) return null;
            int next = assetPath.IndexOf('/', "Assets/".Length);
            if (next < 0) return null;
            return assetPath.Substring(0, next);
        }

        /// <summary>フォルダ名として使えるか（Assets 直下に作る1階層の名前）。</summary>
        public static bool IsValidFolderName(string name, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(name)) { reason = "フォルダ名を入力してください。"; return false; }
            if (name != name.Trim()) { reason = "フォルダ名の前後に空白は使えません。"; return false; }
            if (name.StartsWith(".")) { reason = "「.」で始まる名前は使えません。"; return false; }
            if (name.EndsWith("~")) { reason = "「~」で終わる名前はUnityに無視されるので使えません。"; return false; }
            if (name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|', '[', ']', '!', '#' }) >= 0)
            {
                reason = "フォルダ名に / \\ : * ? \" < > | [ ] ! # は使えません。";
                return false;
            }
            return true;
        }

        /// <summary>"Assets/A/B" のようなフォルダを、なければ順に作る。</summary>
        public static void EnsureAssetFolder(string folder)
        {
            folder = Normalize(folder);
            if (AssetDatabase.IsValidFolder(folder)) return;
            int slash = folder.LastIndexOf('/');
            string parent = folder.Substring(0, slash);
            string name = folder.Substring(slash + 1);
            EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }
    }
}
