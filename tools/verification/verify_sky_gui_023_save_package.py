#!/usr/bin/env python3
"""Independent raw package/plugin/BSA audit for SKY-GUI-023."""

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

EXPECTED_COUNTS = {
    "NPC_": 2,
    "ARMA": 1,
    "ARMO": 1,
    "OTFT": 1,
    "LVLI": 1,
    "LVLN": 1,
}
FORBIDDEN = {
    "WRLD",
    "CELL",
    "LAND",
    "WATR",
    "NAVM",
    "NAVI",
    "VMAD",
}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package", required=True)
    parser.add_argument("--source-package", required=True)
    parser.add_argument("--report", required=True)
    parser.add_argument("--expect-failure", action="store_true")
    parser.add_argument("--plant-wrong-member-hash", action="store_true")
    args = parser.parse_args()
    package = Path(args.package).resolve()
    source = Path(args.source_package).resolve()
    report = Path(args.report).resolve()
    for path in (package, source, report):
        ensure_under_project(path)
    if report.exists():
        raise FileExistsError(f"Report already exists: {report}")

    failures: list[str] = []
    manifest = verify_manifest(package, failures)
    proposal = verify_proposal(package, manifest, failures)
    plugin = verify_plugin(package, manifest, failures)
    archive = verify_archive(
        package,
        source,
        manifest,
        failures,
        args.plant_wrong_member_hash,
    )
    mismatch_count = len(failures)
    expected_failure_observed = args.expect_failure and mismatch_count > 0
    passed = (
        expected_failure_observed
        if args.expect_failure
        else mismatch_count == 0
    )
    verdict = (
        "EXPECTED_FAIL"
        if expected_failure_observed
        else ("PASS" if passed else "FAIL")
    )
    payload = {
        "schema": "npcmanager.sky-gui-023.save-package-raw-audit.v1",
        "verdict": verdict,
        "passed": passed,
        "mismatchCount": mismatch_count,
        "manifest": manifest,
        "proposal": proposal,
        "plugin": plugin,
        "archive": archive,
        "failures": failures,
        "pluginAuthority": not args.expect_failure and mismatch_count == 0,
        "archiveAuthority": not args.expect_failure and mismatch_count == 0,
        "equipmentAuthority": False,
        "runtimeAuthority": False,
        "visualAuthority": False,
    }
    report.parent.mkdir(parents=True, exist_ok=True)
    report.write_text(
        json.dumps(payload, indent=2) + "\n",
        encoding="utf-8",
    )
    print(json.dumps(payload, indent=2))
    return 0 if passed else 1


def verify_manifest(root: Path, failures: list[str]) -> dict[str, object]:
    path = root / "npcmanager-package.json"
    value = read_json(path)
    artifacts = value.get("artifacts")
    if not isinstance(artifacts, list) or not artifacts:
        failures.append("Manifest artifacts are absent or empty")
        artifacts = []
    declared: dict[str, dict[str, object]] = {}
    for row in artifacts:
        if not isinstance(row, dict):
            failures.append("Manifest contains a non-object artifact")
            continue
        relative = str(row.get("relativePath", "")).replace("\\", "/")
        key = relative.lower()
        if not relative or key in declared:
            failures.append(f"Manifest duplicate/empty path: {relative!r}")
            continue
        target = (root / Path(relative)).resolve()
        ensure_under(target, root)
        if not target.is_file():
            failures.append(f"Manifest artifact is missing: {relative}")
            continue
        actual_length = target.stat().st_size
        actual_hash = sha256(target)
        if actual_length != row.get("byteLength"):
            failures.append(f"Manifest length mismatch: {relative}")
        if actual_hash.lower() != str(row.get("sha256", "")).lower():
            failures.append(f"Manifest hash mismatch: {relative}")
        declared[key] = row

    actual = {
        item.relative_to(root).as_posix().lower()
        for item in root.rglob("*")
        if item.is_file() and item.name.lower() != "npcmanager-package.json"
    }
    if actual != set(declared):
        failures.append(
            "Manifest declared/actual inventory differs: "
            f"declared={sorted(declared)} actual={sorted(actual)}"
        )
    if value.get("edition", "").lower() != "skyrimse":
        failures.append("Manifest edition is not skyrimse")
    if value.get("outputPlugin") != "Gate23Transform.esp":
        failures.append("Manifest output plugin changed")
    return {
        "path": path.relative_to(PROJECT_ROOT).as_posix(),
        "sha256": sha256(path),
        "artifactCount": len(declared),
        "declaredPaths": sorted(declared),
        "outputPlugin": value.get("outputPlugin"),
        "targetFormId": value.get("targetFormId"),
        "_value": value,
    }


