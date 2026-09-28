"""Independently verify NPC VMAD bytes written by the runtime-script command."""
from __future__ import annotations

import argparse
import json
import math
import struct
from pathlib import Path
from typing import Any


WIRE_TYPES = {
    "bool": 5,
    "int": 3,
    "float": 4,
    "string": 2,
    "bool[]": 15,
    "int[]": 13,
    "float[]": 14,
    "string[]": 12,
}


def read_utf8(data: bytes, cursor: int) -> tuple[str, int]:
    if cursor + 2 > len(data):
        raise ValueError("truncated UTF-8 length")
    length = struct.unpack_from("<H", data, cursor)[0]
    cursor += 2
    end = cursor + length
    if end > len(data):
        raise ValueError("truncated UTF-8 value")
    return data[cursor:end].decode("utf-8"), end


def read_value(data: bytes, cursor: int, wire_type: int) -> tuple[Any, int]:
    scalar = {2: ("string", read_utf8), 3: ("int", lambda d, c: (struct.unpack_from("<i", d, c)[0], c + 4)), 4: ("float", lambda d, c: (struct.unpack_from("<f", d, c)[0], c + 4)), 5: ("bool", lambda d, c: (bool(d[c]), c + 1))}
    if wire_type in scalar:
        if wire_type in (3, 4) and cursor + 4 > len(data):
            raise ValueError("truncated scalar VMAD value")
        if wire_type == 5 and cursor + 1 > len(data):
            raise ValueError("truncated boolean VMAD value")
        return scalar[wire_type][1](data, cursor)
    element_type = {12: 2, 13: 3, 14: 4, 15: 5}.get(wire_type)
    if element_type is None or cursor + 4 > len(data):
        raise ValueError(f"unsupported VMAD value type {wire_type}")
    count = struct.unpack_from("<I", data, cursor)[0]
    cursor += 4
    values = []
    for _ in range(count):
        value, cursor = read_value(data, cursor, element_type)
        values.append(value)
    return values, cursor


def parse_vmad(payload: bytes) -> list[dict[str, Any]]:
    if len(payload) < 6:
        raise ValueError("VMAD payload is truncated")
    version, object_format, count = struct.unpack_from("<HHH", payload, 0)
    cursor = 6
    scripts: list[dict[str, Any]] = []
    for _ in range(count):
        name, cursor = read_utf8(payload, cursor)
        if cursor + 3 > len(payload):
            raise ValueError("script header is truncated")
        flags = payload[cursor]
        property_count = struct.unpack_from("<H", payload, cursor + 1)[0]
        cursor += 3
        properties: list[dict[str, Any]] = []
        for _ in range(property_count):
            property_name, cursor = read_utf8(payload, cursor)
            if cursor + 2 > len(payload):
                raise ValueError("property header is truncated")
            wire_type, property_flags = payload[cursor : cursor + 2]
            cursor += 2
            value, cursor = read_value(payload, cursor, wire_type)
            properties.append({"name": property_name, "wireType": wire_type, "flags": property_flags, "value": value})
        scripts.append({"name": name, "flags": flags, "properties": properties})
    if cursor != len(payload):
        raise ValueError("VMAD payload has trailing bytes")
    return {"version": version, "objectFormat": object_format, "scripts": scripts}


def subrecords(payload: bytes):
    cursor = 0
    extended_length: int | None = None
    while cursor < len(payload):
        if cursor + 6 > len(payload):
            raise ValueError("truncated subrecord header")
        signature = payload[cursor : cursor + 4].decode("ascii")
        length = struct.unpack_from("<H", payload, cursor + 4)[0]
        cursor += 6
        if signature == "XXXX":
            if length != 4 or cursor + 4 > len(payload):
                raise ValueError("invalid XXXX subrecord")
            extended_length = struct.unpack_from("<I", payload, cursor)[0]
            cursor += 4
            continue
        actual_length = extended_length if extended_length is not None else length
        extended_length = None
        if cursor + actual_length > len(payload):
            raise ValueError(f"truncated {signature} subrecord")
        yield signature, payload[cursor : cursor + actual_length]
        cursor += actual_length


