#!/usr/bin/env python3
"""Offline static decryptor for the KONN-packed gakumas GameAssembly.dll (Python rewrite).

Pipeline (same algorithm as the .NET tool it replaces):

    stage 1   outer layer:  header at 0x1000 -> key / destination RVA / split, section
                            map, dword-stream decryption (fn 0x6E0) + tail copy
    stage 2   loader work:  self-decrypt records + map/flag records (ROL11/13/3/2),
                            applied only when its structural probes pass on this image
    stage 2b  body decode:  per 16 B block helper(key, 14, ct, mid) -> XOR chain ->
                            sbox -> literal copy (packed == unpacked) or bit-unpack
    stage 3   PE rebuild:   restore the original six-section layout (per-build preset,
                            selected by the decoded record count)

Stage 2b needs five inputs. They are resolved in this order, first hit wins:

    1. manual     --key --sbox --pass3 --records --helper --payload
    2. profile    --profile <dir>   (.NET-tool layout: helper_tab/helper_fn/key/pass3/guard)
    3. auto scan  structure judges on the stage-1 image (default):
                    records  longest run of valid 16 B records in the written region
                    pass3    3 B {u16, u8} code table with a strict successor check
                    key      pass3 + 0x10C6 (240 B)
                    helper   pass3 - 0xFB40, taken as a 0x20000 B window (entry at +0x8000)
                  and the packed-file stream cursor (payload):
                    --reference image -> plaintext oracle (zero-conflict cursor)
                    otherwise         -> stream invariant ((bits+7)//8 == packed_size)
                    fallback          -> documented hint 0xE3580

The sbox is the build's byte permutation: derived by majority vote when a --reference
image is available, else the built-in table; both are printed with their sha256.

usage:
    # auto: scan the keys out of the packed file itself
    python ga_static_decrypt.py GameAssembly.dll out.dll

    # manual: frozen profile directory, or individual key files
    python ga_static_decrypt.py GameAssembly.dll out.dll --profile <dir>
    python ga_static_decrypt.py GameAssembly.dll out.dll --key key.bin --sbox sbox.bin \\
        --pass3 pass3.bin --records records.bin --helper helper_blk.bin --payload 0xE3580

    # diagnostics: stop after stage 1 (packed-layout skeleton, NOT a generator input)
    python ga_static_decrypt.py GameAssembly.dll stage1.dll --stage1-only

    # verification: byte-compare against a known-good image / freeze the inputs
    python ga_static_decrypt.py GameAssembly.dll out.dll --reference known_good.dll
    python ga_static_decrypt.py GameAssembly.dll out.dll --emit-profile carve/
"""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import struct
import sys
import time
from pathlib import Path

# --------------------------------------------------------------------------- #
# constants
# --------------------------------------------------------------------------- #
KONN = 0x4E4E4F4B
PACKED_PAYLOAD_OFF = 0x1000

# scan geometry (hints only - every hit is verified before use)
KEY_DELTA = 0x10C6           # key descriptor = pass3 table + 0x10C6
HELPER_DELTA = 0xFB40        # helper entry    = pass3 table - 0xFB40
HELPER_WINDOW = 0x20000      # helper mapping view, entry at window + 0x8000
HELPER_ENTRY = 0x8000
KEY_LEN = 240
RND = 14
MIN_RUN = 512                # consecutive records/entries needed for a structure hit
PAYLOAD_HINT = 0xE3580
PAYLOAD_WINDOW = 0x40000

RECORD_LEN = 16
MAX_SPAN = 0x1000            # packed/unpacked size ceiling per record
PROFILE_RECORDS_OFFSET = 0x2E0   # record table offset inside the .NET profile guard blob

# per-build loader self-decrypt layout (probe-gated: nothing is touched unless all
# structural probes below validate on the image being processed)
STAGE2_SELF_DECRYPT = {
    "name": "frozen-20260826",
    "rol11": 0xBDE3868,
    "rol13": 0xBDEAB10,
    "rol13_seed": 0xB9C62858,
    "probes": [
        (0xBDE9DB8, b"\x48\x83\xEC\x28", "ROL13 helper"),
        (0xBDEDDE0, b"\x48\x83\xEC\x28", "ROL3 helper"),
        (0xBDF4D70, b"\x48\x83\xEC\x28", "stage-2 NOT helper"),
    ],
    "rol3": (0xBDF0A00, 0x7000, 0xBDF0A00),
    "xor_ff": (0xBDF0E00, 17606),
    "map_records": [0xBDEF7E0, 0xBDEF800],
    "flag_table": 0xBDEF900,
    "flag_count_rva": 0xBDEF790,
    "rol3_tail": [
        (0xBE0BC00, 0x10A4, 0xBE0BC00, 0),
        (0xBE0CD98, 0xC3C, 0xBE0CD98, 0),
        (0xBE0CCA4, 0xF4, 0xBE0CCA4, 0),
        (0xBE0D9D4, 0xF4, 0xBE0D9D4, 0),
    ],
}

# original six-section layout for the PE rebuild, keyed by decoded record count
SECTION_PRESETS = {
    47706: {
        "name": "frozen-20260826",
        "final_image_size": 0x0BD26000,
        "size_of_code": 0x378F600,
        "debug_dir": (0xB97D000, 0x3A8678),
        "sections": [
            (".text",  0x987330, 0x1000,    0x60000020),
            ("il2cpp", 0x791DE8C, 0x989000, 0x60000020),
            (".rdata", 0x233396C, 0x82A7000, 0x40000040),
            (".data",  0xCC2FB0,  0xA5DB000, 0xC0000040),
            (".pdata", 0x6DE108,  0xB29E000, 0x40000040),
            (".reloc", 0x3A8678,  0xB97D000, 0x42000040),
        ],
    },
}


def substitute_table() -> bytes:
    """The build's sbox as implemented by the .NET tool: rol8((v + 1) ^ 0xB0, 7) + 0x0E."""
    table = bytearray(256)
    for value in range(256):
        v = ((value + 1) & 0xFF) ^ 0xB0
        table[value] = ((((v >> 1) | (v << 7)) & 0xFF) + 0x0E) & 0xFF
    return bytes(table)


# --------------------------------------------------------------------------- #
# helpers
# --------------------------------------------------------------------------- #
def u16(buf, off: int) -> int:
    return buf[off] | (buf[off + 1] << 8)


def u32(buf, off: int) -> int:
    return buf[off] | (buf[off + 1] << 8) | (buf[off + 2] << 16) | (buf[off + 3] << 24)


def put16(buf: bytearray, off: int, value: int) -> None:
    buf[off] = value & 0xFF
    buf[off + 1] = (value >> 8) & 0xFF


def put32(buf: bytearray, off: int, value: int) -> None:
    buf[off] = value & 0xFF
    buf[off + 1] = (value >> 8) & 0xFF
    buf[off + 2] = (value >> 16) & 0xFF
    buf[off + 3] = (value >> 24) & 0xFF


def rol8(value: int, rotation: int) -> int:
    rotation &= 7
    return ((value << rotation) | (value >> (8 - rotation))) & 0xFF


def rol32(value: int, rotation: int) -> int:
    rotation &= 31
    return ((value << rotation) | (value >> (32 - rotation))) & 0xFFFFFFFF


def align4(n: int) -> int:
    return (n + 3) & ~3


def align16(n: int) -> int:
    return (n + 15) & ~15


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def log(msg: str) -> None:
    print(msg, flush=True)


def xor_chain(mid: bytearray, cipher: bytes, full: int) -> None:
    """mid[j] ^= cipher[j - 16] for j in 16 .. full-1 (single big-int XOR, C speed)."""
    if full <= 16:
        return
    span = full - 16
    left = int.from_bytes(mid[16:full], "little") ^ int.from_bytes(cipher[:span], "little")
    mid[16:full] = left.to_bytes(span, "little")


# --------------------------------------------------------------------------- #
# PE parsing / mapping
# --------------------------------------------------------------------------- #
class Section:
    __slots__ = ("name", "vsize", "va", "rsize", "raw", "chars", "table_off")

    def __init__(self, buf: bytes, off: int) -> None:
        self.name = buf[off:off + 8].split(b"\0")[0].decode("ascii", "replace")
        self.vsize = u32(buf, off + 8)
        self.va = u32(buf, off + 12)
        self.rsize = u32(buf, off + 16)
        self.raw = u32(buf, off + 20)
        self.chars = u32(buf, off + 36)
        self.table_off = off


