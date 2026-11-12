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
                    if (Application.isBatchMode) return false;
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

        static void OnWillCreateAsset(string path)
        {
            try
            {
                if (!Active) return;
                if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - 5);
                // 拡張子のないものはフォルダとみなして無視する
                if (string.IsNullOrEmpty(System.IO.Path.GetExtension(path))) return;
                Consider(path, true);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
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
