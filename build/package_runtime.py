#!/usr/bin/env python3
"""校验固定 ZZZ Runtime 并生成 Sparxie 便携 ZIP。"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from collections.abc import Iterable
from pathlib import Path

SCRIPT_PATH = Path(__file__).resolve()
BUILD_DIRECTORY = SCRIPT_PATH.parent
REPOSITORY_ROOT = BUILD_DIRECTORY.parent
MANIFEST_PATH = BUILD_DIRECTORY / "zzz-runtime.json"
RUNTIME_RELEASE_BASE_URL = "https://github.com/ShadowLemoon/ZZZ-TouchRuntime/releases/download"


def fail(message: str) -> None:
    raise ValueError(message)


def remove_path(path: Path) -> None:
    if path.is_symlink() or path.is_file():
        path.unlink()
    elif path.exists():
        shutil.rmtree(path)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def load_manifest() -> tuple[str, str, str, list[str]]:
    try:
        manifest = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
    except FileNotFoundError:
        fail(f"Runtime manifest 不存在: {MANIFEST_PATH}")
    except json.JSONDecodeError as error:
        fail(f"Runtime manifest 非法 JSON: {error}")

    runtime_version = manifest.get("runtimeVersion")
    release_asset = manifest.get("releaseAsset")
    expected_hash = manifest.get("sha256")
    files = manifest.get("files")

    if not isinstance(runtime_version, str) or not runtime_version.strip():
        fail("runtimeVersion 为空")
    if not isinstance(release_asset, str) or not release_asset.strip():
        fail("releaseAsset 为空")
    if not isinstance(expected_hash, str) or len(expected_hash) != 64:
        fail("sha256 非法")
    try:
        int(expected_hash, 16)
    except ValueError:
        fail("sha256 非法")

    if not isinstance(files, list) or not files:
        fail("files 为空")
    if not all(isinstance(file, str) for file in files):
        fail("files 必须全部为字符串")
    if len(set(files)) != len(files):
        fail("files 存在重复项")

    for file in files:
        if not file or "/" in file or "\\" in file or ".." in file:
            fail(f"非法文件名: {file}")
    if "ZZZTouchCore.dll" not in files:
        fail("files 必须包含 ZZZTouchCore.dll")

    return runtime_version, release_asset, expected_hash.casefold(), files


def download_runtime(destination_path: Path) -> None:
    runtime_version, release_asset, _, _ = load_manifest()
    destination_path = destination_path.resolve()
    if destination_path.is_dir():
        fail(f"Runtime 下载输出路径是目录: {destination_path}")

    release_url = "/".join((
        RUNTIME_RELEASE_BASE_URL,
        urllib.parse.quote(runtime_version, safe=""),
        urllib.parse.quote(release_asset, safe=""),
    ))
    temporary_path = destination_path.with_name(f"{destination_path.name}.part")
    destination_path.parent.mkdir(parents=True, exist_ok=True)

    for attempt in range(1, 4):
        try:
            remove_path(temporary_path)
            request = urllib.request.Request(release_url, headers={"User-Agent": "Sparxie-runtime-fetch"})
            with urllib.request.urlopen(request) as response, temporary_path.open("wb") as target:
                shutil.copyfileobj(response, target)
            temporary_path.replace(destination_path)
            print(f"ZZZ Runtime 已下载：{release_url}")
            return
        except (OSError, urllib.error.URLError) as error:
            remove_path(temporary_path)
            if attempt == 3:
                fail(f"Runtime 下载失败（已重试 3 次）: {release_url}; {error}")
            time.sleep(2 * attempt)


def prepare_runtime(archive_path: Path, destination_path: Path) -> None:
    runtime_version, _, expected_hash, expected_files = load_manifest()
    archive_path = archive_path.resolve()
    if not archive_path.is_file():
        fail(f"Runtime 压缩包不存在: {archive_path}")

    actual_hash = sha256(archive_path)
    if actual_hash != expected_hash:
        fail(f"SHA-256 不符: 期望 {expected_hash} 实际 {actual_hash}")

    try:
        with zipfile.ZipFile(archive_path) as archive:
            expected_entries: dict[str, zipfile.ZipInfo] = {}
            for entry in archive.infolist():
                name = entry.filename.replace("\\", "/")
                if name in expected_files:
                    if name in expected_entries:
                        fail(f"Runtime 压缩包包含重复文件: {name}")
                    expected_entries[name] = entry

            for file in expected_files:
                if file not in expected_entries:
                    fail(f"Runtime 压缩包缺少 {file}")

            remove_path(destination_path)
            destination_path.mkdir(parents=True, exist_ok=True)
            for file in expected_files:
                with archive.open(expected_entries[file]) as source, (destination_path / file).open("wb") as target:
                    shutil.copyfileobj(source, target)
    except zipfile.BadZipFile:
        fail(f"Runtime 压缩包无效: {archive_path}")

    print(f"ZZZ Runtime 已校验并准备完成：{runtime_version}")


def require_file(path: Path, description: str) -> Path:
    if not path.is_file():
        fail(f"{description}缺失: {path}")
    return path


def require_directory(path: Path, description: str) -> Path:
    if not path.is_dir():
        fail(f"{description}缺失: {path}")
    return path


def copy_publish_outputs(staging_path: Path) -> None:
    for name in ("Sparxie.Launcher", "Sparxie.Broker", "Sparxie.SessionHost"):
        publish_path = REPOSITORY_ROOT / "src" / name / "bin" / "Release" / "net10.0-windows" / "win-x64" / "publish"
        require_directory(publish_path, "publish 目录")
        shutil.copytree(publish_path, staging_path, dirs_exist_ok=True)


def remove_pdbs(staging_path: Path) -> None:
    for file in staging_path.rglob("*"):
        if file.is_file() and file.suffix.casefold() == ".pdb":
            file.unlink()

    if any(file.is_file() and file.suffix.casefold() == ".pdb" for file in staging_path.rglob("*")):
        fail("staging 仍含 PDB")


def write_archive(staging_path: Path, archive_path: Path) -> None:
    remove_path(archive_path)
    with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        files: Iterable[Path] = sorted(
            (path for path in staging_path.rglob("*") if path.is_file()),
            key=lambda path: path.relative_to(staging_path).as_posix(),
        )
        for file in files:
            archive.write(file, file.relative_to(staging_path).as_posix())


def package_portable(runtime_directory: Path, hoyo_touch_core_path: Path) -> None:
    _, _, _, runtime_files = load_manifest()
    runtime_directory = require_directory(runtime_directory.resolve(), "已校验 Runtime staging 目录")
    hoyo_touch_core_path = require_file(hoyo_touch_core_path.resolve(), "HoyoTouchCore.dll")

    staging_path = REPOSITORY_ROOT / "artifacts" / "Sparxie"
    archive_path = REPOSITORY_ROOT / "artifacts" / "Sparxie-portable.zip"
    staging_path.parent.mkdir(parents=True, exist_ok=True)
    remove_path(staging_path)
    staging_path.mkdir()

    copy_publish_outputs(staging_path)
    shutil.copy2(hoyo_touch_core_path, staging_path / "HoyoTouchCore.dll")

    for file in runtime_files:
        runtime_file = require_file(runtime_directory / file, "已校验 Runtime staging 文件")
        shutil.copy2(runtime_file, staging_path / file)

    for source, destination in (
        (REPOSITORY_ROOT / "LICENSE", "LICENSE"),
        (REPOSITORY_ROOT / "THIRD-PARTY-NOTICES.md", "THIRD-PARTY-NOTICES.md"),
        (REPOSITORY_ROOT / "native" / "HoyoTouchCore" / "upstream" / "LICENSE", "UPSTREAM-LICENSE-MIT.txt"),
        (REPOSITORY_ROOT / "native" / "HoyoTouchCore" / "adapter" / "licenses" / "inih-LICENSE.txt", "inih-LICENSE.txt"),
        (REPOSITORY_ROOT / "docs" / "RUNTIME-NOTICE.md", "RUNTIME-NOTICE.md"),
    ):
        shutil.copy2(require_file(source, "发布文件"), staging_path / destination)

    required_files = [
        "Sparxie.Launcher.exe",
        "Sparxie.Broker.exe",
        "Sparxie.SessionHost.exe",
        "HoyoTouchCore.dll",
        "LICENSE",
        "THIRD-PARTY-NOTICES.md",
        "UPSTREAM-LICENSE-MIT.txt",
        "inih-LICENSE.txt",
        "RUNTIME-NOTICE.md",
        *runtime_files,
    ]
    for file in required_files:
        require_file(staging_path / file, "staging 文件")

    remove_pdbs(staging_path)
    write_archive(staging_path, archive_path)
    print(f"完整便携 ZIP 已生成：{archive_path}")


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="校验固定 ZZZ Runtime 并生成 Sparxie 便携 ZIP。")
    commands = parser.add_subparsers(dest="command", required=True)

    download = commands.add_parser("download-runtime", help="下载固定 Release Runtime ZIP。")
    download.add_argument("--destination", type=Path, required=True, help="固定 Release ZIP 输出路径。")

    prepare = commands.add_parser("prepare-runtime", help="校验 Runtime ZIP 并准备 staging。")
    prepare.add_argument("--archive", type=Path, required=True, help="固定 Release 下载的 ZIP 路径。")
    prepare.add_argument("--destination", type=Path, required=True, help="已校验 Runtime staging 输出目录。")

    package = commands.add_parser("package", help="生成包含已校验 Runtime 的便携 ZIP。")
    package.add_argument("--runtime-directory", type=Path, required=True, help="已校验 Runtime staging 目录。")
    package.add_argument("--hoyo-touch-core", type=Path, required=True, help="HoyoTouchCore.dll 路径。")

    return parser.parse_args()


def main() -> int:
    try:
        arguments = parse_arguments()
        if arguments.command == "download-runtime":
            download_runtime(arguments.destination)
        elif arguments.command == "prepare-runtime":
            prepare_runtime(arguments.archive, arguments.destination)
        elif arguments.command == "package":
            package_portable(arguments.runtime_directory, arguments.hoyo_touch_core)
        else:
            fail(f"未知命令: {arguments.command}")
    except (OSError, ValueError) as error:
        print(f"错误：{error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
