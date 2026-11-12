using System;
using System.Collections.Generic;
using System.IO;

namespace VrcGitSync
{
    /// <summary>
    /// 配布物の .meta（ID と設定）の扱い。
    /// 配布物の中身は同期しないが .meta は同期し、「GitHub（HEAD）にある ID が正しい」として各PCの ID をそろえる。
    /// Unity API を使わないので、どのスレッドからでも呼べる。
    /// </summary>
    public static class MetaIndex
    {
        static readonly object Lock = new object();
        static string _headId;
        static Dictionary<string, string> _headMap;

        /// <summary>配布物（中身ごと同期するフォルダの外）の .meta か。</summary>
        public static bool IsVendorMeta(string path, string[] syncedFolders)
        {
            path = ProjectPaths.Normalize(path);
            if (!path.StartsWith("Assets/", StringComparison.Ordinal)) return false;
            if (!path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var folder in syncedFolders)
            {
                if (ProjectPaths.IsUnder(path, folder)) return false;
                if (string.Equals(path, folder + ".meta", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        /// <summary>HEAD にある Assets 以下のすべての .meta → GUID。HEAD が変わるまで覚えておく。</summary>
        public static Dictionary<string, string> HeadMap()
        {
            var head = Git.HeadId();
            lock (Lock)
            {
                if (_headMap != null && _headId == head) return _headMap;
            }
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(head))
            {
                foreach (var kv in Git.MetaGuids("HEAD", "Assets")) map[kv.Key] = kv.Value;
            }
            lock (Lock)
            {
                _headMap = map;
                _headId = head;
            }
            return map;
        }

        /// <summary>HEAD に配布物の .meta が1つでもあるか（= この仕組みを使い始めているか）。</summary>
        public static bool HeadHasVendorMetas(string[] syncedFolders)
        {
            foreach (var path in HeadMap().Keys)
            {
                if (IsVendorMeta(path, syncedFolders)) return true;
            }
            return false;
        }

        /// <summary>.meta ファイルから GUID を読む。読めなければ null。</summary>
        public static string ReadGuid(string fullPath)
        {
            try
            {
                foreach (var line in File.ReadLines(fullPath))
                {
                    if (line.StartsWith("guid:", StringComparison.Ordinal)) return line.Substring(5).Trim();
                }
            }
            catch
            {
                // 読めないファイルは無視
            }
            return null;
        }

        /// <summary>手元の .meta の GUID が HEAD と違うか（HEAD にないものは false）。</summary>
        public static bool DiffersFromHead(string metaPath, Dictionary<string, string> headMap)
        {
            if (!headMap.TryGetValue(metaPath, out var headGuid)) return false;
            var local = ReadGuid(ProjectPaths.ToFull(metaPath));
            return local != null && local != headGuid;
        }
    }
}
