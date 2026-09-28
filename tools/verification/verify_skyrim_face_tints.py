"""Independent raw TES4 verifier for Skyrim authored face-tint layers."""
from __future__ import annotations

import argparse
import hashlib
import json
import struct
from pathlib import Path


def records(data: bytes, start: int = 0, end: int | None = None):
    end = len(data) if end is None else end
    pos = start
    while pos < end:
        if pos + 8 > end:
            raise ValueError("truncated record header")
        sig = data[pos : pos + 4]
        size = struct.unpack_from("<I", data, pos + 4)[0]
        record_end = pos + (size if sig == b"GRUP" else 24 + size)
        if record_end > end or record_end < pos + 8:
            raise ValueError("record exceeds container")
        if sig == b"GRUP":
            yield from records(data, pos + 24, record_end)
        else:
            yield sig.decode("ascii", errors="replace"), pos, record_end
        pos = record_end


def subrecords(data: bytes, start: int, end: int):
    pos, result = start + 24, []
    while pos + 6 <= end:
        sig = data[pos : pos + 4].decode("ascii", errors="replace")
        size = struct.unpack_from("<H", data, pos + 4)[0]
        field_end = pos + 6 + size
        if field_end > end:
            raise ValueError("subrecord exceeds NPC")
        result.append((sig, data[pos + 6 : field_end]))
        pos = field_end
    if pos != end:
        raise ValueError("NPC has trailing bytes")
    return result


def target(data: bytes, form_id: int):
    matches = []
    for sig, start, end in records(data):
        if sig == "NPC_" and struct.unpack_from("<I", data, start + 12)[0] == form_id:
            matches.append((start, end, subrecords(data, start, end)))
    if len(matches) != 1:
        raise ValueError(f"expected one target NPC, found {len(matches)}")
    return matches[0]


def expected_fields(expected: list[dict]) -> list[tuple[str, bytes]]:
    result: list[tuple[str, bytes]] = []
    for layer in expected:
        result.extend(
            [
                ("TINI", struct.pack("<H", int(layer["index"]))),
                ("TINC", bytes((int(layer["red"]), int(layer["green"]), int(layer["blue"]), int(layer["alpha"]))),),
                ("TINV", struct.pack("<I", int(layer["coverage"]))),
                ("TIAS", struct.pack("<h", int(layer["presetIndex"]))),
            ]
        )
    return result


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--before", required=True)
    parser.add_argument("--after", required=True)
    parser.add_argument("--json", required=True)
    parser.add_argument("--expected", required=True)
    parser.add_argument("--form-id", required=True)
    args = parser.parse_args()
    before_path, after_path = Path(args.before), Path(args.after)
    before, after = before_path.read_bytes(), after_path.read_bytes()
    evidence = json.loads(Path(args.json).read_text(encoding="utf-8-sig"))
    if not evidence.get("applied") or not evidence.get("outputSha256"):
        raise SystemExit("CLI evidence is not an applied result")
    output_hash = evidence["outputSha256"].get("value") if isinstance(evidence["outputSha256"], dict) else evidence["outputSha256"]
    if output_hash != hashlib.sha256(after).hexdigest():
        raise SystemExit("output hash mismatch")
    expected = json.loads(Path(args.expected).read_text(encoding="utf-8-sig"))
    if not isinstance(expected, list):
        raise SystemExit("expected tint document must be an array")
    form_id = int(args.form_id, 0)
    _, _, before_fields = target(before, form_id)
    _, _, after_fields = target(after, form_id)
    actual = [(sig, payload) for sig, payload in after_fields if sig in ("TINI", "TINC", "TINV", "TIAS")]
    if actual != expected_fields(expected):
        raise SystemExit("TINI/TINC/TINV/TIAS payload mismatch")
    tint_names = {"TINI", "TINC", "TINV", "TIAS"}
    before_unrelated = [(sig, payload) for sig, payload in before_fields if sig not in tint_names]
    after_unrelated = [(sig, payload) for sig, payload in after_fields if sig not in tint_names]
    if before_unrelated != after_unrelated:
        raise SystemExit("unrelated target NPC subrecords changed")
    before_non_target = [before[start:end] for sig, start, end in records(before) if not (sig == "NPC_" and struct.unpack_from("<I", before, start + 12)[0] == form_id)]
    after_non_target = [after[start:end] for sig, start, end in records(after) if not (sig == "NPC_" and struct.unpack_from("<I", after, start + 12)[0] == form_id)]
    if not before_non_target:
        raise SystemExit("fixture must include at least one non-target record")
    if before_non_target != after_non_target:
        raise SystemExit("non-target records changed")
    print("RESULT PASS raw Skyrim tint layer order, RGB/alpha/coverage/TIAS semantics, and non-target preservation")


if __name__ == "__main__":
    main()
