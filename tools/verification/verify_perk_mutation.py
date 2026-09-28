#!/usr/bin/env python3
"""Independent raw TES4 verifier for NPC PRKR perk entries."""
from __future__ import annotations

import argparse
import hashlib
import json
import struct
from pathlib import Path

ROOT = Path(r"K:\ExampleWorkspace").resolve()


class Failure(Exception):
    pass


def fail(message: str) -> None:
    raise Failure(message)


def safe(value: str) -> Path:
    path = Path(value).resolve()
    try:
        path.relative_to(ROOT)
    except ValueError:
        fail("path outside workspace")
    if not path.is_file() or path.is_symlink():
        fail("missing or linked path")
    return path


def subrecords(data: bytes, start: int, end: int) -> dict[str, bytes]:
    result: dict[str, bytes] = {}
    pos = start
    while pos < end:
        if pos + 6 > end:
            fail("truncated subrecord")
        signature = data[pos : pos + 4].decode("ascii")
        size = struct.unpack_from("<H", data, pos + 4)[0]
        pos += 6
        if pos + size > end:
            fail("invalid subrecord")
        result[signature] = result.get(signature, b"") + data[pos : pos + size]
        pos += size
    return result


def find(data: bytes, start: int, end: int, form_id: int, found: list[dict[str, bytes]]) -> None:
    pos = start
    while pos < end:
        if pos + 8 > end:
            fail("truncated record")
        signature = data[pos : pos + 4].decode("ascii")
        size = struct.unpack_from("<I", data, pos + 4)[0]
        record_end = pos + (size if signature == "GRUP" else 24 + size)
        if record_end > end:
            fail("record boundary")
        if signature == "GRUP":
            find(data, pos + 24, record_end, form_id, found)
        elif signature == "NPC_" and struct.unpack_from("<I", data, pos + 12)[0] == form_id:
            found.append(subrecords(data, pos + 24, record_end))
        pos = record_end


def npc(path: Path, form_id: int) -> dict[str, bytes]:
    found: list[dict[str, bytes]] = []
    data = path.read_bytes()
    find(data, 0, len(data), form_id, found)
    if len(found) != 1:
        fail(f"expected one NPC, found {len(found)}")
    return found[0]


def perk_value(record: dict[str, bytes], plugin: str, edition: str) -> str:
    raw = record.get("PRKR", b"")
    entry_size = 5 if edition == "fallout4" else 8
    if len(raw) % entry_size:
        fail(f"PRKR is not {entry_size}-byte entry aligned")
    return ",".join(
        f"{plugin}|0x{struct.unpack_from('<I', raw, offset)[0]:08X}={raw[offset + 4]}"
        for offset in range(0, len(raw), entry_size)
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--json", required=True)
    parser.add_argument("--form-id", required=True)
    parser.add_argument("--plugin", required=True)
    parser.add_argument("--edition", choices=("fallout4", "skyrimse"), required=True)
    parser.add_argument("--perks", required=True)
    args = parser.parse_args()
    try:
        source = safe(args.source)
        output = safe(args.output)
        evidence = safe(args.json)
        document = json.loads(evidence.read_text(encoding="utf-8-sig"))
        if document.get("applied") is not True:
            fail("CLI application evidence")
        if hashlib.sha256(output.read_bytes()).hexdigest() != document.get("outputSha256", "").casefold():
            fail("output hash evidence")
        before = npc(source, int(args.form_id, 0))
        after = npc(output, int(args.form_id, 0))
        plugin = Path(args.plugin).name
        if perk_value(after, plugin, args.edition) != args.perks:
            fail("PRKR output mismatch")
        entries = args.perks.split(",") if args.perks else []
        perk_ids = [entry.split("=", 1)[0].casefold() for entry in entries]
        if len(set(perk_ids)) != len(perk_ids):
            fail("duplicate perk output")
        for signature in set(before) | set(after):
            if signature != "PRKR" and before.get(signature) != after.get(signature):
                fail(f"non-target drift: {signature}")
        print("RESULT PASS PRKR perk list and non-target preservation")
        return 0
    except (Failure, OSError, ValueError, struct.error, json.JSONDecodeError) as error:
        print(f"RESULT FAIL {error}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
