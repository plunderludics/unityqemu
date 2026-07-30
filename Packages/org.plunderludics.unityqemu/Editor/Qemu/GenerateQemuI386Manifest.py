"""Generate qemu-i386.manifest (paths relative to qemu~)."""
from __future__ import annotations

import struct
from pathlib import Path

PACKAGE = Path(__file__).resolve().parents[2]
QEMU = PACKAGE / "qemu~"
OUT = PACKAGE / "qemu-i386.manifest"

SYSTEM = {
    "kernel32.dll", "user32.dll", "gdi32.dll", "advapi32.dll", "shell32.dll",
    "ole32.dll", "oleaut32.dll", "ntdll.dll", "ws2_32.dll", "wsock32.dll",
    "iphlpapi.dll", "setupapi.dll", "winmm.dll", "imm32.dll", "comdlg32.dll",
    "comctl32.dll", "version.dll", "shlwapi.dll", "crypt32.dll", "bcrypt.dll",
    "secur32.dll", "netapi32.dll", "userenv.dll", "dwmapi.dll", "uxtheme.dll",
    "msvcrt.dll", "ucrtbase.dll", "vcruntime140.dll", "msvcp140.dll",
    "dbghelp.dll", "psapi.dll", "winspool.drv", "opengl32.dll", "glu32.dll",
    "d3d11.dll", "dxgi.dll", "d3d9.dll", "cfgmgr32.dll", "powrprof.dll",
    "wtsapi32.dll", "cabinet.dll", "winhttp.dll", "wininet.dll", "urlmon.dll",
    "rpcrt4.dll", "sechost.dll", "normaliz.dll", "wldap32.dll", "cryptbase.dll",
    "sspicli.dll", "profapi.dll", "kernelbase.dll", "gdi32full.dll",
    "msvcp_win.dll", "win32u.dll", "cryptsp.dll", "rsaenh.dll", "imagehlp.dll",
    "hid.dll", "dnsapi.dll", "dwrite.dll", "gdiplus.dll", "msimg32.dll",
    "mswsock.dll", "ncrypt.dll", "usp10.dll",
}


def is_system(name: str) -> bool:
    n = name.lower()
    return n in SYSTEM or n.startswith("api-ms-win-") or n.startswith("ext-ms-")


def read_pe_imports(path: Path) -> list[str]:
    data = path.read_bytes()
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    num_sections = struct.unpack_from("<H", data, e_lfanew + 6)[0]
    opt_size = struct.unpack_from("<H", data, e_lfanew + 20)[0]
    opt_off = e_lfanew + 24
    magic = struct.unpack_from("<H", data, opt_off)[0]
    dd_off = opt_off + (112 if magic == 0x20B else 96)
    import_rva = struct.unpack_from("<I", data, dd_off + 8)[0]
    if not import_rva:
        return []
    sec_off = opt_off + opt_size
    sections = []
    for i in range(num_sections):
        off = sec_off + i * 40
        vsize, vaddr, rawsize, rawptr = struct.unpack_from("<IIII", data, off + 8)
        sections.append((vaddr, vsize, rawptr, rawsize))

    def rva_to_off(rva: int):
        for vaddr, vsize, rawptr, rawsize in sections:
            if vaddr <= rva < vaddr + max(vsize, rawsize):
                return rawptr + (rva - vaddr)
        return None

    imports = []
    desc = rva_to_off(import_rva)
    if desc is None:
        return []
    while True:
        oft, ts, fwd, name_rva, ft = struct.unpack_from("<IIIII", data, desc)
        if oft == 0 and name_rva == 0 and ft == 0:
            break
        noff = rva_to_off(name_rva)
        end = data.index(b"\0", noff)
        imports.append(data[noff:end].decode("ascii", "replace"))
        desc += 20
    return imports


def main() -> None:
    if not QEMU.is_dir():
        raise SystemExit(f"qemu~ missing at {QEMU}")

    pending = ["qemu-system-i386.exe", "qemu-img.exe"]
    seen: set[str] = set()
    entries: list[str] = []

    while pending:
        cur = pending.pop()
        key = cur.lower()
        if key in seen:
            continue
        seen.add(key)
        hits = [p for p in QEMU.iterdir() if p.is_file() and p.name.lower() == key]
        if not hits:
            print(f"WARNING: not found in qemu~: {cur}")
            continue
        path = hits[0]
        if path.suffix.lower() in (".dll", ".exe"):
            entries.append(path.name.replace("\\", "/"))
        for imp in read_pe_imports(path):
            if not is_system(imp) and imp.lower() not in seen:
                pending.append(imp)

    share = QEMU / "share"
    for name in (
        "bios.bin",
        "bios-256k.bin",
        "kvmvapic.bin",
        "linuxboot.bin",
        "linuxboot_dma.bin",
        "multiboot.bin",
        "multiboot_dma.bin",
    ):
        if (share / name).is_file():
            entries.append(f"share/{name}")

    for pattern in ("vgabios*.bin", "efi-*.rom", "pxe-*.rom"):
        for p in sorted(share.glob(pattern)):
            entries.append(f"share/{p.name}")

    keymaps = share / "keymaps"
    if keymaps.is_dir():
        for p in sorted(keymaps.iterdir()):
            if p.is_file():
                entries.append(f"share/keymaps/{p.name}")

    entries = sorted(set(entries), key=str.lower)
    missing = [e for e in entries if not (QEMU / e).is_file()]
    if missing:
        raise SystemExit(f"missing files: {missing}")

    total = sum((QEMU / e).stat().st_size for e in entries)
    header = (
        "# UnityQemu trimmed Windows QEMU package for qemu-system-i386 + qemu-img.\n"
        "# Paths are relative to the qemu~ directory.\n"
        "# Generated from PE import closure + SeaBIOS PC firmware / option-ROMs / keymaps.\n"
        f"# Approx size: {total / 1024 / 1024:.1f} MB ({len(entries)} files).\n"
        "# Does NOT include other softmmu arches, UEFI (edk2), docs, or icons.\n"
        "#\n"
        "# Regenerate: python Editor/Qemu/GenerateQemuI386Manifest.py\n"
        "#\n"
    )
    OUT.write_text(header + "\n".join(entries) + "\n", encoding="utf-8")
    print(f"Wrote {OUT} ({len(entries)} entries, {total / 1024 / 1024:.1f} MB)")


if __name__ == "__main__":
    main()
