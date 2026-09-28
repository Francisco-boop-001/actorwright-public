"""Independent raw TES4 verifier for the section-reset contract.

It deliberately does not load the C# model. The requested section must equal the
baseline NPC subrecord and every other NPC subrecord must equal the pre-reset copy.
"""
from __future__ import annotations

import argparse
import json
import struct
from pathlib import Path


def records(data: bytes, start: int, end: int, form_id: int) -> dict[str, bytes] | None:
    pos = start
    while pos + 8 <= end:
        signature = data[pos : pos + 4].decode("ascii", errors="strict")
        size = struct.unpack_from("<I", data, pos + 4)[0]
        record_end = pos + (24 + size if signature != "GRUP" else size)
        if record_end > end:
            raise ValueError("record exceeds file boundary")
        if signature == "GRUP":
            found = records(data, pos + 24, record_end, form_id)
            if found is not None:
                return found
        elif signature == "NPC_" and struct.unpack_from("<I", data, pos + 12)[0] == form_id:
            fields: dict[str, bytearray] = {}
            cursor = pos + 24
            while cursor + 6 <= record_end:
                field = data[cursor : cursor + 4].decode("ascii", errors="strict")
                field_size = struct.unpack_from("<H", data, cursor + 4)[0]
                field_end = cursor + 6 + field_size
                if field_end > record_end:
                    raise ValueError("subrecord exceeds NPC boundary")
                fields.setdefault(field, bytearray()).extend(data[cursor + 6 : field_end])
                cursor = field_end
            if cursor != record_end:
                raise ValueError("NPC payload has trailing bytes")
            return {key: bytes(value) for key, value in fields.items()}
        pos = record_end
    if pos != end:
        raise ValueError("trailing bytes do not form a record")
    return None


def npc(path: Path, form_id: int) -> dict[str, bytes]:
    result = records(path.read_bytes(), 0, path.stat().st_size, form_id)
    if result is None:
        raise ValueError(f"NPC {form_id:#x} not found in {path}")
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--current", required=True, type=Path)
    parser.add_argument("--baseline", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--json", required=True, type=Path)
    parser.add_argument("--form-id", required=True)
    parser.add_argument("--field", required=True, choices=["identity", "archetype", "weight", "stats", "keywords", "factions", "inventory", "outfits", "perks", "actor-effects", "properties"])
    args = parser.parse_args()
    form_id = int(args.form_id, 0)
    evidence = json.loads(args.json.read_text(encoding="utf-8-sig"))
    if not evidence.get("applied"):
        raise SystemExit("reset evidence is not applied")
    before = npc(args.current, form_id)
    baseline = npc(args.baseline, form_id)
    after = npc(args.output, form_id)
    field_map = {"identity": {"EDID", "FULL"}, "archetype": {"RNAM", "VTCK", "CNAM", "ZNAM"},
                 "weight": {"NAM7", "MWGT"}, "stats": {"ACBS", "DNAM", "NAM6"},
                 "keywords": {"KWDA", "APPR"}, "factions": {"SNAM"}, "inventory": {"CNTO"},
                 "outfits": {"DOFT", "SOFT"}, "perks": {"PRKR"}, "actor-effects": {"SPLO", "SPCT"},
                 "properties": {"PRPS"}}
    changed = field_map[args.field]
    for key in set(after) | set(baseline):
        if key in changed and after.get(key, b"") != baseline.get(key, b""):
            raise SystemExit(f"target field {key} differs from baseline")
    if args.field == "identity":
        before_acbs, baseline_acbs, after_acbs = before.get("ACBS", b""), baseline.get("ACBS", b""), after.get("ACBS", b"")
        if len(before_acbs) != len(baseline_acbs) or len(before_acbs) != len(after_acbs) or len(before_acbs) < 4:
            raise SystemExit("identity reset requires matching ACBS payloads")
        before_flags = struct.unpack_from("<I", before_acbs)[0]
        baseline_flags = struct.unpack_from("<I", baseline_acbs)[0]
        after_flags = struct.unpack_from("<I", after_acbs)[0]
        if (after_flags & ~1) != (before_flags & ~1) or (after_flags & 1) != (baseline_flags & 1) or after_acbs[4:] != before_acbs[4:]:
            raise SystemExit("identity reset changed non-sex ACBS bytes")
    for key in set(after) | set(baseline):
        if key in changed:
            continue
        if after.get(key, b"") != before.get(key, b""):
            raise SystemExit(f"unrelated field {key} drifted")
    print("RESULT PASS reset section equals baseline and unrelated NPC subrecords are preserved")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
