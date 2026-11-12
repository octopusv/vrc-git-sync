using System;
using System.Text.RegularExpressions;

namespace VrcGitSync
{
    /// <summary>
    /// Unity が PC ごとに違う値を書き込む行。この行だけが違うファイルは、変更として扱わない（記録しない・数えない）。
    /// 同期しても意味がなく、そのまま記録すると2台の間で値が行ったり来たりして、何もしていなくても「送る」「受け取る」が出るため。
    /// </summary>
    public static class MachineNoise
    {
        const string ProjectSettingsPath = "ProjectSettings/ProjectSettings.asset";

        // シーンの環境光の反射色。スカイボックスから GPU で計算されるので、Mac と Windows で小数点の下の方がずれる
        static readonly Regex SceneLine = new Regex(@"^\s*m_IndirectSpecularColor:");

        // ビルド先ごとのグラフィックスの設定。Unity が開いた PC に合わせて書き換える（アバターのアップロードには使われない）
        static readonly Regex ProjectSettingsLine = new Regex(@"^\s*(m_GraphicsJobs|m_Automatic):\s*\d+\s*$");

        public static bool MayHaveNoise(string path) =>
            path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) || path == ProjectSettingsPath;

        public static bool IsNoiseLine(string path, string line)
        {
            if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) return SceneLine.IsMatch(line);
            if (path == ProjectSettingsPath) return ProjectSettingsLine.IsMatch(line);
            return false;
        }

        /// <summary>1つのファイルの git diff -U0 の出力が、PC ごとの値の行の書き換えだけか。</summary>
        public static bool IsNoiseOnlyDiff(string path, string diff)
        {
            if (!MayHaveNoise(path) || string.IsNullOrEmpty(diff)) return false;
            bool inHunk = false;
            int added = 0, removed = 0;
            foreach (var raw in diff.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    inHunk = true;
                    continue;
                }
                // 最初の @@ より前は見出し（diff --git、index、--- a/…、+++ b/…）。
                // 中身の行は「---- !u!1 &…」のように - で始まることがあるので、見出しとは @@ の前後で区別する
                if (!inHunk)
                {
                    if (line.StartsWith("Binary files", StringComparison.Ordinal)) return false;
                    continue;
                }
                if (line.Length == 0 || line[0] == '\\') continue;
                if (line[0] != '+' && line[0] != '-') continue;
                if (!IsNoiseLine(path, line.Substring(1))) return false;
                if (line[0] == '+') added++;
                else removed++;
            }
            return added > 0 && added == removed;
        }
    }
}
