#!/usr/bin/env python3
"""Independent raw TES4 verifier for the P03-003 statistics/flags slice."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import struct
from pathlib import Path

WORKSPACE = Path(r"K:\ExampleWorkspace").resolve()


class Failure(Exception):
    pass


def fail(message: str) -> None:
    raise Failure(message)


def safe_file(value: str) -> Path:
    if not os.path.isabs(value) or "\x00" in value:
        fail("path must be absolute")
    path = Path(value).resolve()
    try:
        path.relative_to(WORKSPACE)
    except ValueError:
        fail("path outside workspace")
    if not path.is_file() or path.is_symlink():
        fail("path missing or linked")
    return path


def parse_subrecords(data: bytes, start: int, end: int) -> dict[str, bytes]:
    result: dict[str, bytes] = {}
    pos = start
    while pos < end:
        if pos + 6 > end:
            fail("truncated subrecord")
        sig = data[pos : pos + 4].decode("ascii")
        size = struct.unpack_from("<H", data, pos + 4)[0]
        pos += 6
        if sig == "XXXX":
            if size != 4 or pos + 4 > end:
                fail("invalid XXXX")
            size = struct.unpack_from("<I", data, pos)[0]
            pos += 4
            if pos + 6 > end:
                fail("missing extended subrecord")
            sig = data[pos : pos + 4].decode("ascii")
            pos += 6
        if pos + size > end or sig in result:
            fail(f"invalid or duplicate {sig}")
        result[sig] = data[pos : pos + size]
        pos += size
    if pos != end:
        fail("NPC payload boundary")
    return result


def find_npc(data: bytes, start: int, end: int, form_id: int, found: list[dict[str, bytes]]) -> None:
    pos = start
    while pos < end:
        if pos + 8 > end:
            fail("truncated record")
        sig = data[pos : pos + 4].decode("ascii")
        size = struct.unpack_from("<I", data, pos + 4)[0]
        record_end = pos + (size if sig == "GRUP" else 24 + size)
        if record_end > end or record_end < pos:
            fail("record boundary")
        if sig == "GRUP":
            find_npc(data, pos + 24, record_end, form_id, found)
        elif sig == "NPC_" and pos + 16 <= record_end and struct.unpack_from("<I", data, pos + 12)[0] == form_id:
            found.append(parse_subrecords(data, pos + 24, record_end))
        pos = record_end
    if pos != end:
        fail("parent boundary")


def npc(path: Path, form_id: int) -> dict[str, bytes]:
    found: list[dict[str, bytes]] = []
    data = path.read_bytes()
    find_npc(data, 0, len(data), form_id, found)
    if len(found) != 1:
        fail(f"expected one NPC, found {len(found)}")
    return found[0]


def flag_mask(edition: str, name: str) -> int:
    common = {"female": 1, "essential": 2, "ischargenfacepreset": 4, "respawn": 8, "autocalcstats": 0x10,
              "unique": 0x20, "doesntaffectstealthmeter": 0x40, "protected": 0x800, "summonable": 0x4000,
              "doesnotbleed": 0x10000, "bleedoutoverride": 0x20000, "oppositegenderanims": 0x80000,
              "simpleactor": 0x100000, "isghost": 0x20000000, "invulnerable": 0x80000000}
    if name == "pc-level-mult": return 0x80
    if name in common: return common[name]
    if edition == "fallout4": return {"fallout4calcforeachtemplate": 0x80, "fallout4noactivationorhellos": 0x800000,
                                       "fallout4diffusealphatest": 0x1000000}.get(name, 0)
    return {"skyrimusetemplate": 0x80, "skyrimloopedscript": 0x200000, "skyrimloopedaudio": 0x400000}.get(name, 0)


def raw_value(record: dict[str, bytes], edition: str, field: str) -> str | None:
    name = field.casefold()
    acbs = record.get("ACBS", b"")
    if name == "level":
        off = 8 if edition == "skyrimse" else 6
        if len(acbs) < off + 2: return None
        raw = struct.unpack_from("<H", acbs, off)[0]
        return f"{raw / 1000:g}" if struct.unpack_from("<I", acbs, 0)[0] & 0x80 else str(raw)
    if name.startswith("flag:"):
        mask = flag_mask(edition, name[5:])
        return str(bool(mask and len(acbs) >= 4 and struct.unpack_from("<I", acbs, 0)[0] & mask)).lower()
    offsets = {"xpvalueoffset": (4, "h"), "magickaoffset": (4, "h"), "staminaoffset": (6, "h"),
               "healthoffset": (20 if edition == "skyrimse" else 18, "h"), "calcminlevel": (10 if edition == "skyrimse" else 8, "H"),
               "calcmaxlevel": (12 if edition == "skyrimse" else 10, "H"), "speedmultiplier": (14, "h"),
               "dispositionbase": (16 if edition == "skyrimse" else 12, "h"), "bleedoutoverride": (22 if edition == "skyrimse" else 16, "h")}
    if name in offsets:
        off, fmt = offsets[name]
        return str(struct.unpack_from("<" + fmt, acbs, off)[0]) if len(acbs) >= off + 2 else None
    if name == "height":
        data = record.get("NAM6", b"")
        return f"{struct.unpack_from('<f', data, 0)[0]:g}" if len(data) >= 4 else None
    dnam = record.get("DNAM", b"")
    if name in {"playerhealth", "playermagicka", "playerstamina"}:
        off = {"playerhealth": 36, "playermagicka": 38, "playerstamina": 40}[name]
        return str(struct.unpack_from("<H", dnam, off)[0]) if len(dnam) >= off + 2 else None
    for prefix, base in (("skillvalue:", 0), ("skilloffset:", 18)):
        if name.startswith(prefix):
            skills = ["onehanded", "twohanded", "archery", "block", "smithing", "heavyarmor", "lightarmor", "pickpocket", "lockpicking", "sneak", "alchemy", "speech", "alteration", "conjuration", "destruction", "illusion", "restoration", "enchanting"]
            try: index = skills.index(name[len(prefix):])
            except ValueError: return None
            return str(dnam[base + index]) if len(dnam) > base + index else None
    return None


def verify_mutation_surface(source: dict[str, bytes], output: dict[str, bytes], edition: str, fields: set[str]) -> None:
    allowed: dict[str, set[int]] = {"ACBS": set(), "DNAM": set(), "NAM6": set()}
    for field in fields:
        name = field.casefold()
        if name == "level":
            off = 8 if edition == "skyrimse" else 6
            allowed["ACBS"].update(range(off, off + 2))
        elif name.startswith("flag:"):
            # Flags are a packed uint32; compare all non-requested bits below.
            allowed["ACBS"].update(range(0, 4))
        elif name in {"xpvalueoffset", "magickaoffset", "staminaoffset", "healthoffset", "calcminlevel", "calcmaxlevel", "speedmultiplier", "dispositionbase", "bleedoutoverride"}:
            offsets = {"xpvalueoffset": 4, "magickaoffset": 4, "staminaoffset": 6,
                       "healthoffset": 20 if edition == "skyrimse" else 18,
                       "calcminlevel": 10 if edition == "skyrimse" else 8,
                       "calcmaxlevel": 12 if edition == "skyrimse" else 10,
                       "speedmultiplier": 14, "dispositionbase": 16 if edition == "skyrimse" else 12,
                       "bleedoutoverride": 22 if edition == "skyrimse" else 16}
            off = offsets[name]; allowed["ACBS"].update(range(off, off + 2))
        elif name == "height": allowed["NAM6"].update(range(0, 4))
        elif name in {"playerhealth", "playermagicka", "playerstamina"}:
            off = {"playerhealth": 36, "playermagicka": 38, "playerstamina": 40}[name]; allowed["DNAM"].update(range(off, off + 2))
        elif name.startswith("skillvalue:") or name.startswith("skilloffset:"):
            skills = ["onehanded", "twohanded", "archery", "block", "smithing", "heavyarmor", "lightarmor", "pickpocket", "lockpicking", "sneak", "alchemy", "speech", "alteration", "conjuration", "destruction", "illusion", "restoration", "enchanting"]
            prefix = "skilloffset:" if name.startswith("skilloffset:") else "skillvalue:"
            if name[len(prefix):] not in skills: fail(f"unknown skill {field}")
            allowed["DNAM"].add((18 if prefix == "skilloffset:" else 0) + skills.index(name[len(prefix):]))
    for signature, mask in allowed.items():
        if source.get(signature, b"") == output.get(signature, b""): continue
        before, after = source.get(signature, b""), output.get(signature, b"")
        if len(before) != len(after): fail(f"target subrecord size changed: {signature}")
        if signature == "ACBS" and any(field.casefold().startswith("flag:") for field in fields):
            requested = 0
            for field in fields:
                if field.casefold().startswith("flag:"): requested |= flag_mask(edition, field.split(":", 1)[1].casefold())
            source_flags, output_flags = struct.unpack_from("<I", before, 0)[0], struct.unpack_from("<I", after, 0)[0]
            if (source_flags & ~requested) != (output_flags & ~requested): fail("unrequested ACBS flag drift")
            for i in range(4, len(before)):
                if before[i] != after[i] and i not in mask: fail("unrequested ACBS byte drift")
        else:
            for i, (left, right) in enumerate(zip(before, after)):
                if left != right and i not in mask: fail(f"unrequested {signature} byte drift")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--json", required=True)
    parser.add_argument("--edition", choices=("fallout4", "skyrimse"), required=True)
    parser.add_argument("--form-id", required=True)
    parser.add_argument("--expected", action="append", default=[])
    args = parser.parse_args()
    try:
        source, output, evidence_path = safe_file(args.source), safe_file(args.output), safe_file(args.json)
        evidence = json.loads(evidence_path.read_text(encoding="utf-8-sig"))
        if evidence.get("applied") is not True or hashlib.sha256(output.read_bytes()).hexdigest() != evidence.get("outputSha256", "").casefold():
            fail("CLI application/hash evidence")
        expected = dict(item.split("=", 1) for item in args.expected if "=" in item)
        if len(expected) != len(args.expected): fail("expected field=value")
        source_npc, output_npc = npc(source, int(args.form_id, 0)), npc(output, int(args.form_id, 0))
        changed = {"ACBS" if k.casefold().startswith(("level", "flag:", "xpvalueoffset", "magickaoffset", "staminaoffset", "healthoffset", "calcminlevel", "calcmaxlevel", "speedmultiplier", "dispositionbase", "bleedoutoverride")) else "NAM6" if k.casefold() == "height" else "DNAM" for k in expected}
        for field, value in expected.items():
            if raw_value(output_npc, args.edition, field) != value: fail(f"{field} raw value mismatch")
        for signature in set(source_npc) | set(output_npc):
            if signature not in changed and source_npc.get(signature) != output_npc.get(signature): fail(f"non-target drift: {signature}")
        verify_mutation_surface(source_npc, output_npc, args.edition, set(expected))
        print("RESULT PASS raw statistics/flags and non-target preservation")
        return 0
    except (Failure, OSError, ValueError, struct.error, json.JSONDecodeError) as error:
        print(f"RESULT FAIL {error}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
