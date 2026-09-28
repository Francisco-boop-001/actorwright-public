#!/usr/bin/env python3
"""Independent raw TES4 audit for SKY-GUI-020 ARMA -> ARMO -> OTFT."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import struct

import verify_sky_gui_018_armor as base

SOURCE = "Source.esp"
SOURCE_ADDON = f"{SOURCE}|0x00000907"
SOURCE_VALUES = {
    "slot_mask": 0x04,
    "male_model": r"armor\source_addon_m.nif",
    "female_model": r"armor\source_addon_f.nif",
    "male_first_model": r"armor\source_addon_1st_m.nif",
    "female_first_model": r"armor\source_addon_1st_f.nif",
    "additional_races": [f"{SOURCE}|0x0000090B"],
    "male_skin": f"{SOURCE}|0x00000910",
    "female_skin": f"{SOURCE}|0x00000911",
    "male_swap": f"{SOURCE}|0x00000912",
    "female_swap": f"{SOURCE}|0x00000913",
    "footstep": f"{SOURCE}|0x00000914",
    "art": f"{SOURCE}|0x00000915",
    "male_priority": 6,
    "female_priority": 7,
    "male_slider": 1,
    "female_slider": 1,
    "detection": 9,
    "weapon": 25.0,
}
RELEASE_VALUES = {
    "slot_mask": 0x40,
    "male_model": r"armor\gate020_override_m.nif",
    "female_model": None,
    "male_first_model": r"armor\gate020_override_1st_m.nif",
    "female_first_model": None,
    "additional_races": [
        f"{SOURCE}|0x00000916",
        f"{SOURCE}|0x0000090B",
    ],
    "male_skin": f"{SOURCE}|0x00000910",
    "female_skin": None,
    "male_swap": f"{SOURCE}|0x00000912",
    "female_swap": None,
    "footstep": f"{SOURCE}|0x00000914",
    "art": None,
    "male_priority": 17,
    "female_priority": 29,
    "male_slider": 1,
    "female_slider": 0,
    "detection": 7,
    "weapon": -8.25,
}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--armor-addon-plugin", required=True)
    parser.add_argument("--armor-addon-proposal", required=True)
    parser.add_argument("--armor-plugin", required=True)
    parser.add_argument("--armor-proposal", required=True)
    parser.add_argument("--outfit-plugin", required=True)
    parser.add_argument("--outfit-proposal", required=True)
    parser.add_argument("--report", required=True)
    parser.add_argument(
        "--expected-parent-armor-addon", default=SOURCE_ADDON
    )
    parser.add_argument("--expect-source-values", action="store_true")
    args = parser.parse_args()
    paths = {
        key: Path(value).resolve()
        for key, value in vars(args).items()
        if key not in {"expected_parent_armor_addon", "expect_source_values"}
    }
    for path in paths.values():
        base.ensure_under_project(path)
    for key, path in paths.items():
        if key != "report" and not path.is_file():
            raise FileNotFoundError(f"Required artifact is missing: {path}")

    expected = SOURCE_VALUES if args.expect_source_values else RELEASE_VALUES
    failures: list[str] = []
    addon = inspect_armor_addon(
        paths["armor_addon_plugin"],
        paths["armor_addon_proposal"],
        expected,
        failures,
    )
    armor = base.inspect_armor(
        paths["armor_plugin"],
        paths["armor_proposal"],
        "npcm_ARMO_SourceArmor",
        [args.expected_parent_armor_addon],
        int(expected["slot_mask"]),
        failures,
    )
    outfit = base.inspect_outfit(
        paths["outfit_plugin"], paths["outfit_proposal"], failures
    )
    payload = {
        "schema": "npcmanager.sky-gui-020.armor-addon-raw-audit.v1",
        "passed": not failures,
        "expectation_profile": (
            "pre-edit-source-negative-control"
            if args.expect_source_values
            else "gate020-release"
        ),
        "expected_parent_armor_addon": args.expected_parent_armor_addon,
        "hashes": {
            key: base.sha256(path)
            for key, path in paths.items()
            if key != "report"
        },
        "observed": {
            "armor_addon": addon,
            "parent_armor": armor,
            "follow_on_outfit": outfit,
        },
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


def inspect_armor_addon(
    plugin: Path,
    proposal_path: Path,
    expected: dict[str, object],
    failures: list[str],
) -> dict[str, object]:
    data = plugin.read_bytes()
    plugin_masters = base.masters(data)
    if plugin_masters != [SOURCE]:
        failures.append(
            f"ARMA masters expected={[SOURCE]!r} actual={plugin_masters!r}"
        )
    counts, records = base.inventory(
        data, plugin.name, plugin_masters, "ARMA"
    )
    if counts != {"ARMA": 1}:
        failures.append(
            f"ARMA record surface expected={{'ARMA': 1}} actual={counts!r}"
        )
    base.forbid(counts, "ARMA", failures)
    actual: dict[str, object] = {}
    signatures: list[str] = []
    if len(records) != 1:
        failures.append("Armor-addon output did not expose exactly one ARMA")
    else:
        owner, local_form, major_flags, body = records[0]
        fields = base.field_map(body)
        signatures = [signature for signature, _ in base.subrecords(body)]
        dnam = base.one(fields, "DNAM", failures, "ARMA")
        actual = {
            "owner": owner,
            "provider": plugin.name,
            "local_form": local_form,
            "major_flags": major_flags,
            "editor_id": base.decode_zstring(
                base.one(fields, "EDID", failures, "ARMA")
            ),
            "body": base.unpack(
                base.one(fields, "BOD2", failures, "ARMA"), "<II"
            ),
            "race": base.form_reference(
                fields, "RNAM", plugin.name, plugin_masters, failures
            ),
            "male_model": optional_zstring(fields, "MOD2"),
            "female_model": optional_zstring(fields, "MOD3"),
            "male_first_model": optional_zstring(fields, "MOD4"),
            "female_first_model": optional_zstring(fields, "MOD5"),
            "male_skin": optional_reference(
                fields, "NAM0", plugin.name, plugin_masters
            ),
            "female_skin": optional_reference(
                fields, "NAM1", plugin.name, plugin_masters
            ),
            "male_swap": optional_reference(
                fields, "NAM2", plugin.name, plugin_masters
            ),
            "female_swap": optional_reference(
                fields, "NAM3", plugin.name, plugin_masters
            ),
            "additional_races": [
                base.resolve_raw_reference(
                    base.scalar(payload, "<I"), plugin.name, plugin_masters
                )
                for payload in fields.get("MODL", [])
            ],
            "footstep": optional_reference(
                fields, "SNDD", plugin.name, plugin_masters
            ),
            "art": optional_reference(
                fields, "ONAM", plugin.name, plugin_masters
            ),
            "male_priority": dnam[0] if len(dnam) == 12 else None,
            "female_priority": dnam[1] if len(dnam) == 12 else None,
            "male_slider": (1 if dnam[2] == 2 else 0)
            if len(dnam) == 12 else None,
            "female_slider": (1 if dnam[3] == 2 else 0)
            if len(dnam) == 12 else None,
            "detection": dnam[6] if len(dnam) == 12 else None,
            "weapon": struct.unpack_from("<f", dnam, 8)[0]
            if len(dnam) == 12 else None,
        }

    exact = {
        "owner": SOURCE,
        "provider": plugin.name,
        "local_form": 0x907,
        "major_flags": 0,
        "editor_id": "SourceAddon",
        "body": (int(expected["slot_mask"]), 0),
        "race": f"{SOURCE}|0x00000900",
        **expected,
    }
    exact.pop("slot_mask")
    for key, value in exact.items():
        observed = actual.get(key)
        if key == "weapon" and observed is not None:
            if abs(float(observed) - float(value)) > 0.0001:
                failures.append(
                    f"ARMA {key} expected={value!r} actual={observed!r}"
                )
        elif observed != value:
            failures.append(
                f"ARMA {key} expected={value!r} actual={observed!r}"
            )

    expected_signatures = ["EDID", "BOD2", "RNAM", "DNAM"]
    for signature, key in (
        ("MOD2", "male_model"),
        ("MOD3", "female_model"),
        ("MOD4", "male_first_model"),
        ("MOD5", "female_first_model"),
        ("NAM0", "male_skin"),
        ("NAM1", "female_skin"),
        ("NAM2", "male_swap"),
        ("NAM3", "female_swap"),
    ):
        if expected[key] is not None:
            expected_signatures.append(signature)
    expected_signatures += ["MODL"] * len(expected["additional_races"])
    if expected["footstep"] is not None:
        expected_signatures.append("SNDD")
    if expected["art"] is not None:
        expected_signatures.append("ONAM")
    if signatures != expected_signatures:
        failures.append(
            f"ARMA subrecord sequence expected={expected_signatures!r} "
            f"actual={signatures!r}"
        )
    inspect_armor_addon_proposal(proposal_path, expected, failures)
    return {
        "record_counts": counts,
        "masters": plugin_masters,
        "owner": actual.get("owner"),
        "winning_provider": actual.get("provider"),
        "local_form_id": (
            None
            if actual.get("local_form") is None
            else f"0x{int(actual['local_form']):08X}"
        ),
        "editor_id": actual.get("editor_id"),
        "body": actual.get("body"),
        "models": {
            key: actual.get(key)
            for key in (
                "male_model", "female_model", "male_first_model",
                "female_first_model",
            )
        },
        "references": {
            key: actual.get(key)
            for key in (
                "race", "male_skin", "female_skin", "male_swap",
                "female_swap", "footstep", "art",
            )
        },
        "ordered_additional_races": actual.get("additional_races"),
        "data": {
            key: actual.get(key)
            for key in (
                "male_priority", "female_priority", "male_slider",
                "female_slider", "detection", "weapon",
            )
        },
        "subrecord_sequence": signatures,
    }


def inspect_armor_addon_proposal(
    path: Path,
    expected: dict[str, object],
    failures: list[str],
) -> None:
    proposal = base.load_json(path)
    exact = {
        "schemaVersion": "1",
        "artifactKind": "armor-addon-record-proposal",
        "edition": "skyrimse",
        "mode": "override",
        "sourceFormId": "0x00000907",
        "editorId": "SourceAddon",
        "slotMask": expected["slot_mask"],
        "race": f"{SOURCE}|0x00000900",
        "footstepSet": expected["footstep"],
        "malePriority": expected["male_priority"],
        "femalePriority": expected["female_priority"],
        "maleWeightSliderFlags": expected["male_slider"],
        "femaleWeightSliderFlags": expected["female_slider"],
        "detectionSound": expected["detection"],
        "weaponAdjust": expected["weapon"],
        "maleModel": expected["male_model"],
        "femaleModel": expected["female_model"],
        "maleFirstPersonModel": expected["male_first_model"],
        "femaleFirstPersonModel": expected["female_first_model"],
        "maleSkinTexture": expected["male_skin"],
        "femaleSkinTexture": expected["female_skin"],
        "maleSkinTextureSwapList": expected["male_swap"],
        "femaleSkinTextureSwapList": expected["female_swap"],
        "artObject": expected["art"],
        "additionalRaces": expected["additional_races"],
        "sculpt": [],
        "masterDependencies": [],
        "noUnrelatedRecords": True,
        "targetFormId": None,
        "completeDocument": True,
        "seedFromSource": False,
        "targetPlugin": None,
    }
    unsupported_null = [
        "maleModelFlags", "femaleModelFlags", "maleColorRemapIndex",
        "femaleColorRemapIndex", "maleMaterialSwap", "femaleMaterialSwap",
        "maleFirstPersonMaterialSwap", "femaleFirstPersonMaterialSwap",
        "noUnderarmorScaling", "hasSculptData", "hiResFirstPersonOnly",
    ]
    exact.update({key: None for key in unsupported_null})
    for key, value in exact.items():
        if proposal.get(key) != value:
            failures.append(
                f"ARMA proposal {key} expected={value!r} "
                f"actual={proposal.get(key)!r}"
            )
    if Path(str(proposal.get("sourcePlugin", ""))).name != SOURCE:
        failures.append(
            f"ARMA proposal sourcePlugin does not end in {SOURCE!r}"
        )
    required_changed = {
        "slotMask", "race", "footstepSet", "malePriority",
        "femalePriority", "maleWeightSliderFlags",
        "femaleWeightSliderFlags", "detectionSound", "weaponAdjust",
        "maleModel", "femaleModel", "maleFirstPersonModel",
        "femaleFirstPersonModel", "maleSkinTexture", "femaleSkinTexture",
        "maleSkinTextureSwapList", "femaleSkinTextureSwapList", "artObject",
        "additionalRaces",
    }
    changed = proposal.get("changedFields")
    if not isinstance(changed, list) or set(changed) != required_changed or (
        len(changed) != len(required_changed)
    ):
        failures.append(
            f"ARMA proposal changedFields is not the exact complete Skyrim set: "
            f"{changed!r}"
        )


def optional_zstring(
    fields: dict[str, list[bytes]], signature: str
) -> str | None:
    values = fields.get(signature, [])
    if not values:
        return None
    if len(values) != 1:
        return f"<duplicate:{len(values)}>"
    return base.decode_zstring(values[0])


def optional_reference(
    fields: dict[str, list[bytes]],
    signature: str,
    plugin: str,
    plugin_masters: list[str],
) -> str | None:
    values = fields.get(signature, [])
    if not values:
        return None
    if len(values) != 1 or len(values[0]) != 4:
        return f"<invalid:{len(values)}>"
    return base.resolve_raw_reference(
        struct.unpack("<I", values[0])[0], plugin, plugin_masters
    )


if __name__ == "__main__":
    raise SystemExit(main())
