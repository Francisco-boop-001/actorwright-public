#!/usr/bin/env python3
"""Independently verify BodySlide .bssliders inspection evidence."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import re
from pathlib import Path


WORKSPACE = Path(r"K:\ExampleWorkspace").resolve()
EDITIONS = {"fallout4", "skyrimse"}
HEX_FORM = re.compile(r"^[0-9A-Fa-f]{6}$")
HASH = re.compile(r"^[0-9A-Fa-f]{64}$")
PLUGIN = re.compile(r"^[^\\/:|\x00]+\.(?:esp|esm|esl)$", re.IGNORECASE)


class DuplicateKey(ValueError):
    pass


def fail(message: str) -> None:
    raise SystemExit(f"RESULT FAIL {message}")


def is_under(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


def is_reparse(path: Path) -> bool:
    try:
        attributes = getattr(path.stat(), "st_file_attributes", 0)
        return path.is_symlink() or bool(attributes & 0x400)
    except OSError:
        return False


def load_json(path: Path) -> object:
    resolved = path.resolve()
    if not is_under(resolved, WORKSPACE) or is_reparse(resolved):
        fail("JSON path outside or linked from K workspace")

    def pairs(items: list[tuple[str, object]]) -> dict[str, object]:
        value: dict[str, object] = {}
        for key, item in items:
            if key in value:
                raise DuplicateKey(key)
            value[key] = item
        return value

    try:
        return json.loads(resolved.read_text(encoding="utf-8-sig"), object_pairs_hook=pairs)
    except DuplicateKey as error:
        fail(f"duplicate JSON property {error}")
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        fail(f"invalid JSON: {error}")
    raise AssertionError("unreachable")


def path_field(value: object, name: str) -> Path:
    if not isinstance(value, str) or not os.path.isabs(value) or "\x00" in value:
        fail(f"{name} path")
    path = Path(value).resolve()
    if not is_under(path, WORKSPACE) or is_reparse(path):
        fail(f"{name} outside or linked from K workspace")
    return path


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def finite(value: object) -> bool:
    if isinstance(value, bool):
        return True
    if isinstance(value, (int, float)):
        return math.isfinite(float(value))
    if isinstance(value, list):
        return all(finite(item) for item in value)
    if isinstance(value, dict):
        return all(finite(item) for item in value.values())
    return True


def source_contract(source: object, edition: str, file: Path) -> tuple[str, int, dict[str, object]]:
    if not isinstance(source, dict) or set(source) != {"version", "plugin", "npcs"}:
        fail("source root fields")
    version = source["version"]
    plugin = source["plugin"]
    npcs = source["npcs"]
    if not isinstance(version, int) or isinstance(version, bool) or not 1 <= version <= 11:
        fail("source version")
    if not isinstance(plugin, str) or not PLUGIN.fullmatch(plugin):
        fail("source plugin")
    if Path(plugin).stem.casefold() != file.stem.casefold():
        fail("source plugin/file mismatch")
    if not isinstance(npcs, dict):
        fail("source npcs")
    if not finite(source):
        fail("source non-finite number")
    for identifier, entry in npcs.items():
        if not isinstance(identifier, str) or "|" not in identifier:
            fail("source identifier")
        master, local = identifier.rsplit("|", 1)
        if not PLUGIN.fullmatch(master) or not HEX_FORM.fullmatch(local):
            fail("source identifier shape")
        if not isinstance(entry, dict):
            fail("source NPC entry")
        allowed = {
            "editorId", "bodyMorphs", "bodyMorphsKeyed", "skinTemplateId", "gender", "overlays",
            "sseBodyOverlays", "sseNodeTransforms", "sseNodeScales", "sseHairColor",
            "sseSkinOverrides", "sseCustomMorphs", "sseSculpt", "sseSculptParts", "sseTintTextures",
        }
        if not set(entry).issubset(allowed):
            fail("source NPC unknown field")
        if edition == "fallout4" and any(field in entry for field in allowed if field.startswith("sse")):
            fail("FO4 source contains SSE-only field")
    return plugin, version, npcs


def diagnostics(value: object) -> set[str]:
    if not isinstance(value, list):
        fail("diagnostics")
    errors: set[str] = set()
    for item in value:
        if not isinstance(item, dict) or not {"code", "severity", "message"}.issubset(item):
            fail("diagnostic fields")
        if item.get("severity") in {"error", "Error"}:
            if not isinstance(item.get("code"), str):
                fail("diagnostic code")
            errors.add(item["code"])
    return errors


def check_npcs(value: object, edition: str, source_npcs: dict[str, object]) -> None:
    if not isinstance(value, list):
        fail("CLI NPC summary")
    identifiers: list[str] = []
    for item in value:
        if not isinstance(item, dict):
            fail("CLI NPC item")
        fields = {
            "identifier", "editorId", "bodyMorphs", "bodyMorphsKeyed", "skinTemplateId", "gender",
            "overlayCount", "sseBodyOverlayCount", "sseNodeTransformCount", "sseSkinOverrideCount",
            "sseCustomMorphCount", "sseSculptVertexCount", "sseSculptPartCount", "sseTintTextureCount",
        }
        if set(item) != fields:
            fail("CLI NPC fields")
        identifier = item["identifier"]
        if not isinstance(identifier, str) or identifier not in source_npcs:
            fail("CLI NPC identifier")
        identifiers.append(identifier)
        source_entry = source_npcs[identifier]
        if not isinstance(source_entry, dict):
            fail("source NPC entry")
        source_morphs = source_entry.get("bodyMorphs", {})
        if not isinstance(source_morphs, dict):
            fail("source body morphs")
        output_morphs = item["bodyMorphs"]
        if not isinstance(output_morphs, list) or any(
                not isinstance(morph, dict) or set(morph) != {"name", "value"}
                for morph in output_morphs):
            fail("CLI body morph shape")
        if [morph["name"] for morph in output_morphs] != sorted(source_morphs):
            fail("CLI/source body morph names")
        for morph in output_morphs:
            if float(morph["value"]) != float(source_morphs[morph["name"]]):
                fail("CLI/source body morph values")
        source_keyed = source_entry.get("bodyMorphsKeyed", {})
        if not isinstance(source_keyed, dict):
            fail("source keyed body morphs")
        output_keyed = item["bodyMorphsKeyed"]
        if not isinstance(output_keyed, list) or any(
                not isinstance(morph, dict) or set(morph) != {"name", "keys"}
                for morph in output_keyed):
            fail("CLI keyed body morph shape")
        if [morph["name"] for morph in output_keyed] != sorted(source_keyed):
            fail("CLI/source keyed body morph names")
        for count_name in fields - {"identifier", "editorId", "bodyMorphs", "bodyMorphsKeyed", "skinTemplateId", "gender"}:
            if not isinstance(item[count_name], int) or isinstance(item[count_name], bool) or item[count_name] < 0:
                fail(f"CLI count {count_name}")
        for morphs_name in ("bodyMorphs", "bodyMorphsKeyed"):
            if not isinstance(item[morphs_name], list):
                fail(f"CLI {morphs_name}")
            names: list[str] = []
            for morph in item[morphs_name]:
                if morphs_name == "bodyMorphs":
                    if not isinstance(morph, dict) or set(morph) != {"name", "value"}:
                        fail("CLI morph item")
                    if not isinstance(morph["name"], str) or not isinstance(morph["value"], (int, float)) or not finite(morph["value"]):
                        fail("CLI morph value")
                    names.append(morph["name"])
                else:
                    if not isinstance(morph, dict) or set(morph) != {"name", "keys"} or not isinstance(morph["name"], str):
                        fail("CLI keyed morph item")
                    if not isinstance(morph["keys"], list):
                        fail("CLI keyed morph keys")
                    names.append(morph["name"])
            if names != sorted(names):
                fail(f"CLI {morphs_name} ordering")
            if len(names) != len(set(names)):
                fail(f"CLI {morphs_name} duplicates")
    if identifiers != sorted(identifiers):
        fail("CLI NPC ordering")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", required=True, type=Path)
    parser.add_argument("--expect", choices=("allowed", "refused"), required=True)
    parser.add_argument("--reason")
    args = parser.parse_args()
    value = load_json(args.json)
    if not isinstance(value, dict) or value.get("schemaVersion") != "1":
        fail("CLI schema version")
    edition = value.get("edition")
    if edition not in EDITIONS:
        fail("CLI edition")
    file = path_field(value.get("file"), "sidecar")
    if file.suffix.casefold() != ".bssliders" or not file.is_file():
        fail("sidecar file")
    if not isinstance(value.get("isValid"), bool) or not isinstance(value.get("roundTripPreserved"), bool):
        fail("CLI validity fields")
    errors = diagnostics(value.get("diagnostics"))
    npcs = value.get("npcs")
    if args.expect == "allowed":
        if not value["isValid"] or not value["roundTripPreserved"] or errors:
            fail("allowed result contains errors")
        source = load_json(file)
        plugin, version, source_npcs = source_contract(source, edition, file)
        if value.get("plugin") != plugin or value.get("version") != version:
            fail("CLI source summary mismatch")
        if value.get("sourceSha256", "").casefold() != sha256(file).casefold() or not HASH.fullmatch(value.get("sourceSha256", "")):
            fail("source SHA-256")
        if not HASH.fullmatch(value.get("canonicalSha256", "")):
            fail("canonical SHA-256")
        canonical = json.dumps(source, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
        if value["canonicalSha256"].casefold() != hashlib.sha256(canonical).hexdigest().casefold():
            fail("canonical source SHA-256")
        check_npcs(npcs, edition, source_npcs)
        if len(npcs) != len(source_npcs):
            fail("CLI/source NPC count")
    else:
        if value["isValid"] or not errors or npcs != []:
            fail("refused result lacks a clean partial-state boundary")
        if args.reason and args.reason not in errors:
            fail(f"missing refusal reason {args.reason}")
    print(f"RESULT PASS body-sidecar schema/edition={edition} expect={args.expect}")


if __name__ == "__main__":
    main()
