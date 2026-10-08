"""Copy the game's managed assemblies into a local, git-ignored folder for tests.

The test suite uses the game's own serializers as a reference to check that our
server encodes messages correctly. Some of the game's assemblies are marked
"32-bit required", which a 64-bit .NET runtime refuses to load, so the copies
have that flag cleared. The originals in the install are never modified, and
the copies stay in local/ (git-ignored).

Usage: python tools/prepare_reference_assemblies.py <game install dir> [out dir]
"""
import shutil
import struct
import sys
from pathlib import Path

ASSEMBLIES = {
    "Dead Island Epidemic_Data/Managed": [
        "Assembly-CSharp.dll", "StunCore.dll", "ConductorCrafting.dll",
        "SteamworksManaged.dll",
    ],
}
COMIMAGE_FLAGS_32BITREQUIRED = 0x2


def clear_32bit_required(path: Path) -> bool:
    data = bytearray(path.read_bytes())
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        raise ValueError(f"{path} is not a PE file")
    opt = pe + 24
    magic = struct.unpack_from("<H", data, opt)[0]
    dirs = opt + (96 if magic == 0x10B else 112)
    clr_rva = struct.unpack_from("<I", data, dirs + 14 * 8)[0]
    if clr_rva == 0:
        return False
    nsec = struct.unpack_from("<H", data, pe + 6)[0]
    sec = opt + struct.unpack_from("<H", data, pe + 20)[0]
    for i in range(nsec):
        s = sec + i * 40
        vsize, va, rawsize, rawptr = struct.unpack_from("<IIII", data, s + 8)
        if va <= clr_rva < va + max(vsize, rawsize):
            off = clr_rva - va + rawptr
            flags_off = off + 16
            flags = struct.unpack_from("<I", data, flags_off)[0]
            if flags & COMIMAGE_FLAGS_32BITREQUIRED:
                struct.pack_into("<I", data, flags_off, flags & ~COMIMAGE_FLAGS_32BITREQUIRED)
                path.write_bytes(data)
                return True
            return False
    raise ValueError(f"{path}: CLR header not found")


def main() -> None:
    install = Path(sys.argv[1])
    out = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(__file__).resolve().parent.parent / "local" / "ref"
    out.mkdir(parents=True, exist_ok=True)
    for sub, names in ASSEMBLIES.items():
        for name in names:
            src = install / sub / name
            dst = out / name
            shutil.copyfile(src, dst)
            dst.chmod(0o644)
            changed = clear_32bit_required(dst)
            print(f"{name}: copied{' (cleared 32-bit flag)' if changed else ''}")


if __name__ == "__main__":
    main()
