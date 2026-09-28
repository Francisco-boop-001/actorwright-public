"""Independent raw TES4 verifier for the bounded FO4 TETI/TEND patch."""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import struct
from pathlib import Path


def records(data: bytes, start: int = 0, end: int | None = None):
    end = len(data) if end is None else end
    position = start
    while position < end:
        if position + 8 > end:
            raise ValueError("truncated TES4 header")
        signature = data[position : position + 4]
        size = struct.unpack_from("<I", data, position + 4)[0]
        record_end = position + (size if signature == b"GRUP" else 24 + size)
        if record_end > end or record_end < position + 8:
            raise ValueError("record exceeds container")
        if signature == b"GRUP":
            yield from records(data, position + 24, record_end)
        else:
            yield signature.decode("ascii", errors="replace"), position, record_end
        position = record_end
    if position != end:
        raise ValueError("container has trailing bytes")


def subrecords(data: bytes, start: int, end: int):
    position = start + 24
    result = []
    while position + 6 <= end:
        signature = data[position : position + 4].decode("ascii", errors="replace")
        size = struct.unpack_from("<H", data, position + 4)[0]
        field_end = position + 6 + size
        if field_end > end:
            raise ValueError("subrecord exceeds NPC")
        result.append((signature, data[position + 6 : field_end]))
        position = field_end
    if position != end:
        raise ValueError("NPC has trailing bytes")
    return result


def target(data: bytes, form_id: int):
    matches = []
    for signature, start, end in records(data):
        if signature == "NPC_" and struct.unpack_from("<I", data, start + 12)[0] == form_id:
            matches.append((start, end, subrecords(data, start, end)))
    if len(matches) != 1:
        raise ValueError(f"expected one target NPC, found {len(matches)}")
    return matches[0]


def layers(fields):
    result = []
    pending = None
    for signature, payload in fields:
        if signature == "TETI":
            if pending is not None or len(payload) != 4:
                raise ValueError("invalid TETI sequence")
            pending = struct.unpack_from("<HH", payload)
        elif signature == "TEND":
            if pending is None or len(payload) not in (1, 5, 7):
                raise ValueError("invalid TEND sequence")
            result.append((pending[0], pending[1], payload))
            pending = None
    if pending is not None:
        raise ValueError("dangling TETI")
    return result


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--before", required=True)
    parser.add_argument("--after", required=True)
    parser.add_argument("--json", required=True, help="CLI response JSON")
    parser.add_argument("--expected", required=True, help="expected ordered layer JSON array")
    parser.add_argument("--form-id", required=True)
    args = parser.parse_args()
    before_path, after_path = Path(args.before), Path(args.after)
    before, after = before_path.read_bytes(), after_path.read_bytes()
    evidence = json.loads(Path(args.json).read_text(encoding="utf-8-sig"))
    if not evidence.get("applied") or not evidence.get("outputSha256"):
        raise SystemExit("CLI evidence is not an applied result")
    digest = hashlib.sha256(after).hexdigest()
    output_hash = evidence["outputSha256"].get("value") if isinstance(evidence["outputSha256"], dict) else evidence["outputSha256"]
    if output_hash != digest:
        raise SystemExit("output hash mismatch")
    expected = json.loads(Path(args.expected).read_text(encoding="utf-8-sig"))
    form_id = int(args.form_id, 0)
    before_start, before_end, before_fields = target(before, form_id)
    after_start, after_end, after_fields = target(after, form_id)
    actual = layers(after_fields)
    expected_wire = []
    for item in expected:
        type_value = {"value-color": 1, "valuecolor": 1, "palette": 1, "texture-set": 2, "textureset": 2}[item["dataType"].lower()]
        raw = base64.b64decode(item["rawTendBase64"])
        expected_wire.append((type_value, int(item["optionIndex"]), raw))
    if actual != expected_wire:
        raise SystemExit(f"TETI/TEND mismatch: actual={actual!r} expected={expected_wire!r}")
    if [(sig, payload) for sig, payload in before_fields if sig not in ("TETI", "TEND")] != [(sig, payload) for sig, payload in after_fields if sig not in ("TETI", "TEND")]:
        raise SystemExit("unrelated target NPC subrecords changed")
    before_non_target = [before[start:end] for sig, start, end in records(before) if not (sig == "NPC_" and struct.unpack_from("<I", before, start + 12)[0] == form_id)]
    after_non_target = [after[start:end] for sig, start, end in records(after) if not (sig == "NPC_" and struct.unpack_from("<I", after, start + 12)[0] == form_id)]
    if before_non_target != after_non_target:
        raise SystemExit("non-target records changed")
    print("RESULT PASS raw TETI/TEND order/payload, target unrelated fields, and non-target records preserved")


if __name__ == "__main__":
    main()
