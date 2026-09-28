#!/usr/bin/env python3
"""Independent SKY-GUI-002 artifact verifier.

This verifier intentionally knows only the frozen JSON/file contracts. It does
not load NPC Manager assemblies and it never writes game-facing artifacts.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import pathlib
import struct
import sys
import zlib
from typing import Any


PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-manifest", required=True)
    parser.add_argument("--catalog", required=True)
    parser.add_argument("--session", required=True)
    parser.add_argument("--settings", required=True)
    parser.add_argument("--scene", required=True)
    parser.add_argument("--image", required=True)
    parser.add_argument("--nif", required=True)
    parser.add_argument("--expected-owner", required=True)
    parser.add_argument("--expected-winner", required=True)
    parser.add_argument("--expected-npc", required=True)
    parser.add_argument("--expected-lvln", required=True)
    parser.add_argument("--report", required=True)
    return parser.parse_args()


def no_duplicate_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def load_json(path: pathlib.Path) -> Any:
    with path.open("r", encoding="utf-8-sig") as stream:
        return json.load(stream, object_pairs_hook=no_duplicate_object)


def sha256(path: pathlib.Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def normalize_form(value: Any) -> str:
    text = str(value).strip()
    if text.lower().startswith("0x"):
        number = int(text[2:], 16)
    else:
        number = int(text, 16)
    return f"0x{number:08X}"


def validate_png(path: pathlib.Path) -> tuple[int, int, int]:
    payload = path.read_bytes()
    if not payload.startswith(PNG_SIGNATURE):
        raise ValueError("PNG signature is invalid")
    cursor = len(PNG_SIGNATURE)
    width = height = color_type = bit_depth = None
    compressed = bytearray()
    while cursor + 12 <= len(payload):
        length = struct.unpack(">I", payload[cursor : cursor + 4])[0]
        kind = payload[cursor + 4 : cursor + 8]
        data = payload[cursor + 8 : cursor + 8 + length]
        if cursor + 12 + length > len(payload):
            raise ValueError("PNG chunk exceeds file bounds")
        if kind == b"IHDR":
            width, height, bit_depth, color_type = struct.unpack(
                ">IIBB", data[:10]
            )
        elif kind == b"IDAT":
            compressed.extend(data)
        elif kind == b"IEND":
            break
        cursor += 12 + length
    if (
        width is None
        or height is None
        or width <= 0
        or height <= 0
        or bit_depth != 8
        or color_type not in (0, 2, 4, 6)
    ):
        raise ValueError("PNG IHDR is unsupported or invalid")
    channels = {0: 1, 2: 3, 4: 2, 6: 4}[color_type]
    row_bytes = width * channels
    raw = zlib.decompress(bytes(compressed))
    expected = height * (row_bytes + 1)
    if len(raw) != expected:
        raise ValueError("PNG decompressed byte count is inconsistent")
    prior = bytearray(row_bytes)
    distinct: set[bytes] = set()
    offset = 0
    for _ in range(height):
        filter_type = raw[offset]
        source = raw[offset + 1 : offset + 1 + row_bytes]
        offset += row_bytes + 1
        row = bytearray(row_bytes)
        for index, value in enumerate(source):
            left = row[index - channels] if index >= channels else 0
            above = prior[index]
            upper_left = prior[index - channels] if index >= channels else 0
            if filter_type == 0:
                projected = value
            elif filter_type == 1:
                projected = (value + left) & 0xFF
            elif filter_type == 2:
                projected = (value + above) & 0xFF
            elif filter_type == 3:
                projected = (value + ((left + above) // 2)) & 0xFF
            elif filter_type == 4:
                p = left + above - upper_left
                pa = abs(p - left)
                pb = abs(p - above)
                pc = abs(p - upper_left)
                predictor = left if pa <= pb and pa <= pc else above if pb <= pc else upper_left
                projected = (value + predictor) & 0xFF
            else:
                raise ValueError(f"unsupported PNG filter {filter_type}")
            row[index] = projected
        for pixel in range(0, row_bytes, channels):
            distinct.add(bytes(row[pixel : pixel + channels]))
            if len(distinct) >= 16:
                break
        prior = row
    if len(distinct) < 2:
        raise ValueError("PNG pixels are blank/constant")
    return width, height, len(distinct)


def main() -> int:
    args = parse_args()
    paths = {
        name: pathlib.Path(value).resolve()
        for name, value in {
            "sourceManifest": args.source_manifest,
            "catalog": args.catalog,
            "session": args.session,
            "settings": args.settings,
            "scene": args.scene,
            "image": args.image,
            "nif": args.nif,
        }.items()
    }
    findings: list[str] = []
    checks: dict[str, Any] = {}

    documents: dict[str, Any] = {}
    for name in ("sourceManifest", "catalog", "session", "settings", "scene"):
        try:
            documents[name] = load_json(paths[name])
            checks[f"{name}DuplicateKeySafe"] = True
        except Exception as exception:  # bounded evidence collector
            findings.append(f"{name}: {exception}")

    source = documents.get("sourceManifest")
    if isinstance(source, dict):
        try:
            if source.get("runtimeAuthority") is not False:
                raise ValueError("source manifest claims runtime authority")
            files = source["files"]
            if not isinstance(files, list) or len(files) < 10:
                raise ValueError("source manifest has too few bound files")
            for item in files:
                source_path = pathlib.Path(item["path"]).resolve()
                if not source_path.is_file():
                    raise ValueError(f"source file is absent: {source_path}")
                if sha256(source_path) != str(item["sha256"]).upper():
                    raise ValueError(f"source hash changed: {source_path}")
                if source_path.stat().st_size != int(item["byteLength"]):
                    raise ValueError(f"source length changed: {source_path}")
            checks["sourceFilesUnchanged"] = len(files)
        except Exception as exception:
            findings.append(f"sourceManifest: {exception}")

    catalog = documents.get("catalog")
    expected_npc = normalize_form(args.expected_npc)
    expected_lvln = normalize_form(args.expected_lvln)
    selected_record: dict[str, Any] | None = None
    if isinstance(catalog, dict):
        try:
            if catalog.get("runtimeAuthority") is not False:
                raise ValueError("catalog claims runtime authority")
            records = catalog["records"]
            if len(records) != 4:
                raise ValueError(f"catalog record count is {len(records)}, expected 4")
            selected_record = next(
                row
                for row in records
                if normalize_form(row["formId"]) == expected_npc
            )
            if selected_record["owner"] != args.expected_owner:
                raise ValueError("NPC owner mismatch")
            if selected_record["winner"] != args.expected_winner:
                raise ValueError("NPC winner mismatch")
            if selected_record["signature"] != "NPC_":
                raise ValueError("NPC signature mismatch")
            if len(str(selected_record["rawRecordSha256"])) != 64:
                raise ValueError("NPC raw-record hash is invalid")
            if selected_record.get("deletePending") is not False:
                raise ValueError("delete/restore final state is wrong")
            child_record = next(
                row
                for row in records
                if normalize_form(row["formId"]) == "0x00000801"
            )
            if child_record.get("changed") is not True:
                raise ValueError("verified child did not leave the source-owned NPC dirty")
            if child_record.get("deletePending") is not False:
                raise ValueError("source-owned NPC delete/restore state is wrong")
            lvln = next(
                row
                for row in records
                if normalize_form(row["formId"]) == expected_lvln
            )
            if lvln["owner"] != args.expected_owner:
                raise ValueError("LVLN owner mismatch")
            if lvln["winner"] != args.expected_winner:
                raise ValueError("LVLN winner mismatch")
            if lvln["signature"] != "LVLN":
                raise ValueError("LVLN signature mismatch")
            order = [normalize_form(item["formId"]) for item in lvln["entries"]]
            if order != ["0x00000800", "0x00000801"]:
                raise ValueError(f"LVLN order mismatch: {order}")
            checks["catalogIdentityAndOrder"] = True
        except Exception as exception:
            findings.append(f"catalog: {exception}")

    session = documents.get("session")
    if isinstance(session, dict):
        try:
            if session.get("runtimeAuthority") is not False:
                raise ValueError("session claims runtime authority")
            selection = session["selection"]
            if len(selection) != 2:
                raise ValueError("session must retain both selected actors")
            identity = next(
                item
                for item in selection
                if normalize_form(item["formId"]) == expected_npc
            )
            if (
                identity["ownerPlugin"] != args.expected_owner
                or identity["winningProvider"] != args.expected_winner
                or normalize_form(identity["formId"]) != expected_npc
            ):
                raise ValueError("session selected identity mismatch")
            drafts = session["drafts"]
            if len(drafts) != 1:
                raise ValueError("session must retain one final dirty draft")
            if drafts[0].get("isChanged") is not True:
                raise ValueError("session dirty draft is absent")
            if drafts[0].get("isDeletePending") is not False:
                raise ValueError("session delete/restore state is wrong")
            if normalize_form(drafts[0]["identity"]["formId"]) != "0x00000801":
                raise ValueError("session dirty draft is not bound to the source-owned NPC")
            artifacts = session["artifacts"]
            if len(artifacts) != 1:
                raise ValueError("session must retain one child artifact")
            artifact = artifacts[0]
            if artifact.get("runtimeAuthority") is not False:
                raise ValueError("child artifact claims runtime authority")
            if normalize_form(artifact["identity"]["formId"]) != "0x00000801":
                raise ValueError("child artifact is not bound to the source-owned NPC")
            artifact_path = pathlib.Path(artifact["path"]).resolve()
            if sha256(artifact_path) != str(artifact["sha256"]).upper():
                raise ValueError("child artifact hash mismatch")
            checks["sessionDraftAndArtifact"] = True
        except Exception as exception:
            findings.append(f"session: {exception}")

    settings = documents.get("settings")
    if isinstance(settings, dict):
        try:
            if settings.get("schemaVersion") != "1":
                raise ValueError("settings schema mismatch")
            if not isinstance(settings.get("filter"), dict):
                raise ValueError("settings filter is absent")
            if not isinstance(settings.get("preview"), dict):
                raise ValueError("settings preview is absent")
            checks["settingsParsed"] = True
        except Exception as exception:
            findings.append(f"settings: {exception}")

    scene = documents.get("scene")
    if isinstance(scene, dict):
        try:
            if normalize_form(scene["npcFormId"]) != expected_npc:
                raise ValueError("scene actor mismatch")
            if selected_record is None:
                raise ValueError("catalog actor was unavailable")
            if scene.get("renderedImage", {}).get("sha256", "").upper() != sha256(
                paths["image"]
            ):
                raise ValueError("scene rendered-image hash mismatch")
            assets = scene["assets"]
            if not assets:
                raise ValueError("scene has no source assets")
            asset_root = paths["sourceManifest"].parent / "Data"
            for asset in assets:
                asset_path = asset_root / str(asset["path"]).replace("\\", "/")
                if not asset_path.is_file():
                    raise ValueError(f"scene asset is absent: {asset_path}")
                if sha256(asset_path) != str(asset["sha256"]).upper():
                    raise ValueError(f"scene asset hash mismatch: {asset_path}")
            checks["sceneActorAndAssets"] = len(assets)
        except Exception as exception:
            findings.append(f"scene: {exception}")

    try:
        width, height, colors = validate_png(paths["image"])
        checks["png"] = {
            "width": width,
            "height": height,
            "sampledDistinctColors": colors,
            "sha256": sha256(paths["image"]),
        }
    except Exception as exception:
        findings.append(f"image: {exception}")

    try:
        nif_bytes = paths["nif"].read_bytes()
        if len(nif_bytes) <= 32:
            raise ValueError("NIF is too short")
        if not nif_bytes.startswith(b"Gamebryo File Format, Version 20.2.0.7"):
            raise ValueError("NIF header/version mismatch")
        evidence = load_json(pathlib.Path(str(paths["nif"]) + ".evidence.json"))
        if evidence.get("runtimeAuthority") is not False:
            raise ValueError("NIF evidence claims runtime authority")
        if str(evidence["nifSha256"]).upper() != sha256(paths["nif"]):
            raise ValueError("NIF evidence hash mismatch")
        if str(evidence["sceneSourceSha256"]).upper() != sha256(paths["scene"]):
            raise ValueError("NIF scene-source hash mismatch")
        checks["nif"] = {
            "byteLength": len(nif_bytes),
            "sha256": sha256(paths["nif"]),
            "sceneSourceSha256": sha256(paths["scene"]),
        }
    except Exception as exception:
        findings.append(f"nif: {exception}")

    expected_failure = "negative-control" in pathlib.Path(args.report).name.lower()
    if expected_failure and len(findings) == 1:
        verdict = "EXPECTED_FAIL"
        exit_code = 0
    elif not expected_failure and not findings:
        verdict = "PASS"
        exit_code = 0
    else:
        verdict = "FAIL"
        exit_code = 1

    report = {
        "schemaVersion": 1,
        "artifactKind": "sky-gui-002-independent-verification",
        "verdict": verdict,
        "findingCount": len(findings),
        "findings": findings,
        "checks": checks,
        "inputHashes": {
            name: sha256(path)
            for name, path in paths.items()
            if path.is_file()
        },
        "runtimeAuthority": False,
    }
    report_path = pathlib.Path(args.report).resolve()
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(
        json.dumps(report, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )
    print(f"{verdict} findings={len(findings)} report={report_path}")
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
