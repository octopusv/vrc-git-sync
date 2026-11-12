#!/usr/bin/env python3
"""VRC Git Sync の配布用ファイルを作る。

使い方（リポジトリのルートで）:
    python Tools~/build.py

出力:
    Build~/VrcGitSync-<version>.unitypackage      .unitypackage 版（Assets/VrcGitSync/ に入る）
    Build~/<パッケージ名>-<version>.zip            VPM 版（VCC / ALCOM が Packages/<パッケージ名>/ に入れる）
    Build~/pages/index.json, index.html            VPM のリポジトリ一覧（GitHub Pages の gh-pages ブランチに置く）

GUID は各ファイルの .meta のものをそのまま使うので、何度作っても同じ GUID になる。
.unitypackage と zip の中身は、何度作っても同じバイト列になる（ハッシュが変わらない）。
Python の標準ライブラリだけで動く（Unity は不要）。

.unitypackage には UpdateInfo.txt も入れる。git の履歴にあって今のバージョンにはないファイル（削除・移動したもの）の GUID を書いておき、
上書きでインポートしたときにツール（PackageUpdater）がそれを消す。

index.json は、gh-pages ブランチにある今の一覧（git fetch しておく）に、今のバージョンを足して作る。
zip の URL は GitHub の Release（タグ v<version>）を指すので、zip はその Release に添付する。
"""

import hashlib
import io
import json
import os
import subprocess
import sys
import tarfile
import zipfile
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
# GitHub のリポジトリと、VPM の一覧を置く場所（GitHub Pages）
GITHUB_REPO = "octopusv/vrc-git-sync"
LISTING_URL = "https://octopusv.github.io/vrc-git-sync/index.json"
LISTING_ID = "io.github.octopusv.vrc-git-sync"
LISTING_NAME = "VRC Git Sync"
LISTING_AUTHOR = "octopusv"
PAGES_BRANCH = "gh-pages"
# VPM 版の zip に入れるもの（.meta も一緒に入る）
VPM_INCLUDE = ["package.json", "README.md", "LICENSE", "Editor"]
INSTALL_DIR = "Assets/VrcGitSync"
# Assets/VrcGitSync フォルダ自体の GUID（リポジトリのルートには .meta がないので固定値）
INSTALL_DIR_GUID = "b79fe926f9604a58aba0dcc0d7ed51fa"
# パッケージに入れるもの（リポジトリのルートからの相対パス）
INCLUDE = ["Editor", "README.md", "LICENSE"]
# tar 内の更新日時（何度作っても同じファイルになるように固定）
MTIME = 1735689600  # 2025-01-01
# UpdateInfo.txt の GUID（Editor/Hooks/PackageUpdater.cs の UpdateInfoGuid と同じ）
UPDATE_INFO_PATH = INSTALL_DIR + "/UpdateInfo.txt"
UPDATE_INFO_GUID = "5d589403bad84068a57be737ab9e9691"

TEXT_META = (
    "fileFormatVersion: 2\n"
    "guid: {guid}\n"
    "TextScriptImporter:\n"
    "  externalObjects: {{}}\n"
    "  userData: \n"
    "  assetBundleName: \n"
    "  assetBundleVariant: \n"
)

FOLDER_META = (
    "fileFormatVersion: 2\n"
    "guid: {guid}\n"
    "folderAsset: yes\n"
    "DefaultImporter:\n"
    "  externalObjects: {{}}\n"
    "  userData: \n"
    "  assetBundleName: \n"
    "  assetBundleVariant: \n"
)


def read_guid(meta_path):
    with open(meta_path, encoding="utf-8") as f:
        for line in f:
            if line.startswith("guid:"):
                return line.split(":", 1)[1].strip()
    raise SystemExit("GUID が見つかりません: " + meta_path)


def collect():
    """(Unity 上のパス, 実ファイルのパス or None, .meta の中身) の一覧を返す。"""
    items = [(INSTALL_DIR, None, FOLDER_META.format(guid=INSTALL_DIR_GUID).encode("utf-8"))]

    def add(rel):
        src = os.path.join(ROOT, rel)
        meta = src + ".meta"
        if not os.path.exists(meta):
            raise SystemExit(".meta がありません: " + rel)
        with open(meta, "rb") as f:
            meta_bytes = f.read()
        asset_path = INSTALL_DIR + "/" + rel.replace(os.sep, "/")
        items.append((asset_path, None if os.path.isdir(src) else src, meta_bytes))

    for entry in INCLUDE:
        add(entry)
        top = os.path.join(ROOT, entry)
        if not os.path.isdir(top):
            continue
        for dirpath, dirnames, filenames in os.walk(top):
            # Unity が無視するもの（. で始まる、~ で終わる）は入れない
            dirnames[:] = sorted(d for d in dirnames if not d.startswith(".") and not d.endswith("~"))
            for d in dirnames:
                add(os.path.relpath(os.path.join(dirpath, d), ROOT))
            for name in sorted(filenames):
                if name.endswith(".meta") or name.startswith("."):
                    continue
                add(os.path.relpath(os.path.join(dirpath, name), ROOT))
    return items


