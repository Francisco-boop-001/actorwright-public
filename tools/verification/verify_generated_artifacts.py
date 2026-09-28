"""Independently verify a generated-plugin/FaceGen-sidecar scan artifact."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import struct
from pathlib import Path


WORKSPACE = Path(r"K:\ExampleWorkspace").resolve()
EDITIONS = {"fallout4", "skyrimse"}
MARKER = "NPC Manager"
FORM_ID = re.compile(r"^0x[0-9A-Fa-f]{8}$")
PLUGIN_EXTENSIONS = {".esp", ".esm", ".esl"}


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


def load(path: Path) -> dict:
    resolved = path.resolve()
    if not is_under(resolved, WORKSPACE) or is_reparse(resolved):
        fail("JSON path outside or linked from K workspace")
    try:
        value = json.loads(resolved.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as error:
        fail(f"invalid JSON: {error}")
    if not isinstance(value, dict):
        fail("root must be an object")
    return value


def path_field(value: object, name: str, root: Path = WORKSPACE) -> Path:
    if not isinstance(value, str) or not os.path.isabs(value) or "\x00" in value:
        fail(f"{name} path")
    path = Path(value).resolve()
    if not is_under(path, root) or is_reparse(path):
        fail(f"{name} outside or linked from K workspace")
    return path


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def tes4_author(path: Path) -> str | None:
    data = path.read_bytes()
    if len(data) < 24 or data[:4] != b"TES4":
        fail(f"plugin TES4 header {path.name}")
    body_length = struct.unpack_from("<I", data, 4)[0]
    if body_length > 16 * 1024 * 1024 or 24 + body_length > len(data):
        fail(f"plugin TES4 body {path.name}")
    body = data[24 : 24 + body_length]
    position = 0
    extended: int | None = None
    while position < len(body):
        if len(body) - position < 6:
            fail(f"plugin subrecord header {path.name}")
        signature = body[position : position + 4]
        size = struct.unpack_from("<H", body, position + 4)[0]
        position += 6
        if signature == b"XXXX":
            if size != 4 or len(body) - position < 4:
                fail(f"plugin XXXX subrecord {path.name}")
            extended = struct.unpack_from("<I", body, position)[0]
            position += 4
            continue
        actual_size = extended if extended is not None else size
        extended = None
        if actual_size > len(body) - position:
            fail(f"plugin subrecord body {path.name}")
        if signature == b"CNAM":
            return body[position : position + actual_size].rstrip(b"\0").decode("utf-8").strip()
        position += actual_size
    return None


def diagnostic_errors(value: object) -> set[str]:
    if not isinstance(value, list):
        fail("diagnostics")
    errors: set[str] = set()
    for item in value:
        if not isinstance(item, dict) or not {"code", "severity", "message"}.issubset(item):
            fail("diagnostic fields")
        if item["severity"] in {"error", "Error"}:
            if not isinstance(item["code"], str):
                fail("diagnostic code")
            errors.add(item["code"])
    return errors


def sidecar_expectation(edition: str, kind: str, relative: str) -> tuple[str, str | None, str]:
    parts = relative.split("/")
    if any(not part or part in {".", ".."} for part in parts):
        fail("sidecar relative path")
    plugin = parts[-2]
    filename = parts[-1]
    stem, extension = os.path.splitext(filename)
    variant = "debugSandbox" if stem.endswith("_2") else "canonical"
    if variant == "debugSandbox":
        stem = stem[:-2]

    if kind == "faceGeom":
        if len(parts) != 7 or "/".join(parts[:5]) != "Meshes/Actors/Character/FaceGenData/FaceGeom" or extension.casefold() != ".nif":
            fail("FaceGeom sidecar layout")
        if not re.fullmatch(r"[0-9A-Fa-f]{8}", stem):
            fail("FaceGeom FormID name")
        return plugin, "0x" + stem.upper(), variant

    if edition == "fallout4":
        root = "Textures/Actors/Character/FaceCustomization"
        if "/".join(parts[:4]) != root or len(parts) != 6 or extension.casefold() != ".dds":
            fail("FO4 sidecar layout")
        suffixes = {
            "faceCustomizationDiffuse": "_d",
            "faceCustomizationNormal": "_msn",
            "faceCustomizationSpecular": "_s",
        }
        suffix = suffixes.get(kind)
        if suffix is None or not stem.endswith(suffix):
            fail("FO4 sidecar kind")
        stem = stem[: -len(suffix)]
    else:
        roots = {
            "faceTint": "Textures/Actors/Character/FaceGenData/FaceTint",
            "faceDetailNeutral": "Textures/Actors/Character/FaceGenData/FaceTint",
            "faceDiffuse": "Textures/Actors/Character/FaceGenData/FaceDiffuse",
            "faceNormal": "Textures/Actors/Character/FaceGenData/FaceNormal",
        }
        root = roots.get(kind)
        if root is None or len(parts) != 7 or "/".join(parts[:5]) != root or extension.casefold() != ".dds":
            fail("SSE sidecar layout")
        if kind == "faceDetailNeutral":
            if stem.casefold() != "facedetailneutral":
                fail("SSE detail-neutral name")
            return plugin, None, variant

    if not re.fullmatch(r"[0-9A-Fa-f]{8}", stem):
        fail("sidecar FormID name")
    return plugin, "0x" + stem.upper(), variant


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", required=True, type=Path)
    parser.add_argument("--expect", choices=("allowed", "refused"), required=True)
    parser.add_argument("--reason")
    args = parser.parse_args()

    value = load(args.json)
    if value.get("schemaVersion") != "1":
        fail("schema version")
    edition = value.get("edition")
    if edition not in EDITIONS:
        fail("edition")
    if value.get("markerAuthor") != MARKER or not isinstance(value.get("isValid"), bool):
        fail("marker or validity")
    data_root = path_field(value.get("dataRoot"), "dataRoot")
    errors = diagnostic_errors(value.get("diagnostics"))
    plugins = value.get("plugins")
    sidecars = value.get("sidecars")
    if not isinstance(plugins, list) or not isinstance(sidecars, list):
        fail("plugins or sidecars")

    plugin_names: set[str] = set()
    plugin_objects: dict[str, dict] = {}
    previous: tuple[str, str] | None = None
    for item in plugins:
        fields = {"plugin", "path", "author", "sha256", "readSucceeded", "npcFormIds"}
        if not isinstance(item, dict) or set(item) != fields:
            fail("plugin fields")
        name = item["plugin"]
        if not isinstance(name, str) or not name or Path(name).name != name or Path(name).suffix.casefold() not in PLUGIN_EXTENSIONS:
            fail("plugin name")
        key = name.casefold()
        if key in plugin_names:
            fail("duplicate plugin")
        plugin_names.add(key)
        sort_key = (key, name)
        if previous is not None and sort_key < previous:
            fail("plugin ordering")
        previous = sort_key
        plugin_path = path_field(item["path"], "plugin", data_root)
        if plugin_path.parent != data_root or plugin_path.name.casefold() != key or not plugin_path.is_file():
            fail("plugin/data-root alignment")
        if item["author"] != MARKER or not isinstance(item["readSucceeded"], bool):
            fail("plugin marker/read state")
        expected_hash = sha256(plugin_path)
        if not isinstance(item["sha256"], str) or item["sha256"].casefold() != expected_hash.casefold() or not re.fullmatch(r"[0-9A-Fa-f]{64}", item["sha256"]):
            fail("plugin hash")
        forms = item["npcFormIds"]
        if not isinstance(forms, list) or any(not isinstance(form, str) or not FORM_ID.fullmatch(form) for form in forms):
            fail("plugin NPC FormIDs")
        if forms != sorted(set(forms), key=lambda form: form.upper()):
            fail("plugin NPC FormID ordering")
        if tes4_author(plugin_path) != MARKER:
            fail("plugin CNAM marker")
        plugin_objects[key] = item

    sidecar_keys: set[tuple[str, str, str]] = set()
    previous = None
    for item in sidecars:
        fields = {"originPlugin", "kind", "variant", "relativePath", "path", "formId", "size", "sha256"}
        if not isinstance(item, dict) or set(item) != fields:
            fail("sidecar fields")
        origin = item["originPlugin"]
        kind = item["kind"]
        variant = item["variant"]
        relative = item["relativePath"]
        if not isinstance(origin, str) or origin.casefold() not in plugin_names or not isinstance(kind, str) or not isinstance(variant, str) or not isinstance(relative, str):
            fail("sidecar identity")
        expected_origin, expected_form, expected_variant = sidecar_expectation(edition, kind, relative)
        if expected_origin.casefold() != origin.casefold() or variant != expected_variant or item["formId"] != expected_form:
            fail("sidecar identity/layout")
        absolute = path_field(item["path"], "sidecar", data_root)
        expected_absolute = (data_root / Path(*relative.split("/"))).resolve()
        if absolute != expected_absolute or not absolute.is_file():
            fail("sidecar path alignment")
        key = (relative.casefold(), relative, origin.casefold())
        if key in sidecar_keys:
            fail("duplicate sidecar")
        sidecar_keys.add(key)
        if previous is not None and key < previous:
            fail("sidecar ordering")
        previous = key
        if not isinstance(item["sha256"], str) or not re.fullmatch(r"[0-9A-Fa-f]{64}", item["sha256"]) or item["size"] != absolute.stat().st_size or item["sha256"].casefold() != sha256(absolute).casefold():
            fail("sidecar hash/size")

    if args.expect == "allowed":
        if not value["isValid"] or errors or not data_root.is_dir():
            fail("allowed result contains errors")
    else:
        if value["isValid"] or not errors:
            fail("refused result lacks an error")
        if args.reason and args.reason not in errors:
            fail(f"missing refusal reason {args.reason}")

    print(f"RESULT PASS generated-scan plugins={len(plugins)} sidecars={len(sidecars)} expect={args.expect}")


if __name__ == "__main__":
    main()
