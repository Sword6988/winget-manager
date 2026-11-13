#!/usr/bin/env python3
"""Replace the icons embedded in the 7zSD.sfx stub with the app's own icon.

Why: the official installer SFX module (7zSD.sfx, from the 7-Zip LZMA SDK)
carries the generic 7-Zip icon. The shipped single-file installer should
show the "Winget 软件管家" icon instead (Explorer / file properties read it
from the PE resource directory).

What it does (pure stdlib PE surgery, no external tools):
  1. Parse the PE, walk the resource tree, snapshot every resource blob.
  2. Drop RT_ICON (type 3) and RT_GROUP_ICON (type 14) entries.
  3. Inject all images from src/WingetManager.App/Assets/app.ico as
     RT_ICON ids 1..N plus a RT_GROUP_ICON with id 1 (the id the stub's
     window class uses).
  4. Serialise the new resource tree into a fresh section appended to the
     image, then patch the section table, SizeOfImage and the PE resource
     data directory.

The SFX stub locates its config block and the appended 7z archive by
scanning its own file, so the appended payload is harmless: the module is
rebuilt into a fresh .exe by `packaging/build-dist.sh` afterwards.

Usage:  python packaging/retarget-sfx-icon.py
"""

import struct
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
STUB = REPO / 'packaging' / '7zSD.sfx'
ICO = REPO / 'src' / 'WingetManager.App' / 'Assets' / 'app.ico'

RT_ICON = 3
RT_GROUP_ICON = 14
LANG_EN_US = 1033  # the stub's resources are all lang 0x409; keep it


def align(value: int, pow2: int) -> int:
    return (value + pow2 - 1) // pow2 * pow2


class PeImage:
    """Minimal PE32 reader: section table + resource directory access."""

    def __init__(self, data: bytes) -> None:
        self.data = bytes(data)
        self.pe = struct.unpack('<I', self.data[0x3c:0x40])[0]
        if self.data[self.pe:self.pe + 4] != b'PE\0\0':
            raise ValueError('not a PE file')
        self.nsec = struct.unpack('<H', self.data[self.pe + 6:self.pe + 8])[0]
        self.optsz = struct.unpack('<H', self.data[self.pe + 20:self.pe + 22])[0]
        self.opt = self.pe + 24
        if struct.unpack('<H', self.data[self.opt:self.opt + 2])[0] != 0x10b:
            raise ValueError('only PE32 images are supported')
        self.sect_align = struct.unpack('<I', self.data[self.opt + 32:self.opt + 36])[0]
        self.file_align = struct.unpack('<I', self.data[self.opt + 36:self.opt + 40])[0]
        self.headers_size = struct.unpack('<I', self.data[self.opt + 60:self.opt + 64])[0]
        self.dir_base = self.opt + 96

    def sections(self):
        out = []
        for i in range(self.nsec):
            off = self.opt + self.optsz + 40 * i
            name = bytes(self.data[off:off + 8]).rstrip(b'\0').decode('ascii', 'replace')
            vsz, va, rawsz, rawptr = struct.unpack('<IIII', self.data[off + 8:off + 24])
            out.append({'hdr': off, 'name': name, 'vsz': vsz, 'va': va,
                        'rawsz': rawsz, 'raw': rawptr})
        return out

    def rva_to_off(self, rva: int) -> int:
        for s in self.sections():
            span = max(s['vsz'], s['rawsz'])
            if s['va'] <= rva < s['va'] + span:
                return s['raw'] + (rva - s['va'])
        raise ValueError('rva %#x outside sections' % rva)

    def resource_dir(self):
        return struct.unpack('<II', self.data[self.dir_base + 16:self.dir_base + 24])


def walk_resources(img: PeImage):
    """Return [(type: int, name: int, lang: int, blob: bytes)]."""
    root = img.rva_to_off(img.resource_dir()[0])
    out = []

    def walk(off, path):
        _, _, _, _, nn, ni = struct.unpack('<IIHHHH', img.data[off:off + 16])
        for i in range(nn + ni):
            p = off + 16 + 8 * i
            name, o2 = struct.unpack('<II', img.data[p:p + 8])
            if name & 0x80000000:
                raise ValueError('string-named resources are not expected in this stub')
            if o2 & 0x80000000:
                walk(root + (o2 & 0x7fffffff), path + [name])
            else:
                de = root + o2
                rva, size, cp, _ = struct.unpack('<IIII', img.data[de:de + 16])
                foff = img.rva_to_off(rva)
                out.append((path[0], path[1], name, img.data[foff:foff + size]))

    walk(root, [])
    return out


