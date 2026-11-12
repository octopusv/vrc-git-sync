using System.Runtime.InteropServices;

namespace VrcGitSync
{
    /// <summary>OS ごとの違い（git の入れ方、GitHub へのログイン方法の案内など）。どのスレッドからでも呼べる。</summary>
    public static class Platform
    {
        public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        public static bool IsMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        /// <summary>Unity を Finder や Unity Hub から起動するとシェルの PATH が引き継がれないので、ここも探す。</summary>
        public static readonly string[] MacExtraPaths = { "/opt/homebrew/bin", "/usr/local/bin" };

        public static string GitMissingHelp => IsMac
            ? "git が見つかりません。ターミナルで次を実行してから、Unity を再起動してください（Homebrew が必要です）。\n\nbrew install git git-lfs"
            : "git が見つかりません。Git for Windows をインストールしてから、Unity を再起動してください。\n（Git LFS も一緒に入ります）";

        public static string LfsMissingHelp => IsMac
            ? "Git LFS が見つかりません。ターミナルで次を実行してから、Unity を再起動してください。\n\nbrew install git-lfs"
            : "Git LFS が見つかりません。Git for Windows を入れ直すか、Git LFS をインストールしてください。";

        public static string GitDownloadLabel => IsMac ? "Homebrew のページを開く" : "Git for Windows のダウンロードページを開く";

        public static string GitDownloadUrl => IsMac ? "https://brew.sh/" : "https://git-scm.com/downloads/win";

        /// <summary>GitHub へのログインに失敗したときの案内。</summary>
        public static string AuthHelp => IsMac
            ? "GitHub にログインできませんでした。Mac では、ターミナルで次のどちらかを実行してから、もう一度試してください。\n\n" +
              "・Git Credential Manager を入れる（ブラウザでログインできます）:\n  brew install --cask git-credential-manager\n\n" +
              "・GitHub CLI を使う:\n  brew install gh\n  gh auth login\n  gh auth setup-git"
            : "GitHub にログインできませんでした。ログイン用のウィンドウが開かない場合は、ターミナルでプロジェクトのフォルダに移動して git push を一度実行し、ログインしてください。";

        /// <summary>git の出力が「ログインできなかった」ことを示しているか。</summary>
        public static bool IsAuthError(string gitMessage)
        {
            if (string.IsNullOrEmpty(gitMessage)) return false;
            return gitMessage.Contains("could not read Username")
                || gitMessage.Contains("Authentication failed")
                || gitMessage.Contains("terminal prompts disabled")
                || gitMessage.Contains("Permission denied (publickey)")
                || gitMessage.Contains("Invalid username or password");
        }
    }
}
