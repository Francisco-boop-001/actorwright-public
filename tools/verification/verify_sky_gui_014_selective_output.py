#!/usr/bin/env python3
"""Independent raw TES4 and RaceMenu JSON audit for SKY-GUI-014."""

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

TARGET_ID = 0x800
SOURCE_ID = 0x900
SELECTED_FIELDS = {"PNAM", "FTST", "NAM7", "DOFT", "SOFT", "ACBS"}
FORBIDDEN = {"WRLD", "CELL", "LAND", "WATR", "NAVM", "NAVI", "VMAD"}
EXPECTED_CATEGORIES = [
    "body-weight", "body-shape", "outfits", "face-parts", "sculpt", "chargen-flag"
]
EXPECTED_SECTIONS = ["body-weight", "body-sliders", "face-parts", "sculpt"]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-plugin", required=True)
    parser.add_argument("--source-preset", required=True)
    parser.add_argument("--target-preset", required=True)
    parser.add_argument("--output-plugin", required=True)
    parser.add_argument("--output-preset", required=True)
    parser.add_argument("--proposal", required=True)
    parser.add_argument("--report", required=True)
    args = parser.parse_args()
    paths = {name: Path(value).resolve() for name, value in vars(args).items()}
    for path in paths.values():
        ensure_under_project(path)
    for name, path in paths.items():
        if name != "report" and not path.is_file():
            raise FileNotFoundError(f"Required artifact is missing: {path}")

    source_plugin = paths["source_plugin"]
    output_plugin = paths["output_plugin"]
    source_data = source_plugin.read_bytes()
    output_data = output_plugin.read_bytes()
    source_preset = load_json(paths["source_preset"])
    target_preset = load_json(paths["target_preset"])
    output_preset = load_json(paths["output_preset"])
    proposal = load_json(paths["proposal"])
    failures: list[str] = []

    output_masters = masters(output_data)
    if output_masters != [source_plugin.name]:
        failures.append(
            f"output masters expected={[source_plugin.name]!r} actual={output_masters!r}"
        )
    source_counts, source_npcs = inventory(source_data)
    output_counts, output_npcs = inventory(output_data)
    if output_counts != {"NPC_": 1}:
        failures.append(f"output record surface expected={{'NPC_': 1}} actual={output_counts!r}")
    for signature in sorted(FORBIDDEN & set(output_counts)):
        failures.append(f"output contains forbidden {signature} record surface")
    target = source_npcs.get(TARGET_ID)
    donor = source_npcs.get(SOURCE_ID)
    result = output_npcs.get(TARGET_ID)
    if target is None or donor is None or result is None:
        failures.append("source target, source donor, or output target NPC is missing/duplicated")
    if result is not None and result[0] != TARGET_ID:
        failures.append(f"output target ownership expected=0x00000800 actual=0x{result[0]:08X}")

    observed: dict[str, object] = {
        "source_record_counts": source_counts,
        "output_record_counts": output_counts,
        "output_masters": output_masters,
    }
    if target and donor and result:
        target_fields = field_map(target[1])
        donor_fields = field_map(donor[1])
        result_fields = field_map(result[1])
        for signature in ("PNAM", "FTST", "NAM7", "DOFT", "SOFT"):
            if result_fields.get(signature, []) != donor_fields.get(signature, []):
                failures.append(f"selected {signature} does not equal source NPC")
        for signature in sorted(
            (set(target_fields) | set(result_fields)) - SELECTED_FIELDS
        ):
            if result_fields.get(signature, []) != target_fields.get(signature, []):
                failures.append(f"unchecked {signature} does not preserve target NPC")
        target_acbs = one(target_fields, "ACBS", failures, "target")
        donor_acbs = one(donor_fields, "ACBS", failures, "source")
        result_acbs = one(result_fields, "ACBS", failures, "output")
        if target_acbs and donor_acbs and result_acbs:
            target_flags = struct.unpack_from("<I", target_acbs)[0]
            donor_flags = struct.unpack_from("<I", donor_acbs)[0]
            result_flags = struct.unpack_from("<I", result_acbs)[0]
            expected_flags = (target_flags & ~0x04) | (donor_flags & 0x04)
            if result_flags != expected_flags:
                failures.append(
                    f"ACBS flags expected=0x{expected_flags:08X} actual=0x{result_flags:08X}"
                )
            if result_acbs[4:] != target_acbs[4:]:
                failures.append("ACBS changed bytes outside the selected CharGen flag")
            observed["acbs_flags"] = {
                "target": f"0x{target_flags:08X}",
                "source": f"0x{donor_flags:08X}",
                "output": f"0x{result_flags:08X}",
            }
        observed["plugin_fields"] = {
            "selected_equal_source": sorted(SELECTED_FIELDS),
            "unchecked_equal_target": sorted(set(target_fields) - SELECTED_FIELDS),
        }

    selected_json = {
        "actor.weight": path_value(source_preset, "actor", "weight"),
        "bodyMorphs": source_preset.get("bodyMorphs"),
        "transforms": source_preset.get("transforms"),
        "headParts": source_preset.get("headParts"),
        "actor.headTexture": path_value(source_preset, "actor", "headTexture"),
        "morphs.sculptDivisor": path_value(source_preset, "morphs", "sculptDivisor"),
        "morphs.sculpt": path_value(source_preset, "morphs", "sculpt"),
    }
    actual_selected = {
        "actor.weight": path_value(output_preset, "actor", "weight"),
        "bodyMorphs": output_preset.get("bodyMorphs"),
        "transforms": output_preset.get("transforms"),
        "headParts": output_preset.get("headParts"),
        "actor.headTexture": path_value(output_preset, "actor", "headTexture"),
        "morphs.sculptDivisor": path_value(output_preset, "morphs", "sculptDivisor"),
        "morphs.sculpt": path_value(output_preset, "morphs", "sculpt"),
    }
    if actual_selected != selected_json:
        failures.append("one or more selected jslot carriers do not equal the source")
    unchecked_json = {
        "version": target_preset.get("version"),
        "faceTextures": target_preset.get("faceTextures"),
        "modNames": target_preset.get("modNames"),
        "mods": target_preset.get("mods"),
        "actor.hairColor": path_value(target_preset, "actor", "hairColor"),
        "tintInfo": target_preset.get("tintInfo"),
        "morphs.default": path_value(target_preset, "morphs", "default"),
        "morphs.custom": path_value(target_preset, "morphs", "custom"),
        "overrides": target_preset.get("overrides"),
        "skinOverrides": target_preset.get("skinOverrides"),
    }
    actual_unchecked = {
        "version": output_preset.get("version"),
        "faceTextures": output_preset.get("faceTextures"),
        "modNames": output_preset.get("modNames"),
        "mods": output_preset.get("mods"),
        "actor.hairColor": path_value(output_preset, "actor", "hairColor"),
        "tintInfo": output_preset.get("tintInfo"),
        "morphs.default": path_value(output_preset, "morphs", "default"),
        "morphs.custom": path_value(output_preset, "morphs", "custom"),
        "overrides": output_preset.get("overrides"),
        "skinOverrides": output_preset.get("skinOverrides"),
    }
    if actual_unchecked != unchecked_json:
        failures.append("one or more unchecked/metadata jslot carriers drifted from target")

    if proposal.get("schemaVersion") != "1":
        failures.append(f"transaction proposal schema unexpected: {proposal.get('schemaVersion')!r}")
    if proposal.get("artifactKind") != "skyrim-selective-appearance-paste-proposal":
        failures.append("transaction proposal artifact kind is wrong")
    if proposal.get("categories") != EXPECTED_CATEGORIES:
        failures.append(f"proposal categories unexpected: {proposal.get('categories')!r}")
    if proposal.get("presetSections") != EXPECTED_SECTIONS:
        failures.append(f"proposal preset sections unexpected: {proposal.get('presetSections')!r}")
    exact_paths = {
        "sourcePluginPath": source_plugin,
        "sourcePreset": paths["source_preset"],
        "targetPreset": paths["target_preset"],
        "outputPlugin": output_plugin,
        "outputPreset": paths["output_preset"],
    }
    for key, expected in exact_paths.items():
        if Path(proposal.get(key, "")).resolve() != expected:
            failures.append(f"proposal {key} does not bind the exact artifact")
    hashes = {
        "sourcePluginSha256": source_plugin,
        "sourcePresetSha256": paths["source_preset"],
        "targetPresetSha256": paths["target_preset"],
    }
    for key, artifact in hashes.items():
        if proposal.get(key, "").lower() != sha256(artifact).lower():
            failures.append(f"proposal {key} does not bind exact bytes")

    payload = {
        "schema": "npcmanager.sky-gui-014.selective-output-raw-audit.v1",
        "passed": not failures,
        "source_plugin": str(source_plugin),
        "source_plugin_sha256": sha256(source_plugin),
        "source_preset_sha256": sha256(paths["source_preset"]),
        "target_preset_sha256": sha256(paths["target_preset"]),
        "output_plugin_sha256": sha256(output_plugin),
        "output_preset_sha256": sha256(paths["output_preset"]),
        "proposal_sha256": sha256(paths["proposal"]),
        "observed": observed,
        "selected_preset_carriers_match_source": actual_selected == selected_json,
        "unchecked_preset_carriers_match_target": actual_unchecked == unchecked_json,
        "failures": failures,
        "facegen_authority": False,
        "bodyslide_build_authority": False,
        "runtime_authority": False,
        "visual_authority": False,
    }
    paths["report"].parent.mkdir(parents=True, exist_ok=True)
    paths["report"].write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(payload, indent=2))
    return 0 if not failures else 1


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
    return [payload.rstrip(b"\0").decode("cp1252")
            for signature, payload in subrecords(data[24:24 + size])
            if signature == "MAST"]


