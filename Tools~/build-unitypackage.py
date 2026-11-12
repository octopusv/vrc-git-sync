#!/usr/bin/env python3
"""VRC Git Sync の .unitypackage を作る。

使い方（リポジトリのルートで）:
    python Tools~/build-unitypackage.py

出力:
    Build~/VrcGitSync-<package.json の version>.unitypackage

インポートすると Assets/VrcGitSync/ に入る。
GUID は各ファイルの .meta のものをそのまま使うので、何度作っても同じ GUID になる。
Python の標準ライブラリだけで動く（Unity は不要）。

UpdateInfo.txt も一緒に入れる。git の履歴にあって今のバージョンにはないファイル（削除・移動したもの）の GUID を書いておき、
上書きでインポートしたときにツール（PackageUpdater）がそれを消す。
"""

import io
import json
import os
import subprocess
import sys
import tarfile
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
INSTALL_DIR = "Assets/VrcGitSync"
# Assets/VrcGitSync フォルダ自体の GUID（リポジトリのルートには .meta がないので固定値）
INSTALL_DIR_GUID = "b79fe926f9604a58aba0dcc0d7ed51fa"
# パッケージに入れるもの（リポジトリのルートからの相対パス）
INCLUDE = ["Editor", "README.md"]
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


def main():
    with open(os.path.join(ROOT, "package.json"), encoding="utf-8") as f:
        version = json.load(f)["version"]

    out_dir = os.path.join(ROOT, "Build~")
    os.makedirs(out_dir, exist_ok=True)
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


if __name__ == "__main__":
    sys.exit(main())
