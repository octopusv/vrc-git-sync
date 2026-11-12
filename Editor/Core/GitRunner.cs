using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace VrcGitSync
{
    public readonly struct GitResult
    {
        public readonly int ExitCode;
        public readonly string Output;
        public readonly string Error;

        public GitResult(int exitCode, string output, string error)
        {
            ExitCode = exitCode;
            Output = output ?? "";
            Error = error ?? "";
        }

        public bool Ok => ExitCode == 0;

        /// <summary>ユーザーに見せるためのメッセージ（エラー出力を優先）。</summary>
        public string Message
        {
            get
            {
                var e = Error.Trim();
                var o = Output.Trim();
                if (e.Length > 0 && o.Length > 0) return e + "\n" + o;
                return e.Length > 0 ? e : o;
            }
        }

        public static GitResult Fail(string message) => new GitResult(-1, "", message);
    }

    /// <summary>
    /// git コマンドを実行する。Unity API を使わないので、どのスレッドからでも呼べる。
    /// </summary>
    public static class GitRunner
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        static string _gitPath;

        public static string GitPath
        {
            get
            {
                if (_gitPath == null) _gitPath = FindGit();
                return _gitPath;
            }
        }

        /// <summary>git の場所を探し直す（インストール直後など）。</summary>
        public static void ResetGitPath()
        {
            _gitPath = null;
        }

        static string FindGit()
        {
            bool windows = Platform.IsWindows;
            string exe = windows ? "git.exe" : "git";

            // Mac では Homebrew の git を優先する。
            // Unity を Finder や Unity Hub から起動すると PATH に Homebrew の場所が入らず、/usr/bin/git（Apple 標準）が先に見つかるため。
            if (Platform.IsMac)
            {
                foreach (var dir in Platform.MacExtraPaths)
                {
                    var candidate = Path.Combine(dir, exe);
                    if (File.Exists(candidate)) return candidate;
                }
            }

            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                try
                {
                    var trimmed = dir.Trim().Trim('"');
                    if (trimmed.Length == 0) continue;
                    var candidate = Path.Combine(trimmed, exe);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // 不正な PATH 要素は無視
                }
            }

            if (windows)
            {
                string[] candidates =
                {
                    @"C:\Program Files\Git\cmd\git.exe",
                    @"C:\Program Files (x86)\Git\cmd\git.exe",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Git\cmd\git.exe"),
                };
                foreach (var c in candidates)
                {
                    if (File.Exists(c)) return c;
                }
            }
            else
            {
                string[] candidates = { "/usr/bin/git", "/usr/local/bin/git", "/opt/homebrew/bin/git" };
                foreach (var c in candidates)
                {
                    if (File.Exists(c)) return c;
                }
            }
            return "git";
        }

        /// <summary>
        /// git を実行して終了を待つ。args はコマンドライン文字列（パスなどは Quote で囲む）。
        /// </summary>
        public static GitResult Run(string workDir, string args, int timeoutMs = 120000)
        {
            var psi = new ProcessStartInfo(GitPath, "-c core.quotepath=false -c color.ui=never " + args)
            {
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Utf8,
                StandardErrorEncoding = Utf8,
            };
            // 見えないコンソールでパスワード入力待ちになって固まるのを防ぐ。
            // GitHub へのログインは Git Credential Manager（または gh）が受け持つ。
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            // Mac では git が git-lfs や gh などを PATH から探すので、Homebrew の場所を足しておく
            if (!Platform.IsWindows) psi.EnvironmentVariables["PATH"] = ChildPath();

            try
            {
                using (var p = Process.Start(psi))
                {
                    if (p == null) return GitResult.Fail("git を起動できませんでした。");
                    p.StandardInput.Close();
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { /* 既に終了している */ }
                        return new GitResult(-2, SafeResult(outTask), "git の処理が時間内に終わりませんでした（git " + FirstWord(args) + "）。");
                    }
                    p.WaitForExit();
                    return new GitResult(p.ExitCode, SafeResult(outTask), SafeResult(errTask));
                }
            }
            catch (Exception e)
            {
                return GitResult.Fail(Platform.GitMissingHelp + "\n(" + e.Message + ")");
            }
        }

        /// <summary>Mac / Linux で git に渡す PATH（git のある場所と Homebrew の場所を先頭に足す）。</summary>
        static string ChildPath()
        {
            var parts = new List<string>();
            void Add(string dir)
            {
                if (!string.IsNullOrEmpty(dir) && !parts.Contains(dir)) parts.Add(dir);
            }

            Add(Path.GetDirectoryName(GitPath));
            foreach (var dir in Platform.MacExtraPaths) Add(dir);
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':')) Add(dir);
            foreach (var dir in new[] { "/usr/bin", "/bin", "/usr/sbin", "/sbin" }) Add(dir);
            return string.Join(":", parts);
        }

        static string SafeResult(System.Threading.Tasks.Task<string> t)
        {
            try
            {
                return t.Wait(5000) ? t.Result : "";
            }
            catch
            {
                return "";
            }
        }

        static string FirstWord(string args)
        {
            var trimmed = args.TrimStart();
            int sp = trimmed.IndexOf(' ');
            return sp < 0 ? trimmed : trimmed.Substring(0, sp);
        }

        /// <summary>コマンドライン引数として安全に渡せるように引用符で囲む。</summary>
        public static string Quote(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            bool needs = false;
            foreach (char c in arg)
            {
                // * ? [ は引用符で囲まないと、git（MinGW）が起動時にワイルドカードとして展開することがある
                if (char.IsWhiteSpace(c) || "\"'&|<>^*?[]".IndexOf(c) >= 0)
                {
                    needs = true;
                    break;
                }
            }
            if (!needs) return arg;

            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(c);
                }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }
    }
}
