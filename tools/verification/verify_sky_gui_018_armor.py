#!/usr/bin/env python3
"""Independent raw TES4 audit for SKY-GUI-018 ARMO and follow-on OTFT."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import struct
import sys

PROJECT_ROOT = Path(__file__).resolve().parents[2]
WORKSPACE_ROOT = PROJECT_ROOT.parents[1]
sys.path.insert(0, str(WORKSPACE_ROOT / "tools" / "gates" / "lib"))

from esp_tools import subrecords, walk  # noqa: E402

FORBIDDEN = {"WRLD", "CELL", "LAND", "WATR", "NAVM", "NAVI", "VMAD"}
SOURCE = "Source.esp"
GENERATED = "GeneratedArmor.esp"
GENERATED_REFERENCE = f"{GENERATED}|0x00000B00"
ADDONS = [f"{SOURCE}|0x00000907", f"{SOURCE}|0x00000908"]
KEYWORDS = [f"{SOURCE}|0x00000906", f"{SOURCE}|0x00000909"]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--armor-plugin", required=True)
    parser.add_argument("--armor-proposal", required=True)
    parser.add_argument("--outfit-plugin", required=True)
    parser.add_argument("--outfit-proposal", required=True)
    parser.add_argument("--report", required=True)
    parser.add_argument(
        "--expected-editor-id", default="npcm_ARMO_SourceArmor"
    )
    parser.add_argument(
        "--expected-armor-addon",
        action="append",
        dest="expected_armor_addons",
    )
    parser.add_argument(
        "--expected-armor-slot-mask", type=lambda value: int(value, 0),
        default=4,
    )
    parser.add_argument(
        "--report-schema",
        default="npcmanager.sky-gui-018.armor-raw-audit.v1",
    )
    args = parser.parse_args()
    expected_armor_addons = args.expected_armor_addons or ADDONS
    paths = {
        name: Path(value).resolve()
        for name, value in vars(args).items()
        if name
        not in {
            "expected_editor_id",
            "expected_armor_addons",
            "expected_armor_slot_mask",
            "report_schema",
        }
    }
    for path in paths.values():
        ensure_under_project(path)
    for name, path in paths.items():
        if name != "report" and not path.is_file():
            raise FileNotFoundError(f"Required artifact is missing: {path}")

    failures: list[str] = []
    armor = inspect_armor(
        paths["armor_plugin"],
        paths["armor_proposal"],
        args.expected_editor_id,
        expected_armor_addons,
        args.expected_armor_slot_mask,
        failures,
    )
    outfit = inspect_outfit(
        paths["outfit_plugin"], paths["outfit_proposal"], failures
    )
    payload = {
        "schema": args.report_schema,
        "passed": not failures,
        "armor_plugin_sha256": sha256(paths["armor_plugin"]),
        "armor_proposal_sha256": sha256(paths["armor_proposal"]),
        "outfit_plugin_sha256": sha256(paths["outfit_plugin"]),
        "outfit_proposal_sha256": sha256(paths["outfit_proposal"]),
        "expected_armor_editor_id": args.expected_editor_id,
        "observed": {"armor": armor, "follow_on_outfit": outfit},
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


def inspect_armor(
    plugin: Path,
    proposal_path: Path,
    expected_editor_id: str,
    expected_armor_addons: list[str],
    expected_slot_mask: int,
    failures: list[str],
    expected_male_world_model: str = r"armor\old_m.nif",
    expected_female_world_model: str = r"armor\old_f.nif",
) -> dict[str, object]:
    data = plugin.read_bytes()
    plugin_masters = masters(data)
    if plugin_masters != [SOURCE]:
        failures.append(
            f"ARMO masters expected={[SOURCE]!r} actual={plugin_masters!r}"
        )
    counts, records = inventory(data, plugin.name, plugin_masters, "ARMO")
    if counts != {"ARMO": 1}:
        failures.append(f"ARMO record surface expected={{'ARMO': 1}} actual={counts!r}")
    forbid(counts, "ARMO", failures)

    actual: dict[str, object] = {}
    signatures: list[str] = []
    if len(records) != 1:
        failures.append("Armor output did not expose exactly one ARMO")
    else:
        owner, local_form, major_flags, body = records[0]
        fields = field_map(body)
        signatures = [signature for signature, _ in subrecords(body)]
        actual = {
            "owner": owner,
            "local_form": local_form,
            "major_flags": major_flags,
            "editor_id": decode_zstring(one(fields, "EDID", failures, "ARMO")),
            "name": decode_zstring(one(fields, "FULL", failures, "ARMO")),
            "description": decode_zstring(
                one(fields, "DESC", failures, "ARMO")
            ),
            "bounds": unpack(one(fields, "OBND", failures, "ARMO"), "<hhhhhh"),
            "value_weight": unpack(
                one(fields, "DATA", failures, "ARMO"), "<If"
            ),
            "armor_rating_raw": scalar(
                one(fields, "DNAM", failures, "ARMO"), "<I"
            ),
            "body": unpack(one(fields, "BOD2", failures, "ARMO"), "<II"),
            "race": form_reference(fields, "RNAM", plugin.name, plugin_masters,
                                   failures),
            "enchantment": form_reference(
                fields, "EITM", plugin.name, plugin_masters, failures
            ),
            "pickup_sound": form_reference(
                fields, "YNAM", plugin.name, plugin_masters, failures
            ),
            "drop_sound": form_reference(
                fields, "ZNAM", plugin.name, plugin_masters, failures
            ),
            "equipment_type": form_reference(
                fields, "ETYP", plugin.name, plugin_masters, failures
            ),
            "alternate_block_material": form_reference(
                fields, "BAMT", plugin.name, plugin_masters, failures
            ),
            "template_armor": form_reference(
                fields, "TNAM", plugin.name, plugin_masters, failures
            ),
            "male_world_model": decode_zstring(
                one(fields, "MOD2", failures, "ARMO")
            ),
            "female_world_model": decode_zstring(
                one(fields, "MOD4", failures, "ARMO")
            ),
            "armor_addons": [
                resolve_raw_reference(
                    scalar(payload, "<I"), plugin.name, plugin_masters
                )
                for payload in fields.get("MODL", [])
            ],
            "keywords": [
                resolve_raw_reference(raw, plugin.name, plugin_masters)
                for payload in fields.get("KWDA", [])
                for raw in unpack_form_ids(payload)
            ],
            "keyword_count": scalar(
                one(fields, "KSIZ", failures, "ARMO"), "<I"
            ),
        }

    expected: dict[str, object] = {
        "owner": plugin.name,
        "local_form": 0xB00,
        "major_flags": 0,
        "editor_id": expected_editor_id,
        "name": "Generated travel armor",
        "description": "Source description",
        "bounds": (-1, -1, -1, 1, 1, 1),
        "value_weight": (10, 2.0),
        "armor_rating_raw": 500,
        "body": (expected_slot_mask, 0),
        "race": f"{SOURCE}|0x00000900",
        "enchantment": f"{SOURCE}|0x00000901",
        "pickup_sound": f"{SOURCE}|0x00000902",
        "drop_sound": f"{SOURCE}|0x00000903",
        "equipment_type": f"{SOURCE}|0x00000904",
        "alternate_block_material": f"{SOURCE}|0x00000905",
        "template_armor": f"{SOURCE}|0x00000A01",
        "male_world_model": expected_male_world_model,
        "female_world_model": expected_female_world_model,
        "armor_addons": expected_armor_addons,
        "keywords": KEYWORDS,
        "keyword_count": 2,
    }
    for key, value in expected.items():
        if actual.get(key) != value:
            failures.append(
                f"ARMO {key} expected={value!r} actual={actual.get(key)!r}"
            )

    expected_signatures = [
        "EDID", "OBND", "FULL", "EITM", "MOD2", "MOD4", "BOD2",
        "YNAM", "ZNAM", "ETYP", "BAMT", "RNAM", "KSIZ", "KWDA",
        "DESC",
    ] + ["MODL"] * len(expected_armor_addons) + [
        "DATA", "DNAM", "TNAM",
    ]
    if signatures != expected_signatures:
        failures.append(
            f"ARMO subrecord sequence expected={expected_signatures!r} "
            f"actual={signatures!r}"
        )

    inspect_armor_proposal(
        proposal_path,
        expected_editor_id,
        expected_armor_addons,
        expected_slot_mask,
        failures,
        expected_male_world_model,
        expected_female_world_model,
    )
    return {
        "record_counts": counts,
        "masters": plugin_masters,
        "owner": actual.get("owner"),
        "local_form_id": (
            None
            if actual.get("local_form") is None
            else f"0x{int(actual['local_form']):08X}"
        ),
        "major_flags": f"0x{int(actual.get('major_flags', 0)):08X}",
        "editor_id": actual.get("editor_id"),
        "name": actual.get("name"),
        "description": actual.get("description"),
        "bounds": actual.get("bounds"),
        "value_weight": actual.get("value_weight"),
        "armor_rating_raw_hundredths": actual.get("armor_rating_raw"),
        "bod2": actual.get("body"),
        "references": {
            key: actual.get(key)
            for key in (
                "race", "enchantment", "pickup_sound", "drop_sound",
                "equipment_type", "alternate_block_material", "template_armor",
            )
        },
        "models": {
            "male": actual.get("male_world_model"),
            "female": actual.get("female_world_model"),
        },
        "ordered_armor_addons": actual.get("armor_addons"),
        "unique_keywords": actual.get("keywords"),
        "subrecord_sequence": signatures,
    }


def inspect_armor_proposal(
    path: Path,
    expected_editor_id: str,
    expected_armor_addons: list[str],
    expected_slot_mask: int,
    failures: list[str],
    expected_male_world_model: str = r"armor\old_m.nif",
    expected_female_world_model: str = r"armor\old_f.nif",
) -> None:
    proposal = load_json(path)
    exact = {
        "schemaVersion": "1",
        "artifactKind": "armor-record-proposal",
        "edition": "skyrimse",
        "mode": "new",
        "sourceFormId": "0x00000A00",
        "editorId": expected_editor_id,
        "name": "Generated travel armor",
        "slotMask": expected_slot_mask,
        "race": f"{SOURCE}|0x00000900",
        "maleWorldModel": expected_male_world_model,
        "femaleWorldModel": expected_female_world_model,
        "value": 10,
        "weight": 2,
        "health": None,
        "armorRating": 5,
        "keywords": KEYWORDS,
        "armorAddons": [
            {"index": 0, "addon": addon}
            for addon in expected_armor_addons
        ],
        "masterDependencies": [],
        "noUnrelatedRecords": True,
        "targetFormId": "0x00000B00",
        "description": "Source description",
        "nonPlayable": False,
        "enchantment": f"{SOURCE}|0x00000901",
        "pickupSound": f"{SOURCE}|0x00000902",
        "dropSound": f"{SOURCE}|0x00000903",
        "equipmentType": f"{SOURCE}|0x00000904",
        "alternateBlockMaterial": f"{SOURCE}|0x00000905",
        "templateArmor": f"{SOURCE}|0x00000A01",
        "objectBounds": {
            "minimumX": -1,
            "minimumY": -1,
            "minimumZ": -1,
            "maximumX": 1,
            "maximumY": 1,
            "maximumZ": 1,
        },
        "completeDocument": True,
    }
    for key, value in exact.items():
        if proposal.get(key) != value:
            failures.append(
                f"ARMO proposal {key} expected={value!r} "
                f"actual={proposal.get(key)!r}"
            )
    expected_changed = [
        "name", "slotMask", "race", "maleWorldModel", "femaleWorldModel",
        "value", "weight", "armorRating", "keywords", "armorAddons",
        "description", "nonPlayable", "enchantment", "pickupSound",
        "dropSound", "equipmentType", "alternateBlockMaterial",
        "templateArmor", "objectBounds",
    ]
    if proposal.get("changedFields") != expected_changed:
        failures.append("ARMO proposal changedFields lost complete-document scope")
    for key in ("inputSha256", "patchSha256"):
        if not re.fullmatch(r"[0-9A-Fa-f]{64}", str(proposal.get(key, ""))):
            failures.append(f"ARMO proposal {key} is not a SHA-256")


def inspect_outfit(
    plugin: Path, proposal_path: Path, failures: list[str]
) -> dict[str, object]:
    data = plugin.read_bytes()
    plugin_masters = masters(data)
    expected_masters = [
        "OutfitProviderAfter.esp", "OutfitBase.esm", GENERATED
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
    major_flags = None
    editor = None
    items: list[str] = []
    signatures: list[str] = []
    if len(records) != 1:
        failures.append("Follow-on output did not expose exactly one OTFT")
    else:
        owner, local_form, major_flags, body = records[0]
        fields = field_map(body)
        signatures = [signature for signature, _ in subrecords(body)]
        editor = decode_zstring(one(fields, "EDID", failures, "OTFT"))
        items = [
            resolve_raw_reference(raw, plugin.name, plugin_masters)
            for payload in fields.get("INAM", [])
            for raw in unpack_form_ids(payload)
        ]
    exact = {
        "owner": plugin.name,
        "local_form": 0xA20,
        "major_flags": 0,
        "editor": "npcm_OTFT_GeneratedArmor",
        "items": [GENERATED_REFERENCE],
        "signatures": ["EDID", "INAM"],
    }
    observed = {
        "owner": owner,
        "local_form": local_form,
        "major_flags": major_flags,
        "editor": editor,
        "items": items,
        "signatures": signatures,
    }
    for key, value in exact.items():
        if observed[key] != value:
            failures.append(
                f"OTFT {key} expected={value!r} actual={observed[key]!r}"
            )

    proposal = load_json(proposal_path)
    proposal_exact = {
        "schemaVersion": "1",
        "artifactKind": "outfit-record-proposal",
        "edition": "skyrimse",
        "mode": "new",
        "sourceFormId": "0x00000900",
        "editorId": "npcm_OTFT_GeneratedArmor",
        "items": [GENERATED_REFERENCE],
        "masterDependencies": ["OutfitBase.esm", GENERATED],
        "noUnrelatedRecords": True,
        "targetFormId": "0x00000A20",
        "sourceOwnerPlugin": "OutfitBase.esm",
    }
    for key, value in proposal_exact.items():
        if proposal.get(key) != value:
            failures.append(
                f"OTFT proposal {key} expected={value!r} "
                f"actual={proposal.get(key)!r}"
            )
    if not re.fullmatch(
        r"[0-9A-Fa-f]{64}", str(proposal.get("inputSha256", ""))
    ):
        failures.append("OTFT proposal inputSha256 is not a SHA-256")
    return {
        "record_counts": counts,
        "masters": plugin_masters,
        "owner": owner,
        "local_form_id": (
            None if local_form is None else f"0x{local_form:08X}"
        ),
        "major_flags": (
            None if major_flags is None else f"0x{major_flags:08X}"
        ),
        "editor_id": editor,
        "ordered_items": items,
        "subrecord_sequence": signatures,
    }


def ensure_under_project(path: Path) -> None:
    try:
        path.relative_to(PROJECT_ROOT)
    except ValueError as error:
        raise ValueError(f"Path escaped project root: {path}") from error


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def load_json(path: Path) -> dict[str, object]:
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict):
        raise ValueError(f"Expected JSON object: {path}")
    return value


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
) -> tuple[dict[str, int], list[tuple[str, int, int, bytes]]]:
    counts: dict[str, int] = {}
    records: list[tuple[str, int, int, bytes]] = []

    def capture(
        signature: str, form_id: int, flags: int, body: bytes, depth: int
    ) -> None:
        del depth
        counts[signature] = counts.get(signature, 0) + 1
        if signature == wanted:
            owner, local = resolve_form_id(form_id, self_plugin, plugin_masters)
            records.append((owner, local, flags, body))

    walk(data, capture)
    return counts, records


def forbid(counts: dict[str, int], role: str, failures: list[str]) -> None:
    for signature in sorted(FORBIDDEN & set(counts)):
        failures.append(f"{role} contains forbidden {signature} record surface")


def field_map(body: bytes) -> dict[str, list[bytes]]:
    result: dict[str, list[bytes]] = {}
    for signature, payload in subrecords(body):
        result.setdefault(signature, []).append(payload)
    return result


def one(
    fields: dict[str, list[bytes]],
    signature: str,
    failures: list[str],
    role: str,
) -> bytes:
    values = fields.get(signature, [])
    if len(values) != 1:
        failures.append(f"{role} {signature} is missing or duplicated")
        return b""
    return values[0]


def unpack(payload: bytes, format_: str) -> tuple[object, ...] | None:
    if len(payload) != struct.calcsize(format_):
        return None
    return struct.unpack(format_, payload)


def scalar(payload: bytes, format_: str) -> int:
    value = unpack(payload, format_)
    return -1 if value is None else int(value[0])


def form_reference(
    fields: dict[str, list[bytes]],
    signature: str,
    self_plugin: str,
    plugin_masters: list[str],
    failures: list[str],
) -> str | None:
    payload = one(fields, signature, failures, "ARMO")
    if len(payload) != 4:
        return None
    return resolve_raw_reference(
        struct.unpack("<I", payload)[0], self_plugin, plugin_masters
    )


def decode_zstring(payload: bytes) -> str:
    return payload.rstrip(b"\0").decode("cp1252")


def unpack_form_ids(payload: bytes) -> list[int]:
    if not payload or len(payload) % 4:
        raise ValueError("Packed FormID subrecord has an invalid length")
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
        raise ValueError(f"Raw FormID 0x{raw:08X} exceeds the master table")
    return owner, raw & 0x00FFFFFF


if __name__ == "__main__":
    raise SystemExit(main())