def read_ico(path: Path):
    """Parse an .ico into [(width, height, planes, bitcount, blob)] sorted by size."""
    data = path.read_bytes()
    reserved, kind, count = struct.unpack('<HHH', data[:6])
    if reserved != 0 or kind != 1:
        raise ValueError('not an icon file')
    entries = []
    for i in range(count):
        w, h, colors, rsv, planes, bits, size, off = struct.unpack(
            '<4B2H2I', data[6 + i * 16:22 + i * 16])
        entries.append((w or 256, h or 256, planes or 1, bits or 32, data[off:off + size]))
    entries.sort(key=lambda e: (e[0], e[1]))
    return entries


def build_group(icons) -> bytes:
    """Serialise a GRPICONDIR whose entries point at RT_ICON ids 1..N."""
    out = struct.pack('<HHH', 0, 1, len(icons))
    for idx, (w, h, planes, bits, blob) in enumerate(icons, start=1):
        out += struct.pack('<4B2HIH', w % 256, h % 256, 0, 0,
                           planes, bits, len(blob), idx)
    return out


def build_rsrc_section(resources, base_rva: int) -> bytes:
    """Serialise resources into an .rsrc section body.

    Standard three-level tree (type -> name -> lang), then
    IMAGE_RESOURCE_DATA_ENTRY records, then the 4-byte-aligned data blobs.
    Directory offsets are relative to the section start; data entry RVAs are
    absolute image RVAs (base_rva + blob offset).
    """
    tree = {}
    for rtype, rname, rlang, blob in resources:
        tree.setdefault(rtype, {}).setdefault(rname, {})[rlang] = blob

    order = []  # list of (type, name, lang) in the canonical on-disk order
    for rtype in sorted(tree):
        for rname in sorted(tree[rtype]):
            for rlang in sorted(tree[rtype][rname]):
                order.append((rtype, rname, rlang))

    # ---- pass 1: compute block offsets ----
    root_size = 16 + 8 * len(tree)
    offsets = {'root': 0}
    cur = root_size
    for rtype, names in ((t, tree[t]) for t in sorted(tree)):
        offsets[('t', rtype)] = cur
        cur += 16 + 8 * len(names)
        for rname, langs in ((n, tree[rtype][n]) for n in sorted(names)):
            offsets[('n', rtype, rname)] = cur
            cur += 16 + 8 * len(langs)
    de_base = cur
    cur = de_base + 16 * len(order)
    blob_off = {}
    for key in order:
        cur = align(cur, 4)
        blob_off[key] = cur
        cur += len(tree[key[0]][key[1]][key[2]])
    total = cur

    # ---- pass 2: write directories, data entries, blobs ----
    buf = bytearray(total)

    def write_dir(off, entries):
        struct.pack_into('<IIHHHH', buf, off, 0, 0, 4, 0, 0, len(entries))
        for i, (name, field) in enumerate(entries):
            struct.pack_into('<II', buf, off + 16 + 8 * i, name, field)

    write_dir(0, [(t, 0x80000000 | offsets[('t', t)]) for t in sorted(tree)])
    for rtype in sorted(tree):
        names = sorted(tree[rtype])
        write_dir(offsets[('t', rtype)],
                  [(n, 0x80000000 | offsets[('n', rtype, n)]) for n in names])
        for rname in names:
            langs = sorted(tree[rtype][rname])
            write_dir(offsets[('n', rtype, rname)],
                      [(l, de_base + 16 * order.index((rtype, rname, l))) for l in langs])

    for i, key in enumerate(order):
        blob = tree[key[0]][key[1]][key[2]]
        struct.pack_into('<IIII', buf, de_base + 16 * i,
                         base_rva + blob_off[key], len(blob), 0, 0)
        buf[blob_off[key]:blob_off[key] + len(blob)] = blob

    return bytes(buf)