def verify_proposal(
    root: Path,
    manifest: dict[str, object],
    failures: list[str],
) -> dict[str, object]:
    del manifest
    path = root / "npcmanager-save-proposal.json"
    value = read_json(path)
    options = value.get("options", {})
    expected = {
        "scope": "AllChanged",
        "targetMode": "UpdateExisting",
        "markAsMaster": True,
        "lightMaster": False,
        "encoding": "Utf8",
        "archive": "Bsa",
        "leveledList": "Existing",
        "leveledListEditorId": "Gate23ExistingActors",
        "noDuplicateLeveledEntries": True,
    }
    for key, wanted in expected.items():
        if options.get(key) != wanted:
            failures.append(
                f"Proposal {key} expected={wanted!r} actual={options.get(key)!r}"
            )
    if value.get("npcFormIds") != ["0x00000800", "0x00000801"]:
        failures.append("Proposal did not bind both NPC FormIDs")
    if value.get("runtimeAuthority") is not False:
        failures.append("Proposal claimed runtime authority")
    return {
        "path": path.relative_to(PROJECT_ROOT).as_posix(),
        "sha256": sha256(path),
        "options": options,
        "npcFormIds": value.get("npcFormIds"),
        "runtimeAuthority": value.get("runtimeAuthority"),
    }


def verify_plugin(
    root: Path,
    manifest: dict[str, object],
    failures: list[str],
) -> dict[str, object]:
    plugin_name = str(manifest.get("outputPlugin"))
    path = root / plugin_name
    data = path.read_bytes()
    if len(data) < 24 or data[:4] != b"TES4":
        failures.append("Produced plugin is not TES4")
        return {}
    flags = struct.unpack_from("<I", data, 8)[0]
    if flags & 0x1 == 0 or flags & 0x200 != 0:
        failures.append(f"TES4 flags expected ESM=true ESL=false actual=0x{flags:08X}")
    counts: dict[str, int] = {}
    lvln_bodies: list[bytes] = []

    def capture(
        signature: str,
        form_id: int,
        record_flags: int,
        body: bytes,
        depth: int,
    ) -> None:
        del form_id, record_flags, depth
        counts[signature] = counts.get(signature, 0) + 1
        if signature == "LVLN":
            lvln_bodies.append(body)

    walk(data, capture)
    for signature, wanted in EXPECTED_COUNTS.items():
        if counts.get(signature, 0) != wanted:
            failures.append(
                f"Plugin {signature} count expected={wanted} "
                f"actual={counts.get(signature, 0)}"
            )
    for signature in sorted(FORBIDDEN & set(counts)):
        failures.append(f"Plugin contains forbidden record surface {signature}")
    if b"Br\xc3\xadar" not in data:
        failures.append("Plugin lacks reviewed UTF-8 Bríar byte evidence")

    editor_id = None
    lvln_references: list[str] = []
    if len(lvln_bodies) == 1:
        fields: dict[str, list[bytes]] = {}
        for signature, payload in subrecords(lvln_bodies[0]):
            fields.setdefault(signature, []).append(payload)
        edids = fields.get("EDID", [])
        if len(edids) == 1:
            editor_id = edids[0].rstrip(b"\0").decode("ascii")
        if editor_id != "Gate23ExistingActors":
            failures.append(f"LVLN EditorID changed: {editor_id!r}")
        for payload in fields.get("LVLO", []):
            if len(payload) < 8:
                failures.append("LVLN LVLO payload is truncated")
                continue
            raw = struct.unpack_from("<I", payload, 4)[0]
            lvln_references.append(f"0x{raw & 0x00FFFFFF:08X}")
        if lvln_references != ["0x00000800", "0x00000801"]:
            failures.append(
                "LVLN entries expected two ordered NPCs, "
                f"actual={lvln_references!r}"
            )
    return {
        "path": path.relative_to(PROJECT_ROOT).as_posix(),
        "sha256": sha256(path),
        "tes4Flags": f"0x{flags:08X}",
        "recordCounts": counts,
        "leveledNpcEditorId": editor_id,
        "leveledNpcReferences": lvln_references,
        "utf8Probe": b"Br\xc3\xadar" in data,
        "forbiddenRecordSurface": sorted(FORBIDDEN & set(counts)),
    }


