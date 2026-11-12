using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VrcGitSync
{
    /// <summary>状態表示用のスナップショット。</summary>
    public struct SyncStatus
    {
        public bool Valid;
        public string Branch;
        /// <summary>まだ記録していない、自分のフォルダや設定の変更。</summary>
        public int ChangedFiles;
        /// <summary>まだ記録していない、配布物の .meta（新しく入れた配布物の ID、インポート設定の変更）。</summary>
        public int VendorMetaChanges;
        /// <summary>手元の ID が GitHub と違う配布物。</summary>
        public int GuidMismatches;
        public int Ahead;
        public int Behind;
        public bool HasUpstream;
        public bool HasRemote;
    }

    public struct UploadTag
    {
        public string Name;
        public string Date;
        public string Subject;
    }

    /// <summary>
    /// プロジェクトのリポジトリに対する git 操作。Unity API を使わないので、どのスレッドからでも呼べる。
    /// </summary>
    public static class Git
    {
        public const string Remote = "origin";
        public const int ShortTimeout = 30 * 1000;
        public const int NetworkTimeout = 5 * 60 * 1000;
        public const int LongTimeout = 30 * 60 * 1000;

        /// <summary>この大きさを超えるファイルは LFS に入れる（GitHub は 100MB を超えるファイルを受け付けない）。</summary>
        public const long LargeFileThreshold = 50L * 1024 * 1024;

        static string Root => ProjectPaths.Root;

        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        public static GitResult Run(string args, int timeoutMs = 120000) => GitRunner.Run(Root, args, timeoutMs);

        static string Q(string s) => GitRunner.Quote(s);

        // ---------------------------------------------------------------- 環境

        public static string GitVersion()
        {
            var r = GitRunner.Run(Root, "--version", ShortTimeout);
            return r.Ok ? r.Output.Trim() : null;
        }

        public static string LfsVersion()
        {
            var r = GitRunner.Run(Root, "lfs version", ShortTimeout);
            return r.Ok ? r.Output.Trim() : null;
        }

        public static string ConfigGet(string key)
        {
            var r = Run("config --get " + key, ShortTimeout);
            return r.Ok ? r.Output.Trim() : "";
        }

        public static GitResult ConfigSetGlobal(string key, string value) => Run("config --global " + key + " " + Q(value), ShortTimeout);

        /// <summary>github.com に使われるログイン方法（credential.helper）。なければ空文字。</summary>
        public static string GitHubCredentialHelper()
        {
            var r = Run("config --get-urlmatch credential.helper https://github.com", ShortTimeout);
            return r.Ok ? r.Output.Trim() : "";
        }

        // ---------------------------------------------------------------- リポジトリ

        public static bool IsRepo => Directory.Exists(Root + "/.git") || File.Exists(Root + "/.git");

        public static GitResult Init()
        {
            var r = Run("init -b main", ShortTimeout);
            if (r.Ok) return r;
            r = Run("init", ShortTimeout);
            if (r.Ok) Run("symbolic-ref HEAD refs/heads/main", ShortTimeout);
            return r;
        }

        public static GitResult LfsInstallLocal() => Run("lfs install --local", ShortTimeout);

        public static string RemoteUrl()
        {
            var r = Run("remote get-url " + Remote, ShortTimeout);
            return r.Ok ? r.Output.Trim() : "";
        }

        public static GitResult SetRemote(string url)
        {
            return string.IsNullOrEmpty(RemoteUrl())
                ? Run("remote add " + Remote + " " + Q(url), ShortTimeout)
                : Run("remote set-url " + Remote + " " + Q(url), ShortTimeout);
        }

        public static bool HasCommits() => Run("rev-parse --verify --quiet HEAD", ShortTimeout).Ok;

        public static string CurrentBranch()
        {
            var r = Run("symbolic-ref --short -q HEAD", ShortTimeout);
            return r.Ok ? r.Output.Trim() : "";
        }

        public static bool HasUpstream() => Run("rev-parse --abbrev-ref --symbolic-full-name @{u}", ShortTimeout).Ok;

        public static bool RemoteBranchExists(string branch)
        {
            if (string.IsNullOrEmpty(branch)) return false;
            return Run("rev-parse --verify --quiet " + Q("refs/remotes/" + Remote + "/" + branch), ShortTimeout).Ok;
        }

        public static GitResult SetUpstream(string branch) =>
            Run("branch --set-upstream-to=" + Q(Remote + "/" + branch), ShortTimeout);

        /// <summary>リモートにブランチが1つでもあるか（ネットワークに接続する）。null は接続失敗。</summary>
        public static bool? RemoteHasData(out GitResult result)
        {
            result = Run("ls-remote --heads " + Remote, NetworkTimeout);
            if (!result.Ok) return null;
            return result.Output.Trim().Length > 0;
        }

        /// <summary>リモートのデフォルトブランチ名（取得後に呼ぶ）。</summary>
        public static string RemoteDefaultBranch()
        {
            var r = Run("ls-remote --symref " + Remote + " HEAD", NetworkTimeout);
            if (r.Ok)
            {
                foreach (var line in r.Output.Split('\n'))
                {
                    // "ref: refs/heads/main\tHEAD"
                    if (line.StartsWith("ref: refs/heads/", StringComparison.Ordinal))
                    {
                        var rest = line.Substring("ref: refs/heads/".Length);
                        int tab = rest.IndexOf('\t');
                        return tab >= 0 ? rest.Substring(0, tab) : rest.Trim();
                    }
                }
            }
            if (RemoteBranchExists("main")) return "main";
            if (RemoteBranchExists("master")) return "master";
            return null;
        }

        public static string ShowFile(string revision, string path)
        {
            var r = Run("show " + Q(revision + ":" + path), ShortTimeout);
            return r.Ok ? r.Output : null;
        }

        /// <summary>revision の folder 以下にある .meta ファイルと、その GUID の一覧。</summary>
        public static List<KeyValuePair<string, string>> MetaGuids(string revision, string folder)
        {
            var list = new List<KeyValuePair<string, string>>();
            var r = Run("grep -e \"^guid: \" " + Q(revision) + " -- " + Q(folder + "/*.meta") + " " + Q(folder + ".meta"), NetworkTimeout);
            if (!r.Ok) return list; // 1件もなければ終了コード 1
            string prefix = revision + ":";
            foreach (var raw in r.Output.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                line = line.Substring(prefix.Length);
                int sep = line.IndexOf(":guid: ", StringComparison.Ordinal);
                if (sep < 0) continue;
                list.Add(new KeyValuePair<string, string>(line.Substring(0, sep), line.Substring(sep + ":guid: ".Length).Trim()));
            }
            return list;
        }

        // ---------------------------------------------------------------- 状態

        public struct StatusEntry
        {
            public char X;
            public char Y;
            public string Path;
        }

        /// <summary>変更されたファイル（未コミット）の一覧。未追跡のファイルは1つずつ並べる。</summary>
        public static List<StatusEntry> StatusEntries()
        {
            var list = new List<StatusEntry>();
            var r = Run("--no-optional-locks status --porcelain=v1 -z -uall", NetworkTimeout);
            if (!r.Ok) return list;
            var parts = r.Output.Split('\0');
            for (int i = 0; i < parts.Length; i++)
            {
                var e = parts[i];
                if (e.Length < 4) continue;
                list.Add(new StatusEntry { X = e[0], Y = e[1], Path = e.Substring(3) });
                if (e[0] == 'R' || e[0] == 'C') i++; // 移動元のパスを読み飛ばす
            }
            return list;
        }

        /// <summary>手元の ID が GitHub（HEAD）と違う配布物の .meta。</summary>
        public static List<string> GuidMismatchedVendorMetas(string[] syncedFolders)
        {
            var list = new List<string>();
            var head = MetaIndex.HeadMap();
            foreach (var e in StatusEntries())
            {
                if (e.X == 'D' || e.Y == 'D' || e.X == '?') continue;
                if (MetaIndex.IsVendorMeta(e.Path, syncedFolders) && MetaIndex.DiffersFromHead(e.Path, head)) list.Add(e.Path);
            }
            return list;
        }

        public static string HeadId()
        {
            var r = Run("rev-parse --verify --quiet HEAD", ShortTimeout);
            return r.Ok ? r.Output.Trim() : "";
        }

        public static void AheadBehind(out int ahead, out int behind, out bool hasUpstream)
        {
            ahead = 0;
            behind = 0;
            hasUpstream = false;
            if (!HasCommits()) return;
            if (!HasUpstream())
            {
                var c = Run("rev-list --count HEAD", ShortTimeout);
                int.TryParse(c.Output.Trim(), out ahead);
                return;
            }
            hasUpstream = true;
            var r = Run("rev-list --left-right --count HEAD...@{u}", ShortTimeout);
            if (!r.Ok) return;
            var parts = r.Output.Trim().Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                int.TryParse(parts[0], out ahead);
                int.TryParse(parts[1], out behind);
            }
        }

        public static SyncStatus ReadStatus(string[] syncedFolders)
        {
            var s = new SyncStatus();
            if (!IsRepo) return s;
            s.Valid = true;
            s.Branch = CurrentBranch();
            var head = MetaIndex.HeadMap();
            foreach (var e in StatusEntries())
            {
                if (!MetaIndex.IsVendorMeta(e.Path, syncedFolders))
                {
                    s.ChangedFiles++;
                    continue;
                }
                // 配布物がこのPCにないので Unity が消した .meta は数えない（GitHub の方は消さない）
                if (e.X == 'D' || e.Y == 'D') continue;
                if (e.X != '?' && MetaIndex.DiffersFromHead(e.Path, head)) s.GuidMismatches++;
                else s.VendorMetaChanges++;
            }
            s.HasRemote = !string.IsNullOrEmpty(RemoteUrl());
            AheadBehind(out s.Ahead, out s.Behind, out s.HasUpstream);
            return s;
        }

        public static List<UploadTag> RecentUploads(int count)
        {
            var list = new List<UploadTag>();
            var r = Run("for-each-ref --sort=-creatordate --count=" + count + " " +
                        Q("--format=%(refname:short)%09%(creatordate:format:%Y-%m-%d %H:%M)%09%(subject)") +
                        " refs/tags/upload/", ShortTimeout);
            if (!r.Ok) return list;
            foreach (var line in r.Output.Split('\n'))
            {
                var cols = line.TrimEnd('\r').Split('\t');
                if (cols.Length < 3) continue;
                list.Add(new UploadTag { Name = cols[0], Date = cols[1], Subject = cols[2] });
            }
            return list;
        }

        // ---------------------------------------------------------------- 記録

        /// <summary>
        /// 変更をステージしてコミットする。変更がなければ何もしない（committed = false）。
        /// 自分のフォルダと Assets の外は、追加・変更・削除をすべて記録する。
        /// includeVendorMetas なら配布物の .meta も記録する。ただし削除は記録せず（配布物がないPCで Unity が消すため）、
        /// GitHub と ID が違うものも記録しない（GitHub の ID が正しい）。
        /// 大きいファイルは自動で LFS の対象にする。
        /// </summary>
        public static GitResult CommitAll(string message, string[] syncedFolders, bool includeVendorMetas,
            out bool committed, out List<string> movedToLfs, out int vendorMetas)
        {
            committed = false;
            movedToLfs = new List<string>();
            vendorMetas = 0;

            var add = Run("add -A -- . " + Q(":(exclude)Assets"), LongTimeout);
            if (!add.Ok) return add;
            foreach (var own in syncedFolders.SelectMany(f => new[] { f, f + ".meta" }))
            {
                var r = Run("--literal-pathspecs add -A -- " + Q(own), LongTimeout);
                if (!r.Ok && !r.Message.Contains("did not match any files")) return r;
            }

            if (includeVendorMetas)
            {
                var r = Run("add --ignore-removal -- Assets", LongTimeout);
                if (!r.Ok) return r;

                var head = MetaIndex.HeadMap();
                var staged = Run("diff --cached --name-only -z -- Assets", NetworkTimeout);
                var revert = new List<string>();
                foreach (var path in staged.Output.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!MetaIndex.IsVendorMeta(path, syncedFolders)) continue;
                    if (MetaIndex.DiffersFromHead(path, head)) revert.Add(path);
                    else vendorMetas++;
                }
                if (revert.Count > 0)
                {
                    var rr = WithPathspecFile(revert, f => Run("--literal-pathspecs reset -q HEAD --pathspec-from-file=" + Q(f) + " --pathspec-file-nul", NetworkTimeout));
                    if (!rr.Ok) return rr;
                }
            }

            var diff = Run("diff --cached --quiet", NetworkTimeout);
            if (diff.ExitCode == 0) return add; // 変更なし
            if (diff.ExitCode != 1) return diff;

            var lfs = MoveLargeFilesToLfs(movedToLfs);
            if (!lfs.Ok) return lfs;

            var c = WithMessageFile(message, file => Run("commit -q -F " + Q(file), NetworkTimeout));
            if (c.Ok) committed = true;
            return c;
        }

        /// <summary>ステージ済みのファイルのうち、LFS 対象でない大きいファイルを LFS に移す。</summary>
        static GitResult MoveLargeFilesToLfs(List<string> moved)
        {
            var r = Run("diff --cached --name-only -z --diff-filter=AM", NetworkTimeout);
            if (!r.Ok) return r;

            var large = new List<string>();
            foreach (var path in r.Output.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var info = new FileInfo(Path.Combine(Root, path));
                    if (info.Exists && info.Length > LargeFileThreshold) large.Add(path);
                }
                catch
                {
                    // 読めないファイルは無視
                }
            }
            if (large.Count == 0) return r;

            var notLfs = new List<string>();
            foreach (var path in large)
            {
                var attr = Run("check-attr -z filter -- " + Q(path), ShortTimeout);
                // 出力: path\0filter\0value\0
                var parts = attr.Output.Split('\0');
                if (!(parts.Length >= 3 && parts[2] == "lfs")) notLfs.Add(path);
            }
            if (notLfs.Count == 0) return r;

            RepoFiles.AddLargeFilePatterns(notLfs);
            foreach (var path in notLfs)
            {
                // .gitattributes を変えたので、ステージ済みの内容と「LFS 化した内容」が食い違う。直後に入れ直すので -f で外す
                var rm = Run("rm --cached -f -q -- " + Q(path), ShortTimeout);
                if (!rm.Ok) return rm;
                var add = Run("add -- " + Q(path), LongTimeout);
                if (!add.Ok) return add;
                moved.Add(path);
            }
            return Run("add -- .gitattributes", ShortTimeout);
        }

        /// <summary>パスの一覧を NUL 区切りのファイルにして渡す（コマンドラインの長さの制限を避ける）。</summary>
        static GitResult WithPathspecFile(IEnumerable<string> paths, Func<string, GitResult> action)
        {
            string file = null;
            try
            {
                file = Path.GetTempFileName();
                File.WriteAllText(file, string.Join("\0", paths) + "\0", Utf8);
                return action(file);
            }
            catch (Exception e)
            {
                return GitResult.Fail("一時ファイルを書き込めませんでした: " + e.Message);
            }
            finally
            {
                if (file != null)
                {
                    try { File.Delete(file); } catch { /* 無視 */ }
                }
            }
        }

        /// <summary>指定したファイルを HEAD の内容に戻す（配布物の .meta を GitHub の ID にそろえるとき）。</summary>
        public static GitResult CheckoutFromHead(IList<string> paths)
        {
            if (paths.Count == 0) return new GitResult(0, "", "");
            return WithPathspecFile(paths, f => Run("--literal-pathspecs checkout HEAD --pathspec-from-file=" + Q(f) + " --pathspec-file-nul", LongTimeout));
        }

        /// <summary>
        /// 取り込む前に、GitHub 側で追加された配布物の .meta と同じ場所にある、このPCの未記録の .meta を消す。
        /// （残っていると git が取り込みを拒否する。消した分は GitHub の ID で置き換わる）
        /// </summary>
        public static int RemoveUntrackedIncomingVendorMetas(string[] syncedFolders)
        {
            var r = Run("diff --name-only -z --diff-filter=A HEAD...@{u} -- Assets", NetworkTimeout);
            if (!r.Ok) return 0;
            var head = MetaIndex.HeadMap();
            int removed = 0;
            foreach (var path in r.Output.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!MetaIndex.IsVendorMeta(path, syncedFolders) || head.ContainsKey(path)) continue;
                var full = Path.Combine(Root, path);
                try
                {
                    if (File.Exists(full))
                    {
                        File.Delete(full);
                        removed++;
                    }
                }
                catch
                {
                    // 消せなければ git の取り込みが失敗してエラーになる
                }
            }
            return removed;
        }

        /// <summary>
        /// 追加で同期していたフォルダを、記録の対象から外す（このPCのファイルは残る）。
        /// .meta は配布物と同じように ID として同期し続ける。
        /// </summary>
        public static GitResult UntrackFolder(string folder)
        {
            var r = Run("--literal-pathspecs rm -r --cached -q --ignore-unmatch -- " + Q(folder), LongTimeout);
            if (!r.Ok) return r;
            var add = Run("--literal-pathspecs add --ignore-removal -- " + Q(folder), LongTimeout);
            if (!add.Ok && !add.Message.Contains("did not match any files")) return add;
            return r;
        }

        public static GitResult Tag(string name, string message)
        {
            return WithMessageFile(message, file => Run("tag -a " + Q(name) + " -F " + Q(file), ShortTimeout));
        }

        static GitResult WithMessageFile(string message, Func<string, GitResult> action)
        {
            string file = null;
            try
            {
                file = Path.GetTempFileName();
                File.WriteAllText(file, message.Replace("\r\n", "\n"), Utf8);
                return action(file);
            }
            catch (Exception e)
            {
                return GitResult.Fail("メッセージを書き込めませんでした: " + e.Message);
            }
            finally
            {
                if (file != null)
                {
                    try { File.Delete(file); } catch { /* 無視 */ }
                }
            }
        }

        // ---------------------------------------------------------------- 送受信

        public static GitResult Fetch() => Run("fetch --prune " + Remote, NetworkTimeout);

        public static GitResult Push() => Run("push -u " + Remote + " HEAD --follow-tags", LongTimeout);

        public static bool IsRejectedPush(GitResult r)
        {
            var m = r.Message;
            return m.Contains("[rejected]") || m.Contains("non-fast-forward") || m.Contains("fetch first");
        }

        public static GitResult MergeUpstream() => Run("merge --no-edit @{u}", LongTimeout);

        public static GitResult MergeAbort() => Run("merge --abort", NetworkTimeout);

        public static List<string> ConflictedFiles()
        {
            var r = Run("diff --name-only --diff-filter=U -z", ShortTimeout);
            if (!r.Ok) return new List<string>();
            return r.Output.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        /// <summary>衝突したファイルを、すべて片方の内容にそろえてマージを完了する。</summary>
        public static GitResult ResolveConflicts(IEnumerable<string> files, bool useOurs)
        {
            string side = useOurs ? "--ours" : "--theirs";
            string stage = useOurs ? "2" : "3";
            foreach (var path in files)
            {
                // 選んだ側にファイルがあるか（削除されていないか）を調べる
                var ls = Run("ls-files -u -z -- " + Q(path), ShortTimeout);
                bool exists = false;
                foreach (var entry in ls.Output.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    // "mode sha stage\tpath"
                    int tab = entry.IndexOf('\t');
                    var head = tab >= 0 ? entry.Substring(0, tab) : entry;
                    var cols = head.Split(' ');
                    if (cols.Length >= 3 && cols[2] == stage) exists = true;
                }

                GitResult r;
                if (exists)
                {
                    r = Run("checkout " + side + " -- " + Q(path), LongTimeout);
                    if (!r.Ok) return r;
                    r = Run("add -- " + Q(path), LongTimeout);
                }
                else
                {
                    r = Run("rm -q -- " + Q(path), ShortTimeout);
                }
                if (!r.Ok) return r;
            }
            return Run("commit -q --no-edit", NetworkTimeout);
        }

        /// <summary>2台目のPC用: リモートのブランチをこのPCに展開する（追跡中のファイルは上書き）。</summary>
        public static GitResult CheckoutRemoteBranch(string branch)
        {
            return Run("checkout -f -B " + Q(branch) + " --track " + Q(Remote + "/" + branch), LongTimeout);
        }

        /// <summary>git add -A で記録されるファイルの一覧（実際には記録しない）。</summary>
        public static List<string> DryRunAdd()
        {
            var list = new List<string>();
            var r = Run("add -A --dry-run", LongTimeout);
            if (!r.Ok) return list;
            foreach (var raw in r.Output.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                // "add 'path'"
                int first = line.IndexOf('\'');
                int last = line.LastIndexOf('\'');
                if (first >= 0 && last > first) list.Add(line.Substring(first + 1, last - first - 1));
            }
            return list;
        }

        /// <summary>タグ名に使えない文字を置き換える。</summary>
        public static string SanitizeRefPart(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "avatar";
            var sb = new StringBuilder();
            foreach (char c in s.Trim())
            {
                if (char.IsControl(c) || char.IsWhiteSpace(c) || "~^:?*[\\/".IndexOf(c) >= 0) sb.Append('_');
                else sb.Append(c);
            }
            var result = sb.ToString();
            while (result.Contains("..")) result = result.Replace("..", "_");
            result = result.Replace("@{", "_");
            result = result.Trim('.');
            if (result.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) result = result.Substring(0, result.Length - 5) + "_lock";
            return result.Length == 0 ? "avatar" : result;
        }
    }
}
