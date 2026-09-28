#!/usr/bin/env python3
"""Independent raw TES4 audit for SKY-GUI-016 LVLI and follow-on OTFT."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import struct
import sys

PROJECT_ROOT = Path(__file__).resolve().parents[2]
WORKSPACE_ROOT = PROJECT_ROOT.parents[1]
sys.path.insert(0, str(WORKSPACE_ROOT / "tools" / "gates" / "lib"))

from esp_tools import subrecords, walk  # noqa: E402

FORBIDDEN = {"WRLD", "CELL", "LAND", "WATR", "NAVM", "NAVI", "VMAD"}
EXPECTED_ENTRY = "OutfitBase.esm|0x00000800"
EXPECTED_LIST = "CreatedList.esp|0x00000A10"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--leveled-plugin", required=True)
    parser.add_argument("--leveled-proposal", required=True)
    parser.add_argument("--outfit-plugin", required=True)
    parser.add_argument("--outfit-proposal", required=True)
    parser.add_argument("--report", required=True)
    args = parser.parse_args()
    paths = {name: Path(value).resolve() for name, value in vars(args).items()}
    for path in paths.values():
        ensure_under_project(path)
    for name, path in paths.items():
        if name != "report" and not path.is_file():
            raise FileNotFoundError(f"Required artifact is missing: {path}")

    failures: list[str] = []
    leveled = inspect_leveled(
        paths["leveled_plugin"], paths["leveled_proposal"], failures
    )
    outfit = inspect_outfit(
        paths["outfit_plugin"], paths["outfit_proposal"], failures
    )
    payload = {
        "schema": "npcmanager.sky-gui-016.leveled-list-raw-audit.v1",
        "passed": not failures,
        "leveled_plugin_sha256": sha256(paths["leveled_plugin"]),
        "leveled_proposal_sha256": sha256(paths["leveled_proposal"]),
        "outfit_plugin_sha256": sha256(paths["outfit_plugin"]),
        "outfit_proposal_sha256": sha256(paths["outfit_proposal"]),
        "observed": {"leveled_list": leveled, "follow_on_outfit": outfit},
        "failures": failures,
        "plugin_authority": not failures,
        "preview_authority": False,
        "equipment_authority": False,
        "runtime_authority": False,
        "visual_authority": False,
    }
    paths["report"].parent.mkdir(parents=True, exist_ok=True)
    paths["report"].write_text(
        json.dumps(payload, indent=2) + "\n", encoding="utf-8"
    )
    print(json.dumps(payload, indent=2))
    return 0 if not failures else 1


def inspect_leveled(
    plugin: Path, proposal_path: Path, failures: list[str]
) -> dict[str, object]:
    data = plugin.read_bytes()
    plugin_masters = masters(data)
    if plugin_masters != ["OutfitBase.esm"]:
        failures.append(
            f"LVLI masters expected=['OutfitBase.esm'] actual={plugin_masters!r}"
        )
    counts, records = inventory(data, plugin.name, plugin_masters, "LVLI")
    if counts != {"LVLI": 1}:
        failures.append(f"LVLI record surface expected={{'LVLI': 1}} actual={counts!r}")
    forbid(counts, "LVLI", failures)
    owner = None
    local_form = None
    editor = None
    flags = None
    chance_none = None
    max_count_present = False
    entries: list[dict[str, object]] = []
    if len(records) != 1:
        failures.append("LVLI output did not expose exactly one LVLI")
    else:
        owner, local_form, body = records[0]
        fields = field_map(body)
        editor = decode_zstring(one(fields, "EDID", failures, "LVLI"))
        chance_payload = one(fields, "LVLD", failures, "LVLI")
        flag_payload = one(fields, "LVLF", failures, "LVLI")
        chance_none = chance_payload[0] if len(chance_payload) == 1 else None
        flags = flag_payload[0] if len(flag_payload) == 1 else None
        max_count_present = "LVLM" in fields
        entries = [
            decode_lvlo(payload, plugin.name, plugin_masters)
            for payload in fields.get("LVLO", [])
        ]
    exact = {
        "owner": plugin.name,
        "local_form": 0xA10,
        "editor": "npcm_LVLI_TravelGear",
        "flags": 0x05,
        "chance_none": 25,
        "max_count_present": False,
        "entries": [
            {"item": EXPECTED_ENTRY, "level": 7, "count": 2, "chance_none": 0}
        ],
    }
    actual = {
        "owner": owner,
        "local_form": local_form,
        "editor": editor,
        "flags": flags,
        "chance_none": chance_none,
        "max_count_present": max_count_present,
        "entries": entries,
    }
    for key, expected in exact.items():
        if actual[key] != expected:
            failures.append(
                f"LVLI {key} expected={expected!r} actual={actual[key]!r}"
            )

    proposal = load_json(proposal_path)
    proposal_exact = {
        "schemaVersion": "1",
        "artifactKind": "skyrim-new-leveled-list-production-proposal",
        "edition": "skyrimse",
        "outputPlugin": plugin.name,
        "targetFormId": "0x00000A10",
        "editorId": "npcm_LVLI_TravelGear",
        "chanceNone": 25,
        "maxCount": 0,
        "calculateAllLevels": True,
        "calculateEachInCount": False,
        "useAll": True,
        "masterDependencies": ["OutfitBase.esm"],
        "newSelfOwnedRecord": True,
        "noUnrelatedRecords": True,
    }
    for key, expected in proposal_exact.items():
        if proposal.get(key) != expected:
            failures.append(
                f"LVLI proposal {key} expected={expected!r} actual={proposal.get(key)!r}"
            )
    proposal_entries = proposal.get("entries")
    expected_proposal_entries = [
        {"item": EXPECTED_ENTRY, "level": 7, "count": 2, "chanceNone": 0}
    ]
    if proposal_entries != expected_proposal_entries:
        failures.append(
            "LVLI proposal entries expected="
            f"{expected_proposal_entries!r} actual={proposal_entries!r}"
        )
    return {
        "record_counts": counts,
        "masters": plugin_masters,
        "owner": owner,
        "local_form_id": None if local_form is None else f"0x{local_form:08X}",
        "editor_id": editor,
        "lvlf": None if flags is None else f"0x{flags:02X}",
        "lvld_chance_none": chance_none,
        "lvlm_present": max_count_present,
        "ordered_lvlo": entries,
    }


def inspect_outfit(
    plugin: Path, proposal_path: Path, failures: list[str]
) -> dict[str, object]:
    data = plugin.read_bytes()
    plugin_masters = masters(data)
    expected_masters = [
        "OutfitProviderAfter.esp", "OutfitBase.esm", "CreatedList.esp"
    ]
    if plugin_masters != expected_masters:
        failures.append(
            f"OTFT masters expected={expected_masters!r} actual={plugin_masters!r}"
        )
    counts, records = inventory(data, plugin.name, plugin_masters, "OTFT")
    if counts != {"OTFT": 1}:
        failures.append(f"OTFT record surface expected={{'OTFT': 1}} actual={counts!r}")
    forbid(counts, "OTFT", failures)
    owner = None
    local_form = None
    editor = None
    items: list[str] = []
    if len(records) != 1:
        failures.append("Follow-on output did not expose exactly one OTFT")
    else:
        owner, local_form, body = records[0]
        fields = field_map(body)
        editor = decode_zstring(one(fields, "EDID", failures, "OTFT"))
        items = [
            resolve_raw_reference(raw, plugin.name, plugin_masters)
            for payload in fields.get("INAM", [])
            for raw in unpack_form_ids(payload)
        ]
    if owner != plugin.name or local_form != 0xA20:
        failures.append(
            f"OTFT identity expected={plugin.name}|0x00000A20 "
            f"actual={owner}|0x{(local_form or 0):08X}"
        )
    if editor != "NpcManager_NewOutfit":
        failures.append(
            f"OTFT EditorID expected='NpcManager_NewOutfit' actual={editor!r}"
        )
    if items != [EXPECTED_LIST]:
        failures.append(
            f"OTFT ordered INAM expected={[EXPECTED_LIST]!r} actual={items!r}"
        )
    proposal = load_json(proposal_path)
    if proposal.get("mode") != "new" or proposal.get("targetFormId") != "0x00000A20":
        failures.append("Follow-on outfit proposal lost new-record target identity")
    if proposal.get("items") != [EXPECTED_LIST]:
        failures.append("Follow-on outfit proposal did not retain the generated LVLI")
    return {
        "record_counts": counts,
        "masters": plugin_masters,
        "owner": owner,
        "local_form_id": None if local_form is None else f"0x{local_form:08X}",
        "editor_id": editor,
        "ordered_items": items,
    }


def decode_lvlo(
    payload: bytes, self_plugin: str, plugin_masters: list[str]
) -> dict[str, object]:
    if len(payload) != 12:
        raise ValueError(f"LVLO payload expected 12 bytes, got {len(payload)}")
    level, unused_a, raw, count, unused_b = struct.unpack("<hHIhH", payload)
    if unused_a != 0 or unused_b != 0:
        raise ValueError("LVLO reserved fields are nonzero")
    return {
        "item": resolve_raw_reference(raw, self_plugin, plugin_masters),
        "level": level,
        "count": count,
        "chance_none": 0,
    }


def forbid(counts: dict[str, int], role: str, failures: list[str]) -> None:
    for signature in sorted(FORBIDDEN & set(counts)):
        failures.append(f"{role} contains forbidden {signature} record surface")


def ensure_under_project(path: Path) -> None:
    try:
        path.relative_to(PROJECT_ROOT)
    except ValueError as error:
        raise ValueError(f"Path escaped project root: {path}") from error


def load_json(path: Path) -> dict[str, object]:
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict):
        raise ValueError(f"Expected JSON object: {path}")
    return value


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def masters(data: bytes) -> list[str]:
    if len(data) < 24 or data[:4] != b"TES4":
        raise ValueError("Input is not a TES4 plugin")
    size = struct.unpack_from("<I", data, 4)[0]
    return [
        payload.rstrip(b"\0").decode("cp1252")
        for signature, payload in subrecords(data[24:24 + size])
        if signature == "MAST"
    ]


def inventory(
    data: bytes, self_plugin: str, plugin_masters: list[str], wanted: str
) -> tuple[dict[str, int], list[tuple[str, int, bytes]]]:
    counts: dict[str, int] = {}
    records: list[tuple[str, int, bytes]] = []

    def capture(signature: str, form_id: int, flags: int, body: bytes, depth: int) -> None:
        del flags, depth
        counts[signature] = counts.get(signature, 0) + 1
        if signature == wanted:
            owner, local = resolve_form_id(form_id, self_plugin, plugin_masters)
            records.append((owner, local, body))

    walk(data, capture)
    return counts, records


def field_map(body: bytes) -> dict[str, list[bytes]]:
    result: dict[str, list[bytes]] = {}
    for signature, payload in subrecords(body):
        result.setdefault(signature, []).append(payload)
    return result


def one(
    fields: dict[str, list[bytes]], signature: str,
    failures: list[str], role: str
) -> bytes:
    values = fields.get(signature, [])
    if len(values) != 1:
        failures.append(f"{role} {signature} is missing or duplicated")
        return b""
    return values[0]


def decode_zstring(payload: bytes) -> str:
    return payload.rstrip(b"\0").decode("cp1252")


def unpack_form_ids(payload: bytes) -> list[int]:
    if not payload or len(payload) % 4:
        raise ValueError("INAM payload is not a nonempty packed FormID sequence")
    return [
        struct.unpack_from("<I", payload, offset)[0]
        for offset in range(0, len(payload), 4)
    ]


def resolve_raw_reference(
    raw: int, self_plugin: str, plugin_masters: list[str]
) -> str:
    owner, local = resolve_form_id(raw, self_plugin, plugin_masters)
    return f"{owner}|0x{local:08X}"


def resolve_form_id(
    raw: int, self_plugin: str, plugin_masters: list[str]
) -> tuple[str, int]:
    index = raw >> 24
    if index < len(plugin_masters):
        owner = plugin_masters[index]
    elif index == len(plugin_masters):
        owner = self_plugin
    else:
        raise ValueError(f"Raw FormID 0x{raw:08X} exceeds the ordinary master table")
    return owner, raw & 0x00FFFFFF


if __name__ == "__main__":
    raise SystemExit(main())
