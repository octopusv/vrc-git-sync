using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace VrcGitSync
{
    /// <summary>
    /// プロジェクトごとの設定。ProjectSettings/VrcGitSync.json に保存され、2台のPCで共有される。
    /// </summary>
    [Serializable]
    public class SyncSettings
    {
        public const string RelativePath = "ProjectSettings/VrcGitSync.json";

        /// <summary>NDMF などがビルド中に一時ファイルを置くフォルダ。最初から警告しない。</summary>
        public static readonly string[] DefaultNoWarnFolders =
        {
            "Assets/ZZZ_GeneratedAssets",
        };

        public int version = 1;

        /// <summary>同期する自分のフォルダ（例: "Assets/_Mine"）。Assets 直下の1階層。</summary>
        public string ownFolder = "";

        public bool checkOnStartup = true;
        public bool checkOnQuit = true;
        public bool commitOnUpload = true;
        public bool warnOutsideSaves = true;

        /// <summary>保存されても警告しないフォルダ。</summary>
        public List<string> noWarnFolders = new List<string>(DefaultNoWarnFolders);

        /// <summary>
        /// 自分のフォルダ以外に、中身ごと同期するフォルダ（ツールが決まった場所に書き出すものなど）。
        /// 例: "Assets/Suzuryg/FaceEmo/Generated"
        /// </summary>
        public List<string> extraFolders = new List<string>();

        static SyncSettings _cached;

        public static string FullPath => ProjectPaths.ToFull(RelativePath);

        public static bool FileExists => File.Exists(FullPath);

        public bool IsConfigured => !string.IsNullOrEmpty(ownFolder);

        /// <summary>中身ごと同期するフォルダ（自分のフォルダと、追加したフォルダ）。</summary>
        public string[] SyncedFolders
        {
            get
            {
                var list = new List<string>();
                if (!string.IsNullOrEmpty(ownFolder)) list.Add(ownFolder);
                foreach (var f in extraFolders)
                {
                    var n = ProjectPaths.Normalize(f);
                    if (n.Length > 0 && !list.Contains(n)) list.Add(n);
                }
                return list.ToArray();
            }
        }

        /// <summary>path が、中身ごと同期するフォルダのどれかの中にあるか。</summary>
        public bool IsSynced(string path) => IsUnderAny(path, SyncedFolders);

        public static bool IsUnderAny(string path, string[] folders)
        {
            foreach (var f in folders)
            {
                if (ProjectPaths.IsUnder(path, f)) return true;
            }
            return false;
        }

        /// <summary>追加で同期するフォルダとして使えるか。使えなければ理由を返す。</summary>
        public string ValidateExtraFolder(string folder)
        {
            folder = ProjectPaths.Normalize(folder);
            if (!folder.StartsWith("Assets/", StringComparison.Ordinal)) return "Assets の中のフォルダを選んでください。";
            if (IsSynced(folder)) return "このフォルダは、すでに同期されています。";
            foreach (var f in SyncedFolders)
            {
                if (ProjectPaths.IsUnder(f, folder)) return "同期しているフォルダ（" + f + "）を含むフォルダは選べません。中のフォルダを選んでください。";
            }
            if (!Directory.Exists(ProjectPaths.ToFull(folder))) return "フォルダが見つかりません: " + folder;
            return null;
        }

        public void AddExtraFolder(string folder)
        {
            folder = ProjectPaths.Normalize(folder);
            if (extraFolders.Contains(folder)) return;
            extraFolders.Add(folder);
            Save();
        }

        public void RemoveExtraFolder(string folder)
        {
            folder = ProjectPaths.Normalize(folder);
            extraFolders.RemoveAll(f => string.Equals(ProjectPaths.Normalize(f), folder, StringComparison.OrdinalIgnoreCase));
            Save();
        }

        /// <summary>現在の設定。ファイルがなければ初期値（未設定）を返す。</summary>
        public static SyncSettings Current
        {
            get
            {
                if (_cached == null) _cached = LoadFromDisk();
                return _cached;
            }
        }

        /// <summary>受け取りなどでファイルが変わったときに呼ぶ。</summary>
        public static void Reload()
        {
            _cached = null;
        }

        static SyncSettings LoadFromDisk()
        {
            try
            {
                if (FileExists)
                {
                    var loaded = JsonUtility.FromJson<SyncSettings>(File.ReadAllText(FullPath, Encoding.UTF8));
                    if (loaded != null)
                    {
                        if (loaded.noWarnFolders == null) loaded.noWarnFolders = new List<string>();
                        if (loaded.extraFolders == null) loaded.extraFolders = new List<string>();
                        loaded.ownFolder = ProjectPaths.Normalize(loaded.ownFolder);
                        return loaded;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VRC Git Sync] 設定ファイルを読み込めませんでした: " + e.Message);
            }
            return new SyncSettings();
        }

        public static SyncSettings FromJson(string json)
        {
            try
            {
                var s = JsonUtility.FromJson<SyncSettings>(json);
                if (s != null) s.ownFolder = ProjectPaths.Normalize(s.ownFolder);
                return s;
            }
            catch
            {
                return null;
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FullPath));
            var json = JsonUtility.ToJson(this, true).Replace("\r\n", "\n") + "\n";
            File.WriteAllText(FullPath, json, new UTF8Encoding(false));
            _cached = this;
        }

        public bool IsNoWarn(string assetPath)
        {
            foreach (var f in noWarnFolders)
            {
                if (ProjectPaths.IsUnder(assetPath, f)) return true;
            }
            return false;
        }

        public void AddNoWarnFolder(string folder)
        {
            folder = ProjectPaths.Normalize(folder);
            if (string.IsNullOrEmpty(folder)) return;
            foreach (var f in noWarnFolders)
            {
                if (string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)) return;
            }
            noWarnFolders.Add(folder);
            Save();
        }
    }
}