class Pe:
    __slots__ = ("lfanew", "opt", "opt_size", "entry", "image_base", "size_of_image",
                 "size_of_headers", "num_rva", "sections")


def parse_pe(buf: bytes) -> Pe | None:
    if len(buf) < 0x40 or buf[0] != 0x4D or buf[1] != 0x5A:
        return None
    lfanew = u32(buf, 0x3C)
    if lfanew + 0x18 >= len(buf) or u32(buf, lfanew) != 0x4550:
        return None
    sections = u16(buf, lfanew + 6)
    opt_size = u16(buf, lfanew + 20)
    opt = lfanew + 24
    if u16(buf, opt) != 0x20B:
        return None
    pe = Pe()
    pe.lfanew = lfanew
    pe.opt = opt
    pe.opt_size = opt_size
    pe.entry = u32(buf, opt + 16)
    pe.image_base = struct.unpack_from("<Q", buf, opt + 24)[0]
    pe.size_of_image = u32(buf, opt + 56)
    pe.size_of_headers = u32(buf, opt + 60)
    pe.num_rva = u32(buf, opt + 108)
    sec_off = opt + opt_size
    pe.sections = [Section(buf, sec_off + i * 40) for i in range(sections)]
    return pe


def map_disk_sections(file: bytes, pe: Pe, image: bytearray) -> None:
    header = min(pe.size_of_headers, len(file), len(image))
    image[0:header] = file[0:header]
    for section in pe.sections:
        if section.rsize <= 0 or section.raw <= 0:
            continue
        count = min(section.rsize, section.vsize, len(file) - section.raw)
        if count <= 0 or section.va + count > len(image):
            continue
        image[section.va:section.va + count] = file[section.raw:section.raw + count]
        log(f"[map] {section.name:<8} file 0x{section.raw:X} -> VA 0x{section.va:X} {count:,}B")


# --------------------------------------------------------------------------- #
# stage 1: outer layer
# --------------------------------------------------------------------------- #
def decode_header(file: bytes, off: int) -> list[int] | None:
    if len(file) < off + 32:
        log(f"[err] file too small for header at 0x{off:X}")
        return None
    src = [u32(file, off + i * 4) for i in range(8)]
    dst = [0] * 8
    dst[0] = src[0]
    acc = src[0]
    dst[1] = src[1] ^ acc
    acc = (acc + src[1]) & 0xFFFFFFFF
    dst[2] = src[2] ^ acc
    t = ((src[2] + acc - 1) ^ 1) & 0xFFFFFFFF
    dst[3] = src[3] ^ t
    t = ((src[3] + t - 2) ^ 4) & 0xFFFFFFFF
    dst[4] = src[4] ^ t
    t = ((src[4] + t - 3) ^ 9) & 0xFFFFFFFF
    dst[5] = src[5] ^ t
    t = ((src[5] + t - 4) ^ 0x10) & 0xFFFFFFFF
    dst[6] = src[6] ^ t
    t = ((src[6] + t - 5) ^ 0x19) & 0xFFFFFFFF
    dst[7] = src[7] ^ t
    log("[hdr] raw  " + " ".join(f"{v:08X}" for v in src))
    log("[hdr] dec  " + " ".join(f"{v & 0xFFFFFFFF:08X}" for v in dst))
    if dst[1] != KONN:
        log(f"[err] magic mismatch: got 0x{dst[1]:08X} expected KONN (0x{KONN:08X})")
        return None
    log("[hdr] magic KONN ok")
    return dst


def decrypt_6e0(dest: bytearray, dest_off: int, src: bytes, src_off: int, size: int, key: int) -> None:
    count = size >> 2
    k = (key + (~size & 0xFFFFFFFF)) & 0xFFFFFFFF
    values = struct.unpack_from(f"<{count}I", src, src_off) if count else ()
    out = bytearray(count * 4)
    for i in range(count):
        c = values[i]
        p = c ^ k
        k = (k + c + i) & 0xFFFFFFFF
        k ^= (i * i) & 0xFFFFFFFF
        put32(out, i * 4, p)
    dest[dest_off:dest_off + count * 4] = out
    rem = size & 3
    if rem:
        dest[dest_off + count * 4:dest_off + count * 4 + rem] = src[src_off + count * 4:src_off + count * 4 + rem]
    log(f"[dec] 0x6E0 wrote {count:,} dwords ({count * 4:,} bytes)")


def stage1(packed: bytes, out_path: Path | None, header_key: int | None = None,
           header_rva: int | None = None, header_split: int | None = None) -> tuple[bytearray, dict, Pe]:
    pe = parse_pe(packed)
    if pe is None:
        raise SystemExit("[err] packed file is not a PE")
    log(f"[pe] ImageBase=0x{pe.image_base:X} SizeOfImage=0x{pe.size_of_image:X} "
        f"entry=0x{pe.entry:X} secs={len(pe.sections)}")

    header = decode_header(packed, PACKED_PAYLOAD_OFF)
    if header is None:
        raise SystemExit(4)
    if header_key is not None:
        header[0] = header_key
    if header_rva is not None:
        header[3] = header_rva
    if header_split is not None:
        header[6] = header_split
    if header_key is not None or header_rva is not None or header_split is not None:
        log(f"[hdr] manual overrides: key=0x{header[0]:08X} destRVA=0x{header[3]:08X} "
            f"split=0x{header[6]:08X}")

    image = bytearray(pe.size_of_image)
    map_disk_sections(packed, pe, image)

    dest_rva = header[3]
    src_off = PACKED_PAYLOAD_OFF + header[4]
    chunk = header[6] - header[3] + 0x2000
    key = header[0]
    log(f"[hdr] [0] key/flags = 0x{header[0]:08X}")
    log(f"[hdr] [3] dest RVA  = 0x{header[3]:08X}")
    log(f"[hdr] [4] src add   = 0x{header[4]:08X}")
    log(f"[hdr] [5] total     = 0x{header[5]:08X} ({header[5]:,})")
    log(f"[hdr] [6] split     = 0x{header[6]:08X}")
    log(f"[dec] destRVA=0x{dest_rva:X} srcOff=0x{src_off:X} chunk=0x{chunk:X}({chunk:,}) key=0x{key:08X}")
    if src_off < 0 or dest_rva < 0 or chunk <= 0 or src_off + chunk > len(packed) or dest_rva + chunk > len(image):
        raise SystemExit("[err] header offsets out of range")

    decrypt_6e0(image, dest_rva, packed, src_off, chunk, key)

    tail = header[5] - chunk
    if tail > 0:
        tsrc = src_off + chunk
        tdst = dest_rva + chunk
        if tsrc + tail > len(packed) or tdst + tail > len(image):
            raise SystemExit(f"[err] tail copy out of range: 0x{tail:X} bytes")
        image[tdst:tdst + tail] = packed[tsrc:tsrc + tail]
        log(f"[dec] tail memcpy 0x{tail:X} bytes -> RVA 0x{tdst:X}")

    meta = {"header": header, "dest_rva": dest_rva, "chunk": chunk, "tail": tail,
            "written": [dest_rva, dest_rva + max(chunk, chunk + tail)]}
    if out_path is not None:
        out_path.parent.mkdir(parents=True, exist_ok=True)
        out_path.write_bytes(bytes(image))
        log(f"[out] stage1 only: {out_path} ({len(image):,} bytes)")
        log("[warn] packed-layout skeleton (~99.5% zeros): the method tables and code/data")
        log("[warn] records are not materialized, so codereg scan and LibCpp2IL reject it.")
    return image, meta, pe


# --------------------------------------------------------------------------- #
# stage 2: loader self-decrypt records (per-build layout, probe-gated)
# --------------------------------------------------------------------------- #
def decrypt_rol(image: bytearray, destination: int, size: int, seed: int, rotation: int) -> None:
    count = size >> 2
    state = seed
    for i in range(count):
        value = u32(image, destination + i * 4) ^ state
        state = (state + i) & 0xFFFFFFFF
        put32(image, destination + i * 4, (rol32(value, rotation) - i) & 0xFFFFFFFF)
    log(f"[dec] ROL{rotation} RVA=0x{destination:X} size=0x{size:X} seed=0x{seed:08X} words={count}")


