using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace VrcGitSync
{
    /// <summary>
    /// Unity を開いたときに新着を確認し、閉じるときに送っていない変更があれば送信を促す。
    /// </summary>
    [InitializeOnLoad]
    static class StartupHooks
    {
        const string StartupDoneKey = "VrcGitSync.StartupDone";

        static StartupHooks()
        {
            if (Application.isBatchMode) return;
            EditorApplication.wantsToQuit -= OnWantsToQuit;
            EditorApplication.wantsToQuit += OnWantsToQuit;

            // スクリプトの再コンパイルごとではなく、Unity を開いたときに1回だけ
            if (!SessionState.GetBool(StartupDoneKey, false))
            {
                SessionState.SetBool(StartupDoneKey, true);
                EditorApplication.delayCall += () => _ = OnStartup();
            }
        }

        static async Task OnStartup()
        {
            try
            {
                if (!SyncOperations.IsReady) return;

                // clone した直後などは LFS のフックがないことがあるので毎回入れておく（何度実行しても安全）。
                // 古いバージョンで作った .gitignore なども、今のバージョンの内容にしておく。
                string own = SyncSettings.Current.ownFolder;
                var extras = SyncSettings.Current.extraFolders.ToArray();
                await Task.Run(() =>
                {
                    Git.LfsInstallLocal();
                    RepoFiles.WriteAll(own, extras);
                });

                var settings = SyncSettings.Current;
                await SyncOperations.RefreshStatus(settings.checkOnStartup);
                if (!settings.checkOnStartup) return;

                var status = SyncOperations.Status;
                if (status.Behind <= 0) return;
                if (SyncOperations.Busy || EditorApplication.isPlayingOrWillChangePlaymode) return;

                bool receive = EditorUtility.DisplayDialog(SyncOperations.Title,
                    "GitHubに、もう一方のPCで送った新しい変更があります（" + status.Behind + " 件）。\n\n" +
                    "作業を始める前に受け取っておくと、変更がぶつかりにくくなります。今受け取りますか？",
                    "受け取る", "あとで");
                if (receive)
                {
                    var ok = await SyncOperations.Receive();
                    ShowResult(ok);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        static bool OnWantsToQuit()
        {
            try
            {
                if (!SyncOperations.IsReady) return true;
                if (SyncOperations.Busy)
                {
                    return EditorUtility.DisplayDialog(SyncOperations.Title,
                        "GitHub とのやり取りの途中です（" + SyncOperations.BusyText + "）。\n今閉じると途中で止まります。それでも閉じますか？",
                        "閉じる", "閉じない");
                }
                if (!SyncSettings.Current.checkOnQuit) return true;
                if (string.IsNullOrEmpty(Git.RemoteUrl())) return true;

                // シーンだけでなく、保存していないマテリアルなども「送っていない変更」として扱う
                bool dirty = SyncOperations.AnyDirtyScene() || SyncOperations.AnyDirtyAsset();
                var status = Git.ReadStatus(SyncSettings.Current.SyncedFolders);
                if (!dirty && status.ChangedFiles == 0 && status.VendorMetaChanges == 0 && status.Ahead == 0) return true;

                int choice = EditorUtility.DisplayDialogComplex(SyncOperations.Title,
                    "GitHubに送っていない変更があります。\nもう一方のPCで続きをするなら、送ってから閉じてください。",
                    "送ってから閉じる", "キャンセル", "送らずに閉じる");
                if (choice == 1) return false;
                if (choice == 2) return true;

                bool ok = SyncOperations.SendBlocking(null);
                if (ok) return true;
                if (SyncOperations.WasCancelled) return false;
                return EditorUtility.DisplayDialog(SyncOperations.Title,
                    "GitHubに送れませんでした。\n\n" + SyncOperations.LastError + "\n\nそれでも閉じますか？",
                    "閉じる", "閉じない");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return true;
            }
        }

        /// <summary>結果をダイアログで知らせる（ウィンドウを開いていないときのため）。</summary>
        internal static void ShowResult(bool ok)
        {
            if (ok)
            {
                if (!string.IsNullOrEmpty(SyncOperations.LastInfo))
                {
                    EditorUtility.DisplayDialog(SyncOperations.Title, SyncOperations.LastInfo, "OK");
                }
            }
            else if (!string.IsNullOrEmpty(SyncOperations.LastError))
            {
                EditorUtility.DisplayDialog(SyncOperations.Title, SyncOperations.LastError, "OK");
            }
        }
    }
}
