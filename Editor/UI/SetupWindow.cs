using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace VrcGitSync
{
    /// <summary>初期設定ウィザード。</summary>
    public class SetupWindow : EditorWindow
    {
        const string DefaultFolderName = "_Mine";
        const string NewFolderLabel = "（新しく作る）";

        class SceneEntry
        {
            public string Path;
            public bool Move;
        }

        int _mode;
        Vector2 _scroll;

        // git の確認
        bool _envLoading;
        string _gitVersion;
        string _lfsVersion;
        string _userName;
        string _userEmail;
        string _credentialHelper;
        string _inputName = "";
        string _inputEmail = "";

        // 1台目
        string[] _folderOptions = { NewFolderLabel };
        int _folderIndex;
        string _newFolderName = DefaultFolderName;
        List<SceneEntry> _scenes = new List<SceneEntry>();
        string _scenesFor;
        string _remoteUrl = "";

        // 2台目
        string _importUrl = "";

        [MenuItem("Tools/VRC Git Sync/初期設定", priority = 1)]
        public static void Open()
        {
            var w = GetWindow<SetupWindow>(false, "VRC Git Sync 初期設定", true);
            w.minSize = new Vector2(480, 560);
        }

        void OnEnable()
        {
            SyncOperations.Changed += Repaint;
            RefreshFolders();
            _ = RefreshEnv();
        }

        void OnDisable()
        {
            SyncOperations.Changed -= Repaint;
        }

        void OnFocus()
        {
            RefreshFolders();
        }

        async Task RefreshEnv()
        {
            _envLoading = true;
            Repaint();
            GitRunner.ResetGitPath();
            var env = await Task.Run(() => (
                git: Git.GitVersion(),
                lfs: Git.LfsVersion(),
                name: Git.ConfigGet("user.name"),
                email: Git.ConfigGet("user.email"),
                helper: Git.GitHubCredentialHelper()));
            _gitVersion = env.git;
            _lfsVersion = env.lfs;
            _userName = env.name;
            _userEmail = env.email;
            _credentialHelper = env.helper;
            _envLoading = false;
            Repaint();
        }

        void RefreshFolders()
        {
            var current = SelectedFolderName();
            var tool = ToolFolder();
            var folders = AssetDatabase.GetSubFolders("Assets")
                .Where(f => !string.Equals(f, tool, System.StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Substring("Assets/".Length))
                .OrderBy(f => f)
                .ToList();
            _folderOptions = new[] { NewFolderLabel }.Concat(folders).ToArray();

            string preferred = current;
            if (string.IsNullOrEmpty(preferred))
            {
                var configured = SyncSettings.Current.ownFolder;
                preferred = string.IsNullOrEmpty(configured) ? DefaultFolderName : configured.Substring("Assets/".Length);
            }
            int idx = System.Array.IndexOf(_folderOptions, preferred);
            _folderIndex = idx > 0 ? idx : 0;
            if (_folderIndex == 0 && !string.IsNullOrEmpty(current) && current != DefaultFolderName) _newFolderName = current;
        }

        /// <summary>.unitypackage で Assets に入れた場合の、このツール自身のフォルダ（例: "Assets/VrcGitSync"）。</summary>
        static string ToolFolder()
        {
            if (_toolFolderResolved) return _toolFolder;
            _toolFolderResolved = true;
            foreach (var guid in AssetDatabase.FindAssets("VrcGitSync.Editor t:AssemblyDefinitionAsset"))
            {
                var top = ProjectPaths.TopLevelAssetFolder(AssetDatabase.GUIDToAssetPath(guid));
                if (top != null)
                {
                    _toolFolder = top;
                    break;
                }
            }
            return _toolFolder;
        }

        static string _toolFolder;
        static bool _toolFolderResolved;

        string SelectedFolderName()
        {
            if (_folderOptions == null || _folderIndex < 0 || _folderIndex >= _folderOptions.Length) return null;
            return _folderIndex == 0 ? _newFolderName : _folderOptions[_folderIndex];
        }

        void RefreshScenes(string ownFolder)
        {
            if (_scenesFor == ownFolder) return;
            var previous = _scenes.ToDictionary(s => s.Path, s => s.Move);
            _scenes = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".unity") && !ProjectPaths.IsUnder(p, ownFolder))
                .OrderBy(p => p)
                .Select(p => new SceneEntry
                {
                    Path = p,
                    Move = previous.TryGetValue(p, out var m) ? m : IsLikelyUserScene(p),
                })
                .ToList();
            _scenesFor = ownFolder;
        }

        /// <summary>Assets 直下や Assets/Scenes のシーンは自分のものとみなして、最初からチェックしておく。</summary>
        static bool IsLikelyUserScene(string path)
        {
            var dir = ProjectPaths.Normalize(Path.GetDirectoryName(path));
            return dir == "Assets" || dir == "Assets/Scenes";
        }

        bool EnvOk => !string.IsNullOrEmpty(_gitVersion) && !string.IsNullOrEmpty(_lfsVersion)
                      && !string.IsNullOrEmpty(_userName) && !string.IsNullOrEmpty(_userEmail);

        // ================================================================ 画面

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (SyncOperations.IsReady)
            {
                DrawAlreadyConfigured();
                DrawResult();
                EditorGUILayout.EndScrollView();
                return;
            }

            DrawEnvironment();
            EditorGUILayout.Space(8);

            _mode = GUILayout.Toolbar(_mode, new[] { "このPCから始める（1台目）", "GitHubから取り込む（2台目以降）" }, GUILayout.Height(26));
            EditorGUILayout.Space(4);

            using (new EditorGUI.DisabledScope(SyncOperations.Busy))
            {
                if (_mode == 0) DrawFirstPc();
                else DrawImport();
            }

            DrawResult();
            EditorGUILayout.EndScrollView();
        }

        void DrawAlreadyConfigured()
        {
            EditorGUILayout.HelpBox("このプロジェクトは初期設定が完了しています。\n自分のフォルダ: " + SyncSettings.Current.ownFolder, MessageType.Info);
            if (GUILayout.Button("同期ウィンドウを開く", GUILayout.Height(28))) SyncWindow.Open();
        }

        void DrawEnvironment()
        {
            Header("1. git の確認");
            if (_envLoading)
            {
                EditorGUILayout.LabelField("確認しています…");
                return;
            }

            if (string.IsNullOrEmpty(_gitVersion))
            {
                EditorGUILayout.HelpBox(Platform.GitMissingHelp, MessageType.Error);
                if (GUILayout.Button(Platform.GitDownloadLabel)) Application.OpenURL(Platform.GitDownloadUrl);
            }
            else
            {
                EditorGUILayout.LabelField("git", _gitVersion);
                if (string.IsNullOrEmpty(_lfsVersion))
                {
                    EditorGUILayout.HelpBox(Platform.LfsMissingHelp, MessageType.Error);
                    if (GUILayout.Button("Git LFS のページを開く")) Application.OpenURL("https://git-lfs.com/");
                }
                else
                {
                    EditorGUILayout.LabelField("Git LFS", _lfsVersion);
                }

                // Mac の標準（osxkeychain）だけでは GitHub にブラウザでログインできないので案内する
                if (Platform.IsMac && !HasGitHubLogin(_credentialHelper))
                {
                    EditorGUILayout.HelpBox(
                        "GitHub へのログイン方法が設定されていないようです（今の設定: " +
                        (string.IsNullOrEmpty(_credentialHelper) ? "なし" : _credentialHelper) + "）。\n" +
                        "送るときにログインできないので、ターミナルで次のどちらかを実行してから「もう一度確認」を押してください。\n\n" +
                        "・brew install --cask git-credential-manager\n" +
                        "・brew install gh → gh auth login → gh auth setup-git",
                        MessageType.Warning);
                }
            }

            if (!string.IsNullOrEmpty(_gitVersion))
            {
                if (!string.IsNullOrEmpty(_userName) && !string.IsNullOrEmpty(_userEmail))
                {
                    EditorGUILayout.LabelField("記録に使う名前", _userName + " <" + _userEmail + ">");
                }
                else
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.HelpBox(
                        "git の記録に付ける名前とメールアドレスを設定してください（このPCのすべての git で使われます）。\n" +
                        "メールアドレスは、GitHub の設定（Settings → Emails）にある「…@users.noreply.github.com」のアドレスを使うと、本当のアドレスを出さずに済みます。",
                        MessageType.Warning);
                    _inputName = EditorGUILayout.TextField("名前", string.IsNullOrEmpty(_inputName) ? _userName : _inputName);
                    _inputEmail = EditorGUILayout.TextField("メールアドレス", string.IsNullOrEmpty(_inputEmail) ? _userEmail : _inputEmail);
                    using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_inputName) || string.IsNullOrWhiteSpace(_inputEmail)))
                    {
                        if (GUILayout.Button("保存"))
                        {
                            Git.ConfigSetGlobal("user.name", _inputName.Trim());
                            Git.ConfigSetGlobal("user.email", _inputEmail.Trim());
                            _ = RefreshEnv();
                        }
                    }
                }
            }

            if (GUILayout.Button("もう一度確認", GUILayout.Width(120))) _ = RefreshEnv();
        }

        void DrawFirstPc()
        {
            Header("2. 自分のフォルダ");
            EditorGUILayout.HelpBox(
                "Assets の中では、このフォルダだけが同期されます。シーンや、コピーして改変したマテリアル・アニメーション・テクスチャなどは、ここに入れてください。\n" +
                "Boothの素体や衣装などの配布アセットは同期されません（各PCでインポートします）。",
                MessageType.None);
            int newIndex = EditorGUILayout.Popup("フォルダ", _folderIndex, _folderOptions);
            if (newIndex != _folderIndex)
            {
                _folderIndex = newIndex;
                _scenesFor = null;
            }
            if (_folderIndex == 0)
            {
                var name = EditorGUILayout.TextField("新しいフォルダの名前", _newFolderName);
                if (name != _newFolderName)
                {
                    _newFolderName = name;
                    _scenesFor = null;
                }
            }

            string folderName = SelectedFolderName();
            bool folderValid = ProjectPaths.IsValidFolderName(folderName, out var reason);
            string own = "Assets/" + folderName;
            if (folderValid && string.Equals(own, ToolFolder(), System.StringComparison.OrdinalIgnoreCase))
            {
                folderValid = false;
                reason = "このツール自身のフォルダは選べません。別の名前にしてください。";
            }
            if (!folderValid) EditorGUILayout.HelpBox(reason, MessageType.Error);

            EditorGUILayout.Space(8);
            Header("3. 自分のフォルダに移すシーン");
            if (folderValid) RefreshScenes(own);
            if (_scenes.Count == 0)
            {
                EditorGUILayout.LabelField("フォルダの外にシーンはありません。", EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "フォルダの外にあるシーンは同期されません。チェックしたシーンを " + own + " に移動します（参照は切れません）。\n" +
                    "配布物に入っているサンプルシーンなどは、チェックを外したままにしてください。",
                    MessageType.None);
                foreach (var s in _scenes)
                {
                    s.Move = EditorGUILayout.ToggleLeft(s.Path, s.Move);
                }
            }

            EditorGUILayout.Space(8);
            Header("4. GitHub のリポジトリ（あとでも設定できます）");
            EditorGUILayout.HelpBox(
                "GitHub で「Private」のリポジトリを作ってください。README などは追加せず、空のまま作成します。\n" +
                "作成後に表示される URL（https://github.com/ユーザー名/リポジトリ名.git）を貼り付けてください。",
                MessageType.None);
            if (GUILayout.Button("GitHub で新しいリポジトリを作る")) Application.OpenURL("https://github.com/new");
            _remoteUrl = EditorGUILayout.TextField("URL", _remoteUrl);
            bool urlValid = string.IsNullOrWhiteSpace(_remoteUrl) || IsValidUrl(_remoteUrl);
            if (!urlValid) EditorGUILayout.HelpBox("https:// か git@ で始まる URL を入力してください。", MessageType.Error);

            EditorGUILayout.Space(12);
            if (!EnvOk) EditorGUILayout.HelpBox("先に「1. git の確認」を済ませてください。", MessageType.Warning);
            using (new EditorGUI.DisabledScope(!EnvOk || !folderValid || !urlValid))
            {
                if (GUILayout.Button("初期設定を実行", GUILayout.Height(32)))
                {
                    var scenes = _scenes.Where(s => s.Move).Select(s => s.Path).ToList();
                    _ = RunFirstPc(folderName, scenes, _remoteUrl);
                }
            }
        }

        void DrawImport()
        {
            Header("2. GitHub から取り込む");
            EditorGUILayout.HelpBox(
                "1台目のPCで初期設定したリポジトリの内容（自分のフォルダ・設定）を、このプロジェクトに取り込みます。\n\n" +
                "・Boothの素体や衣装などの配布アセットは同期されないので、1台目と同じものをこのプロジェクトにインポートしておいてください。\n" +
                "・VPMパッケージ（VRChat SDK、lilToon、Modular Avatar など）も、1台目と同じものを VCC / ALCOM で入れておいてください。\n" +
                "・このプロジェクトにすでにある自分のフォルダは、GitHub の内容で上書きされます（上書き前にバックアップします）。",
                MessageType.None);
            _importUrl = EditorGUILayout.TextField("URL", _importUrl);
            bool urlValid = IsValidUrl(_importUrl);

            EditorGUILayout.Space(12);
            if (!EnvOk) EditorGUILayout.HelpBox("先に「1. git の確認」を済ませてください。", MessageType.Warning);
            using (new EditorGUI.DisabledScope(!EnvOk || !urlValid))
            {
                if (GUILayout.Button("取り込む", GUILayout.Height(32))) _ = RunImport(_importUrl);
            }
        }

        void DrawResult()
        {
            EditorGUILayout.Space(8);
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
        }

        async Task RunFirstPc(string folderName, List<string> scenes, string url)
        {
            bool ok = await SyncOperations.SetupFirstPc(folderName, scenes, url);
            _scenesFor = null;
            RefreshFolders();
            Repaint();
            if (SyncOperations.IsReady)
            {
                EditorUtility.DisplayDialog(SyncOperations.Title,
                    ok ? SyncOperations.LastInfo : "初期設定はできましたが、GitHub への送信でつまずきました。\n\n" + SyncOperations.LastError,
                    "OK");
                SyncWindow.Open();
            }
        }

        async Task RunImport(string url)
        {
            bool ok = await SyncOperations.SetupFromRemote(url);
            Repaint();
            if (ok)
            {
                EditorUtility.DisplayDialog(SyncOperations.Title, SyncOperations.LastInfo, "OK");
                SyncWindow.Open();
            }
        }

        /// <summary>Git Credential Manager か GitHub CLI が、GitHub のログインに使われるようになっているか。</summary>
        static bool HasGitHubLogin(string helper)
        {
            if (string.IsNullOrEmpty(helper)) return false;
            return helper.Contains("manager") || helper.Contains("gh auth git-credential");
        }

        static bool IsValidUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            url = url.Trim();
            return url.StartsWith("https://") || url.StartsWith("git@") || url.StartsWith("ssh://");
        }

        static void Header(string text)
        {
            EditorGUILayout.LabelField(text, EditorStyles.boldLabel);
        }
    }
}