def decrypt_rol3(image: bytearray, destination: int, size: int, key: int,
                 incoming_r11: int | None = None) -> None:
    if incoming_r11 is None:
        incoming_r11 = key & 0xFF
    r11 = ((key >> 8) + incoming_r11) & 0xFF
    dl = (r11 + 1) & 0xFF
    for i in range(min(size, len(image) - destination)):
        value = rol8(image[destination + i], 3) ^ dl
        dl = (dl + 1) & 0xFF
        value = rol8(value, 3) ^ r11
        r11 = (r11 + 1) & 0xFF
        image[destination + i] = rol8(value, 3)
    log(f"[dec] ROL3 RVA=0x{destination:X} size=0x{size:X} key=0x{key:08X} r11in=0x{incoming_r11:02X}")


def decrypt_rol2(image: bytearray, destination: int, size: int, key: int) -> None:
    state = key & 0xFF
    for i in range(min(size, len(image) - destination)):
        value = rol8(image[destination + i], 2)
        nxt = (state + 1) & 0xFF
        value = (rol8(value ^ nxt, 2)) ^ state
        state = nxt
        image[destination + i] = rol8(value, 2)
    log(f"[dec] ROL2 RVA=0x{destination:X} size=0x{size:X} key=0x{key:08X}")


def stage2_self_decrypt(image: bytearray, layout: dict, force: bool = False) -> bytearray:
    """Apply the loader's self-decrypt records.

    Order matters and mirrors the .NET tool: the two ROL pointer records are decrypted
    first, *then* the helper probes validate them, then the ROL3/XOR regions and the
    map/flag records follow. In auto mode the whole step runs on a copy and is committed
    only if every probe passes, so a layout from a different build cannot corrupt the image.
    """
    work = image if force else bytearray(image)

    rva11 = u32(work, layout["rol11"])
    size11 = u32(work, layout["rol11"] + 4)
    if rva11 <= 0 or size11 < 8 or rva11 + size11 > len(work):
        if force:
            raise SystemExit(f"[err] ROL11 record invalid: RVA=0x{rva11:X} size=0x{size11:X}")
        log(f"[skip] stage-2 self-decrypt: ROL11 record invalid "
            f"(RVA=0x{rva11:X} size=0x{size11:X}, layout {layout['name']} does not fit this image)")
        return image
    decrypt_rol(work, rva11, size11, u32(work, rva11), 11)

    rva13 = u32(work, layout["rol13"])
    size13 = u32(work, layout["rol13"] + 4)
    if rva13 <= 0 or size13 < 8 or rva13 + size13 > len(work):
        if force:
            raise SystemExit(f"[err] ROL13 record invalid: RVA=0x{rva13:X} size=0x{size13:X}")
        log(f"[skip] stage-2 self-decrypt: ROL13 record invalid "
            f"(RVA=0x{rva13:X} size=0x{size13:X}, layout {layout['name']} does not fit this image)")
        return image
    decrypt_rol(work, rva13, size13, layout["rol13_seed"], 13)

    for rva, magic, name in layout["probes"]:
        bad = rva + len(magic) > len(work) or bytes(work[rva:rva + len(magic)]) != magic
        if bad and name == "stage-2 NOT helper":
            break        # this probe is validated after the ROL3/XOR step below
        if bad:
            if force:
                raise SystemExit(f"[err] stage-2 {name} probe failed at RVA 0x{rva:X}")
            log(f"[skip] stage-2 self-decrypt: {name} probe failed at RVA 0x{rva:X} "
                f"(layout {layout['name']} does not fit this image)")
            return image

    rol3_rva, rol3_size, rol3_key = layout["rol3"]
    decrypt_rol3(work, rol3_rva, rol3_size, rol3_key)
    xor_rva, xor_len = layout["xor_ff"]
    for i in range(xor_len):
        work[xor_rva + i] ^= 0xFF

    rva, magic, name = layout["probes"][-1]
    if bytes(work[rva:rva + len(magic)]) != magic:
        if force:
            raise SystemExit(f"[err] {name} validation failed at RVA 0x{rva:X}")
        log(f"[skip] stage-2 self-decrypt: {name} probe failed at RVA 0x{rva:X} "
            f"(layout {layout['name']} does not fit this image)")
        return image

    for record in layout["map_records"]:
        apply_map_record(work, record)
    apply_flag_table(work, layout)
    log(f"[dec] stage-2 self-decrypt applied ({layout['name']})")
    return work


def apply_map_record(image: bytearray, record: int) -> None:
    flags = u32(image, record)
    destination = u32(image, record + 4)
    size = u32(image, record + 8)
    call_offset = u32(image, record + 16)
    if destination <= 0 or size < 8 or destination + size > len(image):
        raise SystemExit(f"[err] map record 0x{record:X} is out of range")
    if flags & 1:
        decrypt_rol3(image, destination, size, destination)
    for i in range(size - 0x400):
        image[destination + 0x400 + i] ^= 0xFF
    if bytes(image[destination + call_offset:destination + call_offset + 4]) != b"\x48\x83\xEC\x28":
        raise SystemExit(f"[err] map helper 0x{record:X} validation failed")


def apply_flag_table(image: bytearray, layout: dict) -> None:
    table = layout["flag_table"]
    count = u32(image, layout["flag_count_rva"])
    if count <= 0 or count > 8:
        raise SystemExit(f"[err] invalid stage-2 flag record count: {count}")
    for index in range(count):
        record = table + index * 16
        flags = u32(image, record)
        destination = u32(image, record + 4)
        size = u32(image, record + 8)
        if destination <= 0 or size <= 0 or destination + size > len(image):
            raise SystemExit(f"[err] flag record {index} is out of range")
        if flags & 1:
            decrypt_rol3(image, destination, size, destination)
        if flags & 0x10:
            apply_nested_prefix(image, destination)
        if flags & 2 == 0:
            continue
        cursor = destination
        for _ in range(64):
            if cursor + 16 > len(image):
                break
            decrypt_rol2(image, cursor, 16, cursor)
            source = u32(image, cursor)
            length = u32(image, cursor + 4)
            target = u32(image, cursor + 8)
            length2 = u32(image, cursor + 12)
            if length == 0:
                break
            cursor += 16
            if length != length2 or target + length > len(image) or source + length > len(image):
                continue
            if any(image[source + i] for i in range(min(64, length))):
                image[target:target + length] = image[source:source + length]
    for rva, size, key, incoming in layout["rol3_tail"]:
        decrypt_rol3(image, rva, size, key, incoming)


def apply_nested_prefix(image: bytearray, destination: int) -> None:
    decrypt_rol2(image, destination, 16, destination)
    first_start = u32(image, destination)
    first_end = u32(image, destination + 4)
    second_start = u32(image, destination + 8)
    second_end = u32(image, destination + 12)
    if (first_end <= first_start or destination + first_end > len(image)
            or second_end <= second_start or destination + second_end > len(image)):
        raise SystemExit("[err] nested stage-2 prefix is invalid")
    decrypt_rol2(image, destination + first_start, first_end - first_start, destination + first_start)
    decrypt_rol2(image, destination + second_start, second_end - second_start, destination + second_start)


# --------------------------------------------------------------------------- #
# auto key scan (structure judges: version agnostic, every hit verified)
# --------------------------------------------------------------------------- #
def valid_record(buf, off: int) -> bool:
    dest = u32(buf, off)
    return (dest == u32(buf, off + 8) and dest >= 0x1000
            and 0 < u32(buf, off + 4) <= MAX_SPAN and 0 < u32(buf, off + 12) <= MAX_SPAN)


def find_record_table(buf, lo: int = 0, hi: int | None = None, min_run: int = MIN_RUN):
    """Longest run of valid 16 B records -> (offset, count, sentinel) or (-1, 0, 0)."""
    hi = len(buf) if hi is None else min(hi, len(buf))
    best = (-1, 0, 0)
    off = lo
    limit = hi - RECORD_LEN * min_run
    while off < limit:
        if valid_record(buf, off):
            k = off
            while k + RECORD_LEN <= hi and valid_record(buf, k):
                k += RECORD_LEN
            count = (k - off) // RECORD_LEN
            if count >= min_run and count > best[1]:
                best = (off, count, u32(buf, off))
                off = k
                continue
        off += 4
    return best


def valid_code_entry(buf, off: int) -> bool:
    val = u16(buf, off)
    ln = buf[off + 2]
    if not 4 <= ln <= 24:
        return False
    if val & 0x8000:
        return (val & 0x7F) < 256
    return 256 <= val < 8192


