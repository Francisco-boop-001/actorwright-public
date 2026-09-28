#!/usr/bin/env python3
"""Independent raw audit for packaged SKY-GUI-021 ARMA/ARMO mesh selection."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import verify_sky_gui_018_armor as base
import verify_sky_gui_020_armor_addon as gate020


REAL_ARMA_MODEL = r"armor\Briar\Briar_1.nif"
REAL_ARMO_MODEL = r"armor\Briar\Briar_GND.nif"
REAL_ARMA_NIF_SHA256 = "19dcfe2dc1545f823e2dc5df2a44571568fe27ff8ffff2899215c34c77af4c88"
REAL_ARMO_NIF_SHA256 = "5c5fe6d84ba76f8bd82df7ac1dedec07190e62eedb5606f8a8ff47854d56ad9d"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--armor-addon-plugin", required=True)
    parser.add_argument("--armor-addon-proposal", required=True)
    parser.add_argument("--armor-plugin", required=True)
    parser.add_argument("--armor-proposal", required=True)
    parser.add_argument("--outfit-plugin", required=True)
    parser.add_argument("--outfit-proposal", required=True)
    parser.add_argument("--armor-addon-nif", required=True)
    parser.add_argument("--armor-world-nif", required=True)
    parser.add_argument("--report", required=True)
    parser.add_argument("--expect-source-values", action="store_true")
    args = parser.parse_args()
    paths = {
        key: Path(value).resolve()
        for key, value in vars(args).items()
        if key != "expect_source_values"
    }
    for path in paths.values():
        base.ensure_under_project(path)
    for key, path in paths.items():
        if key != "report" and not path.is_file():
            raise FileNotFoundError(f"Required artifact is missing: {path}")

    expected_addon = gate020.SOURCE_VALUES.copy()
    expected_male_world = r"armor\old_m.nif"
    expected_female_world = r"armor\old_f.nif"
    if not args.expect_source_values:
        expected_addon = gate020.RELEASE_VALUES.copy()
        expected_addon.update(
            {
                "male_model": REAL_ARMA_MODEL,
                "female_model": REAL_ARMA_MODEL,
                "male_first_model": REAL_ARMA_MODEL,
                "female_first_model": REAL_ARMA_MODEL,
            }
        )
        expected_male_world = REAL_ARMO_MODEL
        expected_female_world = REAL_ARMO_MODEL

    failures: list[str] = []
    addon = gate020.inspect_armor_addon(
        paths["armor_addon_plugin"],
        paths["armor_addon_proposal"],
        expected_addon,
        failures,
    )
    armor = base.inspect_armor(
        paths["armor_plugin"],
        paths["armor_proposal"],
        "npcm_ARMO_SourceArmor",
        [gate020.SOURCE_ADDON],
        int(expected_addon["slot_mask"]),
        failures,
        expected_male_world,
        expected_female_world,
    )
    outfit = base.inspect_outfit(
        paths["outfit_plugin"], paths["outfit_proposal"], failures
    )
    verify_hash(
        paths["armor_addon_nif"], REAL_ARMA_NIF_SHA256,
        "selected ARMA NIF", failures
    )
    verify_hash(
        paths["armor_world_nif"], REAL_ARMO_NIF_SHA256,
        "selected ARMO world NIF", failures
    )

    payload = {
        "schema": "npcmanager.sky-gui-021.mesh-picker-raw-audit.v1",
        "passed": not failures,
        "expectationProfile": (
            "pre-picker-source-negative-control"
            if args.expect_source_values
            else "packaged-real-briar-mesh-selection"
        ),
        "hashes": {
            key: base.sha256(path)
            for key, path in paths.items()
            if key != "report"
        },
        "expected": {
            "armorAddonModels": {
                "maleThirdPerson": expected_addon["male_model"],
                "femaleThirdPerson": expected_addon["female_model"],
                "maleFirstPerson": expected_addon["male_first_model"],
                "femaleFirstPerson": expected_addon["female_first_model"],
            },
            "armorWorldModels": {
                "male": expected_male_world,
                "female": expected_female_world,
            },
        },
        "observed": {
            "armorAddon": addon,
            "parentArmor": armor,
            "followOnOutfit": outfit,
        },
        "failures": failures,
        "pluginAuthority": not failures,
        "offEnginePreviewAuthority": not failures,
        "equipmentAuthority": False,
        "runtimeAuthority": False,
        "visualAuthority": False,
    }
    paths["report"].parent.mkdir(parents=True, exist_ok=True)
    paths["report"].write_text(
        json.dumps(payload, indent=2) + "\n", encoding="utf-8"
    )
    print(json.dumps(payload, indent=2))
    return 0 if not failures else 1


def verify_hash(
    path: Path, expected: str, role: str, failures: list[str]
) -> None:
    actual = base.sha256(path)
    if actual.lower() != expected.lower():
        failures.append(f"{role} hash expected={expected} actual={actual}")


if __name__ == "__main__":
    raise SystemExit(main())
