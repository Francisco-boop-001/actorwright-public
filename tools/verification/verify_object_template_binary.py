"""Independent raw verifier for the bounded Fallout 4 OBTS writer."""
from __future__ import annotations

import argparse
import json
import struct
from pathlib import Path


def u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


def subrecords(payload: bytes):
    offset = 0
    while offset < len(payload):
        if offset + 6 > len(payload):
            raise ValueError("truncated subrecord header")
        signature = payload[offset : offset + 4].decode("ascii")
        length = struct.unpack_from("<H", payload, offset + 4)[0]
        offset += 6
        if offset + length > len(payload):
            raise ValueError(f"truncated {signature} payload")
        yield signature, payload[offset : offset + length]
        offset += length
    if offset != len(payload):
        raise ValueError("subrecord stream did not terminate cleanly")


def records(data: bytes):
    for offset in range(0, len(data) - 24):
        if data[offset : offset + 4] != b"ARMO":
            continue
        size = u32(data, offset + 4)
        end = offset + 24 + size
        if end > len(data):
            continue
        payload = data[offset + 24 : end]
        try:
            rows = list(subrecords(payload))
        except ValueError:
            continue
        if any(signature == "EDID" for signature, _ in rows):
            yield offset, u32(data, offset + 12), rows


def parse_form_reference(value: str) -> tuple[str, int]:
    plugin, form = value.split("|", 1)
    return plugin.lower(), int(form, 0)


VALUE_TYPES = {
    "IntType": 0,
    "FloatType": 1,
    "BoolType": 2,
    "StringType": 3,
    "FormIDInt": 4,
    "EnumType": 5,
    "FormIDFloat": 6,
}


