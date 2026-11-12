using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VrcGitSync
{
    /// <summary>
    /// 配布物のマテリアルが保存される直前の内容を Temp フォルダに控えておき、
    /// 「コピーして差し替え」のあとで配布物を元に戻せるようにする。
    /// Temp は Unity を閉じると消えるので、控えはその間だけ有効。
    /// </summary>
    static class OriginalCache
    {
        static string Dir => ProjectPaths.ToFull("Temp/VrcGitSync/originals");

        static string FileFor(string assetPath)
        {
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid)) return null;
            return Path.Combine(Dir, guid + ".bak");
        }

        /// <summary>保存前の内容を控える。すでに控えていれば何もしない（最初の状態を残す）。</summary>
        public static void Remember(string assetPath)
        {
            try
            {
                var backup = FileFor(assetPath);
                if (backup == null || File.Exists(backup)) return;
                var full = ProjectPaths.ToFull(assetPath);
                if (!File.Exists(full)) return;
                // 大きいファイル（テクスチャなど）は Unity が保存し直すことはまずないので控えない
                if (new FileInfo(full).Length > 20L * 1024 * 1024) return;
                Directory.CreateDirectory(Dir);
                File.Copy(full, backup, false);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VRC Git Sync] 元の内容を控えられませんでした: " + assetPath + " (" + e.Message + ")");
            }
        }

        public static bool Has(string assetPath)
        {
            var backup = FileFor(assetPath);
            return backup != null && File.Exists(backup);
        }

        /// <summary>控えた内容に戻して読み込み直す。</summary>
        public static bool Restore(string assetPath)
        {
            try
            {
                var backup = FileFor(assetPath);
                if (backup == null || !File.Exists(backup)) return false;
                File.Copy(backup, ProjectPaths.ToFull(assetPath), true);
                File.Delete(backup);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VRC Git Sync] 元に戻せませんでした: " + assetPath + " (" + e.Message + ")");
                return false;
            }
        }
    }
}
