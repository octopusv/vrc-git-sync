using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace VrcGitSync
{
    /// <summary>
    /// 自分のフォルダの外にあるファイルを、自分のフォルダへコピー・移動する。
    /// </summary>
    static class AssetRelocator
    {
        static readonly Regex GuidReference = new Regex(@"guid: ([0-9a-f]{32})", RegexOptions.Compiled);
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public class CopyResult
        {
            public bool Success;
            public string Message;
            /// <summary>コピー元 → コピー先（選んだものだけ）。</summary>
            public List<KeyValuePair<string, string>> Copies = new List<KeyValuePair<string, string>>();
        }

        /// <summary>"Assets/ShopName/AvatarName/Face.mat" → "Assets/_Mine/ShopName/AvatarName/Face.mat"（配布物と同じ並びでコピーする）。</summary>
        static string OwnCopyPath(string assetPath)
        {
            return SyncSettings.Current.ownFolder + "/" + assetPath.Substring("Assets/".Length);
        }

        /// <summary>
        /// ファイル（またはフォルダ）を自分のフォルダにコピーし、参照をコピーに切り替える。元のファイルはそのまま残す。
        /// 切り替えるのは (1) 自分のフォルダ内のファイルに書かれた参照（シーン・アニメーション・コピーしたものどうしなど）と、
        /// (2) 開いているシーンでプレハブから引き継いでいる参照（シーン側の上書きとして記録する）。
        /// コピーは新しい GUID になるので、もう一方のPCの配布物と GUID が重複することはない。
        /// </summary>
        public static CopyResult CopyIntoOwnFolder(IEnumerable<string> sources)
        {
            var result = new CopyResult();
            var settings = SyncSettings.Current;
            if (!settings.IsConfigured)
            {
                result.Message = "まだ初期設定がされていません。";
                return result;
            }

            var targets = sources
                .Select(ProjectPaths.Normalize)
                .Where(p => p.StartsWith("Assets/", StringComparison.Ordinal) && !settings.IsSynced(p))
                .Distinct()
                .ToList();
            // フォルダとその中身が両方選ばれていたら、フォルダだけにする
            targets = targets.Where(p => !targets.Any(o => o != p && ProjectPaths.IsUnder(p, o))).ToList();
            if (targets.Count == 0)
            {
                result.Message = "自分のフォルダの外にあるファイルが選ばれていません。";
                return result;
            }

            // スクリプトをコピーすると同じ名前のクラスが2つになってしまうので断る
            foreach (var t in targets)
            {
                foreach (var p in ExpandAssets(t))
                {
                    var ext = Path.GetExtension(p).ToLowerInvariant();
                    if (ext == ".cs" || ext == ".dll" || ext == ".asmdef" || ext == ".asmref")
                    {
                        result.Message = "スクリプトを含むものはコピーできません: " + p;
                        return result;
                    }
                }
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                result.Message = "キャンセルしました。";
                return result;
            }
            AssetDatabase.SaveAssets();
            var openScenes = EditorSceneManager.GetSceneManagerSetup()
                .Where(s => !string.IsNullOrEmpty(s.path))
                .ToArray();

            var guidMap = new Dictionary<string, string>();
            using (SaveWatcher.Suppress())
            {
                // 1. コピーして、元の GUID → コピーの GUID の対応表を作る
                foreach (var src in targets)
                {
                    var srcAssets = ExpandAssets(src);
                    var wanted = OwnCopyPath(src);
                    // 親フォルダがないと GenerateUniqueAssetPath は空文字を返すので、先に作る
                    ProjectPaths.EnsureAssetFolder(wanted.Substring(0, wanted.LastIndexOf('/')));
                    var dest = AssetDatabase.GenerateUniqueAssetPath(wanted);
                    if (string.IsNullOrEmpty(dest) || !AssetDatabase.CopyAsset(src, dest))
                    {
                        result.Message = "コピーできませんでした: " + src;
                        return result;
                    }
                    foreach (var s in srcAssets)
                    {
                        var d = dest + s.Substring(src.Length);
                        var oldGuid = AssetDatabase.AssetPathToGUID(s);
                        var newGuid = AssetDatabase.AssetPathToGUID(d);
                        if (!string.IsNullOrEmpty(oldGuid) && !string.IsNullOrEmpty(newGuid) && oldGuid != newGuid) guidMap[oldGuid] = newGuid;
                    }
                    result.Copies.Add(new KeyValuePair<string, string>(src, dest));
                }

                // 2. 開いているシーンをいったん閉じて、自分のフォルダ内のファイルの参照を書き換える
                if (openScenes.Length > 0) EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                int rewrittenFiles = RewriteReferences(settings.SyncedFolders, guidMap);
                AssetDatabase.Refresh();

                // 3. シーンを開き直して、プレハブから引き継いでいる参照もコピーに切り替える
                int sceneReferences = 0;
                if (openScenes.Length > 0)
                {
                    if (!openScenes.Any(s => s.isActive)) openScenes[0].isActive = true;
                    if (!openScenes.Any(s => s.isLoaded)) openScenes[0].isLoaded = true;
                    EditorSceneManager.RestoreSceneManagerSetup(openScenes);
                    sceneReferences = ReplaceInOpenScenes(guidMap);
                }

                result.Success = true;
                var sb = new StringBuilder();
                sb.Append(result.Copies.Count).Append(" 個を自分のフォルダにコピーしました。\n");
                foreach (var c in result.Copies) sb.Append("・").Append(c.Value).Append('\n');
                sb.Append("自分のフォルダの ").Append(rewrittenFiles).Append(" 個のファイルと、開いているシーンの ")
                  .Append(sceneReferences).Append(" か所の参照をコピーに切り替えました。元のファイルはそのまま残っています。");
                if (result.Copies.Any(c => c.Value.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)))
                {
                    sb.Append("\nシーンをコピーした場合は、コピーしたシーンを開いて作業してください。");
                }
                result.Message = sb.ToString();
                return result;
            }
        }

        /// <summary>path がフォルダならその中のすべてのアセット（自身を含む）、ファイルならそれだけ。</summary>
        static List<string> ExpandAssets(string path)
        {
            var list = new List<string> { path };
            if (AssetDatabase.IsValidFolder(path))
            {
                list.AddRange(AssetDatabase.FindAssets("", new[] { path })
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => ProjectPaths.IsUnder(p, path) && p != path)
                    .Distinct());
            }
            return list;
        }

        /// <summary>自分のフォルダ内のテキスト形式のアセットで、guid の参照を書き換える。書き換えたファイル数を返す。</summary>
        static int RewriteReferences(string[] syncedFolders, Dictionary<string, string> guidMap)
        {
            if (guidMap.Count == 0) return 0;
            int count = 0;
            var files = syncedFolders.Select(ProjectPaths.ToFull).Where(Directory.Exists)
                .SelectMany(dir => Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
            foreach (var file in files)
            {
                // .meta の guid はそのファイル自身の ID なので触らない
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsUnityYaml(file)) continue;
                var text = File.ReadAllText(file, Encoding.UTF8);
                bool changed = false;
                var replaced = GuidReference.Replace(text, m =>
                {
                    if (!guidMap.TryGetValue(m.Groups[1].Value, out var newGuid)) return m.Value;
                    changed = true;
                    return "guid: " + newGuid;
                });
                if (!changed) continue;
                File.WriteAllText(file, replaced, Utf8);
                count++;
            }
            return count;
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

        /// <summary>開いているシーンの全コンポーネントで、コピー元を指している参照をコピーに切り替えて保存する。</summary>
        static int ReplaceInOpenScenes(Dictionary<string, string> guidMap)
        {
            if (guidMap.Count == 0) return 0;
            var cache = new Dictionary<Object, Object>();
            int replaced = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                bool sceneChanged = false;
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var component in root.GetComponentsInChildren<Component>(true))
                    {
                        if (component == null) continue; // スクリプトが見つからないコンポーネント
                        var so = new SerializedObject(component);
                        var it = so.GetIterator();
                        bool changed = false;
                        while (it.NextVisible(true))
                        {
                            if (it.propertyType != SerializedPropertyType.ObjectReference || it.name == "m_Script") continue;
                            var target = it.objectReferenceValue;
                            if (target == null) continue;
                            var replacement = Resolve(target, guidMap, cache);
                            if (replacement == null) continue;
                            it.objectReferenceValue = replacement;
                            changed = true;
                            replaced++;
                        }
                        if (!changed) continue;
                        so.ApplyModifiedProperties();
                        sceneChanged = true;
                    }
                }
                if (sceneChanged)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    if (!string.IsNullOrEmpty(scene.path)) EditorSceneManager.SaveScene(scene);
                }
            }
            return replaced;
        }

        /// <summary>コピー元のオブジェクトに対応する、コピー側のオブジェクト（同じローカル ID のもの）。</summary>
        static Object Resolve(Object source, Dictionary<string, string> guidMap, Dictionary<Object, Object> cache)
        {
            if (cache.TryGetValue(source, out var cached)) return cached;
            Object found = null;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long localId)
                && guidMap.TryGetValue(guid, out var newGuid))
            {
                foreach (var candidate in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(newGuid)))
                {
                    if (candidate == null || candidate.GetType() != source.GetType()) continue;
                    if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(candidate, out string _, out long id) && id == localId)
                    {
                        found = candidate;
                        break;
                    }
                }
            }
            cache[source] = found;
            return found;
        }

        /// <summary>
        /// 保存の警告から呼ぶ: 変更された配布物のファイルを自分のフォルダにコピーして参照を切り替え、
        /// 配布物は保存前の内容に戻す。
        /// </summary>
        public static CopyResult CopyAndRestore(IList<string> vendorPaths)
        {
            // 保存していない変更もコピーに含める
            using (SaveWatcher.Suppress())
            {
                foreach (var path in vendorPaths)
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(path);
                    if (asset != null) AssetDatabase.SaveAssetIfDirty(asset);
                }
            }

            var result = CopyIntoOwnFolder(vendorPaths);
            if (!result.Success) return result;

            var notRestored = new List<string>();
            using (SaveWatcher.Suppress())
            {
                foreach (var path in vendorPaths)
                {
                    if (!OriginalCache.Restore(path)) notRestored.Add(path);
                }
            }
            result.Message += notRestored.Count == 0
                ? "\n配布物のファイルは、変更する前の内容に戻しました。"
                : "\n次の配布物のファイルは変更されたままです（元に戻したい場合は、その配布物をインポートし直してください）:\n・" + string.Join("\n・", notRestored);
            return result;
        }

        /// <summary>ファイルを自分のフォルダへ移動する（GUID は変わらないので参照は切れない）。</summary>
        /// <param name="relativeDest">自分のフォルダの中での置き場所（例: "TexSet/Body/a.png"）。省略するとファイル名だけで自分のフォルダの直下に置く。</param>
        public static string MoveToOwnFolder(string path, out bool success, string relativeDest = null)
        {
            success = false;
            var settings = SyncSettings.Current;
            using (SaveWatcher.Suppress())
            {
                var target = settings.ownFolder + "/" + (string.IsNullOrEmpty(relativeDest) ? Path.GetFileName(path) : relativeDest);
                ProjectPaths.EnsureAssetFolder(Path.GetDirectoryName(target).Replace('\\', '/'));
                var dest = AssetDatabase.GenerateUniqueAssetPath(target);
                var err = AssetDatabase.MoveAsset(path, dest);
                if (!string.IsNullOrEmpty(err)) return "移動できませんでした: " + err;
                success = true;
                return path + " を " + dest + " に移動しました。";
            }
        }
    }
}