def historical_guids():
    """git の履歴のどこかにあった、パッケージに入るファイルの GUID → Unity 上のパス。"""
    try:
        revs = subprocess.run(["git", "-C", ROOT, "rev-list", "--all"],
                              capture_output=True, text=True, encoding="utf-8", check=True).stdout.split()
    except (OSError, subprocess.CalledProcessError):
        print("注意: git の履歴を読めなかったので、古いバージョンのファイルの一覧は空になります。")
        return {}
    result = {}
    for rev in revs:
        out = subprocess.run(["git", "-C", ROOT, "grep", "-e", "^guid: ", rev, "--"] + INCLUDE + [e + ".meta" for e in INCLUDE],
                             capture_output=True, text=True, encoding="utf-8").stdout
        for line in out.splitlines():
            rest = line[len(rev) + 1:]  # "<rev>:<path>:guid: <hex>"
            path, sep, guid = rest.rpartition(":guid: ")
            if sep and path.endswith(".meta"):
                result.setdefault(guid.strip(), INSTALL_DIR + "/" + path[:-5])
    return result


def build_update_info(version, current_guids):
    removed = {g: p for g, p in historical_guids().items() if g not in current_guids}
    # テスト用: 追加で消すものを指定できる（1行に "GUID パス"）
    extra = os.environ.get("VGS_EXTRA_REMOVED")
    if extra:
        with open(extra, encoding="utf-8") as f:
            for line in f:
                parts = line.strip().split(" ", 1)
                if len(parts) == 2:
                    removed[parts[0]] = parts[1]
    lines = [
        "# VRC Git Sync の更新情報（自動生成）",
        "# 上書きでインポートしたときに、古いバージョンにだけあったファイルを消すために使います。",
        "version " + version,
    ]
    for guid, path in sorted(removed.items(), key=lambda kv: kv[1]):
        lines.append("removed {} {}".format(guid, path))
    return "\n".join(lines) + "\n", len(removed)


def add_dir(tar, name):
    # Unity はフォルダの項目がないと中身を展開しない
    info = tarfile.TarInfo(name)
    info.type = tarfile.DIRTYPE
    info.mtime = MTIME
    info.mode = 0o755
    tar.addfile(info)


def add_bytes(tar, name, data):
    info = tarfile.TarInfo(name)
    info.size = len(data)
    info.mtime = MTIME
    info.mode = 0o644
    tar.addfile(info, io.BytesIO(data))


def build_unitypackage(version, out_dir):
    out_path = os.path.join(out_dir, "VrcGitSync-{}.unitypackage".format(version))

    items = collect()

    def guid_of(meta):
        for line in meta.decode("utf-8").splitlines():
            if line.startswith("guid:"):
                return line.split(":", 1)[1].strip()
        return None

    current = {guid_of(meta) for _, _, meta in items}
    info_text, removed_count = build_update_info(version, current | {UPDATE_INFO_GUID})
    items.append((UPDATE_INFO_PATH, info_text.encode("utf-8"), TEXT_META.format(guid=UPDATE_INFO_GUID).encode("utf-8")))

    seen = set()
    tar_bytes = io.BytesIO()
    with tarfile.open(fileobj=tar_bytes, mode="w", format=tarfile.GNU_FORMAT) as tar:
        for asset_path, src, meta in items:
            guid = guid_of(meta)
            if not guid or guid in seen:
                raise SystemExit("GUID がない、または重複しています: " + asset_path)
            seen.add(guid)
            add_dir(tar, guid)
            add_bytes(tar, guid + "/pathname", asset_path.encode("utf-8"))
            add_bytes(tar, guid + "/asset.meta", meta)
            if isinstance(src, bytes):
                add_bytes(tar, guid + "/asset", src)
            elif src is not None:
                with open(src, "rb") as f:
                    add_bytes(tar, guid + "/asset", f.read())

    # gzip で圧縮する。Python の gzip モジュールはヘッダーに元のファイル名を入れるが、
    # 「.unitypackage」という名前が入っていると Unity は中身を展開しない（エラーも出さない）。
    # zlib で、ファイル名のない gzip ヘッダーにして書き出す。
    compressor = zlib.compressobj(9, zlib.DEFLATED, 31)
    with open(out_path, "wb") as f:
        f.write(compressor.compress(tar_bytes.getvalue()) + compressor.flush())

    print("作成しました: {}（{} 項目、古いバージョンから消すもの {} 個）".format(
        os.path.relpath(out_path, ROOT), len(items), removed_count))


