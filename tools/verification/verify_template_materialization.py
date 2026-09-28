#!/usr/bin/env python3
"""Independent raw TES4 verifier for supported NPC template materialization."""
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
    find(path.read_bytes(), 0, path.stat().st_size, form_id, found)
    if len(found) != 1:
        fail(f"expected one NPC, found {len(found)} for 0x{form_id:08X}")
    return found[0]


def acbs_without_template_bits(raw: bytes, game: str) -> bytes:
    offset = 14 if game == "fallout4" else 18
    if len(raw) < offset + 2:
        fail("ACBS is too short")
    result = bytearray(raw)
    result[offset : offset + 2] = b"\0\0"
    return bytes(result)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--json", required=True)
    parser.add_argument("--form-id", required=True)
    parser.add_argument("--game", required=True, choices=("fallout4", "skyrimse"))
    args = parser.parse_args()
    try:
        source = safe(args.source)
        output = safe(args.output)
        evidence_path = safe(args.json)
        evidence = json.loads(evidence_path.read_text(encoding="utf-8-sig"))
        if evidence.get("applied") is not True:
            fail("CLI application evidence does not report applied")
        if hashlib.sha256(output.read_bytes()).hexdigest() != evidence.get("outputSha256", "").casefold():
            fail("output hash evidence mismatch")
        form_id = int(args.form_id, 0)
        before = npc(source, form_id)
        after = npc(output, form_id)
        categories = [item for item in evidence.get("categories", []) if item.get("inherited") and item.get("supported")]
        if not categories:
            fail("no supported inherited categories in evidence")
        changed = {"ACBS"}
        game = args.game
        for item in categories:
            category = item["category"].replace("spellList", "spell-list")
            resolved = item.get("resolvedSource", {}).get("formId", {}).get("value")
            if not isinstance(resolved, int):
                fail(f"missing resolved source for {category}")
            source_record = npc(source, resolved)
            if category == "stats":
                if acbs_without_template_bits(after.get("ACBS", b""), game) != acbs_without_template_bits(source_record.get("ACBS", b""), game):
                    fail("ACBS stats materialization mismatch")
                changed.add("ACBS")
            elif category == "factions":
                if after.get("SNAM", b"") != source_record.get("SNAM", b""):
                    fail("SNAM factions materialization mismatch")
                changed.add("SNAM")
            elif category == "spell-list":
                if after.get("SPLO", b"") != source_record.get("SPLO", b""):
                    fail("SPLO materialization mismatch")
                changed.add("SPLO")
                changed.add("SPCT")
            elif category == "keywords":
                if after.get("KWDA", b"") != source_record.get("KWDA", b""):
                    fail("KWDA materialization mismatch")
                changed.add("KWDA")
                changed.add("KSIZ")
            else:
                fail(f"unsupported category in evidence: {category}")
        for signature in set(before) | set(after):
            if signature not in changed and before.get(signature) != after.get(signature):
                fail(f"non-target drift: {signature}")
        acbs = after.get("ACBS", b"")
        template_offset = 14 if game == "fallout4" else 18
        if len(acbs) < template_offset + 2 or struct.unpack_from("<H", acbs, template_offset)[0] != 0:
            fail("template flags were not cleared")
        print("RESULT PASS template categories, resolved values, flag clearing, and non-target preservation")
        return 0
    except (Failure, OSError, ValueError, struct.error, json.JSONDecodeError) as error:
        print(f"RESULT FAIL {error}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
