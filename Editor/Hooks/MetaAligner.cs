using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VrcGitSync
{
    /// <summary>
    /// zip で配られた素材などを Assets に置いた瞬間に、Unity が新しく作った .meta の ID を、
    /// GitHub に記録されている ID（もう一方のPCの ID）に置き換える。
    /// これで、もう一方のPCで設定したマテリアルなどの参照がそのままつながる。
    /// </summary>
    class MetaAligner : AssetPostprocessor
    {
        static readonly HashSet<string> Fresh = new HashSet<string>();
        static bool _scheduled;

        void OnPreprocessAsset()
        {
            // .meta がない状態で読み込まれた = Unity がその場で新しい ID を作った
            if (assetImporter != null && assetImporter.importSettingsMissing)
            {
                lock (Fresh) Fresh.Add(assetPath);
            }
        }

        static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            lock (Fresh)
            {
                if (Fresh.Count == 0) return;
            }
            if (_scheduled) return;
            _scheduled = true;
            EditorApplication.delayCall += AlignFresh;
        }

        static void AlignFresh()
        {
            _scheduled = false;
            List<string> paths;
            lock (Fresh)
            {
                paths = Fresh.ToList();
                Fresh.Clear();
            }
            try
            {
                if (!SyncOperations.IsReady || SyncOperations.Busy) return;
                var settings = SyncSettings.Current;
                var head = MetaIndex.HeadMap();
                var metas = new List<string>();
                foreach (var path in paths)
                {
                    if (!path.StartsWith("Assets/") || settings.IsSynced(path)) continue;
                    if (head.TryGetValue(path + ".meta", out var guid) && AssetDatabase.AssetPathToGUID(path) != guid)
                    {
                        metas.Add(path + ".meta");
                    }
                }
                if (metas.Count == 0) return;

                var r = Git.CheckoutFromHead(metas);
                if (!r.Ok)
                {
                    Debug.LogWarning("[VRC Git Sync] 配布物の ID をそろえられませんでした: " + r.Message);
                    return;
                }
                AssetDatabase.Refresh();
                var message = metas.Count + " 個の配布物の ID を、もう一方のPCとそろえました。";
                SyncOperations.AppendLog(message);
                Debug.Log("[VRC Git Sync] " + message);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
