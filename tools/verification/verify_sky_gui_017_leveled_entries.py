#!/usr/bin/env python3
"""Independent raw TES4 audit for SKY-GUI-017 ordered LVLI entries."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

from verify_sky_gui_016_leveled_list import (
    EXPECTED_ENTRY,
    decode_lvlo,
    decode_zstring,
    ensure_under_project,
    field_map,
    forbid,
    inspect_outfit,
    inventory,
    load_json,
    masters,
    one,
    sha256,
)


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
        "schema": "npcmanager.sky-gui-017.leveled-entry-raw-audit.v1",
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

    expected_entries = [
        {"item": EXPECTED_ENTRY, "level": 1, "count": 1, "chance_none": 0},
        {
            "item": EXPECTED_ENTRY,
            "level": 32767,
            "count": 32767,
            "chance_none": 0,
        },
    ]
    exact = {
        "owner": plugin.name,
        "local_form": 0xA10,
        "editor": "npcm_LVLI_TravelGear",
        "flags": 0x05,
        "chance_none": 25,
        "max_count_present": False,
        "entries": expected_entries,
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
    expected_proposal_entries = [
        {"item": EXPECTED_ENTRY, "level": 1, "count": 1, "chanceNone": 0},
        {
            "item": EXPECTED_ENTRY,
            "level": 32767,
            "count": 32767,
            "chanceNone": 0,
        },
    ]
    proposal_entries = proposal.get("entries")
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


if __name__ == "__main__":
    raise SystemExit(main())
