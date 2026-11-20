import struct
import unittest

import audit_package as a


def fake_dll(exports: list[str], filler: bytes = b"") -> bytes:
    """构造带导出表的最小 64 位 PE（一个 .edata 节）。"""
    exports = sorted(exports)
    headers = 0x200
    names_blob = b"".join(n.encode() + b"\0" for n in exports) + b"fake.dll\0"
    dir_size = 40
    names_array_off = dir_size
    strings_off = names_array_off + 4 * len(exports)
    rva = 0x1000
    strings = bytearray(); pointers = []
    for n in exports:
        pointers.append(rva + strings_off + len(strings)); strings += n.encode() + b"\0"
    directory = struct.pack("<IIHHIIIIIII", 0, 0, 0, 0, 0, 1, len(exports), len(exports), 0, rva + names_array_off, 0)
    body = directory + b"".join(struct.pack("<I", p) for p in pointers) + bytes(strings) + filler
    body = body.ljust(0x200, b"\0")
    dos = b"MZ" + b"\0" * 0x3A + struct.pack("<I", 0x40)
    coff = struct.pack("<HHIIIHH", 0x8664, 1, 0, 0, 0, 240, 0x2022)
    optional = bytearray(240)
    struct.pack_into("<H", optional, 0, 0x20B)
    struct.pack_into("<II", optional, 112, rva, len(body))
    section = struct.pack("<8sIIIIIIHHI", b".edata", len(body), rva, len(body), headers, 0, 0, 0, 0, 0x40000040)
    image = (dos + b"PE\0\0" + coff + bytes(optional) + section).ljust(headers, b"\0") + body
    return image


class ParseImports(unittest.TestCase):
    def test_dllimport_with_entry_point_and_attributes(self):
        src = '''
        [DllImport("ZZZTouchCore.dll", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern int ZZZTouchInjectToProcess(uint pid, bool quiet, int wait);
        [System.Runtime.InteropServices.LibraryImport("User32.dll", EntryPoint = "GetWindowRect")]
        private static partial bool GetRect(nint h, out Rect r);
        [DllImport("kernel32")] static extern int Beep();
        '''
        self.assertEqual(a.parse_imports(src), [
            ("ZZZTouchCore.dll", "ZZZTouchInjectToProcess"),
            ("User32.dll", "GetWindowRect"),
            ("kernel32.dll", "Beep"),
        ])


class Exports(unittest.TestCase):
    def test_reads_named_exports(self):
        self.assertEqual(a.pe_exports(fake_dll(["Foo", "Bar"])), {"Foo", "Bar"})

    def test_not_a_pe(self):
        self.assertIsNone(a.pe_exports(b"hello"))


SRC = {"src/Z.cs": '[DllImport("ZZZTouchCore.dll")] static extern int ZZZTouchInjectToProcess(int a);\n'
                   '[DllImport("HoyoTouchCore.dll")] static extern int hoyo_launch(int a);'}


def package(**over):
    files = {
        "HoyoTouchCore.dll": fake_dll(["hoyo_launch"], b"h"),
        "EndfieldCore.dll": fake_dll(["ef_core"], b"e"),
        "EndfieldRuntime.dll": fake_dll(["ef_runtime"], b"r"),
        "ZZZTouchCore.dll": fake_dll(["ZZZTouchInjectToProcess"], b"z"),
        "ZZZTouchRuntime.dll": fake_dll(["zzz_rt"], b"zr"),
    }
    files.update(over)
    return files


class Audit(unittest.TestCase):
    def test_good_package_passes(self):
        self.assertEqual(a.audit(SRC, package()), [])

    def test_zzz_core_replaced_by_hoyo_core_is_caught_twice(self):
        files = package()
        files["ZZZTouchCore.dll"] = files["HoyoTouchCore.dll"]
        problems = a.audit(SRC, files)
        self.assertTrue(any("同一份文件" in p for p in problems), problems)
        self.assertTrue(any("没有导出 ZZZTouchInjectToProcess" in p for p in problems), problems)

    def test_missing_own_dll(self):
        files = package(); del files["EndfieldRuntime.dll"]
        required = a.OWN_NATIVE + a.ENDFIELD_NATIVE
        self.assertEqual(a.audit(SRC, files, required), ["包里缺少 EndfieldRuntime.dll"])
        self.assertEqual(a.audit(SRC, files), [])  # 仓库没有终末地时不要求

    def test_system_dll_is_ignored(self):
        src = {"x.cs": '[DllImport("user32.dll")] static extern int Whatever();'}
        self.assertEqual(a.audit(src, package()), [])


if __name__ == "__main__":
    unittest.main()