def inventory(data: bytes) -> tuple[dict[str, int], dict[int, tuple[int, bytes]]]:
    counts: dict[str, int] = {}
    matches: dict[int, list[tuple[int, bytes]]] = {TARGET_ID: [], SOURCE_ID: []}

    def capture(signature: str, form_id: int, flags: int, body: bytes, depth: int) -> None:
        del flags, depth
        counts[signature] = counts.get(signature, 0) + 1
        local = form_id & 0x00FFFFFF
        if signature == "NPC_" and local in matches:
            matches[local].append((form_id, body))

    walk(data, capture)
    return counts, {key: values[0] for key, values in matches.items() if len(values) == 1}


def field_map(body: bytes) -> dict[str, list[bytes]]:
    result: dict[str, list[bytes]] = {}
    for signature, payload in subrecords(body):
        result.setdefault(signature, []).append(payload)
    return result


def one(fields: dict[str, list[bytes]], signature: str,
        failures: list[str], role: str) -> bytes | None:
    values = fields.get(signature, [])
    if len(values) != 1 or len(values[0]) < 4:
        failures.append(f"{role} {signature} missing, duplicated, or too short")
        return None
    return values[0]


def path_value(value: dict[str, object], *parts: str) -> object:
    current: object = value
    for part in parts:
        if not isinstance(current, dict):
            return None
        current = current.get(part)
    return current


if __name__ == "__main__":
    raise SystemExit(main())
