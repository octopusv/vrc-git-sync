using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace VrcGitSync
{
    /// <summary>
    /// 自分のフォルダの外で保存・作成されたファイルや、外から入れたファイルの一覧と、その場で直すためのボタン。
    /// 新しいファイルと外から入れたファイルは、フォルダごとにまとめて出す。
    /// </summary>
    public class OutsideSaveWindow : EditorWindow
    {
        public enum Kind
        {
            /// <summary>Unity の中で、フォルダの外に新しく作られたファイル。</summary>
            NewFile,
            /// <summary>配布物のマテリアルが変更された。</summary>
            VendorMaterial,
            /// <summary>フォルダの外にあるシーンが保存された。</summary>
            SceneOutside,
            /// <summary>配布物のその他のファイルが変更された。</summary>
            VendorOther,
            /// <summary>外から入れたファイル（zip の素材など）。</summary>
            Imported,
        }

        [Serializable]
        class Item
        {
            /// <summary>ファイルのパス。まとめた項目では、まとめたフォルダのパス。</summary>
            public string path;
            public Kind kind;
            /// <summary>まとめた項目のファイル。1つだけの項目では空。</summary>
            public List<string> files = new List<string>();
            public string message;
            public bool done;

            public bool IsGroup => files != null && files.Count > 0;
            public IEnumerable<string> AllFiles => IsGroup ? (IEnumerable<string>)files : new[] { path };
        }

        class PendingItem
        {
            public string Path;
            public Kind Kind;
            public string HashBeforeSave;
        }

        /// <summary>自動テスト用: ウィンドウに出す内容。</summary>
        public class Shown
        {
            public string Path;
            public Kind Kind;
            public List<string> Files;
        }

        /// <summary>自動テスト用: 設定すると、ウィンドウを出す代わりにここに渡す。</summary>
        public static Action<List<Shown>> ShowOverride;

        const string IgnoredKey = "VrcGitSync.IgnoredPaths";
        static readonly List<PendingItem> Pending = new List<PendingItem>();

        [SerializeField] List<Item> _items = new List<Item>();
        Vector2 _scroll;

        // ---------------------------------------------------------------- 受け付け

        public static void Enqueue(string path, Kind kind)
        {
            if (Pending.Any(p => p.Path == path)) return;
            string hash = null;
            if (kind != Kind.NewFile && kind != Kind.Imported) hash = HashFile(ProjectPaths.ToFull(path));
            Pending.Add(new PendingItem { Path = path, Kind = kind, HashBeforeSave = hash });
            ScheduleFlush();
        }

        /// <summary>今の読み込みが終わったあとで、まとめて表示する。</summary>
        public static void ScheduleFlush()
        {
            EditorApplication.delayCall -= Flush;
            EditorApplication.delayCall += Flush;
        }

        static void Flush()
        {
            try
            {
                if (Pending.Count == 0) return;
                var valid = new List<PendingItem>();
                foreach (var p in Pending)
                {
                    // 保存しても中身が変わっていなければ知らせない（開いただけで保存対象になる場合など）
                    if (p.HashBeforeSave != null && HashFile(ProjectPaths.ToFull(p.Path)) == p.HashBeforeSave) continue;
                    if (!File.Exists(ProjectPaths.ToFull(p.Path))) continue;
                    valid.Add(p);
                }
                Pending.Clear();

                var items = new List<Item>();
                // 外から入れたファイル: いっしょに入ってきたフォルダごとにまとめる
                foreach (var g in valid.Where(p => p.Kind == Kind.Imported).GroupBy(p => SaveWatcher.ImportGroupFolder(p.Path)))
                {
                    items.Add(new Item { path = g.Key, kind = Kind.Imported, files = g.Select(p => p.Path).OrderBy(p => p, StringComparer.Ordinal).ToList() });
                }
                // Unity の中で作られたファイル: 同じフォルダのものをまとめる
                foreach (var g in valid.Where(p => p.Kind == Kind.NewFile).GroupBy(p => SaveWatcher.ParentFolder(p.Path) ?? "Assets"))
                {
                    if (g.Count() == 1) items.Add(new Item { path = g.First().Path, kind = Kind.NewFile });
                    else items.Add(new Item { path = g.Key, kind = Kind.NewFile, files = g.Select(p => p.Path).OrderBy(p => p, StringComparer.Ordinal).ToList() });
                }
                foreach (var p in valid.Where(p => p.Kind != Kind.Imported && p.Kind != Kind.NewFile))
                {
                    items.Add(new Item { path = p.Path, kind = p.Kind });
                }
                ShowItems(items);
            }
            finally
            {
                SaveWatcher.EndBatch();
            }
        }

        /// <summary>同期の処理中に見つかったもの（アップロード時の記録など）を、あとでウィンドウに出す。</summary>
        public static void ShowCaptured(IEnumerable<SaveWatcher.Captured> captured)
        {
            if (Application.isBatchMode && ShowOverride == null) return;
            ShowItems(captured.Select(c => new Item { path = c.Path, kind = c.Kind }).ToList());
        }

        static void ShowItems(List<Item> items)
        {
            if (items.Count == 0) return;
            if (ShowOverride != null)
            {
                ShowOverride(items.Select(i => new Shown { Path = i.path, Kind = i.kind, Files = i.files.ToList() }).ToList());
                return;
            }
            var w = GetWindow<OutsideSaveWindow>(true, "VRC Git Sync: 自分のフォルダの外のファイル", true);
            foreach (var item in items)
            {
                var same = w._items.FirstOrDefault(i => !i.done && i.path == item.path && i.kind == item.kind);
                if (same != null)
                {
                    foreach (var f in item.files)
                    {
                        if (!same.files.Contains(f)) same.files.Add(f);
                    }
                    continue;
                }
                w._items.Add(item);
            }
            w.minSize = new Vector2(520, 260);
            w.Show();
            w.Repaint();
        }

        public static string HashFile(string full)
        {
            try
            {
                if (!File.Exists(full)) return null;
                using (var md5 = MD5.Create())
                using (var stream = File.OpenRead(full))
                {
                    return Convert.ToBase64String(md5.ComputeHash(stream));
                }
            }
            catch
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- このセッションでは無視

        public static bool IsIgnoredThisSession(string path)
        {
            var list = SessionState.GetString(IgnoredKey, "");
            return list.Split('\n').Contains(path);
        }

        static void IgnoreThisSession(IEnumerable<string> paths)
        {
            var list = SessionState.GetString(IgnoredKey, "");
            SessionState.SetString(IgnoredKey, list + string.Concat(paths.Select(p => p + "\n")));
        }

        // ---------------------------------------------------------------- 画面

        void OnGUI()
        {
            var settings = SyncSettings.Current;
            EditorGUILayout.HelpBox(
                "次のファイルは「自分のフォルダ」（" + settings.ownFolder + "）の外にあるので、中身はもう一方のPCには届きません。",
                MessageType.Warning);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var item in _items.ToList())
            {
                DrawItem(item, settings);
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                var vendorItems = _items.Where(i => !i.done && IsVendor(i.kind)).ToList();
                if (vendorItems.Count > 1 && GUILayout.Button("配布物をすべてコピーして差し替える"))
                {
                    Defer(() => CopyAndReplace(vendorItems));
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("閉じる", GUILayout.Width(100)))
                {
                    IgnoreThisSession(_items.Where(i => !i.done).SelectMany(i => i.AllFiles));
                    _items.Clear();
                    Close();
                }
            }
        }

        void DrawItem(Item item, SyncSettings settings)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (GUILayout.Button(item.IsGroup ? item.path + "/" : item.path, EditorStyles.linkLabel))
                {
                    var obj = AssetDatabase.LoadMainAssetAtPath(item.path);
                    if (obj != null) EditorGUIUtility.PingObject(obj);
                }
                if (item.IsGroup) EditorGUILayout.LabelField(FileSummary(item), EditorStyles.wordWrappedMiniLabel);

                if (item.done)
                {
                    EditorGUILayout.LabelField(item.message, EditorStyles.wordWrappedMiniLabel);
                    return;
                }

                bool canSyncFolder = item.kind == Kind.Imported && settings.ValidateExtraFolder(item.path) == null;
                EditorGUILayout.LabelField(Describe(item, canSyncFolder), EditorStyles.wordWrappedMiniLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (item.kind == Kind.Imported)
                    {
                        if (GUILayout.Button("配布物として扱う")) TreatAsVendor(item, settings);
                        if (canSyncFolder)
                        {
                            if (GUILayout.Button("このフォルダも中身ごと同期する")) Defer(() => SyncFolder(item));
                        }
                        else
                        {
                            if (GUILayout.Button(MoveLabel(item))) Defer(() => Move(item));
                        }
                    }
                    else
                    {
                        if (IsVendor(item.kind))
                        {
                            if (GUILayout.Button("自分のフォルダにコピーして差し替える")) Defer(() => CopyAndReplace(new List<Item> { item }));
                        }
                        else
                        {
                            if (GUILayout.Button(MoveLabel(item))) Defer(() => Move(item));
                        }

                        var top = ProjectPaths.TopLevelAssetFolder(item.AllFiles.First());
                        if (top != null && GUILayout.Button(new GUIContent("このフォルダは今後警告しない", top + " の中のファイルは、保存しても知らせません。")))
                        {
                            settings.AddNoWarnFolder(top);
                            _items.RemoveAll(i => !i.done && i.AllFiles.All(f => ProjectPaths.IsUnder(f, top)));
                            CloseIfEmpty();
                        }
                    }
                    if (GUILayout.Button("無視する", GUILayout.Width(80)))
                    {
                        IgnoreThisSession(item.AllFiles);
                        _items.Remove(item);
                        CloseIfEmpty();
                    }
                }
            }
        }

        static string FileSummary(Item item)
        {
            const int shown = 5;
            var names = item.files.Select(f => f.StartsWith(item.path + "/") ? f.Substring(item.path.Length + 1) : f).ToList();
            var text = string.Join(", ", names.Take(shown));
            if (names.Count > shown) text += " ほか " + (names.Count - shown) + " 個";
            return names.Count + " 個のファイル: " + text;
        }

        static string MoveLabel(Item item) =>
            item.IsGroup ? "自分のフォルダに移動する（" + item.files.Count + " 個）" : "自分のフォルダに移動する";

        static string Describe(Item item, bool canSyncFolder)
        {
            switch (item.kind)
            {
                case Kind.Imported:
                    return "外から入れたファイルです（zip の素材など）。\n" +
                           "配布物なら「配布物として扱う」を押してください。中身は同期されず、ID（.meta）とインポート設定だけが同期されます。もう一方のPCでも、同じ場所に同じものを入れてください。\n" +
                           (canSyncFolder
                               ? "自分で作ったファイルなら「このフォルダも中身ごと同期する」を押してください（場所はそのままです）。"
                               : "自分で作ったファイルなら、自分のフォルダに移動してください。");
                case Kind.VendorMaterial:
                    return "配布物のマテリアルが変更されました。この変更は同期されません。\n" +
                           "「コピーして差し替える」と、自分のフォルダにコピーを作ってシーンなどの参照をコピーに切り替え、配布物は変更前の内容に戻します。";
                case Kind.NewFile:
                    return item.IsGroup
                        ? "自分のフォルダの外に、新しいファイルが " + item.files.Count + " 個作られました。自分のファイルなら、自分のフォルダに移動してください。"
                        : "自分のフォルダの外に新しいファイルが作られました。自分のファイルなら、自分のフォルダに移動してください。";
                case Kind.SceneOutside:
                    return "自分のフォルダの外にあるシーンが保存されました。同期したいシーンなら、自分のフォルダに移動してください。";
                default:
                    return "配布物のファイルが変更されました。この変更は同期されません。\n" +
                           "「コピーして差し替える」と、自分のフォルダにコピーを作ってシーンなどの参照をコピーに切り替え、配布物は変更前の内容に戻します。";
            }
        }

        static bool IsVendor(Kind kind) => kind == Kind.VendorMaterial || kind == Kind.VendorOther;

        /// <summary>シーンを開き直す処理などは OnGUI の中で行うと画面が崩れるので、次のフレームで実行する。</summary>
        void Defer(Action action)
        {
            EditorApplication.delayCall += () =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
                if (this != null) Repaint();
            };
        }

        void TreatAsVendor(Item item, SyncSettings settings)
        {
            if (item.path != "Assets")
            {
                settings.AddVendorFolder(item.path);
                item.message = "配布物として扱います。中身は同期されず、ID（.meta）とインポート設定だけが同期されます。" +
                               "このフォルダに外から入れたファイルは、今後は聞きません。";
            }
            else
            {
                item.message = "配布物として扱います。中身は同期されず、ID（.meta）とインポート設定だけが同期されます。";
            }
            item.done = true;
            Repaint();
        }

        void SyncFolder(Item item)
        {
            if (!SyncWindow.AddExtraFolderInteractive(item.path)) return;
            item.message = "同期するフォルダに追加しました。次に「送る」を押したときに、中身ごと記録されます。";
            item.done = true;
        }

        void CopyAndReplace(List<Item> items)
        {
            var result = AssetRelocator.CopyAndRestore(items.Select(i => i.path).ToList());
            foreach (var item in items)
            {
                item.message = result.Message;
                item.done = result.Success;
            }
            if (!result.Success) EditorUtility.DisplayDialog(SyncOperations.Title, result.Message, "OK");
        }

        void Move(Item item)
        {
            var failures = new List<string>();
            string lastMessage = null;
            foreach (var file in item.AllFiles.ToList())
            {
                // まとめた項目は、フォルダの中の並びを保ったまま移す（例: Assets/TexSet/Body/a.png → <自分のフォルダ>/TexSet/Body/a.png）
                string rel = null;
                if (item.IsGroup && file.StartsWith(item.path + "/"))
                {
                    var inner = file.Substring(item.path.Length + 1);
                    rel = item.path == "Assets" ? inner : Path.GetFileName(item.path) + "/" + inner;
                }
                lastMessage = AssetRelocator.MoveToOwnFolder(file, out bool ok, rel);
                if (!ok) failures.Add(lastMessage);
            }
            item.done = failures.Count == 0;
            item.message = !item.IsGroup ? lastMessage
                : item.done ? item.files.Count + " 個のファイルを自分のフォルダに移動しました。"
                : string.Join("\n", failures);
            if (!item.done) EditorUtility.DisplayDialog(SyncOperations.Title, item.message, "OK");
        }

        void CloseIfEmpty()
        {
            if (_items.Count == 0) Close();
            else Repaint();
        }
    }
}
