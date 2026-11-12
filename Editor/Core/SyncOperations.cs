using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VrcGitSync
{
    /// <summary>
    /// 送る・受け取る・初期設定などの一連の処理。
    /// git はバックグラウンドで動かし、Unity API（保存・Refresh・ダイアログ）はメインスレッドで呼ぶ。
    /// </summary>
    public static class SyncOperations
    {
        public const string Title = "VRC Git Sync";

        public static bool Busy { get; private set; }
        public static string BusyText { get; private set; } = "";
        public static string LastInfo { get; private set; } = "";
        public static string LastError { get; private set; } = "";
        public static string FetchError { get; private set; } = "";
        public static bool WasCancelled { get; private set; }
        public static SyncStatus Status { get; private set; }
        public static DateTime? LastFetch { get; private set; }
        public static List<UploadTag> RecentUploads { get; private set; } = new List<UploadTag>();

        static readonly StringBuilder LogBuilder = new StringBuilder();
        public static string Log => LogBuilder.ToString();

        /// <summary>状態が変わったとき（メインスレッド）。</summary>
        public static event Action Changed;

        static bool _blocking;
        static bool _fetching;
        static int _progressId = -1;

        public static bool IsReady => SyncSettings.Current.IsConfigured && Git.IsRepo;

        // ================================================================ 公開する操作

        public static Task<bool> Send(string message) => SendCore(message, false);

        /// <summary>Unity の終了時などに使う。終わるまで戻らない。</summary>
        public static bool SendBlocking(string message)
        {
            var t = SendCore(message, true);
            return t.IsCompleted && !t.IsFaulted && t.Result;
        }

        public static Task<bool> Receive() => ReceiveCore();

        /// <summary>状態を読み直す。fetch = true なら GitHub の新着も確認する。</summary>
        public static async Task RefreshStatus(bool fetch)
        {
            if (!IsReady)
            {
                Status = default;
                Notify();
                return;
            }
            if (fetch && (Busy || _fetching)) fetch = false;
            if (fetch) _fetching = true;
            var synced = SyncSettings.Current.SyncedFolders;
            try
            {
                var result = await Task.Run(() =>
                {
                    GitResult? fetchResult = null;
                    if (fetch && !string.IsNullOrEmpty(Git.RemoteUrl())) fetchResult = Git.Fetch();
                    return (status: Git.ReadStatus(synced), uploads: Git.RecentUploads(10), fetch: fetchResult);
                });
                Status = result.status;
                RecentUploads = result.uploads;
                if (result.fetch.HasValue)
                {
                    if (result.fetch.Value.Ok)
                    {
                        LastFetch = DateTime.Now;
                        FetchError = "";
                    }
                    else
                    {
                        FetchError = "GitHubに接続できませんでした。\n" + result.fetch.Value.Message;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                if (fetch) _fetching = false;
                Notify();
            }
        }

        /// <summary>アバターのアップロード成功時に呼ばれる。変更を記録してタグを付ける（送信はしない）。</summary>
        public static async Task CommitUpload(string avatarName, string avatarId)
        {
            if (!IsReady || !SyncSettings.Current.commitOnUpload) return;
            if (Busy)
            {
                Debug.LogWarning("[VRC Git Sync] 別の処理中だったため、アップロードの記録をスキップしました。");
                return;
            }
            if (!BeginOp(false)) return;
            bool ok = false;
            try
            {
                List<SaveWatcher.Captured> captured;
                using (SaveWatcher.Capture(out captured))
                {
                    SaveOpenScenesSilently();
                    AssetDatabase.SaveAssets();
                }
                // 自分のフォルダの外に保存されたものがあれば、記録が終わってからウィンドウで知らせる
                var outside = SaveWatcher.ChangedOnly(captured);
                if (outside.Count > 0) EditorApplication.delayCall += () => OutsideSaveWindow.ShowCaptured(outside);
                ShowProgress("アップロードを記録しています…");

                string msg = "アップロード: " + avatarName + "\n\nアバターID: " + avatarId + "\nPC: " + Environment.MachineName;
                string tag = "upload/" + Git.SanitizeRefPart(avatarName) + "/" + DateTime.Now.ToString("yyyyMMdd-HHmmss");

                if (!await CommitStep(msg)) return;
                var t = await Bg(() => Git.HasCommits() ? Git.Tag(tag, msg) : GitResult.Fail("記録がまだ1つもありません。"));
                if (!t.Ok)
                {
                    Fail("アップロードのタグを付けられませんでした。", t);
                    return;
                }
                Info("アップロードを記録しました（" + tag + "）。GitHubには次に「送る」を押したときに送られます。");
                Debug.Log("[VRC Git Sync] " + LastInfo);
                ok = true;
            }
            catch (Exception e)
            {
                HandleException(e);
            }
            finally
            {
                EndOp(ok);
            }
        }

        // ================================================================ 初期設定

        /// <summary>1台目のPC: 自分のフォルダを決めて、git の準備をして、最初の記録と送信をする。</summary>
        public static async Task<bool> SetupFirstPc(string ownFolderName, IList<string> scenesToMove, string remoteUrl)
        {
            if (!BeginOp(false)) return false;
            bool ok = false;
            try
            {
                if (!PrepareUnity()) return false;
                string own = "Assets/" + ownFolderName;

                // 自分のフォルダとシーンの移動
                ProjectPaths.EnsureAssetFolder(own);
                foreach (var scene in scenesToMove)
                {
                    var dest = AssetDatabase.GenerateUniqueAssetPath(own + "/" + Path.GetFileName(scene));
                    var err = AssetDatabase.MoveAsset(scene, dest);
                    if (string.IsNullOrEmpty(err)) AppendLog("シーンを移動しました: " + scene + " → " + dest);
                    else AppendLog("シーンを移動できませんでした: " + scene + "（" + err + "）");
                }
                AssetDatabase.SaveAssets();

                // 設定と .gitignore / .gitattributes
                var settings = SyncSettings.Current;
                settings.ownFolder = own;
                settings.Save();
                RepoFiles.WriteAll(own);

                // git の準備
                ShowProgress("git を準備しています…");
                var init = await Bg(() =>
                {
                    if (!Git.IsRepo)
                    {
                        var r = Git.Init();
                        if (!r.Ok) return r;
                    }
                    return Git.LfsInstallLocal();
                });
                if (!init.Ok)
                {
                    Fail("git の準備ができませんでした。", init);
                    return false;
                }

                // 記録される内容を確認してもらう
                ShowProgress("記録するファイルを数えています…");
                var files = await Bg(Git.DryRunAdd);
                HideProgress();
                if (!SyncDialogs.Confirm(BuildFileSummary(files, own), "記録する", "やめる"))
                {
                    WasCancelled = true;
                    Info("初期設定を途中で止めました。設定を見直して、もう一度実行できます。");
                    return false;
                }

                ShowProgress("最初の記録をしています…");
                if (!await CommitStep("VRC Git Sync: 初期設定", true)) return false;

                if (!string.IsNullOrWhiteSpace(remoteUrl))
                {
                    ShowProgress("GitHubに接続しています…");
                    var url = remoteUrl.Trim();
                    var set = await Bg(() => Git.SetRemote(url));
                    if (!set.Ok)
                    {
                        Fail("GitHubのURLを登録できませんでした。", set);
                        return false;
                    }
                    GitResult lsResult = default;
                    var hasData = await Bg(() => Git.RemoteHasData(out lsResult));
                    if (hasData == null)
                    {
                        Fail("GitHubに接続できませんでした。URLとログイン状態を確認してください。\n（初期設定はできているので、あとで同期ウィンドウの「送る」で送れます）", lsResult);
                        return false;
                    }
                    if (hasData == true)
                    {
                        LastError = "このGitHubリポジトリにはすでにデータがあります。\n" +
                                    "2台目のPCなら、初期設定の「GitHubから取り込む」を使ってください。\n" +
                                    "1台目のPCなら、空のリポジトリ（READMEなし）を作り直して、同期ウィンドウの設定でURLを変更してください。";
                        return false;
                    }
                    ShowProgress("GitHubに送っています…（初回は時間がかかります）");
                    var push = await Bg(Git.Push);
                    if (!push.Ok)
                    {
                        Fail("GitHubに送れませんでした。（初期設定はできているので、あとで同期ウィンドウの「送る」で送れます）", push);
                        return false;
                    }
                }

                Info(string.IsNullOrWhiteSpace(remoteUrl)
                    ? "初期設定が完了しました。GitHubのURLは同期ウィンドウの設定から登録できます。"
                    : "初期設定が完了し、GitHubに送りました。");
                ok = true;
                return true;
            }
            catch (Exception e)
            {
                HandleException(e);
                return false;
            }
            finally
            {
                EndOp(ok);
            }
        }

        /// <summary>2台目以降のPC: GitHub の内容を、このプロジェクトに上書きで取り込む。</summary>
        public static async Task<bool> SetupFromRemote(string remoteUrl)
        {
            if (!BeginOp(false)) return false;
            bool ok = false;
            bool refresh = false;
            try
            {
                if (!PrepareUnity()) return false;
                if (string.IsNullOrWhiteSpace(remoteUrl))
                {
                    LastError = "GitHubのリポジトリのURLを入力してください。";
                    return false;
                }
                if (Git.IsRepo && Git.HasCommits())
                {
                    LastError = "このプロジェクトはすでに git で管理されています。\n取り込みは、まだ git を使っていないプロジェクトでだけ行えます。";
                    return false;
                }

                ShowProgress("GitHubに接続しています…");
                var url = remoteUrl.Trim();
                var prep = await Bg(() =>
                {
                    var o = new RemoteProbe();
                    if (!Git.IsRepo)
                    {
                        var r = Git.Init();
                        if (!r.Ok) { o.Error = r; o.ErrorTitle = "git の準備ができませんでした。"; return o; }
                    }
                    Git.LfsInstallLocal();
                    var set = Git.SetRemote(url);
                    if (!set.Ok) { o.Error = set; o.ErrorTitle = "GitHubのURLを登録できませんでした。"; return o; }
                    var f = Git.Fetch();
                    if (!f.Ok) { o.Error = f; o.ErrorTitle = "GitHubに接続できませんでした。URLとログイン状態を確認してください。"; return o; }
                    o.Branch = Git.RemoteDefaultBranch();
                    if (o.Branch != null) o.SettingsJson = Git.ShowFile(Git.Remote + "/" + o.Branch, SyncSettings.RelativePath);
                    return o;
                });
                if (prep.Error.HasValue)
                {
                    Fail(prep.ErrorTitle, prep.Error.Value);
                    return false;
                }
                if (prep.Branch == null)
                {
                    LastError = "GitHubのリポジトリが空です。先に1台目のPCで初期設定をしてください。";
                    return false;
                }
                var remoteSettings = prep.SettingsJson == null ? null : SyncSettings.FromJson(prep.SettingsJson);
                if (remoteSettings == null || !remoteSettings.IsConfigured)
                {
                    LastError = "VRC Git Sync で作られたリポジトリではないようです（" + SyncSettings.RelativePath + " がありません）。";
                    return false;
                }

                string own = remoteSettings.ownFolder;
                HideProgress();
                bool go = SyncDialogs.Confirm(
                    "GitHubの内容をこのプロジェクトに取り込みます。\n\n" +
                    "次のものはGitHubの内容に置き換わります:\n" +
                    "・" + own + "\n" +
                    "・ProjectSettings\n" +
                    "・Packages の一覧ファイル（manifest.json など）\n\n" +
                    "今の内容は、プロジェクトの「" + RepoFiles.BackupFolderName + "」フォルダにバックアップします。\n" +
                    "開いているシーンは閉じます（取り込んだあとで開き直してください）。\n\n" +
                    "Boothの素体や衣装などの配布アセットは同期されないので、もう一台と同じものをこのPCでもインポートしておいてください。",
                    "取り込む", "やめる");
                if (!go)
                {
                    WasCancelled = true;
                    Info("取り込みをやめました。");
                    return false;
                }

                // 置き換わるファイルを開いたままにしないよう、空のシーンにしておく
                EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

                // もう一方のPCで自分のフォルダに移したファイル（シーンなど）が、このPCでは移動前の場所に残っていると
                // GUID が重複してしまう。GitHub側の .meta の GUID を調べて、そういうファイルもバックアップへ移す。
                ShowProgress("今の内容をバックアップしています…");
                string remoteRef = Git.Remote + "/" + prep.Branch;
                var remoteGuids = await Bg(() => Git.MetaGuids(remoteRef, own));
                var duplicates = new List<string>();
                foreach (var kv in remoteGuids)
                {
                    var local = AssetDatabase.GUIDToAssetPath(kv.Value);
                    if (string.IsNullOrEmpty(local) || ProjectPaths.IsUnder(local, own)) continue;
                    var full = ProjectPaths.ToFull(local);
                    if (File.Exists(full) || Directory.Exists(full)) duplicates.Add(local);
                }

                string backup;
                AssetDatabase.DisallowAutoRefresh();
                try
                {
                    refresh = true;
                    backup = await Bg(() => BackupForImport(own, duplicates));
                    AppendLog("バックアップ: " + backup);
                    if (duplicates.Count > 0)
                    {
                        AppendLog("移動前の場所に残っていた同じファイルもバックアップに移しました: " + string.Join(", ", duplicates));
                    }

                    ShowProgress("GitHubの内容を取り込んでいます…（初回は時間がかかります）");
                    var co = await Bg(() => Git.CheckoutRemoteBranch(prep.Branch));
                    if (!co.Ok)
                    {
                        Fail("GitHubの内容を取り込めませんでした。（バックアップ: " + backup + "）", co);
                        return false;
                    }
                }
                finally
                {
                    AssetDatabase.AllowAutoRefresh();
                    if (refresh) AfterWorkingTreeChanged();
                }

                Info("取り込みが完了しました。" + own + " のシーンを開いてください。\n" +
                     "パッケージの一覧も更新されたので、Unityを一度再起動しておくと確実です。\nバックアップ: " + backup);
                ok = true;
                return true;
            }
            catch (Exception e)
            {
                HandleException(e);
                return false;
            }
            finally
            {
                EndOp(ok);
            }
        }

        class RemoteProbe
        {
            public GitResult? Error;
            public string ErrorTitle;
            public string Branch;
            public string SettingsJson;
        }

        // ================================================================ 送る・受け取る

        static async Task<bool> SendCore(string message, bool blocking)
        {
            if (!BeginOp(blocking)) return false;
            bool ok = false;
            try
            {
                if (!PrepareUnity()) return false;
                ShowProgress("GitHubに送っています…");
                bool refresh = false;
                AssetDatabase.DisallowAutoRefresh();
                try
                {
                    string msg = string.IsNullOrWhiteSpace(message) ? DefaultMessage("の変更") : message.Trim();
                    if (!await CommitStep(msg)) return false;
                    if (!await CheckRemote()) return false;

                    var merge = await MergeFromRemote();
                    if (merge == MergeResult.Failed) return false;
                    if (merge == MergeResult.Merged) refresh = true;

                    // 配布物の ID と設定（.meta）は、GitHub の変更を取り込んでから記録する
                    if (!await CommitVendorMetas()) return false;

                    ShowProgress("GitHubに送っています…");
                    var push = await Bg(Git.Push);
                    if (!push.Ok && Git.IsRejectedPush(push))
                    {
                        // 直前にもう一方のPCが送っていた場合は、取り込んでから送り直す
                        merge = await MergeFromRemote();
                        if (merge == MergeResult.Failed) return false;
                        if (merge == MergeResult.Merged) refresh = true;
                        push = await Bg(Git.Push);
                    }
                    if (!push.Ok)
                    {
                        Fail("GitHubに送れませんでした。", push);
                        return false;
                    }
                    Info((refresh ? "もう一方のPCの変更を受け取ってから、GitHubに送りました。" : "GitHubに送りました。") +
                         (_vendorMetasPostponed ? "\n配布物の ID は、まだ記録していません（もう一方のPCで先に送ってから、このPCで受け取ってください）。" : ""));
                    ok = true;
                    return true;
                }
                finally
                {
                    AssetDatabase.AllowAutoRefresh();
                    if (refresh) AfterWorkingTreeChanged();
                }
            }
            catch (Exception e)
            {
                HandleException(e);
                return false;
            }
            finally
            {
                EndOp(ok);
            }
        }

        static bool _vendorMetasPostponed;
        static readonly Regex GuidReference = new Regex(@"guid: ([0-9a-f]{32})", RegexOptions.Compiled);

        /// <summary>
        /// 配布物の .meta（新しく入れた配布物の ID、インポート設定の変更）を記録する。
        /// 初めて記録するときに、自分のフォルダにこのPCで見つからない参照があれば、どちらのPCの ID でそろえるか確認する。
        /// </summary>
        static async Task<bool> CommitVendorMetas()
        {
            _vendorMetasPostponed = false;
            var synced = SyncSettings.Current.SyncedFolders;
            ShowProgress("配布物の ID を確認しています…");
            var status = await Bg(() => Git.ReadStatus(synced));
            if (status.VendorMetaChanges == 0) return true;

            bool firstTime = !await Bg(() => MetaIndex.HeadHasVendorMetas(synced));
            if (firstTime)
            {
                var missing = MissingReferencesInOwnFolder(synced);
                if (missing.Count > 0)
                {
                    HideProgress();
                    var examples = string.Join("\n", missing.Take(5).Select(kv => "・" + kv.Key + "（" + kv.Value + " 個）"));
                    bool go = SyncDialogs.Confirm(
                        "配布物の ID（.meta）を、初めて GitHub に記録します。\n\n" +
                        "自分のフォルダの中に、このPCでは見つからない参照があります（もう一方のPCで設定したテクスチャなど）:\n" +
                        examples + "\n\n" +
                        "先に記録したPCの ID に、もう一方のPCがそろいます。その参照を作ったのがもう一方のPCなら、「あとで」を選んで、もう一方のPCで先に「送る」をしてください。\n" +
                        "このPCの ID で記録しますか？",
                        "このPCの ID で記録する", "あとで");
                    if (!go)
                    {
                        _vendorMetasPostponed = true;
                        AppendLog("配布物の ID は記録しませんでした（もう一方のPCで先に送るのを待ちます）。");
                        return true;
                    }
                }
            }
            ShowProgress("配布物の ID と設定を記録しています…");
            return await CommitStep("配布物の ID と設定を記録（" + Environment.MachineName + "）", true);
        }

        /// <summary>自分のフォルダのファイルにある参照のうち、このPCで見つからないもの（ファイルごとの個数）。</summary>
        static Dictionary<string, int> MissingReferencesInOwnFolder(string[] synced)
        {
            var result = new Dictionary<string, int>();
            var files = synced
                .Select(ProjectPaths.ToFull)
                .Where(Directory.Exists)
                .SelectMany(dir => Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
            foreach (var file in files)
            {
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) || !IsUnityYaml(file)) continue;
                var missing = new HashSet<string>();
                foreach (Match m in GuidReference.Matches(File.ReadAllText(file, Encoding.UTF8)))
                {
                    var guid = m.Groups[1].Value;
                    if (guid.StartsWith("0000000000000000", StringComparison.Ordinal)) continue; // Unity の組み込みリソース
                    if (string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid))) missing.Add(guid);
                }
                if (missing.Count > 0) result[ProjectPaths.Normalize(file).Substring(ProjectPaths.Root.Length + 1)] = missing.Count;
            }
            return result;
        }

        static bool IsUnityYaml(string file)
        {
            try
            {
                using (var stream = File.OpenRead(file))
                {
                    var head = new byte[5];
                    return stream.Read(head, 0, 5) == 5 && Encoding.ASCII.GetString(head) == "%YAML";
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>追加で同期していたフォルダを外し、GitHub からそのフォルダのファイルを外す記録をする（このPCのファイルは残る）。</summary>
        public static async Task<bool> RemoveExtraFolder(string folder)
        {
            if (!BeginOp(false)) return false;
            bool ok = false;
            try
            {
                if (!PrepareUnity()) return false;
                var settings = SyncSettings.Current;
                settings.RemoveExtraFolder(folder);
                RepoFiles.WriteAll(settings.ownFolder, settings.extraFolders);
                ShowProgress("同期するフォルダから外しています…");
                var r = await Bg(() => Git.UntrackFolder(folder));
                if (!r.Ok)
                {
                    Fail("同期するフォルダから外せませんでした。", r);
                    return false;
                }
                if (!await CommitStep("同期するフォルダから外す: " + folder)) return false;
                Info(folder + " を同期するフォルダから外しました。次に「送る」を押したときに GitHub に反映されます。");
                ok = true;
                return true;
            }
            catch (Exception e)
            {
                HandleException(e);
                return false;
            }
            finally
            {
                EndOp(ok);
            }
        }

        /// <summary>手元の ID が GitHub と違う配布物の .meta を、GitHub の内容に戻す。</summary>
        public static async Task<bool> AlignVendorMetas()
        {
            if (!BeginOp(false)) return false;
            bool ok = false;
            try
            {
                if (!PrepareUnity()) return false;
                var synced = SyncSettings.Current.SyncedFolders;
                ShowProgress("配布物の ID を確認しています…");
                var paths = await Bg(() => Git.GuidMismatchedVendorMetas(synced));
                HideProgress();
                if (paths.Count == 0)
                {
                    Info("GitHub と ID が違う配布物はありませんでした。");
                    ok = true;
                    return true;
                }
                bool go = SyncDialogs.Confirm(
                    paths.Count + " 個の配布物の ID を、GitHub（もう一方のPC）に合わせます。\n\n" +
                    string.Join("\n", paths.Take(8).Select(p => "・" + p.Substring(0, p.Length - 5))) +
                    (paths.Count > 8 ? "\n…ほか " + (paths.Count - 8) + " 個" : "") + "\n\n" +
                    "自分のフォルダの参照は、もう一方のPCと同じようにつながります。配布物のフォルダの中で、このPCの ID を使っている参照があれば切れます。",
                    "GitHub に合わせる", "やめる");
                if (!go)
                {
                    WasCancelled = true;
                    return false;
                }
                ShowProgress("配布物の ID をそろえています…");
                AssetDatabase.DisallowAutoRefresh();
                GitResult r;
                try
                {
                    r = await Bg(() => Git.CheckoutFromHead(paths));
                }
                finally
                {
                    AssetDatabase.AllowAutoRefresh();
                    AssetDatabase.Refresh();
                }
                if (!r.Ok)
                {
                    Fail("配布物の ID をそろえられませんでした。", r);
                    return false;
                }
                Info(paths.Count + " 個の配布物の ID を GitHub に合わせました。");
                ok = true;
                return true;
            }
            catch (Exception e)
            {
                HandleException(e);
                return false;
            }
            finally
            {
                EndOp(ok);
            }
        }

        static async Task<bool> ReceiveCore()
        {
            if (!BeginOp(false)) return false;
            bool ok = false;
            try
            {
                if (!PrepareUnity()) return false;
                ShowProgress("GitHubから受け取っています…");
                bool refresh = false;
                AssetDatabase.DisallowAutoRefresh();
                try
                {
                    if (!await CheckRemote()) return false;
                    // このPCの未記録の変更は、受け取る前に記録しておく（消えないように）
                    if (!await CommitStep(DefaultMessage("の変更（受け取る前に自動で記録）"))) return false;

                    var merge = await MergeFromRemote();
                    if (merge == MergeResult.Failed) return false;
                    refresh = merge == MergeResult.Merged;
                    var info = refresh ? "GitHubの変更を受け取りました。" : "新しい変更はありませんでした。";
                    var packages = await Bg(() => PackageCheck.Find(ProjectPaths.Root));
                    if (packages.Count > 0)
                    {
                        info += "\nこのPCのパッケージが GitHub の記録と違います（" + packages.Count + " 個）。同期ウィンドウを見て、VCC / ALCOM でそろえてください。";
                    }
                    Info(info);
                    ok = true;
                    return true;
                }
                finally
                {
                    AssetDatabase.AllowAutoRefresh();
                    if (refresh) AfterWorkingTreeChanged();
                }
            }
            catch (Exception e)
            {
                HandleException(e);
                return false;
            }
            finally
            {
                EndOp(ok);
            }
        }

        enum MergeResult
        {
            UpToDate,
            Merged,
            Failed,
        }

        class MergeOutcome
        {
            public bool Merged;
            public List<string> Conflicts;
            public GitResult? Error;
            public string ErrorTitle;
            public int ReplacedVendorMetas;
            public List<string> RestoredMachineOnly;
        }

        class CommitOutcome
        {
            public GitResult Result;
            public bool Committed;
            public List<string> MovedToLfs;
            public int VendorMetas;
        }

        /// <summary>
        /// 変更を記録する。includeVendorMetas が false なら、自分のフォルダと Assets の外だけを記録する
        /// （配布物の .meta は、GitHub の変更を取り込んだあとで記録する。先に記録すると、もう一方のPCと同じ場所でぶつかるため）。
        /// </summary>
        static async Task<bool> CommitStep(string message, bool includeVendorMetas = false)
        {
            var settings = SyncSettings.Current;
            string own = settings.ownFolder;
            var extras = settings.extraFolders.ToArray();
            var synced = settings.SyncedFolders;
            var o = await Bg(() =>
            {
                var c = new CommitOutcome();
                // 古いバージョンで作った .gitignore などを、今のバージョンの内容にしておく
                RepoFiles.WriteAll(own, extras);
                c.Result = Git.CommitAll(message, synced, includeVendorMetas, out c.Committed, out c.MovedToLfs, out c.VendorMetas);
                return c;
            });
            if (!o.Result.Ok)
            {
                Fail("変更を記録できませんでした。", o.Result);
                return false;
            }
            if (o.Committed) AppendLog("記録しました: " + FirstLine(message) + (o.VendorMetas > 0 ? "（配布物の ID と設定 " + o.VendorMetas + " 個を含む）" : ""));
            if (o.MovedToLfs != null && o.MovedToLfs.Count > 0)
            {
                AppendLog("大きいファイルを Git LFS の対象にしました: " + string.Join(", ", o.MovedToLfs));
            }
            return true;
        }

        static async Task<bool> CheckRemote()
        {
            var url = await Bg(Git.RemoteUrl);
            if (!string.IsNullOrEmpty(url)) return true;
            LastError = "GitHubのリポジトリが登録されていません。同期ウィンドウの「設定」からURLを登録してください。";
            return false;
        }

        static async Task<MergeResult> MergeFromRemote()
        {
            ShowProgress("GitHubの新着を確認しています…");
            var synced = SyncSettings.Current.SyncedFolders;
            var o = await Bg(() => FetchAndMerge(synced));
            if (o.ReplacedVendorMetas > 0)
            {
                AppendLog(o.ReplacedVendorMetas + " 個の配布物の ID を、もう一方のPCが記録した ID に置き換えました。");
            }
            if (o.RestoredMachineOnly != null && o.RestoredMachineOnly.Count > 0)
            {
                AppendLog("PC ごとの値だけが違っていたファイルを、受け取る前に元に戻しました: " + string.Join(", ", o.RestoredMachineOnly));
            }
            if (o.Error.HasValue)
            {
                Fail(o.ErrorTitle, o.Error.Value);
                return MergeResult.Failed;
            }
            if (o.Conflicts == null || o.Conflicts.Count == 0)
            {
                if (o.Merged) LastFetch = DateTime.Now;
                return o.Merged ? MergeResult.Merged : MergeResult.UpToDate;
            }

            HideProgress();
            const int maxList = 15;
            var list = string.Join("\n", o.Conflicts.Take(maxList).Select(p => "・" + p));
            if (o.Conflicts.Count > maxList) list += "\n…ほか " + (o.Conflicts.Count - maxList) + " 個";
            int choice = SyncDialogs.Choice(
                "このPCともう一方のPCの両方で、同じファイルが変更されています。\n\n" + list + "\n\n" +
                "これらのファイルは、どちらの内容を使いますか？\n（選ばなかった方の内容も、履歴には残ります）",
                "このPCの内容を使う", "やめる", "もう一方のPCの内容を使う");
            if (choice == 1)
            {
                await Bg(Git.MergeAbort);
                WasCancelled = true;
                LastError = "受け取りをやめました。GitHubの変更はまだ取り込まれていません。";
                return MergeResult.Failed;
            }

            bool ours = choice == 0;
            ShowProgress("衝突したファイルをそろえています…");
            var r = await Bg(() => Git.ResolveConflicts(o.Conflicts, ours));
            if (!r.Ok)
            {
                await Bg(Git.MergeAbort);
                Fail("衝突したファイルをそろえられませんでした。", r);
                return MergeResult.Failed;
            }
            AppendLog((ours ? "このPC" : "もう一方のPC") + "の内容を使いました: " + string.Join(", ", o.Conflicts));
            return MergeResult.Merged;
        }

        /// <summary>（バックグラウンド）GitHub の新着を取得して、あれば取り込む。</summary>
        static MergeOutcome FetchAndMerge(string[] synced)
        {
            var o = new MergeOutcome();
            var f = Git.Fetch();
            if (!f.Ok)
            {
                o.Error = f;
                o.ErrorTitle = "GitHubに接続できませんでした。";
                return o;
            }
            if (!Git.HasUpstream())
            {
                var branch = Git.CurrentBranch();
                if (!Git.RemoteBranchExists(branch)) return o; // まだ一度も送っていない
                Git.SetUpstream(branch);
            }
            Git.AheadBehind(out _, out int behind, out _);
            if (behind == 0) return o;

            // Unity が PC ごとに書き換える値だけが違うファイル（記録していない）は、取り込みの邪魔になるので先に戻す
            o.RestoredMachineOnly = Git.RestoreMachineNoiseTouchedByIncoming();
            // もう一方のPCが記録した配布物の .meta と同じ場所に、このPCの未記録の .meta があると取り込めないので消す
            // （このPCの ID は GitHub の ID で置き換わる）
            o.ReplacedVendorMetas = Git.RemoveUntrackedIncomingVendorMetas(synced);
            var m = Git.MergeUpstream();
            if (m.Ok)
            {
                o.Merged = true;
                return o;
            }
            var conflicts = Git.ConflictedFiles();
            if (conflicts.Count > 0)
            {
                o.Conflicts = conflicts;
                return o;
            }
            o.Error = m;
            o.ErrorTitle = "GitHubの変更を取り込めませんでした。";
            return o;
        }

        // ================================================================ Unity 側の処理

        /// <summary>
        /// 同期の前に、未保存のシーンとアセットを保存する。
        /// そのとき自分のフォルダの外（配布物など）に保存されたものがあれば、届かないことを知らせて、自分のフォルダに入れるか聞く。
        /// </summary>
        static bool PrepareUnity()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                LastError = "プレイモード中は同期できません。プレイモードを終了してから実行してください。";
                return false;
            }
            List<SaveWatcher.Captured> captured;
            using (SaveWatcher.Capture(out captured))
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    WasCancelled = true;
                    return false;
                }
                AssetDatabase.SaveAssets();
            }
            var changed = SaveWatcher.ChangedOnly(captured);
            return changed.Count == 0 || HandleOutsideSaves(changed);
        }

        /// <summary>自分のフォルダの外に保存されたものを、自分のフォルダに入れるか聞いて、入れる。続けてよければ true。</summary>
        static bool HandleOutsideSaves(List<SaveWatcher.Captured> items)
        {
            const int maxList = 10;
            var vendor = items.Where(i => i.IsVendor).Select(i => i.Path).ToList();
            var outside = items.Where(i => !i.IsVendor).Select(i => i.Path).ToList();
            var sb = new StringBuilder();
            if (vendor.Count > 0)
            {
                sb.Append("配布物のファイルが変更されています。自分のフォルダにコピーしないと、もう一方のPCには届きません:\n");
                foreach (var p in vendor.Take(maxList)) sb.Append("・").Append(p).Append('\n');
                if (vendor.Count > maxList) sb.Append("…ほか ").Append(vendor.Count - maxList).Append(" 個\n");
                sb.Append('\n');
            }
            if (outside.Count > 0)
            {
                sb.Append("自分のフォルダの外にファイルが保存されています。自分のフォルダに移さないと、もう一方のPCには届きません:\n");
                foreach (var p in outside.Take(maxList)) sb.Append("・").Append(p).Append('\n');
                if (outside.Count > maxList) sb.Append("…ほか ").Append(outside.Count - maxList).Append(" 個\n");
                sb.Append('\n');
            }
            sb.Append("「自分のフォルダに入れてから続ける」と、配布物はコピーして参照を切り替え（配布物は変更前に戻します）、それ以外は自分のフォルダに移動してから続けます。");

            int choice = SyncDialogs.Choice(sb.ToString(), "自分のフォルダに入れてから続ける", "やめる", "このまま続ける");
            if (choice == 1)
            {
                WasCancelled = true;
                return false;
            }
            if (choice == 2)
            {
                AppendLog("自分のフォルダの外の変更は、そのままにしました: " + string.Join(", ", items.Select(i => i.Path)));
                return true;
            }

            if (vendor.Count > 0)
            {
                var r = AssetRelocator.CopyAndRestore(vendor);
                AppendLog(r.Message);
                if (!r.Success)
                {
                    LastError = r.Message;
                    return false;
                }
            }
            foreach (var p in outside)
            {
                var m = AssetRelocator.MoveToOwnFolder(p, out bool moved);
                AppendLog(m);
                if (!moved)
                {
                    LastError = m;
                    return false;
                }
            }
            AssetDatabase.SaveAssets();
            return true;
        }

        /// <summary>Assets の中に、保存していないアセット（マテリアルなど）があるか。</summary>
        public static bool AnyDirtyAsset()
        {
            foreach (var o in Resources.FindObjectsOfTypeAll<UnityEngine.Object>())
            {
                if (o == null || !EditorUtility.IsPersistent(o) || !EditorUtility.IsDirty(o)) continue;
                var path = AssetDatabase.GetAssetPath(o);
                if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        static void SaveOpenScenesSilently()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.isDirty && !string.IsNullOrEmpty(scene.path))
                {
                    EditorSceneManager.SaveScene(scene);
                }
            }
        }

        public static bool AnyDirtyScene()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (SceneManager.GetSceneAt(i).isDirty) return true;
            }
            return false;
        }

        static void AfterWorkingTreeChanged()
        {
            SyncSettings.Reload();
            AssetDatabase.Refresh();
            CheckUnityVersion();
        }

        static void CheckUnityVersion()
        {
            try
            {
                var file = ProjectPaths.ToFull("ProjectSettings/ProjectVersion.txt");
                if (!File.Exists(file)) return;
                var line = File.ReadAllLines(file).FirstOrDefault(l => l.StartsWith("m_EditorVersion:", StringComparison.Ordinal));
                if (line == null) return;
                var version = line.Substring("m_EditorVersion:".Length).Trim();
                if (version.Length > 0 && version != Application.unityVersion)
                {
                    SyncDialogs.Notice(
                        "もう一方のPCは Unity " + version + "、このPCは Unity " + Application.unityVersion + " です。\n" +
                        "Unityのバージョンが違うと表示が変わったり壊れたりすることがあるので、そろえてください。");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VRC Git Sync] Unityのバージョンを確認できませんでした: " + e.Message);
            }
        }

        static string BuildFileSummary(List<string> files, string own)
        {
            long total = 0;
            var groups = new Dictionary<string, (int count, long size)>();
            foreach (var f in files)
            {
                long size = 0;
                try
                {
                    var info = new FileInfo(ProjectPaths.ToFull(f));
                    if (info.Exists) size = info.Length;
                }
                catch
                {
                    // 無視
                }
                total += size;
                var key = GroupKey(f);
                groups.TryGetValue(key, out var g);
                groups[key] = (g.count + 1, g.size + size);
            }
            var sb = new StringBuilder();
            sb.Append("次の ").Append(files.Count).Append(" 個のファイル（合計 ").Append(FormatSize(total)).Append("）を記録して、GitHubに送れるようにします。\n\n");
            foreach (var kv in groups.OrderByDescending(kv => kv.Value.size).Take(12))
            {
                sb.Append("・").Append(kv.Key).Append("　").Append(kv.Value.count).Append(" 個 / ").Append(FormatSize(kv.Value.size)).Append('\n');
            }
            sb.Append("\nAssets のうち「").Append(own).Append("」以外（配布物）は、.meta（ID と設定）だけを記録します。配布物の中身は記録しません。よろしいですか？");
            return sb.ToString();
        }

        static string GroupKey(string path)
        {
            var parts = path.Split('/');
            if (parts.Length >= 3 && parts[0] == "Assets") return parts[0] + "/" + parts[1];
            // "Assets/_Mine.meta" はフォルダ本体と同じグループにまとめる
            if (parts.Length == 2 && parts[0] == "Assets" && parts[1].EndsWith(".meta")) return "Assets/" + parts[1].Substring(0, parts[1].Length - 5);
            if (parts.Length >= 2) return parts[0];
            return "(プロジェクト直下)";
        }

        public static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00") + " GB";
            if (bytes >= 1024L * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0") + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0") + " KB";
            return bytes + " B";
        }

        /// <summary>
        /// 取り込みの前に、今の自分のフォルダと重複ファイルをバックアップへ「移動」し、
        /// 上書きされる設定ファイルを「コピー」する。バックアップ先（プロジェクトからの相対パス）を返す。
        /// </summary>
        static string BackupForImport(string own, List<string> duplicates)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var root = ProjectPaths.Root;
            var dest = Path.Combine(root, RepoFiles.BackupFolderName, stamp);
            Directory.CreateDirectory(dest);

            // 自分のフォルダは GitHub の内容に丸ごと置き換えるので、今の内容は移動しておく
            MoveToBackup(root, own, dest);
            foreach (var dup in duplicates) MoveToBackup(root, dup, dest);

            var ps = Path.Combine(root, "ProjectSettings");
            if (Directory.Exists(ps)) CopyDirectory(ps, Path.Combine(dest, "ProjectSettings"));
            var pk = Path.Combine(root, "Packages");
            if (Directory.Exists(pk))
            {
                Directory.CreateDirectory(Path.Combine(dest, "Packages"));
                foreach (var json in Directory.GetFiles(pk, "*.json"))
                {
                    File.Copy(json, Path.Combine(dest, "Packages", Path.GetFileName(json)), true);
                }
            }
            foreach (var name in new[] { ".gitignore", ".gitattributes" })
            {
                var f = Path.Combine(root, name);
                if (File.Exists(f)) File.Copy(f, Path.Combine(dest, name), true);
            }
            return RepoFiles.BackupFolderName + "/" + stamp;
        }

        static void MoveToBackup(string root, string relativePath, string backupRoot)
        {
            var src = Path.Combine(root, relativePath);
            var dst = Path.Combine(backupRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            if (Directory.Exists(src)) Directory.Move(src, dst);
            else if (File.Exists(src)) File.Move(src, dst);
            if (File.Exists(src + ".meta")) File.Move(src + ".meta", dst + ".meta");
        }

        static void CopyDirectory(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.GetFiles(src))
            {
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), true);
            }
            foreach (var dir in Directory.GetDirectories(src))
            {
                CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
            }
        }

        // ================================================================ 共通

        static Task<T> Bg<T>(Func<T> f) => _blocking ? Task.FromResult(f()) : Task.Run(f);

        static bool BeginOp(bool blocking)
        {
            if (Busy)
            {
                LastError = "別の処理を実行中です。終わってからもう一度押してください。";
                Notify();
                return false;
            }
            if (_fetching && !blocking)
            {
                LastError = "GitHubの新着を確認中です。少し待ってからもう一度押してください。";
                Notify();
                return false;
            }
            Busy = true;
            _blocking = blocking;
            WasCancelled = false;
            LastError = "";
            LastInfo = "";
            BusyText = "";
            Notify();
            return true;
        }

        static void EndOp(bool ok)
        {
            HideProgress(ok);
            Busy = false;
            BusyText = "";
            bool wasBlocking = _blocking;
            _blocking = false;
            Notify();
            if (!wasBlocking) _ = RefreshStatus(false);
            try
            {
                OperationFinished?.Invoke();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
            }
        }

        /// <summary>送る・受け取る・初期設定などの処理が終わったとき（成功・失敗とも）。</summary>
        public static event Action OperationFinished;

        static void ShowProgress(string text)
        {
            BusyText = text;
            if (_blocking)
            {
                EditorUtility.DisplayProgressBar(Title, text, 0.5f);
            }
            else
            {
                if (_progressId < 0) _progressId = Progress.Start(Title, text, Progress.Options.Indefinite);
                else Progress.SetDescription(_progressId, text);
            }
            Notify();
        }

        static void HideProgress(bool succeeded = true)
        {
            if (_blocking)
            {
                EditorUtility.ClearProgressBar();
            }
            else if (_progressId >= 0)
            {
                Progress.Finish(_progressId, succeeded ? Progress.Status.Succeeded : Progress.Status.Failed);
                _progressId = -1;
            }
        }

        static void Info(string message)
        {
            LastInfo = message;
            AppendLog(message);
        }

        static void Fail(string title, GitResult r)
        {
            LastError = title + "\n" + r.Message;
            if (Platform.IsAuthError(r.Message)) LastError += "\n\n" + Platform.AuthHelp;
            AppendLog("失敗: " + LastError);
            Debug.LogWarning("[VRC Git Sync] " + LastError);
        }

        static void HandleException(Exception e)
        {
            LastError = "予期しないエラーが起きました: " + e.Message;
            AppendLog(LastError);
            Debug.LogException(e);
        }

        internal static void AppendLog(string message)
        {
            LogBuilder.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ").Append(message).Append('\n');
            if (LogBuilder.Length > 20000) LogBuilder.Remove(0, LogBuilder.Length - 15000);
        }

        static string DefaultMessage(string suffix)
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " " + Environment.MachineName + suffix;
        }

        static string FirstLine(string s)
        {
            int nl = s.IndexOf('\n');
            return nl < 0 ? s : s.Substring(0, nl);
        }

        static void Notify()
        {
            try
            {
                Changed?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
