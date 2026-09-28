"""Independent raw TES4 verifier for PNAM/HCLF face patches."""
from __future__ import annotations

import argparse
import json
import hashlib
import struct
from pathlib import Path


def npc_fields(path: Path, form_id: int) -> dict[str, bytes]:
    data = path.read_bytes()

    def walk(start: int, end: int) -> dict[str, bytes] | None:
        pos = start
        while pos + 8 <= end:
            sig = data[pos : pos + 4].decode("ascii", errors="strict")
            size = struct.unpack_from("<I", data, pos + 4)[0]
            record_end = pos + (size if sig == "GRUP" else 24 + size)
            if record_end > end:
                raise ValueError("record exceeds file boundary")
            if sig == "GRUP":
                found = walk(pos + 24, record_end)
                if found is not None:
                    return found
            elif sig == "NPC_" and struct.unpack_from("<I", data, pos + 12)[0] == form_id:
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
                return {key: bytes(value) for key, value in fields.items()}
            pos = record_end
        return None

    found = walk(0, len(data))
    if found is None:
        raise ValueError(f"NPC {form_id:#x} not found in {path}")
    return found


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--before", required=True, type=Path)
    parser.add_argument("--after", required=True, type=Path)
    parser.add_argument("--json", required=True, type=Path)
    parser.add_argument("--form-id", required=True)
    parser.add_argument("--headparts", required=True, help="comma-separated hexadecimal FormIDs in PNAM order")
    parser.add_argument("--hair-color", required=True, help="hexadecimal FormID or none")
    args = parser.parse_args()
    evidence = json.loads(args.json.read_text(encoding="utf-8-sig"))
    if not evidence.get("applied"):
        raise SystemExit("face evidence is not applied")
    output_hash = evidence.get("outputSha256", {}).get("value") if isinstance(evidence.get("outputSha256"), dict) else evidence.get("outputSha256")
    if not output_hash or hashlib.sha256(args.after.read_bytes()).hexdigest().casefold() != str(output_hash).casefold():
        raise SystemExit("output hash evidence mismatch")
    form_id = int(args.form_id, 0)
    before, after = npc_fields(args.before, form_id), npc_fields(args.after, form_id)
    expected_parts = [int(item, 0) for item in args.headparts.split(",") if item]
    actual_parts = [value[0] for value in struct.iter_unpack("<I", after.get("PNAM", b""))]
    if actual_parts != expected_parts:
        raise SystemExit(f"PNAM mismatch: actual={actual_parts!r} expected={expected_parts!r}")
    expected_hair = b"" if args.hair_color.lower() == "none" else struct.pack("<I", int(args.hair_color, 0))
    actual_hair = after.get("HCLF", b"")
    if actual_hair != expected_hair:
        raise SystemExit("HCLF mismatch")
    for key in set(before) | set(after):
        if key in {"PNAM", "HCLF"}:
            continue
        if before.get(key, b"") != after.get(key, b""):
            raise SystemExit(f"unrelated field {key} drifted")
    print("RESULT PASS raw PNAM/HCLF match and unrelated NPC subrecords are preserved")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