def verify_archive(
    root: Path,
    source: Path,
    manifest: dict[str, object],
    failures: list[str],
    plant_wrong: bool,
) -> dict[str, object]:
    del manifest
    archives = list(root.glob("*.bsa"))
    if len(archives) != 1:
        failures.append(
            f"Expected exactly one BSA, found {len(archives)}"
        )
        return {}
    path = archives[0]
    version, compressed, members = read_bsa(path)
    if version != 105 or compressed:
        failures.append(
            f"BSA expected version=105 compressed=false, "
            f"actual={version}/{compressed}"
        )
    expected_paths = [
        item.relative_to(source).as_posix()
        for prefix in ("Meshes", "Textures")
        for item in (source / prefix).rglob("*")
        if item.is_file() and item.suffix.lower() != ".ini"
    ]
    expected = {
        relative.lower(): sha256(source / Path(relative))
        for relative in expected_paths
    }
    if plant_wrong and expected:
        expected[sorted(expected)[0]] = "0" * 64
    actual = {
        relative.lower(): digest
        for relative, (_, digest) in members.items()
    }
    if set(actual) != set(expected):
        failures.append(
            f"BSA member inventory differs: expected={sorted(expected)} "
            f"actual={sorted(actual)}"
        )
    for relative in sorted(set(actual) & set(expected)):
        if actual[relative].lower() != expected[relative].lower():
            failures.append(f"BSA member hash mismatch: {relative}")
    return {
        "path": path.relative_to(PROJECT_ROOT).as_posix(),
        "sha256": sha256(path),
        "version": version,
        "compressed": compressed,
        "memberCount": len(members),
        "members": [
            {
                "path": relative,
                "byteLength": length,
                "sha256": digest,
            }
            for relative, (length, digest) in sorted(members.items())
        ],
        "plantedWrongMemberHash": plant_wrong,
    }


def read_bsa(path: Path) -> tuple[int, bool, dict[str, tuple[int, str]]]:
    data = path.read_bytes()
    if len(data) < 36 or data[:4] != b"BSA\0":
        raise ValueError("Archive is not a BSA")
    (
        version,
        folder_offset,
        archive_flags,
        folder_count,
        file_count,
        total_folder_name_length,
        total_file_name_length,
        file_flags,
    ) = struct.unpack_from("<8I", data, 4)
    del folder_offset, total_folder_name_length, file_flags
    if archive_flags & 0x4:
        raise ValueError("Default-compressed BSA is outside Gate 023")
    cursor = 36
    folder_rows: list[tuple[int, int]] = []
    for _ in range(folder_count):
        _, count, _, offset = struct.unpack_from("<QIIQ", data, cursor)
        folder_rows.append((count, offset))
        cursor += 24
    del total_file_name_length
    records: list[tuple[str, int, int]] = []
    for count, _ in folder_rows:
        name_length = data[cursor]
        cursor += 1
        folder = data[cursor:cursor + name_length]
        cursor += name_length
        folder_name = folder.rstrip(b"\0").decode("cp1252")
        for _ in range(count):
            _, size, offset = struct.unpack_from("<QII", data, cursor)
            cursor += 16
            if size & 0x40000000:
                raise ValueError("Per-file compressed BSA is outside Gate 023")
            records.append((folder_name, size, offset))
    names: list[str] = []
    for _ in range(file_count):
        end = data.index(0, cursor)
        names.append(data[cursor:end].decode("cp1252"))
        cursor = end + 1
    if len(records) != file_count or len(names) != file_count:
        raise ValueError("BSA folder/file table counts are inconsistent")
    members: dict[str, tuple[int, str]] = {}
    for (folder, size, offset), name in zip(records, names, strict=True):
        relative = f"{folder}/{name}".replace("\\", "/")
        payload = data[offset:offset + size]
        if len(payload) != size:
            raise ValueError(f"BSA member is truncated: {relative}")
        if relative.lower() in {key.lower() for key in members}:
            raise ValueError(f"BSA duplicate member: {relative}")
        members[relative] = (
            size,
            hashlib.sha256(payload).hexdigest().upper(),
        )
    return version, False, members


def read_json(path: Path) -> dict[str, object]:
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict):
        raise ValueError(f"Expected JSON object: {path}")
    return value


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def ensure_under_project(path: Path) -> None:
    try:
        path.relative_to(PROJECT_ROOT)
    except ValueError as exception:
        raise ValueError(
            f"Path must remain under the project root: {path}"
        ) from exception


def ensure_under(path: Path, root: Path) -> None:
    try:
        path.relative_to(root)
    except ValueError as exception:
        raise ValueError(f"Path escaped root: {path}") from exception


if __name__ == "__main__":
    raise SystemExit(main())
