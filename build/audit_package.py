#!/usr/bin/env python3
"""Sparxie 打包审计：原生 DLL 的导出必须和托管侧 P/Invoke 声明对得上，互不相同的原生 DLL 不能是同一份文件。

用法：
  python build/audit_package.py --repo <仓库根> --app <app-<版本> 目录>
  python build/audit_package.py --repo <仓库根> --zip <Sparxie-desktop-*.zip>

检查项：
  1. src 下每个 [DllImport]/[LibraryImport] 指向的 DLL 如果在包里，它声明的每个入口点都必须是该 DLL 的导出；
  2. Sparxie 自己的原生 DLL（HoyoTouchCore、ZZZTouchCore、ZZZTouchRuntime，仓库里有 native/EndfieldCore 时再加 EndfieldCore、EndfieldRuntime）必须都在包里，
     并且彼此的 SHA-256 不同（打包时拿错文件覆盖会让两个名字指向同一份内容）。
任何一项失败都返回非零退出码。
"""
from __future__ import annotations

import argparse
import hashlib
import io
import re
import struct
import sys
import zipfile
from pathlib import Path

OWN_NATIVE = ("HoyoTouchCore.dll", "ZZZTouchCore.dll", "ZZZTouchRuntime.dll")
ENDFIELD_NATIVE = ("EndfieldCore.dll", "EndfieldRuntime.dll")


def own_native(repo: Path) -> tuple[str, ...]:
    """随包分发的 Sparxie 原生 DLL；仓库里有 native/EndfieldCore 时，终末地的两个也必须在包里。"""
    return OWN_NATIVE + (ENDFIELD_NATIVE if (repo / "native" / "EndfieldCore").is_dir() else ())

_ATTRIBUTE = re.compile(
    r"\[\s*(?:System\.Runtime\.InteropServices\.)?(?P<kind>DllImport|LibraryImport)\(\s*\"(?P<dll>[^\"]+)\"(?P<args>[^\]]*)\]"
    r"(?P<tail>(?:\s*\[[^\]]*\])*[^;{]*?)\b(?P<name>[A-Za-z_]\w*)\s*\(",
    re.S,
)
_ENTRY_POINT = re.compile(r"EntryPoint\s*=\s*\"([^\"]+)\"")


def parse_imports(source: str) -> list[tuple[str, str]]:
    """返回源码里每个 P/Invoke 声明的 (DLL 文件名, 入口点名)。"""
    found = []
    for m in _ATTRIBUTE.finditer(source):
        dll = m.group("dll")
        if not dll.lower().endswith(".dll"):
            dll += ".dll"
        entry = _ENTRY_POINT.search(m.group("args"))
        found.append((dll, entry.group(1) if entry else m.group("name")))
    return found


def pe_exports(data: bytes) -> set[str] | None:
    """读 PE 导出表里按名字导出的函数；不是 PE 或没有导出表返回 None。"""
    if len(data) < 0x40 or data[:2] != b"MZ":
        return None
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        return None
    sections_count = struct.unpack_from("<H", data, pe + 6)[0]
    optional_size = struct.unpack_from("<H", data, pe + 20)[0]
    optional = pe + 24
    magic = struct.unpack_from("<H", data, optional)[0]
    directory = optional + (112 if magic == 0x20B else 96)
    export_rva, export_size = struct.unpack_from("<II", data, directory)
    if export_rva == 0 or export_size == 0:
        return set()
    table = optional + optional_size
    sections = [struct.unpack_from("<IIII", data, table + 40 * i + 8) for i in range(sections_count)]

    def to_offset(rva: int) -> int:
        for virtual_size, virtual_address, raw_size, raw_pointer in sections:
            if virtual_address <= rva < virtual_address + max(virtual_size, raw_size):
                return rva - virtual_address + raw_pointer
        raise ValueError(f"RVA 0x{rva:x} 不在任何节里")

    base = to_offset(export_rva)
    name_count, _functions, names_rva = struct.unpack_from("<III", data, base + 24)
    names_at = to_offset(names_rva)
    out = set()
    for i in range(name_count):
        name_rva = struct.unpack_from("<I", data, names_at + 4 * i)[0]
        start = to_offset(name_rva)
        out.add(data[start:data.index(b"\0", start)].decode("ascii"))
    return out


def audit(sources: dict[str, str], files: dict[str, bytes], required: tuple[str, ...] = OWN_NATIVE) -> list[str]:
    """sources：相对路径→C# 源码；files：包内文件名（app 目录下，小写比较）→内容；required：必须随包的原生 DLL。返回问题列表，空表示通过。"""
    problems = []
    by_name = {name.lower(): data for name, data in files.items()}
    shown = {name.lower(): name for name in files}

    for own in required:
        if own.lower() not in by_name:
            problems.append(f"包里缺少 {own}")

    hashes: dict[str, list[str]] = {}
    for own in required:
        if own.lower() in by_name:
            hashes.setdefault(hashlib.sha256(by_name[own.lower()]).hexdigest(), []).append(own)
    for digest, names in hashes.items():
        if len(names) > 1:
            problems.append(f"{' 和 '.join(names)} 是同一份文件（SHA-256 {digest[:12]}），打包时有一个被另一个覆盖")

    declared: dict[str, set[tuple[str, str]]] = {}
    for path, text in sources.items():
        for dll, entry in parse_imports(text):
            declared.setdefault(dll.lower(), set()).add((entry, path))

    for dll, entries in sorted(declared.items()):
        data = by_name.get(dll)
        if data is None:
            continue  # 系统 DLL 或不随包分发
        exports = pe_exports(data)
        if exports is None:
            problems.append(f"{shown[dll]} 不是有效的 PE 文件")
            continue
        for entry, path in sorted(entries):
            if entry not in exports:
                problems.append(f"{shown[dll]} 没有导出 {entry}（{path} 里声明了它）")
    return problems


def _read_sources(repo: Path) -> dict[str, str]:
    return {
        str(p.relative_to(repo)).replace("\\", "/"): p.read_text(encoding="utf-8-sig", errors="replace")
        for p in sorted((repo / "src").rglob("*.cs"))
        if "obj" not in p.parts and "bin" not in p.parts
    }


def _files_from_dir(app: Path) -> dict[str, bytes]:
    return {p.name: p.read_bytes() for p in app.iterdir() if p.is_file() and p.suffix.lower() == ".dll"}


def _files_from_zip(path: Path) -> dict[str, bytes]:
    out = {}
    with zipfile.ZipFile(path) as z:
        for info in z.infolist():
            parts = info.filename.replace("\\", "/").split("/")
            if len(parts) == 3 and parts[1].startswith("app-") and parts[2].lower().endswith(".dll"):
                out[parts[2]] = z.read(info)
    return out


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--repo", required=True, type=Path)
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--app", type=Path)
    group.add_argument("--zip", type=Path)
    args = parser.parse_args(argv)

    files = _files_from_dir(args.app) if args.app else _files_from_zip(args.zip)
    problems = audit(_read_sources(args.repo), files, own_native(args.repo))
    if problems:
        for p in problems:
            print(f"审计失败：{p}", file=sys.stderr)
        return 1
    print(f"审计通过：{len(files)} 个 DLL，原生 DLL 导出与 P/Invoke 声明一致，且互不相同")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