def verify(output: Path, proposal: Path, properties_path: Path | None) -> None:
    artifact = json.loads(proposal.read_text(encoding="utf-8"))
    properties = json.loads(properties_path.read_text(encoding="utf-8")) if properties_path else None
    if properties is not None:
        if properties.get("artifactKind") != "object-template-properties-proposal":
            raise AssertionError("unexpected property artifact kind")
        if properties.get("sourcePlugin", "").casefold() != artifact.get("sourcePlugin", "").casefold() or properties.get("sourceFormId") != artifact.get("sourceFormId"):
            raise AssertionError("property artifact source identity mismatch")
        if artifact.get("mode") == "override" and properties.get("editorId") != artifact.get("editorId"):
            raise AssertionError("property artifact EditorID mismatch")
    data = output.read_bytes()
    found = list(records(data))
    if len(found) != 1:
        raise AssertionError(f"expected one identifiable ARMO record, found {len(found)}")
    _, form_id, rows = found[0]
    expected_target = int(artifact["targetFormId"], 0) & 0x00FFFFFF if artifact["mode"] == "new" else int(artifact["sourceFormId"], 0) & 0x00FFFFFF
    if form_id & 0x00FFFFFF != expected_target:
        raise AssertionError(f"unexpected ARMO local FormID: 0x{form_id:08X}")
    editor_id = next(payload.rstrip(b"\0").decode("utf-8") for signature, payload in rows if signature == "EDID")
    if editor_id != artifact["editorId"]:
        raise AssertionError(f"unexpected EditorID {editor_id!r}")
    tes4_start = data.find(b"TES4")
    if tes4_start < 0:
        raise AssertionError("TES4 header missing")
    tes4_size = u32(data, tes4_start + 4)
    tes4 = data[tes4_start + 24 : tes4_start + 24 + tes4_size]
    masters = [payload.rstrip(b"\0").decode("ascii").lower() for signature, payload in subrecords(tes4) if signature == "MAST"]
    if not masters:
        raise AssertionError("output has no MAST for source references")
    block_start = next((index for index, (signature, _) in enumerate(rows) if signature == "OBTE"), None)
    if block_start is None:
        raise AssertionError("OBTE block missing")
    if u32(rows[block_start][1], 0) != len(artifact["combinations"]):
        raise AssertionError("OBTE count mismatch")
    index = block_start + 1
    for combination_index, combination in enumerate(artifact["combinations"]):
        editor_only = bool(combination.get("isEditorOnly", False))
        if editor_only:
            if rows[index][0] != "OBTF" or rows[index][1]:
                raise AssertionError(f"combination {combination_index} OBTF mismatch")
            index += 1
        name = combination.get("displayName")
        if name:
            if rows[index][0] != "FULL" or rows[index][1].rstrip(b"\0").decode("utf-8") != name:
                raise AssertionError(f"combination {combination_index} FULL mismatch")
            index += 1
        if rows[index][0] != "OBTS":
            raise AssertionError(f"combination {combination_index} OBTS missing")
        payload = rows[index][1]
        index += 1
        include_count, property_count = struct.unpack_from("<II", payload, 0)
        if properties is None and property_count != 0:
            raise AssertionError("unexpected OMOD properties in bounded writer output")
        if include_count != len(combination.get("includes", [])):
            raise AssertionError(f"combination {combination_index} include count mismatch")
        level_min, level_max = payload[8], payload[10]
        parent = struct.unpack_from("<h", payload, 12)[0]
        if level_min != combination.get("levelMin", 0) or level_max != combination.get("levelMax", 0):
            raise AssertionError(f"combination {combination_index} level mismatch")
        expected_parent = combination.get("parentCombinationIndex")
        if parent != (-1 if expected_parent is None else expected_parent):
            raise AssertionError(f"combination {combination_index} parent mismatch")
        if bool(payload[14]) != bool(combination.get("isDefault", False)):
            raise AssertionError(f"combination {combination_index} default mismatch")
        keyword_count = payload[15]
        expected_keywords = [parse_form_reference(item) for item in combination.get("keywords", [])]
        if keyword_count != len(expected_keywords):
            raise AssertionError(f"combination {combination_index} keyword count mismatch")
        cursor = 16
        for plugin, local_id in expected_keywords:
            actual = u32(payload, cursor)
            cursor += 4
            master_index = actual >> 24
            if master_index == 0 or master_index > len(masters) or masters[master_index - 1] != plugin or actual & 0x00FFFFFF != local_id:
                raise AssertionError(f"combination {combination_index} keyword mapping mismatch")
        if payload[cursor : cursor + 2] != bytes((combination.get("minLevelForRanks", 0), combination.get("altLevelsPerTier", 0))):
            raise AssertionError(f"combination {combination_index} level-tier mismatch")
        cursor += 2
        for include in combination.get("includes", []):
            plugin, local_id = parse_form_reference(include["mod"])
            actual = u32(payload, cursor)
            cursor += 4
            if actual >> 24 == 0 or actual >> 24 > len(masters) or masters[(actual >> 24) - 1] != plugin or actual & 0x00FFFFFF != local_id:
                raise AssertionError(f"combination {combination_index} include mapping mismatch")
            expected_flags = bytes((include.get("attachPointIndex", 0), int(include.get("isOptional", False)), int(include.get("dontUseAll", False))))
            if payload[cursor : cursor + 3] != expected_flags:
                raise AssertionError(f"combination {combination_index} include flags mismatch")
            cursor += 3
        expected_properties = [] if properties is None else [item for item in properties.get("properties", []) if item.get("combinationIndex", 0) == combination_index]
        actual_property_count = struct.unpack_from("<I", payload, 4)[0]
        if actual_property_count != len(expected_properties):
            raise AssertionError(f"combination {combination_index} property count mismatch")
        for property_index, expected in enumerate(expected_properties):
            if cursor + 24 > len(payload):
                raise AssertionError(f"combination {combination_index} property {property_index} is truncated")
            row = payload[cursor : cursor + 24]
            cursor += 24
            value_type = expected["valueType"]
            if VALUE_TYPES.get(value_type) != row[0] or expected.get("functionType", 0) != row[4] or expected.get("propertyIndex", 0) != struct.unpack_from("<H", row, 8)[0]:
                raise AssertionError(f"combination {combination_index} property {property_index} header mismatch")
            if value_type in ("FormIDInt", "FormIDFloat"):
                plugin, local_id = parse_form_reference(expected["value1FormId"])
                actual = u32(row, 12)
                master_index = actual >> 24
                if master_index == 0 or master_index > len(masters) or masters[master_index - 1] != plugin or actual & 0x00FFFFFF != local_id:
                    raise AssertionError(f"combination {combination_index} property {property_index} FormID mapping mismatch")
            else:
                if value_type == "FloatType":
                    expected_value1 = struct.pack("<f", expected.get("value1Float", 0.0))
                else:
                    expected_value1 = struct.pack("<i", int(expected.get("value1Integer", 0)))
                if row[12:16] != expected_value1:
                    raise AssertionError(f"combination {combination_index} property {property_index} Value1 mismatch")
            if value_type in ("FloatType", "FormIDFloat"):
                expected_value2 = struct.pack("<f", expected.get("value2Float", 0.0))
            else:
                expected_value2 = struct.pack("<i", int(expected.get("value2Integer", 0)))
            if row[16:20] != expected_value2 or row[20:24] != struct.pack("<f", expected.get("stepValue", 0.0)):
                raise AssertionError(f"combination {combination_index} property {property_index} Value2/Step mismatch")
        if cursor != len(payload):
            raise AssertionError(f"combination {combination_index} OBTS payload has trailing bytes")
    if rows[index][0] != "STOP" or rows[index][1] or index + 1 != len(rows):
        raise AssertionError("OBTS block terminator or record tail mismatch")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--proposal", type=Path, required=True)
    parser.add_argument("--properties", type=Path)
    args = parser.parse_args()
    verify(args.output, args.proposal, args.properties)
    print(f"OBJECT TEMPLATE BINARY INDEPENDENT PASS output={args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