def rollback_if_patched(img: PeImage) -> bytes:
    """Return pristine image bytes; undo a previous run of this script.

    A previous run appended a '.rsrc2' section and repointed the resource
    data directory at it. Detect that, drop the section, and restore the
    section count / SizeOfImage / resource directory to the original .rsrc.
    Makes this script idempotent without keeping a pristine copy around.
    """
    sect = img.sections()
    if not sect or sect[-1]['name'] != '.rsrc2':
        return img.data
    rsrc = next(s for s in sect if s['name'] == '.rsrc')
    data = bytearray(img.data[:sect[-1]['raw']])
    struct.pack_into('<H', data, img.pe + 6, img.nsec - 1)
    struct.pack_into('<I', data, img.opt + 56,
                     align(rsrc['va'] + max(rsrc['vsz'], rsrc['rawsz']), img.sect_align))
    struct.pack_into('<II', data, img.dir_base + 16, rsrc['va'], rsrc['vsz'])
    print('previous patch detected: rolled back to the pristine 128KB stub')
    return bytes(data)


def main() -> int:
    if not STUB.exists() or not ICO.exists():
        print('missing input: %s / %s' % (STUB, ICO))
        return 1

    img = PeImage(rollback_if_patched(PeImage(STUB.read_bytes())))
    old = walk_resources(img)

    kept = [r for r in old if r[0] not in (RT_ICON, RT_GROUP_ICON)]
    dropped = len(old) - len(kept)
    icons = read_ico(ICO)

    new_res = list(kept)
    for idx, (w, h, planes, bits, blob) in enumerate(icons, start=1):
        new_res.append((RT_ICON, idx, LANG_EN_US, blob))
    new_res.append((RT_GROUP_ICON, 1, LANG_EN_US, build_group(icons)))

    sect = img.sections()
    last = max(sect, key=lambda s: s['va'])
    new_va = align(last['va'] + max(last['vsz'], last['rawsz']), img.sect_align)
    body = build_rsrc_section(new_res, new_va)

    out = bytearray(img.data)
    raw_off = align(len(out), img.file_align)
    if raw_off > len(out):
        out.extend(b'\0' * (raw_off - len(out)))
    out.extend(body)
    # FileAlignment padding: the loader requires the file to cover the full
    # SizeOfRawData of every section, otherwise it reports BAD_EXE_FORMAT.
    raw_size = align(len(body), img.file_align)
    if raw_off + raw_size > len(out):
        out.extend(b'\0' * (raw_off + raw_size - len(out)))

    hdr = img.opt + img.optsz + 40 * img.nsec
    if hdr + 40 > img.headers_size:
        raise ValueError('no room for another section header')
    # Full 40-byte IMAGE_SECTION_HEADER. Characteristics must include
    # IMAGE_SCN_CNT_INITIALIZED_DATA | IMAGE_SCN_MEM_READ, otherwise the
    # loader rejects the image ("exec format error").
    struct.pack_into('<8sIIIIIIHHI', out, hdr,
                     b'.rsrc2',                       # Name
                     len(body),                       # VirtualSize
                     new_va,                          # VirtualAddress
                     align(len(body), img.file_align),  # SizeOfRawData
                     raw_off,                         # PointerToRawData
                     0, 0, 0, 0,                      # relocs / line numbers
                     0x40000040)                      # INITIALIZED_DATA | MEM_READ
    struct.pack_into('<H', out, img.pe + 6, img.nsec + 1)                       # NumberOfSections
    struct.pack_into('<I', out, img.opt + 56, align(new_va + len(body), img.sect_align))  # SizeOfImage
    struct.pack_into('<II', out, img.dir_base + 16, new_va, len(body))          # resource data dir

    STUB.write_bytes(bytes(out))
    print('icons replaced: dropped %d old icon resources, added %d RT_ICON ids 1..%d + 1 group'
          % (dropped, len(icons), len(icons)))
    print('module: %d -> %d bytes, resources %d -> %d, new .rsrc2 rva=%#x size=%#x'
          % (len(img.data), len(out), len(old), len(new_res), new_va, len(body)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
