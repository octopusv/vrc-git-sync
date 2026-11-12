using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VrcGitSync
{
    /// <summary>
    /// .unitypackage を上書きでインポートして更新したときに、古いバージョンにだけあったファイルを消す。
    /// （.unitypackage はファイルを追加・上書きするだけで、消すことはできないため）
    ///
    /// 消すファイルの一覧は、.unitypackage に入っている UpdateInfo.txt（GUID 固定）に書かれている。
    /// Unity はどこかにコンパイルエラーがあると新しいコードを読み込まないので、
    /// 古いファイルが原因のエラーを防げるよう、インポート中（まだ古いバージョンのこのコードが動いている間）に消す。
    /// </summary>
    class PackageUpdater : AssetPostprocessor
    {
        /// <summary>UpdateInfo.txt の GUID（Tools~/build-unitypackage.py の UPDATE_INFO_GUID と同じ）。</summary>
        const string UpdateInfoGuid = "5d589403bad84068a57be737ab9e9691";

        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            var infoPath = AssetDatabase.GUIDToAssetPath(UpdateInfoGuid);
            if (string.IsNullOrEmpty(infoPath)) return;
            // ツールのフォルダの中身が変わったとき（パッケージのインポートなど）に確認する。
            // コンパイルエラーがあると、Unity はスクリプト以外（UpdateInfo.txt）の読み込みを後回しにするので、
            // UpdateInfo.txt が読み込まれるのを待たずに、ディスクにある新しい内容を直接読む。
            var root = ProjectPaths.Normalize(Path.GetDirectoryName(infoPath));
            if (!imported.Concat(deleted).Concat(moved).Any(p => ProjectPaths.IsUnder(p, root))) return;
            CleanUp(infoPath);
        }

        /// <summary>スクリプトの読み込み直後にも確認する（インポート中に消せなかった場合のため）。</summary>
        [InitializeOnLoadMethod]
        static void AfterReload()
        {
            EditorApplication.delayCall += () =>
            {
                var infoPath = AssetDatabase.GUIDToAssetPath(UpdateInfoGuid);
                if (!string.IsNullOrEmpty(infoPath)) CleanUp(infoPath);
            };
        }

        static void CleanUp(string infoPath)
        {
            try
            {
                var full = ProjectPaths.ToFull(infoPath);
                if (!File.Exists(full)) return;
                // ツールのフォルダ（UpdateInfo.txt があるフォルダ）の中のものだけを消す
                var toolRoot = ProjectPaths.Normalize(Path.GetDirectoryName(infoPath));
                string version = "";
                var targets = new List<string>();
                foreach (var raw in File.ReadAllLines(full))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("version ", StringComparison.Ordinal)) version = line.Substring(8).Trim();
                    if (!line.StartsWith("removed ", StringComparison.Ordinal)) continue;
                    var parts = line.Split(new[] { ' ' }, 3);
                    if (parts.Length < 2) continue;
                    var path = AssetDatabase.GUIDToAssetPath(parts[1]);
                    if (string.IsNullOrEmpty(path) || !ProjectPaths.IsUnder(path, toolRoot)) continue;
                    var assetFull = ProjectPaths.ToFull(path);
                    if (File.Exists(assetFull) || Directory.Exists(assetFull)) targets.Add(path);
                }
                if (targets.Count == 0) return;

                // フォルダとその中身が両方あれば、フォルダだけ消す
                targets = targets.Where(p => !targets.Any(o => o != p && ProjectPaths.IsUnder(p, o))).ToList();
                var failed = new List<string>();
                using (SaveWatcher.Suppress())
                {
                    AssetDatabase.DeleteAssets(targets.ToArray(), failed);
                }
                var removed = targets.Except(failed).ToList();
                if (removed.Count > 0)
                {
                    Debug.Log("[VRC Git Sync] v" + version + " への更新で、古いバージョンにだけあったファイルを消しました:\n" + string.Join("\n", removed));
                }
                if (failed.Count > 0)
                {
                    Debug.LogWarning("[VRC Git Sync] 古いバージョンのファイルを消せませんでした。手で消してください:\n" + string.Join("\n", failed));
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
