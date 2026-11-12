using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace VrcGitSync
{
    /// <summary>送る・受け取るのメインウィンドウ。</summary>
    public class SyncWindow : EditorWindow
    {
        string _message = "";
        string _newRemoteUrl;
        bool _showUploads = true;
        bool _showSettings;
        bool _showLog;
        Vector2 _scroll;
        Vector2 _logScroll;

        [MenuItem("Tools/VRC Git Sync/同期ウィンドウ", priority = 0)]
        public static void Open()
        {
            var w = GetWindow<SyncWindow>(false, "VRC Git Sync", true);
            w.minSize = new Vector2(360, 300);
        }

        void OnEnable()
        {
            SyncOperations.Changed += Repaint;
            _ = SyncOperations.RefreshStatus(false);
        }

        void OnDisable()
        {
            SyncOperations.Changed -= Repaint;
        }

        void OnFocus()
        {
            if (!SyncOperations.Busy) _ = SyncOperations.RefreshStatus(false);
        }

        void OnGUI()
        {
            if (!SyncOperations.IsReady)
            {
                EditorGUILayout.HelpBox("このプロジェクトはまだ初期設定がされていません。", MessageType.Info);
                if (GUILayout.Button("初期設定を開く", GUILayout.Height(28))) SetupWindow.Open();
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            var settings = SyncSettings.Current;
            var status = SyncOperations.Status;

            // ---------------------------------------------------------------- 状態
            EditorGUILayout.LabelField("自分のフォルダ", settings.ownFolder);
            if (!status.Valid)
            {
                EditorGUILayout.LabelField("状態", "確認しています…");
            }
            else
            {
                EditorGUILayout.LabelField("まだ記録していない変更", status.ChangedFiles + " 個のファイル");
                if (status.VendorMetaChanges > 0)
                {
                    EditorGUILayout.LabelField(new GUIContent("配布物の ID と設定", "新しく入れた配布物の .meta や、インポート設定の変更。「送る」で記録されます。"),
                        new GUIContent(status.VendorMetaChanges + " 個（「送る」で記録）"));
                }
                if (status.MachineOnlyChanges > 0)
                {
                    EditorGUILayout.LabelField(new GUIContent("PC ごとの値だけの違い",
                            "シーンの環境光の色など、Unity が PC ごとに書き換える値だけが違うファイルです。同期しても意味がないので記録しません。"),
                        new GUIContent(status.MachineOnlyChanges + " 個（記録しません）"));
                }
                EditorGUILayout.LabelField("まだ送っていない記録", status.Ahead + " 件");
                string incoming;
                if (!status.HasRemote) incoming = "GitHub が未登録です";
                else if (!status.HasUpstream) incoming = "まだ一度も送っていません";
                else incoming = status.Behind + " 件" + (SyncOperations.LastFetch.HasValue ? "（" + SyncOperations.LastFetch.Value.ToString("HH:mm") + " に確認）" : "（未確認）");
                EditorGUILayout.LabelField("GitHub の新着", incoming);
            }

            using (new EditorGUI.DisabledScope(SyncOperations.Busy))
            {
                if (GUILayout.Button("新着を確認", GUILayout.Width(100))) _ = SyncOperations.RefreshStatus(true);
            }
            if (!string.IsNullOrEmpty(SyncOperations.FetchError)) EditorGUILayout.HelpBox(SyncOperations.FetchError, MessageType.Warning);

            if (status.Valid && status.PackageMismatches != null && status.PackageMismatches.Count > 0)
            {
                const int maxList = 6;
                var lines = status.PackageMismatches.Take(maxList)
                    .Select(m => "・" + m.Name + "（記録 " + m.Recorded + " / このPC " + (m.Installed ?? "入っていません") + "）");
                EditorGUILayout.HelpBox(
                    "このPCのパッケージが、GitHub の記録（もう一方のPC）と違います。VCC / ALCOM で同じバージョンにそろえてください。\n" +
                    "違うままだと、シーンなどに書き込まれる内容がPCごとに変わって、何もしていなくても変更が出ることがあります。\n" +
                    string.Join("\n", lines) +
                    (status.PackageMismatches.Count > maxList ? "\n…ほか " + (status.PackageMismatches.Count - maxList) + " 個" : ""),
                    MessageType.Warning);
            }

            if (status.Valid && status.GuidMismatches > 0)
            {
                EditorGUILayout.HelpBox(
                    "ID が GitHub（もう一方のPC）と違う配布物が " + status.GuidMismatches + " 個あります。\n" +
                    "zip で配られた素材を、それぞれのPCで別々に入れた場合などに起きます。このままだと、もう一方のPCで設定したテクスチャなどが見つかりません。",
                    MessageType.Warning);
                using (new EditorGUI.DisabledScope(SyncOperations.Busy))
                {
                    if (GUILayout.Button("GitHub に合わせる")) _ = RunAndReport(SyncOperations.AlignVendorMetas());
                }
            }

            EditorGUILayout.Space(8);

            // ---------------------------------------------------------------- 送る・受け取る
            _message = EditorGUILayout.TextField(new GUIContent("メモ（省略可）", "何を変えたかのメモ。空なら日時とPC名になります。"), _message);
            using (new EditorGUI.DisabledScope(SyncOperations.Busy))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("送る", "変更を記録して GitHub に送ります。"), GUILayout.Height(36)))
                    {
                        var m = _message;
                        _message = "";
                        GUI.FocusControl(null);
                        _ = RunAndReport(SyncOperations.Send(m));
                    }
                    if (GUILayout.Button(new GUIContent("受け取る", "もう一方のPCで送った変更を GitHub から受け取ります。"), GUILayout.Height(36)))
                    {
                        _ = RunAndReport(SyncOperations.Receive());
                    }
                }
            }

            if (SyncOperations.Busy)
            {
                EditorGUILayout.HelpBox(string.IsNullOrEmpty(SyncOperations.BusyText) ? "処理中…" : SyncOperations.BusyText, MessageType.Info);
            }
            else if (!string.IsNullOrEmpty(SyncOperations.LastError))
            {
                EditorGUILayout.HelpBox(SyncOperations.LastError, MessageType.Error);
            }
            else if (!string.IsNullOrEmpty(SyncOperations.LastInfo))
            {
                EditorGUILayout.HelpBox(SyncOperations.LastInfo, MessageType.Info);
            }

            EditorGUILayout.Space(8);

            // ---------------------------------------------------------------- 最近のアップロード
            _showUploads = EditorGUILayout.Foldout(_showUploads, "最近のアップロード", true);
            if (_showUploads)
            {
                if (SyncOperations.RecentUploads.Count == 0)
                {
                    EditorGUILayout.LabelField("まだありません。VRChat にアップロードすると、ここに記録されます。", EditorStyles.wordWrappedMiniLabel);
                }
                foreach (var u in SyncOperations.RecentUploads)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(u.Date, GUILayout.Width(110));
                        EditorGUILayout.LabelField(new GUIContent(u.Subject, u.Name));
                    }
                }
            }

            // ---------------------------------------------------------------- 設定
            _showSettings = EditorGUILayout.Foldout(_showSettings, "設定", true);
            if (_showSettings) DrawSettings(settings);

            // ---------------------------------------------------------------- ログ
            _showLog = EditorGUILayout.Foldout(_showLog, "ログ", true);
            if (_showLog)
            {
                _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(140));
                EditorGUILayout.TextArea(SyncOperations.Log, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.EndScrollView();
        }

        void DrawSettings(SyncSettings settings)
        {
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUI.BeginChangeCheck();
                settings.checkOnStartup = EditorGUILayout.ToggleLeft("Unity を開いたときに新着を確認する", settings.checkOnStartup);
                settings.checkOnQuit = EditorGUILayout.ToggleLeft("Unity を閉じるときに、送っていない変更があれば知らせる", settings.checkOnQuit);
                settings.commitOnUpload = EditorGUILayout.ToggleLeft("VRChat にアップロードしたら自動で記録する", settings.commitOnUpload);
                settings.warnOutsideSaves = EditorGUILayout.ToggleLeft("自分のフォルダの外で保存したら知らせる", settings.warnOutsideSaves);
                if (EditorGUI.EndChangeCheck()) settings.Save();

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("保存しても知らせないフォルダ", EditorStyles.miniBoldLabel);
                string remove = null;
                foreach (var f in settings.noWarnFolders)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(f);
                        if (GUILayout.Button("削除", GUILayout.Width(50))) remove = f;
                    }
                }
                if (remove != null)
                {
                    settings.noWarnFolders.Remove(remove);
                    settings.Save();
                }

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField(new GUIContent("自分のフォルダ以外に同期するフォルダ",
                    "ツールが決まった場所に書き出すもの（FaceEmo の Generated など）を、中身ごと同期します。"), EditorStyles.miniBoldLabel);
                string removeExtra = null;
                foreach (var f in settings.extraFolders)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(f);
                        using (new EditorGUI.DisabledScope(SyncOperations.Busy))
                        {
                            if (GUILayout.Button("外す", GUILayout.Width(50))) removeExtra = f;
                        }
                    }
                }
                using (new EditorGUI.DisabledScope(SyncOperations.Busy))
                {
                    if (GUILayout.Button("フォルダを追加…", GUILayout.Width(120)))
                    {
                        var picked = EditorUtility.OpenFolderPanel("同期するフォルダを選ぶ", Application.dataPath, "");
                        var assetPath = ToAssetPath(picked);
                        if (!string.IsNullOrEmpty(picked))
                        {
                            if (assetPath == null) EditorUtility.DisplayDialog(SyncOperations.Title, "このプロジェクトの Assets の中のフォルダを選んでください。", "OK");
                            else AddExtraFolderInteractive(assetPath);
                        }
                        GUIUtility.ExitGUI();
                    }
                }
                if (removeExtra != null)
                {
                    bool go = EditorUtility.DisplayDialog(SyncOperations.Title,
                        removeExtra + " を、同期するフォルダから外します。\n\n" +
                        "GitHub からこのフォルダのファイルを外すので、もう一方のPCでは、次に「受け取る」とこのフォルダのファイルが消えます（このPCのファイルは残ります）。\n外しますか？",
                        "外す", "やめる");
                    if (go) _ = RunAndReport(SyncOperations.RemoveExtraFolder(removeExtra));
                    GUIUtility.ExitGUI();
                }

                if (settings.vendorFolders.Count > 0)
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField(new GUIContent("配布物として扱うフォルダ",
                        "外からファイルを入れたときに「配布物として扱う」を選んだフォルダです。中身は同期せず、ID（.meta）とインポート設定だけを同期します。" +
                        "外すと、次にこのフォルダへ外からファイルを入れたときに、また聞きます。"), EditorStyles.miniBoldLabel);
                    string removeVendor = null;
                    foreach (var f in settings.vendorFolders)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField(f);
                            if (GUILayout.Button("外す", GUILayout.Width(50))) removeVendor = f;
                        }
                    }
                    if (removeVendor != null)
                    {
                        settings.vendorFolders.Remove(removeVendor);
                        settings.Save();
                    }
                }

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("GitHub のリポジトリ", EditorStyles.miniBoldLabel);
                if (_newRemoteUrl == null) _newRemoteUrl = Git.RemoteUrl();
                using (new EditorGUILayout.HorizontalScope())
                {
                    _newRemoteUrl = EditorGUILayout.TextField(_newRemoteUrl);
                    using (new EditorGUI.DisabledScope(SyncOperations.Busy || string.IsNullOrWhiteSpace(_newRemoteUrl)))
                    {
                        if (GUILayout.Button("登録", GUILayout.Width(50)))
                        {
                            var r = Git.SetRemote(_newRemoteUrl.Trim());
                            EditorUtility.DisplayDialog(SyncOperations.Title,
                                r.Ok ? "URLを登録しました。「送る」で GitHub に送れます。" : "URLを登録できませんでした。\n" + r.Message,
                                "OK");
                            _newRemoteUrl = null;
                            _ = SyncOperations.RefreshStatus(false);
                        }
                    }
                }
            }
        }

        /// <summary>選んだフォルダを、中身ごと同期するフォルダに追加する（確認つき）。右クリックメニューからも使う。</summary>
        /// <summary>確認してから、同期するフォルダに追加する。追加したら true。</summary>
        public static bool AddExtraFolderInteractive(string folder)
        {
            var settings = SyncSettings.Current;
            var reason = settings.ValidateExtraFolder(folder);
            if (reason != null)
            {
                EditorUtility.DisplayDialog(SyncOperations.Title, reason, "OK");
                return false;
            }
            long size = FolderSize(ProjectPaths.ToFull(folder));
            bool go = EditorUtility.DisplayDialog(SyncOperations.Title,
                folder + " を、中身ごと同期するフォルダに追加します（今の大きさ: " + SyncOperations.FormatSize(size) + "）。\n\n" +
                "ツールが決まった場所に書き出すフォルダ（FaceEmo の Generated など）や、自分で作ったファイルを置いたフォルダに使ってください。\n" +
                "Booth の素体や衣装などの配布物のフォルダは追加しないでください（有料アセットが GitHub に上がってしまいます）。\n" +
                (size > 200L * 1024 * 1024 ? "\n※大きいフォルダです。配布物が入っていないか確認してください。\n" : "") +
                "\n次に「送る」を押したときに記録されます。",
                "追加する", "やめる");
            if (!go) return false;
            settings.AddExtraFolder(folder);
            RepoFiles.WriteAll(settings.ownFolder, settings.extraFolders);
            _ = SyncOperations.RefreshStatus(false);
            return true;
        }

        static long FolderSize(string fullPath)
        {
            try
            {
                return new System.IO.DirectoryInfo(fullPath)
                    .EnumerateFiles("*", System.IO.SearchOption.AllDirectories)
                    .Sum(f => f.Length);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>フォルダ選択で選んだ絶対パスを "Assets/..." にする。プロジェクトの Assets の外なら null。</summary>
        static string ToAssetPath(string absolute)
        {
            if (string.IsNullOrEmpty(absolute)) return null;
            var full = ProjectPaths.Normalize(absolute);
            var assets = ProjectPaths.Normalize(Application.dataPath);
            if (string.Equals(full, assets, StringComparison.OrdinalIgnoreCase)) return null;
            if (!full.StartsWith(assets + "/", StringComparison.OrdinalIgnoreCase)) return null;
            return "Assets" + full.Substring(assets.Length);
        }

        static async Task RunAndReport(Task<bool> task)
        {
            try
            {
                await task;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
