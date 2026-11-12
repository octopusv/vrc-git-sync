using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace VrcGitSync
{
    /// <summary>Project ウィンドウの右クリックメニュー。</summary>
    static class ContextMenus
    {
        const string CopyMenu = "Assets/VRC Git Sync/自分のフォルダにコピーして使う";
        const string AddFolderMenu = "Assets/VRC Git Sync/このフォルダも同期する";

        [MenuItem(CopyMenu, false, 2000)]
        static void CopySelected()
        {
            var paths = SelectedOutsideSyncedFolders();
            if (paths.Count == 0) return;

            const int maxList = 15;
            var list = string.Join("\n", paths.Take(maxList).Select(p => "・" + p));
            if (paths.Count > maxList) list += "\n…ほか " + (paths.Count - maxList) + " 個";
            bool go = EditorUtility.DisplayDialog(SyncOperations.Title,
                "次のものを自分のフォルダ（" + SyncSettings.Current.ownFolder + "）にコピーして、シーンなどの参照をコピーに切り替えます。\n\n" +
                list + "\n\n" +
                "元のファイルはそのまま残ります。開いているシーンは、保存してから開き直します。",
                "コピーして使う", "やめる");
            if (!go) return;

            var result = AssetRelocator.CopyIntoOwnFolder(paths);
            EditorUtility.DisplayDialog(SyncOperations.Title, result.Message, "OK");
            if (result.Success && result.Copies.Count > 0)
            {
                var copy = AssetDatabase.LoadMainAssetAtPath(result.Copies[0].Value);
                if (copy != null)
                {
                    Selection.activeObject = copy;
                    EditorGUIUtility.PingObject(copy);
                }
            }
        }

        [MenuItem(CopyMenu, true)]
        static bool CanCopySelected()
        {
            return SyncOperations.IsReady && SelectedOutsideSyncedFolders().Count > 0;
        }

        /// <summary>ツールが決まった場所に書き出すフォルダ（FaceEmo の Generated など）を、中身ごと同期する。</summary>
        [MenuItem(AddFolderMenu, false, 2001)]
        static void AddSelectedFolder()
        {
            var folder = SelectedFolder();
            if (folder != null) SyncWindow.AddExtraFolderInteractive(folder);
        }

        [MenuItem(AddFolderMenu, true)]
        static bool CanAddSelectedFolder()
        {
            if (!SyncOperations.IsReady) return false;
            var folder = SelectedFolder();
            return folder != null && SyncSettings.Current.ValidateExtraFolder(folder) == null;
        }

        static string SelectedFolder()
        {
            if (Selection.assetGUIDs.Length != 1) return null;
            var path = AssetDatabase.GUIDToAssetPath(Selection.assetGUIDs[0]);
            return AssetDatabase.IsValidFolder(path) ? path : null;
        }

        static List<string> SelectedOutsideSyncedFolders()
        {
            var settings = SyncSettings.Current;
            return Selection.assetGUIDs
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.StartsWith("Assets/") && !settings.IsSynced(p))
                .Distinct()
                .ToList();
        }
    }
}
