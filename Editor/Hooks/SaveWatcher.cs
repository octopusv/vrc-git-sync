using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VrcGitSync
{
    /// <summary>
    /// 自分のフォルダの外でファイルが保存・作成されたら知らせる。
    /// （その場所のファイルはもう一方のPCに届かないため）
    /// </summary>
    public class SaveWatcher : AssetModificationProcessor
    {
        /// <summary>Capture 中に見つかった、自分のフォルダの外への保存。</summary>
        public class Captured
        {
            public string Path;
            public OutsideSaveWindow.Kind Kind;
            public string HashBeforeSave;

            public bool IsVendor => Kind == OutsideSaveWindow.Kind.VendorMaterial || Kind == OutsideSaveWindow.Kind.VendorOther;
        }

        static bool _importingPackage;
        static int _suppress;
        static List<Captured> _capture;

        [InitializeOnLoadMethod]
        static void Init()
        {
            AssetDatabase.importPackageStarted += _ => _importingPackage = true;
            AssetDatabase.importPackageCompleted += _ => _importingPackage = false;
            AssetDatabase.importPackageCancelled += _ => _importingPackage = false;
            AssetDatabase.importPackageFailed += (_, __) => _importingPackage = false;
        }

        /// <summary>このツール自身がファイルを触る間は警告しない。</summary>
        public static IDisposable Suppress()
        {
            _suppress++;
            return new Scope(() => _suppress--);
        }

        /// <summary>
        /// 「送る」などの前の保存で、自分のフォルダの外に保存されたものを集める（ウィンドウは出さない）。
        /// 同期の処理中でも集める。終わったら ChangedOnly で、本当に中身が変わったものだけにする。
        /// </summary>
        public static IDisposable Capture(out List<Captured> captured)
        {
            captured = new List<Captured>();
            var previous = _capture;
            _capture = captured;
            return new Scope(() => _capture = previous);
        }

        /// <summary>保存しても中身が変わらなかったもの（開いただけで保存対象になったものなど）を除く。</summary>
        public static List<Captured> ChangedOnly(List<Captured> captured)
        {
            return captured.Where(c =>
            {
                var full = ProjectPaths.ToFull(c.Path);
                if (!System.IO.File.Exists(full)) return false;
                return c.HashBeforeSave == null || OutsideSaveWindow.HashFile(full) != c.HashBeforeSave;
            }).ToList();
        }

        sealed class Scope : IDisposable
        {
            readonly Action _onDispose;
            bool _disposed;

            public Scope(Action onDispose)
            {
                _onDispose = onDispose;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _onDispose();
            }
        }

        static bool Active
        {
            get
            {
                if (_suppress > 0 || _importingPackage) return false;
                if (EditorApplication.isPlayingOrWillChangePlaymode) return false;
                if (SyncHooks.ExternalBuildRunning) return false;
                if (_capture == null)
                {
                    if (Application.isBatchMode && !ActiveInBatchMode) return false;
                    if (SyncOperations.Busy) return false;
                }
                var s = SyncSettings.Current;
                return s.IsConfigured && s.warnOutsideSaves;
            }
        }

        static string[] OnWillSaveAssets(string[] paths)
        {
            try
            {
                if (Active)
                {
                    foreach (var p in paths) Consider(p, false);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            return paths;
        }

        // ---------------------------------------------------------------- 新しいファイル
        //
        // Unity の中で作られたファイル（作成メニュー、AssetDatabase.CreateAsset、シーンの別名保存、CopyAsset）は、
        // 先にファイル自体のパスで OnWillCreateAsset が呼ばれ（まだファイルはない）、そのあと .meta のパスで呼ばれる。
        // 外から入れたファイル（エクスプローラーでのコピー、ドラッグ＆ドロップ、zip の展開）は、
        // ファイルがもうある状態で、.meta のパスでだけ呼ばれる。これで2つを見分ける。
        // （ツールが画像などを直接書き出して読み込ませた場合も、外から入れたファイルと同じに見える）

        /// <summary>このまとまり（1回の読み込み）の間に、Unity の中で作られたファイル。</summary>
        static readonly HashSet<string> CreatedInUnity = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>このまとまりの間に新しくできたフォルダ（外から入れたフォルダのまとまりを見つけるため）。</summary>
        static readonly HashSet<string> NewFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>自動テスト用: バッチモードでも警告の処理を動かす。</summary>
        public static bool ActiveInBatchMode;

        static void OnWillCreateAsset(string path)
        {
            try
            {
                if (!Active) return;
                path = ProjectPaths.Normalize(path);
                bool metaCall = path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
                if (metaCall) path = path.Substring(0, path.Length - 5);
                var full = ProjectPaths.ToFull(path);

                if (System.IO.Directory.Exists(full))
                {
                    NewFolders.Add(path);
                    OutsideSaveWindow.ScheduleFlush();
                    return;
                }
                // 拡張子のないものはフォルダとみなして無視する
                if (string.IsNullOrEmpty(System.IO.Path.GetExtension(path))) return;

                if (!metaCall)
                {
                    CreatedInUnity.Add(path);
                    OutsideSaveWindow.ScheduleFlush();
                    Consider(path, true);
                }
                else if (CreatedInUnity.Contains(path))
                {
                    // ファイル自体のパスで受け付け済み
                }
                else if (System.IO.File.Exists(full))
                {
                    ConsiderImported(path);
                }
                else
                {
                    Consider(path, true);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        /// <summary>外から入れたファイル。配布物か自分のファイルかを、フォルダごとにまとめて聞く。</summary>
        static void ConsiderImported(string path)
        {
            if (!path.StartsWith("Assets/", StringComparison.Ordinal)) return;
            // 同期の処理中（受け取りなど）に増えたファイルは対象にしない
            if (_capture != null) return;
            var s = SyncSettings.Current;
            if (s.IsSynced(path) || s.IsNoWarn(path) || s.IsVendorFolder(path)) return;
            if (OutsideSaveWindow.IsIgnoredThisSession(path)) return;
            OutsideSaveWindow.Enqueue(path, OutsideSaveWindow.Kind.Imported);
        }

        /// <summary>
        /// 外から入れたファイルをまとめるフォルダ。いっしょに新しくできたフォルダをたどって、いちばん上のものを返す
        /// （例: zip を展開した "Assets/TexSet/Body/a.png" → "Assets/TexSet"）。既存のフォルダに入れた場合は、そのフォルダ。
        /// </summary>
        public static string ImportGroupFolder(string path)
        {
            var folder = ParentFolder(path);
            while (folder != null && folder != "Assets" && NewFolders.Contains(folder))
            {
                var parent = ParentFolder(folder);
                if (parent == null || parent == "Assets" || !NewFolders.Contains(parent)) break;
                folder = parent;
            }
            return folder ?? "Assets";
        }

        public static string ParentFolder(string path)
        {
            var i = path.LastIndexOf('/');
            return i > 0 ? path.Substring(0, i) : null;
        }

        /// <summary>1回の読み込みのまとまりが終わったとき（OutsideSaveWindow が呼ぶ）。</summary>
        public static void EndBatch()
        {
            CreatedInUnity.Clear();
            NewFolders.Clear();
        }

        static void Consider(string path, bool creating)
        {
            path = ProjectPaths.Normalize(path);
            if (!path.StartsWith("Assets/", StringComparison.Ordinal)) return;
            if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return;

            var s = SyncSettings.Current;
            if (s.IsSynced(path)) return;
            if (s.IsNoWarn(path)) return;
            if (OutsideSaveWindow.IsIgnoredThisSession(path)) return;

            var full = ProjectPaths.ToFull(path);
            if (System.IO.Directory.Exists(full)) return;
            bool exists = System.IO.File.Exists(full);

            OutsideSaveWindow.Kind kind;
            if (creating || !exists) kind = OutsideSaveWindow.Kind.NewFile;
            else if (path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) kind = OutsideSaveWindow.Kind.VendorMaterial;
            else if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) kind = OutsideSaveWindow.Kind.SceneOutside;
            else kind = OutsideSaveWindow.Kind.VendorOther;

            // 配布物のファイルは、保存で上書きされる前の内容を控えておく（「コピーして差し替え」で元に戻すため）
            if (kind == OutsideSaveWindow.Kind.VendorMaterial || kind == OutsideSaveWindow.Kind.VendorOther) OriginalCache.Remember(path);

            if (_capture != null)
            {
                if (_capture.Any(c => c.Path == path)) return;
                _capture.Add(new Captured
                {
                    Path = path,
                    Kind = kind,
                    HashBeforeSave = kind == OutsideSaveWindow.Kind.NewFile ? null : OutsideSaveWindow.HashFile(full),
                });
                return;
            }
            OutsideSaveWindow.Enqueue(path, kind);
        }
    }
}