def vpm_files():
    """VPM 版の zip に入れるファイル（リポジトリのルートからの相対パス、/ 区切り）。"""
    files = []

    def add(rel):
        if not os.path.exists(os.path.join(ROOT, rel + ".meta")):
            raise SystemExit(".meta がありません: " + rel)
        files.append(rel + ".meta")
        if os.path.isfile(os.path.join(ROOT, rel)):
            files.append(rel)

    for entry in VPM_INCLUDE:
        add(entry)
        top = os.path.join(ROOT, entry)
        if not os.path.isdir(top):
            continue
        for dirpath, dirnames, filenames in os.walk(top):
            dirnames[:] = sorted(d for d in dirnames if not d.startswith(".") and not d.endswith("~"))
            for d in dirnames:
                add(os.path.relpath(os.path.join(dirpath, d), ROOT).replace(os.sep, "/"))
            for name in sorted(filenames):
                if name.endswith(".meta") or name.startswith("."):
                    continue
                add(os.path.relpath(os.path.join(dirpath, name), ROOT).replace(os.sep, "/"))
    return sorted(set(files))


def build_vpm_zip(pkg, out_dir):
    """package.json がルートにある zip を作り、(パス, SHA-256) を返す。"""
    out_path = os.path.join(out_dir, "{}-{}.zip".format(pkg["name"], pkg["version"]))
    files = vpm_files()
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        for rel in files:
            info = zipfile.ZipInfo(rel, date_time=(2025, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            with open(os.path.join(ROOT, rel), "rb") as f:
                z.writestr(info, f.read())
    data = buf.getvalue()
    with open(out_path, "wb") as f:
        f.write(data)
    sha = hashlib.sha256(data).hexdigest()
    print("作成しました: {}（{} ファイル）".format(os.path.relpath(out_path, ROOT), len(files)))
    return out_path, sha


def current_listing():
    """gh-pages ブランチにある今の index.json。なければ None。"""
    for ref in ("origin/" + PAGES_BRANCH, PAGES_BRANCH):
        r = subprocess.run(["git", "-C", ROOT, "show", ref + ":index.json"],
                           capture_output=True, text=True, encoding="utf-8")
        if r.returncode == 0:
            return json.loads(r.stdout)
    return None


def build_pages(pkg, zip_path, sha, out_dir):
    listing = current_listing()
    if listing is None:
        print("注意: {} ブランチに index.json がないので、新しく作ります。".format(PAGES_BRANCH))
        listing = {}
    listing.update({"name": LISTING_NAME, "id": LISTING_ID, "url": LISTING_URL, "author": LISTING_AUTHOR})
    versions = listing.setdefault("packages", {}).setdefault(pkg["name"], {}).setdefault("versions", {})
    entry = dict(pkg)
    entry["url"] = "https://github.com/{}/releases/download/v{}/{}".format(
        GITHUB_REPO, pkg["version"], os.path.basename(zip_path))
    entry["zipSHA256"] = sha
    old = versions.get(pkg["version"])
    if old and old.get("zipSHA256") != sha:
        print("注意: 一覧にある {} の zip とハッシュが違うので、置き換えます。".format(pkg["version"]))
    versions[pkg["version"]] = entry

    pages = os.path.join(out_dir, "pages")
    os.makedirs(pages, exist_ok=True)
    with open(os.path.join(pages, "index.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(listing, f, ensure_ascii=False, indent=2)
        f.write("\n")
    with open(os.path.join(ROOT, "Tools~", "pages", "index.html"), encoding="utf-8") as f:
        html = f.read()
    html = html.replace("{{LISTING_URL}}", LISTING_URL)
    html = html.replace("{{GITHUB_URL}}", "https://github.com/" + GITHUB_REPO)
    with open(os.path.join(pages, "index.html"), "w", encoding="utf-8", newline="\n") as f:
        f.write(html)
    open(os.path.join(pages, ".nojekyll"), "w").close()
    print("作成しました: {}（一覧にあるバージョン: {}）".format(
        os.path.relpath(pages, ROOT), ", ".join(sorted(versions))))


def main():
    with open(os.path.join(ROOT, "package.json"), encoding="utf-8") as f:
        pkg = json.load(f)
    out_dir = os.path.join(ROOT, "Build~")
    os.makedirs(out_dir, exist_ok=True)
    build_unitypackage(pkg["version"], out_dir)
    zip_path, sha = build_vpm_zip(pkg, out_dir)
    build_pages(pkg, zip_path, sha, out_dir)


if __name__ == "__main__":
    sys.exit(main())