def target_npc(data: bytes, form_id: int) -> list[list[tuple[str, bytes]]]:
    found = []
    for offset in range(0, len(data) - 24):
        if data[offset : offset + 4] != b"NPC_":
            continue
        size = struct.unpack_from("<I", data, offset + 4)[0]
        end = offset + 24 + size
        if end > len(data) or struct.unpack_from("<I", data, offset + 12)[0] & 0x00FFFFFF != form_id:
            continue
        payload = data[offset + 24 : end]
        flags = struct.unpack_from("<I", data, offset + 8)[0]
        if flags & 0x00040000:
            import zlib

            if len(payload) < 4:
                raise ValueError("compressed NPC record is truncated")
            payload = zlib.decompress(payload[4:])
        rows = list(subrecords(payload))
        if any(signature == "VMAD" for signature, _ in rows):
            found.append(rows)
    return found


def equal_value(expected: Any, actual: Any) -> bool:
    if isinstance(expected, list):
        return isinstance(actual, list) and len(expected) == len(actual) and all(equal_value(left, right) for left, right in zip(expected, actual))
    if isinstance(expected, float):
        return isinstance(actual, (float, int)) and math.isclose(expected, actual, rel_tol=1e-6, abs_tol=1e-6)
    return expected == actual


def verify(output: Path, proposal: Path, source: Path | None) -> None:
    artifact = json.loads(proposal.read_text(encoding="utf-8"))
    form_id = int(artifact["npcFormId"], 0) & 0x00FFFFFF
    output_records = target_npc(output.read_bytes(), form_id)
    if len(output_records) != 1:
        raise AssertionError(f"expected one target NPC record with VMAD, found {len(output_records)}")
    vmad_rows = [value for signature, value in output_records[0] if signature == "VMAD"]
    if len(vmad_rows) != 1:
        raise AssertionError(f"expected one VMAD subrecord, found {len(vmad_rows)}")
    vmad = parse_vmad(vmad_rows[0])
    expected_version = 6 if artifact["edition"] == "fallout4" else 5
    if (vmad["version"], vmad["objectFormat"]) != (expected_version, 2):
        raise AssertionError("unexpected VMAD version/object format")
    scripts = vmad["scripts"]
    owned = [script for script in scripts if script["name"].casefold() == artifact["scriptName"].casefold()]
    if len(owned) != 1:
        raise AssertionError(f"expected exactly one owned script, found {len(owned)}")
    properties = sorted(artifact["properties"], key=lambda item: item["name"])
    actual = owned[0]["properties"]
    if len(actual) != len(properties):
        raise AssertionError("VMAD property count mismatch")
    for expected, row in zip(properties, actual):
        if expected["name"] != row["name"] or WIRE_TYPES[expected["type"]] != row["wireType"] or row["flags"] != 1 or not equal_value(expected["value"], row["value"]):
            raise AssertionError(f"VMAD property mismatch for {expected['name']}")
    if source is not None:
        source_records = target_npc(source.read_bytes(), form_id)
        if len(source_records) == 1:
            source_rows = [value for signature, value in source_records[0] if signature == "VMAD"]
            if source_rows:
                source_scripts = parse_vmad(source_rows[0])["scripts"]
                source_non_owned = [script for script in source_scripts if not script["name"].startswith("NPCM_Manolov_")]
                output_non_owned = [script for script in scripts if not script["name"].startswith("NPCM_Manolov_")]
                if source_non_owned != output_non_owned:
                    raise AssertionError("non-owned VMAD scripts were not preserved")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--proposal", type=Path, required=True)
    parser.add_argument("--source", type=Path)
    args = parser.parse_args()
    verify(args.output, args.proposal, args.source)
    print(f"RUNTIME SCRIPT VMAD INDEPENDENT PASS output={args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
