using System;

namespace VrcGitSync
{
    /// <summary>
    /// 別アセンブリ（VRChat SDK 連携）から状態を知らせてもらうための窓口。
    /// </summary>
    public static class SyncHooks
    {
        /// <summary>VRChat SDK がビルド・アップロード中なら true を返す関数。SDK 連携が設定する。</summary>
        public static Func<bool> IsExternalBuildRunning;

        public static bool ExternalBuildRunning
        {
            get
            {
                try
                {
                    return IsExternalBuildRunning != null && IsExternalBuildRunning();
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