def find_pass3_table(buf, hint: int | None = None, lo: int = 0, hi: int | None = None,
                     span: int = 0x400000, min_run: int = MIN_RUN):
    """3 B {u16 val, u8 len} code table with a strict successor check -> (offset, entries)."""
    hi = len(buf) if hi is None else min(hi, len(buf))

    def longest(start: int, end: int):
        best = (-1, 0)
        off = max(0, start)
        limit = min(end, hi)
        while off + 3 <= limit:
            if valid_code_entry(buf, off):
                k = off
                while k + 3 <= limit and valid_code_entry(buf, k):
                    k += 3
                count = (k - off) // 3
                if count >= min_run and count > best[1]:
                    best = (off, count)
                    off = k + 3
                    continue
            off += 3
        return best

    def strict(off: int, count: int) -> bool:
        for i in range(count):
            val = u16(buf, off + 3 * i)
            if not val & 0x8000 and val >= count:
                return False
        return True

    if hint is not None:
        off, count = longest(hint - span // 2, min(hi, hint + span))
        if count and strict(off, count):
            return off, count
    off, count = longest(lo, hi)
    return off, count


class Helper:
    """Expose a native helper window (RWX) with its entry at window + entry_offset."""

    def __init__(self, window: bytes, entry_offset: int = HELPER_ENTRY) -> None:
        if sys.platform != "win32":
            raise SystemExit("[err] the stage-2 body helper is Windows x64 machine code")
        self.buffer = ctypes.create_string_buffer(window, len(window) + 64)
        if entry_offset + 64 > len(window):
            raise SystemExit("[err] helper entry lies outside the window")
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.VirtualProtect.argtypes = [ctypes.c_void_p, ctypes.c_size_t,
                                            ctypes.c_uint32, ctypes.POINTER(ctypes.c_uint32)]
        old = ctypes.c_uint32()
        address = ctypes.addressof(self.buffer)
        if not kernel32.VirtualProtect(address, len(self.buffer), 0x40, ctypes.byref(old)):
            raise SystemExit(f"[err] VirtualProtect failed: {ctypes.get_last_error()}")
        proto = ctypes.CFUNCTYPE(None, ctypes.c_void_p, ctypes.c_uint, ctypes.c_void_p, ctypes.c_void_p)
        self.entry = proto(address + entry_offset)

    def call(self, key_buf, source: int, target: int) -> None:
        self.entry(key_buf, RND, source, target)


def record_offsets(records, payload: int) -> list[int]:
    offs, cursor = [], payload
    for _, packed_size, _ in records:
        offs.append(cursor)
        cursor += align4(packed_size)
    return offs


def mid_no_sbox(helper: Helper, key_buf, cipher: bytes) -> bytes:
    """helper(key, 14, ..) per 16 B block + XOR chain; sbox is applied by the caller."""
    n = len(cipher)
    source = ctypes.create_string_buffer(bytes(cipher), n + 16)
    target = ctypes.create_string_buffer(bytes(cipher), n + 16)
    src_addr, dst_addr = ctypes.addressof(source), ctypes.addressof(target)
    call = helper.call
    for j in range(0, n & ~15, 16):
        call(key_buf, src_addr + j, dst_addr + j)
    mid = bytearray(ctypes.string_at(dst_addr, n))
    xor_chain(mid, cipher, n & ~15)
    return bytes(mid)


def record_mid(helper: Helper, key_buf, cipher: bytes, packed_size: int, span: int,
               sbox: bytes) -> bytearray:
    """Stage-2b mid buffer for one record, shared by the decoder and the payload oracles.

    `cipher` is the `align4(packed_size)` slice; the helper runs over 16 B blocks up to
    `packed_size & ~15`, bytes in [full, span) keep their cipher value / zero padding,
    the XOR chain covers exactly [0, full) and the sbox covers the payload bytes.
    """
    full = packed_size & ~15
    source = ctypes.create_string_buffer(bytes(cipher), span + 16)
    target = ctypes.create_string_buffer(bytes(cipher), span + 16)
    src_addr, dst_addr = ctypes.addressof(source), ctypes.addressof(target)
    call = helper.call
    for offset in range(0, full, 16):
        call(key_buf, src_addr + offset, dst_addr + offset)
    mid = bytearray(ctypes.string_at(dst_addr, span))
    xor_chain(mid, cipher, full)
    mid[:packed_size] = mid[:packed_size].translate(sbox)
    return mid


def derive_sbox(helper: Helper, key_buf, records, offs, packed: bytes, reference: bytes):
    """Majority vote over (helper output -> plaintext) on literal-copy records."""
    votes: dict[int, dict[int, int]] = {}
    seen = 0
    for i, (dest, packed_size, unpacked) in enumerate(records):
        if packed_size != unpacked or dest + packed_size > len(reference) or offs[i] + packed_size > len(packed):
            continue
        mid = mid_no_sbox(helper, key_buf, packed[offs[i]:offs[i] + packed_size])
        want = reference[dest:dest + packed_size]
        seen += packed_size
        for x, p in zip(mid, want):
            votes.setdefault(x, {})[p] = votes.setdefault(x, {}).get(p, 0) + 1
    sbox = bytearray(256)
    conflicts = 0
    for x, v in votes.items():
        p, count = max(v.items(), key=lambda kv: kv[1])
        conflicts += sum(c for pp, c in v.items() if pp != p)
        sbox[x] = p
    return bytes(sbox), {"bytes": seen, "covered": len(votes), "conflicts": conflicts}


def payload_by_reference(helper: Helper, key_buf, records, packed: bytes, reference: bytes,
                         centre: int, sample: int = 384, tol: int = 4):
    """Cursor whose literal-copy records decode to the reference (zero conflicts).

    Coarse byte-exact pass within +/-0x8000 of the hint, then every surviving candidate is
    re-checked on three literal-copy records spread across the table (a short sample is
    ambiguous when the packed file repeats spans). Falls back to a 4-aligned sweep over
    +/-0x40000 only if nothing survives.
    """
    literal = [i for i, r in enumerate(records) if r[1] == r[2]]
    if not literal:
        return None, "no literal-copy record"
    prefixes = [0] * (len(records) + 1)
    for i, (_, packed_size, _) in enumerate(records):
        prefixes[i + 1] = prefixes[i] + align4(packed_size)

    def conflicts(f: int, index: int, n: int) -> int:
        dest, packed_size, unpacked = records[index]
        n = min(n, packed_size, unpacked, len(reference) - dest)
        start = f + prefixes[index]
        if n < 32 or start < 0 or start + n > len(packed):
            return n + 1
        mid = mid_no_sbox(helper, key_buf, packed[start:start + n])
        seen, bad = {}, 0
        for x, p in zip(mid, reference[dest:dest + n]):
            if seen.setdefault(x, p) != p:
                bad += 1
                if bad > tol:
                    break
        return bad

    checks = [literal[0]]
    if len(literal) > 8:
        checks += [literal[len(literal) // 3], literal[(2 * len(literal)) // 3]]

    def survive(f: int):
        return all(conflicts(f, index, sample) == 0 for index in checks)

    def scan(lo: int, hi: int, stride: int, limit: int):
        lo, hi = max(0, lo), min(len(packed), hi)
        found = []
        for f in range(lo, hi, stride):
            if conflicts(f, checks[0], 256) == 0:
                found.append(f)
                if len(found) > limit:
                    break
        return found

    candidates = scan(centre - 0x8000, centre + 0x8000, 1, 64)
    survivors = [f for f in candidates if survive(f)]
    if len(survivors) == 1:
        return survivors[0], f"unique cursor (byte-exact pass, {len(candidates)} raw candidates)"
    if not survivors:
        candidates = scan(centre - PAYLOAD_WINDOW, centre + PAYLOAD_WINDOW, 4, 64)
        survivors = [f for f in candidates if survive(f)]
        if len(survivors) == 1:
            return survivors[0], f"unique cursor (4-aligned sweep, {len(candidates)} raw candidates)"
    if not survivors:
        return None, "no zero-conflict cursor in the search window"
    return None, f"{len(survivors)} candidates survive cross-record refinement"


def payload_by_stream(helper: Helper, key_buf, sbox: bytes, records, packed: bytes,
                      table: bytes, centre: int, probe_count: int = 6):
    """No-reference fallback: a correct cursor makes every compressed record decode cleanly."""
    probe = [i for i, r in enumerate(records) if r[1] != r[2]][:probe_count]
    if not probe:
        return None, "no compressed records to test"
    lo = max(0, centre - PAYLOAD_WINDOW)
    hi = min(len(packed), centre + PAYLOAD_WINDOW)

    def score(cursor: int, subset) -> int:
        offs = record_offsets(records, cursor)
        good = 0
        for i in subset:
            dest, packed_size, unpacked = records[i]
            store = align4(packed_size)
            span = align16(store)
            if offs[i] + store > len(packed):
                return -1
            cipher = packed[offs[i]:offs[i] + store]
            mid = record_mid(helper, key_buf, cipher, packed_size, span, sbox)
            scratch = bytearray(unpacked + 64)
            bits = decode_stream(bytes(mid), span, table, scratch, 0, unpacked)
            if bits >= 0 and (bits + 7) // 8 == packed_size:
                good += 1
        return good

    def try_cursor(f: int):
        # one record is a cheap pre-filter; only survivors get the full sample set decoded
        if lo <= f < hi and score(f, probe[:1]) == 1 and score(f, probe) == len(probe):
            return f
        return None

    scanned = 0

    def sweep(step: int, label: str, forward: bool):
        nonlocal scanned
        rng = range(centre, hi, step) if forward else range(centre - step, lo, -step)
        for f in rng:
            scanned += 1
            hit = try_cursor(f)
            if hit is not None:
                return hit, f"all {len(probe)} sampled compressed records validate ({label})"
            if scanned % 2048 == 0:
                log(f"[payload] stream oracle: {scanned:,} candidates scanned…")
        return None, ""

    for step, label in ((16, "4-aligned"),):
        for forward in (True, False):
            hit, why = sweep(step, label, forward)
            if hit is not None:
                return hit, why

    # byte-exact sweep as a last resort, but bound its cost: with a wrong sbox (per-build
    # table) the score stays 0 everywhere and an unbounded sweep would run for hours.
    best = 0
    for tried, f in enumerate(list(range(centre, hi)) + list(range(centre - 1, lo, -1))):
        got = score(f, probe)
        best = max(best, got)
        if got == len(probe):
            return f, f"all {len(probe)} sampled compressed records validate (byte-exact)"
        if tried >= 4096 and best == 0:
            break
    return None, (f"no cursor in +/-0x{PAYLOAD_WINDOW:X} passes the stream invariant "
                  f"(best sample score {best}/{len(probe)})")


# --------------------------------------------------------------------------- #
# stage 2b: body decode
# --------------------------------------------------------------------------- #
def peek32(source: bytes, length: int, bit_offset: int) -> int:
    byte_offset = bit_offset >> 3
    length = min(length, len(source))
    value = 0
    for i in range(4):
        if byte_offset + i < length:
            value |= source[byte_offset + i] << (8 * i)
    return (value >> (bit_offset & 7)) & 0xFFFFFFFF


def decode_stream(source: bytes, source_length: int, table: bytes, destination,
                  start: int, length: int) -> int:
    """Port of the build's bit-unpack decoder; returns bits consumed, or -output on error."""
    bit_offset = 0
    output = start
    end = start + length
    hold = 0
    table_len = len(table)
    while output < end:
        bits = peek32(source, source_length, bit_offset)
        index = bits & 0xFF
        entry = index * 3
        if entry + 3 > table_len:
            return -output
        node = u16(table, entry)
        bit_length = table[entry + 2]
        if node & 0x8000:
            symbol, length_bits = node & 0x7FFF, bit_length
        else:
            consumed = bit_length + 1
            mask = 1 << (consumed - 1)
            index = (node & 0x7FFF) + (1 if bits & mask else 0)
            for _ in range(32):
                entry = index * 3
                if entry + 3 > table_len:
                    return -output
                node = u16(table, entry)
                bit_length = table[entry + 2]
                if node & 0x8000:
                    break
                mask <<= 1
                consumed += 1
                index = (node & 0x7FFF) + (1 if bits & mask else 0)
            if not node & 0x8000:
                return -output
            symbol, length_bits = node & 0x7FFF, bit_length

        bit_offset += length_bits
        flags = symbol & 0x300
        payload = symbol & 0xFF
        if flags == 0:
            destination[output] = payload
            output += 1
        elif flags == 0x100:
            hold = payload if hold == 0 else ((hold << 8) | payload)
        elif flags == 0x300:
            distance = hold + payload
            if distance <= 0 or payload > end - output or output < distance:
                return -output
            for _ in range(payload):
                destination[output] = destination[output - distance]
                output += 1
            hold = 0
        elif flags == 0x200:
            repetitions = hold if hold else 1
            unit = payload
            count = repetitions * unit
            if count > end - output:
                return -output
            if unit in (1, 2, 4) and output >= unit:
                for i in range(count):
                    destination[output + i] = destination[output - unit + i % unit]
            output += count
            hold = 0
        else:                                    # unreachable for a well-formed table
            destination[output] = payload
            output += 1
    return bit_offset


def decode_bodies(image: bytearray, packed: bytes, *, records, key: bytes, sbox: bytes,
                  table: bytes, payload: int, helper: Helper, reference: bytes | None,
                  expect_records: int | None) -> dict:
    key_buf = ctypes.create_string_buffer(bytes(key), 256)
    cursor = payload
    valid = invalid = 0
    compared = matched = 0
    started = time.time()
    for index, (dest, packed_size, unpacked) in enumerate(records):
        if (dest < 0x1000 or dest + unpacked > len(image) or packed_size == 0
                or packed_size > MAX_SPAN or unpacked == 0 or unpacked > MAX_SPAN):
            raise SystemExit(f"[err] record {index} is invalid (dest=0x{dest:X} "
                             f"pk=0x{packed_size:X} un=0x{unpacked:X})")
        store = align4(packed_size)
        span = align16(store)
        if cursor + store > len(packed):
            raise SystemExit(f"[err] record {index} exceeds packed input at 0x{cursor:X}")

        cipher = packed[cursor:cursor + store]
        mid = record_mid(helper, key_buf, cipher, packed_size, span, sbox)

        if packed_size == unpacked:
            image[dest:dest + unpacked] = mid[:unpacked]
            bits = packed_size * 8
        else:
            bits = decode_stream(bytes(mid), span, table, image, dest, unpacked)

        if bits >= 0 and (bits + 7) // 8 == packed_size:
            valid += 1
        else:
            invalid += 1
            if invalid <= 4:
                log(f"[warn] record {index}: dest=0x{dest:X} pk=0x{packed_size:X} "
                    f"un=0x{unpacked:X} bits={bits}")

        if reference is not None and dest + unpacked <= len(reference):
            got = bytes(image[dest:dest + unpacked])
            want = reference[dest:dest + unpacked]
            compared += unpacked
            matched += sum(1 for a, b in zip(got, want) if a == b)

        cursor += store

    elapsed = time.time() - started
    log(f"[body] records={len(records):,} streams={valid:,}/{invalid:,} payload=0x{payload:X} "
        f"({elapsed:.1f}s)")
    if reference is not None and compared:
        log(f"[body] reference match={matched:,}/{compared:,} ({matched / compared:.6%})")
    if expect_records is not None and len(records) != expect_records:
        raise SystemExit(f"[err] record-count mismatch: expected {expect_records}, got {len(records)}")
    if invalid:
        raise SystemExit(f"[err] body stream invariant failed: valid={valid}, invalid={invalid}")
    return {"records": len(records), "valid": valid, "invalid": invalid,
            "compared": compared, "matched": matched, "cursor_end": cursor}


# --------------------------------------------------------------------------- #
# stage 3: original PE rebuild
# --------------------------------------------------------------------------- #
def rebuild_original_pe(image: bytearray, preset: dict) -> int:
    sections = preset["sections"]
    final_size = preset["final_image_size"]
    if u16(image, 0) != 0x5A4D:
        raise SystemExit("[err] decoded image is not an MZ executable")
    pe = u32(image, 0x3C)
    if pe + 0x108 > len(image) or u32(image, pe) != 0x4550:
        raise SystemExit("[err] decoded image has an invalid PE header")
    optional = pe + 24
    section_table = optional + u16(image, pe + 20)
    if section_table + len(sections) * 40 > len(image):
        raise SystemExit("[err] decoded section table is out of range")

    put16(image, pe + 6, len(sections))
    put32(image, optional + 8, preset["size_of_code"])
    put32(image, optional + 56, final_size)
    put32(image, optional + 112 + 8, 0)
    put32(image, optional + 112 + 12, 0)
    put32(image, optional + 112 + 40, preset["debug_dir"][0])
    put32(image, optional + 112 + 44, preset["debug_dir"][1])

    for i, (name, vsize, vaddr, chars) in enumerate(sections):
        section = section_table + i * 40
        image[section:section + 40] = b"\0" * 40
        image[section:section + len(name)] = name.encode("ascii")
        nxt = sections[i + 1][2] if i + 1 < len(sections) else final_size
        put32(image, section + 8, vsize)
        put32(image, section + 12, vaddr)
        put32(image, section + 16, nxt - vaddr)
        put32(image, section + 20, vaddr)
        put32(image, section + 36, chars)

    debug_rva = u32(image, optional + 112 + 48)
    debug_size = u32(image, optional + 112 + 52)
    if debug_rva + debug_size <= len(image):
        for offset in range(0, max(0, debug_size - 27), 28):
            put32(image, debug_rva + offset + 24, u32(image, debug_rva + offset + 20))
    log(f"[pe] rebuilt original layout: {len(sections)} sections, size 0x{final_size:X} "
        f"({preset.get('name', 'custom')})")
    return final_size


# --------------------------------------------------------------------------- #
# input resolution
# --------------------------------------------------------------------------- #
def read_key(value: str) -> bytes:
    path = Path(value)
    if path.exists():
        return path.read_bytes()
    text = value[2:] if value.lower().startswith("0x") else value
    try:
        return bytes.fromhex(text)
    except ValueError:
        raise SystemExit(f"[err] --key must be a file path or hex string, got {value!r}")


def load_profile(directory: Path) -> dict:
    """Load a .NET-tool style profile dir (helper_tab/helper_fn/key/pass3/guard)."""
    def pick(pattern: str) -> Path | None:
        for p in sorted(directory.glob(pattern)):
            if p.suffix.lower() in (".json", ".txt", ".pkl"):
                continue
            return p
        return None

    table = pick("helper_tab*.bin")
    code = pick("helper_fn*.bin")
    key_path = pick("*key*.bin")
    pass3_path = pick("*pass3tab*.bin")
    guard = pick("*guard_s2*.bin")
    if guard is None:
        raise SystemExit(f"[err] profile directory {directory} has no guard/stage-2 blob")
    window = bytearray(0x8000)
    entry = 0x16C4
    if table is not None:
        data = table.read_bytes()
        window[0:len(data)] = data
    if code is not None:
        data = code.read_bytes()
        window[entry:entry + len(data)] = data
    return {"window": bytes(window), "entry": entry,
            "key": key_path.read_bytes() if key_path else None,
            "pass3": pass3_path.read_bytes() if pass3_path else None,
            "guard": guard}


def scan_inputs(image: bytes, packed: bytes, meta: dict, reference: bytes | None):
    """Derive records/pass3/key/helper from the stage-1 image; print every provenance."""
    lo = min(max(0, meta["written"][0]), len(image))
    hi = min(max(lo, meta["written"][1]), len(image))
    log(f"[scan] stage-1 written region 0x{lo:X}-0x{hi:X} ({hi - lo:,} bytes)")

    off, count, sentinel = find_record_table(image, lo, hi)
    if not count:
        log("[scan] record table not in the written region; scanning the whole image")
        off, count, sentinel = find_record_table(image, 0, len(image))
    if not count:
        raise SystemExit("[err] record table not found - pass --records/--profile")
    log(f"[scan] records @0x{off:X} n={count:,} sentinel=0x{sentinel:X}")

    pass3_off, pass3_count = find_pass3_table(image, hint=hi - 0x2000, lo=lo, hi=hi)
    if not pass3_count:
        log("[scan] pass3 table not in the written region; scanning the whole image")
        pass3_off, pass3_count = find_pass3_table(image, None, 0, len(image))
    if not pass3_count:
        raise SystemExit("[err] pass3 code table not found - pass --pass3/--profile")
    log(f"[scan] pass3 @0x{pass3_off:X} entries={pass3_count:,} ({pass3_count * 3:,} B)")

    key_rva = pass3_off + KEY_DELTA
    if key_rva + KEY_LEN > len(image):
        raise SystemExit("[err] derived key RVA is outside the image - pass --key")
    key = bytes(image[key_rva:key_rva + KEY_LEN])
    log(f"[scan] key @0x{key_rva:X} ({KEY_LEN} B, pass3+0x{KEY_DELTA:X}) "
        f"sha256={sha256(key)[:16]}")

    helper_rva = pass3_off - HELPER_DELTA
    if helper_rva < HELPER_ENTRY:
        raise SystemExit("[err] derived helper RVA is outside the image - pass --helper")
    win_lo = helper_rva - HELPER_ENTRY
    win_hi = min(len(image), win_lo + HELPER_WINDOW)
    window = bytes(image[win_lo:win_hi])
    padded = HELPER_WINDOW - len(window)
    window += b"\0" * padded
    log(f"[scan] helper @0x{helper_rva:X} window 0x{win_lo:X}-0x{win_hi:X} "
        f"(zero padded {padded:,} B, pass3-0x{HELPER_DELTA:X})")

    records = [(u32(image, off + RECORD_LEN * i), u32(image, off + RECORD_LEN * i + 4),
                u32(image, off + RECORD_LEN * i + 12)) for i in range(count)]
    table = bytes(image[pass3_off:pass3_off + pass3_count * 3])
    return {"records": records, "table": table, "key": key, "window": window,
            "entry": HELPER_ENTRY, "pass3_rva": pass3_off, "records_rva": off}


def load_carve(directory: Path) -> dict:
    """Load a notes/stable-inputs.py carve directory (key/sbox/pass3/records/helper_blk)."""
    def read(name: str) -> bytes | None:
        path = directory / name
        return path.read_bytes() if path.exists() else None

    records = None
    if (directory / "records.bin").exists():
        blob = (directory / "records.bin").read_bytes()
        records = [(u32(blob, i * RECORD_LEN), u32(blob, i * RECORD_LEN + 4),
                    u32(blob, i * RECORD_LEN + 12)) for i in range(len(blob) // RECORD_LEN)]
    elif (directory / "records.pkl").exists():
        import pickle                                    # local research artifact only
        loaded = pickle.loads((directory / "records.pkl").read_bytes())
        records = [(int(r[0]), int(r[1]), int(r[2])) for r in loaded]
    manifest = {}
    if (directory / "manifest.json").exists():
        manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8"))
    payload = manifest.get("payload")
    if not isinstance(payload, int):
        payload = None
    return {"records": records, "key": read("key.bin"), "sbox": read("sbox.bin"),
            "pass3": read("pass3.bin"), "window": read("helper_blk.bin"),
            "entry": HELPER_ENTRY, "payload": payload, "manifest": manifest}


def key_consistency(helper: Helper, key: bytes, records, offs, packed: bytes, reference: bytes,
                    rows: int = 12, sample: int = 384) -> tuple[int, int]:
    """Sampled oracle: for a correct key the sbox-free helper output is a function of the
    reference byte inside every row (a wrong key collides within a few hundred bytes)."""
    literal = [i for i, r in enumerate(records) if r[1] == r[2]]
    if not literal:
        return 0, 0
    stride = max(1, len(literal) // rows)
    picked = literal[::stride][:rows]
    key_buf = ctypes.create_string_buffer(key, 256)
    clean = 0
    for i in picked:
        dest, packed_size, _ = records[i]
        n = min(sample, packed_size)
        if offs[i] + n > len(packed) or dest + n > len(reference):
            continue
        mid = mid_no_sbox(helper, key_buf, packed[offs[i]:offs[i] + n])
        seen, ok = {}, True
        for x, p in zip(mid, reference[dest:dest + n]):
            if seen.setdefault(x, p) != p:
                ok = False
                break
        clean += 1 if ok else 0
    return clean, len(picked)


def scan_dump(dump: bytes, reference: bytes | None) -> dict:
    """Auto key scan over a loader-workspace dump (the only place the plaintext record
    table and code table exist; both are ciphertext inside the image, see case E-010)."""
    off, count, sentinel = find_record_table(dump)
    if not count:
        raise SystemExit("[err] dump scan: record table not found (no structure hit)")
    log(f"[dump] records @0x{off:X} n={count:,} sentinel=0x{sentinel:X}")

    pass3_off, pass3_count = find_pass3_table(dump, hint=off)
    if not pass3_count:
        raise SystemExit("[err] dump scan: pass3 code table not found")
    log(f"[dump] pass3 @0x{pass3_off:X} entries={pass3_count:,} ({pass3_count * 3:,} B)")

    key_rva = pass3_off + KEY_DELTA
    key = bytes(dump[key_rva:key_rva + KEY_LEN])
    if len(key) != KEY_LEN:
        raise SystemExit("[err] dump scan: key hint (pass3+0x10C6) runs past the dump end")
    log(f"[dump] key @0x{key_rva:X} ({KEY_LEN} B, pass3+0x{KEY_DELTA:X}) "
        f"sha256={sha256(key)[:16]}")

    entry = pass3_off - HELPER_DELTA
    win_lo = max(0, entry - HELPER_ENTRY)
    window = bytes(dump[win_lo:win_lo + HELPER_WINDOW])
    padded = HELPER_WINDOW - len(window)
    window += b"\0" * padded
    log(f"[dump] helper @0x{entry:X} window 0x{win_lo:X}-0x{win_lo + len(dump) - win_lo:X} "
        f"(zero padded {padded:,} B, pass3-0x{HELPER_DELTA:X})")

    records = [(u32(dump, off + RECORD_LEN * i), u32(dump, off + RECORD_LEN * i + 4),
                u32(dump, off + RECORD_LEN * i + 12)) for i in range(count)]
    table = bytes(dump[pass3_off:pass3_off + pass3_count * 3])
    return {"records": records, "table": table, "key": key, "window": window,
            "entry": HELPER_ENTRY, "pass3_rva": pass3_off, "records_rva": off}


class LazyScan:
    """Scan the image/dump only when a needed input is missing.

    The plaintext record table and code table exist only in the loader workspace (they are
    ciphertext inside the image), so a scan is pointless - and must not abort the run - when
    --profile/--carve/manual arguments already supply everything.
    """

    def __init__(self, factory) -> None:
        self._factory = factory
        self._cache = None

    def __getitem__(self, name: str):
        if self._cache is None:
            self._cache = self._factory()
        return self._cache[name]


# --------------------------------------------------------------------------- #
# main
# --------------------------------------------------------------------------- #
def main() -> int:
    parser = argparse.ArgumentParser(
        description="Offline static decryptor for the KONN-packed gakumas GameAssembly.dll",
        formatter_class=argparse.RawDescriptionHelpFormatter, epilog=__doc__)
    parser.add_argument("packed", help="input GameAssembly.dll (KONN packed)")
    parser.add_argument("out", nargs="?", help="output image (omit with --stage1-only)")
    parser.add_argument("--profile", help="frozen .NET-tool profile directory (manual inputs)")
    parser.add_argument("--carve", help="stable-inputs.py carve directory (derived inputs)")
    parser.add_argument("--dump", help="loader-workspace dump: auto key scan source "
                                       "(records/pass3/key/helper live only there, see E-010)")
    parser.add_argument("--key", help="stage-2 key: file path or hex string (240 B)")
    parser.add_argument("--sbox", help="sbox table file (256 B)")
    parser.add_argument("--pass3", help="pass3 code table file (3 B entries)")
    parser.add_argument("--records", help="record table file (16 B entries)")
    parser.add_argument("--helper", help="helper window file (0x20000 B, entry at +0x8000)")
    parser.add_argument("--payload", type=lambda s: int(s, 0), help="packed-file stream cursor")
    parser.add_argument("--reference", help="known-good decrypted image (enables plaintext oracles)")
    parser.add_argument("--stage1-only", action="store_true",
                        help="stop after stage 1 (diagnostics only, NOT a generator input)")
    parser.add_argument("--no-scan", action="store_true", help="never scan; require manual inputs")
    parser.add_argument("--no-self-decrypt", action="store_true",
                        help="skip the stage-2 loader self-decrypt records")
    parser.add_argument("--force-self-decrypt", action="store_true",
                        help="fail instead of skipping when the stage-2 layout does not fit")
    parser.add_argument("--no-rebuild-pe", action="store_true",
                        help="keep the packed PE header instead of restoring the original layout")
    parser.add_argument("--sections", help="section preset name/number or JSON file for the rebuild")
    parser.add_argument("--sections-from", help="copy the PE header/section table from this "
                                                "same-build image (no preset needed)")
    parser.add_argument("--expect", help="byte-compare the result against this image")
    parser.add_argument("--expect-hash", help="expected sha256 of the written output")
    parser.add_argument("--expect-records", type=int, help="fail unless exactly N records decode")
    parser.add_argument("--emit-profile", help="write the resolved inputs + manifest.json here")
    parser.add_argument("--header-key", type=lambda s: int(s, 0), help="override stage-1 header key")
    parser.add_argument("--header-rva", type=lambda s: int(s, 0), help="override stage-1 dest RVA")
    parser.add_argument("--header-split", type=lambda s: int(s, 0), help="override stage-1 split")
    args = parser.parse_args()

    packed_path = Path(args.packed)
    if not packed_path.exists():
        raise SystemExit(f"[err] packed input not found: {packed_path}")
    packed = packed_path.read_bytes()
    log(f"[in] {packed_path} ({len(packed):,} bytes, sha256={sha256(packed)[:16]})")

    if args.stage1_only:
        if not args.out:
            raise SystemExit("[err] --stage1-only also needs an output path")
        stage1(packed, Path(args.out), args.header_key, args.header_rva, args.header_split)
        return 0
    if not args.out:
        raise SystemExit("[err] missing output path")

    image, meta, _ = stage1(packed, None, args.header_key, args.header_rva, args.header_split)

    if args.no_self_decrypt:
        log("[skip] stage-2 self-decrypt disabled by --no-self-decrypt")
    else:
        image = stage2_self_decrypt(image, STAGE2_SELF_DECRYPT, force=args.force_self_decrypt)

    reference = Path(args.reference).read_bytes() if args.reference else None
    if reference is not None:
        log(f"[ref] {args.reference} ({len(reference):,} bytes, sha256={sha256(reference)[:16]})")

    profile = load_profile(Path(args.profile)) if args.profile else None
    carve = load_carve(Path(args.carve)) if args.carve else None
    dump = Path(args.dump).read_bytes() if args.dump else None
    if dump is not None:
        log(f"[dump] {args.dump} ({len(dump):,} bytes, sha256={sha256(dump)[:16]})")

    def run_scan():
        if args.no_scan:
            raise SystemExit("[err] inputs are missing and --no-scan was given "
                             "(need --profile, --carve, or --records/--pass3/--key/--helper)")
        return scan_dump(dump, reference) if dump is not None else scan_inputs(image, packed, meta, reference)

    scanned = LazyScan(run_scan)

    # records: manual > carve > profile > scan
    if args.records:
        blob = Path(args.records).read_bytes()
        if blob[:1] == b"\x80":                          # records.pkl from stable-inputs.py
            import pickle                                # local research artifact only
            loaded = pickle.loads(blob)
            records = [(int(r[0]), int(r[1]), int(r[2])) for r in loaded]
            log(f"[in] records from {args.records} (pickle): {len(records):,}")
        else:
            records = [(u32(blob, i * RECORD_LEN), u32(blob, i * RECORD_LEN + 4),
                        u32(blob, i * RECORD_LEN + 12)) for i in range(len(blob) // RECORD_LEN)]
            log(f"[in] records from {args.records}: {len(records):,}")
    elif carve and carve["records"]:
        records = carve["records"]
        log(f"[in] records from carve {args.carve}: {len(records):,}")
    elif profile is not None:
        guard = profile["guard"].read_bytes()
        offset = PROFILE_RECORDS_OFFSET
        records = []
        while offset + RECORD_LEN <= len(guard):
            if not (u32(guard, offset) == u32(guard, offset + 8) and u32(guard, offset) >= 0x1000
                    and 0 < u32(guard, offset + 4) <= MAX_SPAN and 0 < u32(guard, offset + 12) <= MAX_SPAN):
                break
            records.append((u32(guard, offset), u32(guard, offset + 4), u32(guard, offset + 12)))
            offset += RECORD_LEN
        if not records:
            raise SystemExit(f"[err] profile guard blob has no record table at 0x{PROFILE_RECORDS_OFFSET:X}")
        log(f"[in] records from profile guard blob @0x{PROFILE_RECORDS_OFFSET:X}: {len(records):,}")
    else:
        records = scanned["records"]

    # pass3
    if args.pass3:
        table = Path(args.pass3).read_bytes()
        log(f"[in] pass3 from {args.pass3}: {len(table) // 3:,} entries")
    elif carve and carve["pass3"]:
        table = carve["pass3"]
        log(f"[in] pass3 from carve: {len(table) // 3:,} entries")
    elif profile is not None and profile["pass3"]:
        table = profile["pass3"]
        log(f"[in] pass3 from profile: {len(table) // 3:,} entries")
    else:
        table = scanned["table"]

    # key
    if args.key:
        key = read_key(args.key)
        log(f"[in] key from --key ({len(key)} B, sha256={sha256(key)[:16]})")
    elif carve and carve["key"]:
        key = carve["key"]
        log(f"[in] key from carve ({len(key)} B, sha256={sha256(key)[:16]})")
    elif profile is not None and profile["key"]:
        key = profile["key"]
        log(f"[in] key from profile ({len(key)} B, sha256={sha256(key)[:16]})")
    else:
        key = scanned["key"]
    key = bytes(key)[:KEY_LEN].ljust(KEY_LEN, b"\0")

    # helper window
    if args.helper:
        window, entry = Path(args.helper).read_bytes(), HELPER_ENTRY
        log(f"[in] helper window from {args.helper} ({len(window):,} B, entry +0x{entry:X})")
    elif carve and carve["window"]:
        window, entry = carve["window"], carve["entry"]
        log(f"[in] helper window from carve ({len(window):,} B, entry +0x{entry:X})")
    elif profile is not None:
        window, entry = profile["window"], profile["entry"]
        log(f"[in] helper window from profile ({len(window):,} B, entry +0x{entry:X})")
    else:
        window, entry = scanned["window"], scanned["entry"]
    helper = Helper(window, entry)
    key_buf = ctypes.create_string_buffer(key, 256)

    # an explicitly supplied sbox (manual/carve) is needed before the stream oracle below
    explicit_sbox = None
    if args.sbox:
        explicit_sbox = Path(args.sbox).read_bytes()
    elif carve and carve["sbox"]:
        explicit_sbox = carve["sbox"]

    # payload cursor: manual > carve > reference oracle > stream oracle > documented hint
    payload = args.payload
    if payload is None and carve and carve["payload"] is not None:
        payload = carve["payload"]
        log(f"[payload] from carve manifest: 0x{payload:X}")
    if payload is None and reference is not None:
        payload, why = payload_by_reference(helper, key_buf, records, packed, reference, PAYLOAD_HINT)
        log(f"[payload] reference oracle: {why}" + (f" -> 0x{payload:X}" if payload else ""))
    if payload is None and not args.no_scan:
        oracle_sbox = explicit_sbox if explicit_sbox is not None else substitute_table()
        payload, why = payload_by_stream(helper, key_buf, oracle_sbox, records, packed, table, PAYLOAD_HINT)
        log(f"[payload] stream oracle: {why}" + (f" -> 0x{payload:#X}" if payload else ""))
    if payload is None:
        payload = PAYLOAD_HINT
        log(f"[payload] falling back to the hint 0x{payload:X} (no oracle confirmed it)")
    else:
        log(f"[payload] using 0x{payload:X}")

    # sbox: explicit > reference-derived > built-in
    if explicit_sbox is not None:
        sbox = explicit_sbox
        log(f"[in] sbox provided explicitly ({len(sbox)} B, sha256={sha256(sbox)[:16]})")
    elif reference is not None:
        sbox, stats = derive_sbox(helper, key_buf, records, record_offsets(records, payload),
                                  packed, reference)
        log(f"[sbox] derived from reference: covered={stats['covered']}/256 "
            f"conflicts={stats['conflicts']} sha256={sha256(sbox)[:16]}")
        builtin = substitute_table()
        log(f"[sbox] built-in table {'matches' if sbox == builtin else 'differs'} the derived one"
            + ("; using the derived one" if sbox != builtin else ""))
        if sbox != builtin and stats["covered"] != 256:
            raise SystemExit("[err] derived sbox coverage incomplete - pass --sbox")
    else:
        sbox = substitute_table()
        log(f"[sbox] built-in table (sha256={sha256(sbox)[:16]}) - pass --sbox or --reference "
            f"to override/derive")
    if len(sbox) != 256:
        raise SystemExit(f"[err] sbox must be 256 bytes, got {len(sbox)}")

    if reference is not None and not args.key:
        clean, rows = key_consistency(helper, key, records, record_offsets(records, payload),
                                      packed, reference)
        log(f"[key] oracle: {clean}/{rows} sampled literal-copy rows are self-consistent")
        if rows and clean < max(1, rows // 2):
            raise SystemExit("[err] key oracle rejected the derived key - pass --key with the "
                             "correct 240 B key (the key is per-build; see the case notes)")

    stats = decode_bodies(image, packed, records=records, key=key, sbox=sbox, table=table,
                          payload=payload, helper=helper, reference=reference,
                          expect_records=args.expect_records)

    if args.emit_profile:
        out_dir = Path(args.emit_profile)
        out_dir.mkdir(parents=True, exist_ok=True)
        records_blob = b"".join(struct.pack("<IIII", dest, pk, dest, un)
                                for dest, pk, un in records)
        (out_dir / "key.bin").write_bytes(key)
        (out_dir / "sbox.bin").write_bytes(sbox)
        (out_dir / "pass3.bin").write_bytes(table)
        (out_dir / "records.bin").write_bytes(records_blob)
        (out_dir / "helper_blk.bin").write_bytes(window[:HELPER_WINDOW])
        manifest = {
            "packed": str(packed_path), "packed_sha256": sha256(packed),
            "stage1": meta,
            "records": {"count": len(records), "sha256": sha256(records_blob)},
            "key": {"sha256": sha256(key)},
            "sbox": {"sha256": sha256(sbox)},
            "pass3": {"entries": len(table) // 3, "sha256": sha256(table)},
            "payload": payload,
            "helper": {"entry_in_window": entry, "sha256": sha256(window[:HELPER_WINDOW])},
            "decode": stats,
        }
        (out_dir / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
        log(f"[out] emitted profile inputs to {out_dir}")

    # stage 3
    final_size = len(image)
    if args.no_rebuild_pe:
        log("[pe] rebuilt layout disabled by --no-rebuild-pe")
    elif args.sections_from:
        source = Path(args.sections_from).read_bytes()
        source_pe = parse_pe(source)
        if source_pe is None:
            raise SystemExit(f"[err] --sections-from: {args.sections_from} is not a PE image")
        header_len = min(source_pe.size_of_headers, len(source), len(image))
        image[0:header_len] = source[0:header_len]
        final_size = min(source_pe.size_of_image, len(image))
        log(f"[pe] header/section table copied from {args.sections_from} "
            f"({len(source_pe.sections)} sections, SizeOfImage 0x{source_pe.size_of_image:X})")
        if source_pe.size_of_image > len(image):
            log(f"[pe] warning: reference SizeOfImage exceeds the decoded image; "
                f"writing {final_size:,} bytes")
    else:
        preset = None
        if args.sections:
            if args.sections in SECTION_PRESETS:
                preset = SECTION_PRESETS[args.sections]
            elif args.sections.isdigit() and int(args.sections) in SECTION_PRESETS:
                preset = SECTION_PRESETS[int(args.sections)]
            else:
                path = Path(args.sections)
                if not path.exists():
                    raise SystemExit(f"[err] --sections: unknown preset {args.sections!r}")
                preset = json.loads(path.read_text(encoding="utf-8"))
        else:
            preset = SECTION_PRESETS.get(len(records))
            if preset is None:
                log(f"[pe] no section preset for {len(records):,} records; keeping the packed "
                    f"header (pass --sections <json> to rebuild a different layout)")
        if preset is not None:
            final_size = rebuild_original_pe(image, preset)

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    temp = out_path.with_name(out_path.name + f".tmp-{time.time_ns():x}")
    temp.write_bytes(bytes(image[:final_size]))
    temp.replace(out_path)
    digest = sha256(bytes(image[:final_size]))
    log(f"[out] {out_path} ({final_size:,} bytes, sha256={digest})")

    if args.expect:
        expected = Path(args.expect).read_bytes()
        got = bytes(image[:final_size])
        if len(expected) != len(got):
            log(f"[expect] size differs: reference {len(expected):,} vs output {len(got):,} "
                f"- comparing the common prefix")
        n = min(len(expected), len(got))
        diff = sum(1 for i in range(n) if expected[i] != got[i]) if n else 0
        log(f"[expect] byte compare vs {args.expect}: {n - diff:,}/{n:,} equal "
            f"({(n - diff) / n:.6%}), differing={diff:,}")
        if diff:
            return 1
    if args.expect_hash:
        wanted = args.expect_hash.lower()
        if digest == wanted:
            log(f"[expect] sha256 matches {wanted}")
        else:
            log(f"[expect] sha256 DIFFERS from {wanted}")
            return 1
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        raise SystemExit(130)
    except OSError as exc:                        # missing input file, bad path, ...
        raise SystemExit(f"[err] {exc}")
