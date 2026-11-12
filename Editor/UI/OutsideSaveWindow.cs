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
    /// 自分のフォルダの外で保存・作成されたファイルの一覧と、その場で直すためのボタン。
    /// </summary>
    public class OutsideSaveWindow : EditorWindow
    {
        public enum Kind
        {
            /// <summary>フォルダの外に新しく作られたファイル。</summary>
            NewFile,
            /// <summary>配布物のマテリアルが変更された。</summary>
            VendorMaterial,
            /// <summary>フォルダの外にあるシーンが保存された。</summary>
            SceneOutside,
            /// <summary>配布物のその他のファイルが変更された。</summary>
            VendorOther,
        }

        [Serializable]
        class Item
        {
            public string path;
            public Kind kind;
            public string message;
            public bool done;
        }

        class PendingItem
        {
            public string Path;
            public Kind Kind;
            public string HashBeforeSave;
        }

        const string IgnoredKey = "VrcGitSync.IgnoredPaths";
        static readonly List<PendingItem> Pending = new List<PendingItem>();

        [SerializeField] List<Item> _items = new List<Item>();
        Vector2 _scroll;

        // ---------------------------------------------------------------- 受け付け

        public static void Enqueue(string path, Kind kind)
        {
            if (Pending.Any(p => p.Path == path)) return;
            string hash = null;
            if (kind != Kind.NewFile) hash = HashFile(ProjectPaths.ToFull(path));
            Pending.Add(new PendingItem { Path = path, Kind = kind, HashBeforeSave = hash });
            EditorApplication.delayCall -= Flush;
            EditorApplication.delayCall += Flush;
        }

        static void Flush()
        {
            if (Pending.Count == 0) return;
            var items = new List<Item>();
            foreach (var p in Pending)
            {
                // 保存しても中身が変わっていなければ知らせない（開いただけで保存対象になる場合など）
                if (p.HashBeforeSave != null && HashFile(ProjectPaths.ToFull(p.Path)) == p.HashBeforeSave) continue;
                if (!File.Exists(ProjectPaths.ToFull(p.Path))) continue;
                items.Add(new Item { path = p.Path, kind = p.Kind });
            }
            Pending.Clear();
            ShowItems(items);
        }

        /// <summary>同期の処理中に見つかったもの（アップロード時の記録など）を、あとでウィンドウに出す。</summary>
        public static void ShowCaptured(IEnumerable<SaveWatcher.Captured> captured)
        {
            if (Application.isBatchMode) return;
            ShowItems(captured.Select(c => new Item { path = c.Path, kind = c.Kind }).ToList());
        }

        static void ShowItems(List<Item> items)
        {
            if (items.Count == 0) return;
            var w = GetWindow<OutsideSaveWindow>(true, "VRC Git Sync: 同期されない場所への保存", true);
            foreach (var item in items)
            {
                if (w._items.Any(i => i.path == item.path && !i.done)) continue;
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

        static void IgnoreThisSession(string path)
        {
            var list = SessionState.GetString(IgnoredKey, "");
            SessionState.SetString(IgnoredKey, list + path + "\n");
        }

        // ---------------------------------------------------------------- 画面

        void OnGUI()
        {
            var settings = SyncSettings.Current;
            EditorGUILayout.HelpBox(
                "次のファイルは「自分のフォルダ」（" + settings.ownFolder + "）の外にあるので、もう一方のPCには届きません。",
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
                    foreach (var i in _items.Where(i => !i.done)) IgnoreThisSession(i.path);
                    _items.Clear();
                    Close();
                }
            }
        }

        void DrawItem(Item item, SyncSettings settings)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(item.path, EditorStyles.linkLabel))
                    {
                        var obj = AssetDatabase.LoadMainAssetAtPath(item.path);
                        if (obj != null) EditorGUIUtility.PingObject(obj);
                    }
                }

                if (item.done)
                {
                    EditorGUILayout.LabelField(item.message, EditorStyles.wordWrappedMiniLabel);
                    return;
                }

                EditorGUILayout.LabelField(Describe(item.kind), EditorStyles.wordWrappedMiniLabel);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (IsVendor(item.kind))
                    {
                        if (GUILayout.Button("自分のフォルダにコピーして差し替える")) Defer(() => CopyAndReplace(new List<Item> { item }));
                    }
                    else
                    {
                        if (GUILayout.Button("自分のフォルダに移動する")) Defer(() => Move(item));
                    }

                    var top = ProjectPaths.TopLevelAssetFolder(item.path);
                    if (top != null && GUILayout.Button(new GUIContent("このフォルダは今後警告しない", top + " の中のファイルは、保存しても知らせません。")))
                    {
                        settings.AddNoWarnFolder(top);
                        _items.RemoveAll(i => !i.done && ProjectPaths.IsUnder(i.path, top));
                        CloseIfEmpty();
                    }
                    if (GUILayout.Button("無視する", GUILayout.Width(80)))
                    {
                        IgnoreThisSession(item.path);
                        _items.Remove(item);
                        CloseIfEmpty();
                    }
                }
            }
        }

        static string Describe(Kind kind)
        {
            switch (kind)
            {
                case Kind.VendorMaterial:
                    return "配布物のマテリアルが変更されました。この変更は同期されません。\n" +
                           "「コピーして差し替える」と、自分のフォルダにコピーを作ってシーンなどの参照をコピーに切り替え、配布物は変更前の内容に戻します。";
                case Kind.NewFile:
                    return "自分のフォルダの外に新しいファイルが作られました。自分のファイルなら、自分のフォルダに移動してください。";
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
            item.message = AssetRelocator.MoveToOwnFolder(item.path, out bool ok);
            item.done = ok;
            if (!ok) EditorUtility.DisplayDialog(SyncOperations.Title, item.message, "OK");
        }

        void CloseIfEmpty()
        {
            if (_items.Count == 0) Close();
            else Repaint();
        }
    }
}
