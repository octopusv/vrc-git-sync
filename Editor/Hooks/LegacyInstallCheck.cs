using System.IO;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace VrcGitSync
{
    /// <summary>
    /// .unitypackage で入れた版（Assets/VrcGitSync）を使っているときに、
    /// もう一方のPCが VCC / ALCOM（VPM）や Package Manager で入れる形に切り替えたことに気づいて、こちらを消すか聞く。
    ///
    /// 両方が入ると、同じ名前のアセンブリが2つになって Unity がどちらもコンパイルできなくなる（このコードも動かなくなる）。
    /// 受け取った vpm-manifest.json / manifest.json にこのツールが書かれた時点（VPM の Resolve で実際に入る前）に聞く。
    /// VCC / ALCOM で直接入れた場合は、package.json の legacyFolders を見て VCC / ALCOM が Assets/VrcGitSync を消す。
    /// </summary>
    [InitializeOnLoad]
    public static class LegacyInstallCheck
    {
        /// <summary>VPM / Package Manager でのパッケージ名（package.json の name と同じ）。</summary>
        public const string PackageName = "io.github.octopusv.vrc-git-sync";
        /// <summary>.unitypackage 版のフォルダの GUID（Tools~/build.py の INSTALL_DIR_GUID と同じ）。</summary>
        const string LegacyFolderGuid = "b79fe926f9604a58aba0dcc0d7ed51fa";
        const string DeclinedKey = "VrcGitSync.LegacyInstallDeclined";

        static bool _scheduled;

        static LegacyInstallCheck()
        {
            if (Application.isBatchMode || InstalledAsPackage) return;
            Schedule();
            // 受け取ったとき（vpm-manifest.json だけが変わったときも）と、アセットが変わったとき
            SyncOperations.OperationFinished += Schedule;
            EditorApplication.projectChanged += Schedule;
        }

        static void Schedule()
        {
            if (_scheduled) return;
            _scheduled = true;
            EditorApplication.delayCall += () =>
            {
                _scheduled = false;
                if (SyncOperations.Busy || SessionState.GetBool(DeclinedKey, false) || !NeedsSwitch()) return;
                // 「あとで」を選んだら、このセッションの間は聞かない
                if (!AskAndRemove()) SessionState.SetBool(DeclinedKey, true);
            };
        }

        /// <summary>このツールが VPM などでパッケージとして入っているか。</summary>
        public static bool InstalledAsPackage => PackageInfo.FindForAssembly(typeof(LegacyInstallCheck).Assembly) != null;

        /// <summary>.unitypackage 版のフォルダ。なければ null。</summary>
        public static string LegacyFolder()
        {
            var path = AssetDatabase.GUIDToAssetPath(LegacyFolderGuid);
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/") || !AssetDatabase.IsValidFolder(path)) return null;
            return path;
        }

        /// <summary>プロジェクトのパッケージ一覧（vpm-manifest.json / manifest.json）にこのツールが書かれているか。</summary>
        public static bool ListedInPackageManifests()
        {
            foreach (var name in new[] { "vpm-manifest.json", "manifest.json" })
            {
                var path = Path.Combine(ProjectPaths.Root, "Packages", name);
                try
                {
                    if (File.Exists(path) && File.ReadAllText(path).Contains("\"" + PackageName + "\"")) return true;
                }
                catch (IOException)
                {
                }
            }
            return false;
        }

        /// <summary>.unitypackage 版で動いていて、パッケージ一覧にはこのツールが書かれている（切り替えが必要）か。</summary>
        public static bool NeedsSwitch() => !InstalledAsPackage && ListedInPackageManifests() && LegacyFolder() != null;

        /// <summary>切り替えが必要なら、.unitypackage 版を消すか聞く。消したら true。</summary>
        public static bool Check() => NeedsSwitch() && AskAndRemove();

        static bool AskAndRemove()
        {
            var legacy = LegacyFolder();
            var ok = SyncDialogs.Confirm(
                "このプロジェクトでは、VRC Git Sync を VCC / ALCOM で入れる形に切り替えています（もう一方のPCで切り替えた内容を受け取りました）。\n\n" +
                $"このPCの .unitypackage 版（{legacy}）を消しますか？\n" +
                "両方が入っているとエラーになります。消したあとで VCC / ALCOM からプロジェクトを開き直すと、VCC / ALCOM 版が入ります。",
                "消す", "あとで");
            if (!ok) return false;
            if (!AssetDatabase.DeleteAsset(legacy))
            {
                Debug.LogWarning($"[VRC Git Sync] {legacy} を消せませんでした。Unity を閉じてから手で消してください。");
                return false;
            }
            Debug.Log($"[VRC Git Sync] .unitypackage 版のフォルダ {legacy} を消しました。VCC / ALCOM からプロジェクトを開き直してください。");
            return true;
        }
    }
}
