from __future__ import annotations

import copy
import hashlib
import importlib.util
import json
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path


PROJECT = Path(__file__).resolve().parents[3]
VERIFIER = PROJECT / "tools" / "verify_skyrim_follower_finish_package.py"
ROOT_DOCUMENTS = (
    "BUILD_INFO.txt",
    "README-NPCMANAGER-RUNTIME-TEST.txt",
    "RUNTIME-TEST-INSTRUCTIONS.md",
)
EVIDENCE_FILES = (
    "evidence/follower-finish-request.json",
    "evidence/follower-finish-proposal.json",
    "evidence/follower-finish-verification.json",
    "evidence/source-package-binding.json",
    "evidence/world-conflict-audit.json",
    "evidence/runtime-test-instructions.json",
)


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def canonical(value: object) -> bytes:
    return json.dumps(
        value, ensure_ascii=False, separators=(",", ":"), sort_keys=False
    ).encode("utf-8")


def subrecord(signature: str, data: bytes) -> bytes:
    assert len(signature) == 4 and len(data) <= 0xFFFF
    return signature.encode("ascii") + struct.pack("<H", len(data)) + data


def record(
    signature: str,
    form_id: int,
    *subrecords: bytes,
    flags: int = 0,
) -> bytes:
    data = b"".join(subrecords)
    return (
        signature.encode("ascii")
        + struct.pack("<III", len(data), flags, form_id)
        + struct.pack("<HHHH", 0, 0, 44, 0)
        + data
    )


def group(label: str | int, group_type: int, *children: bytes) -> bytes:
    if isinstance(label, str):
        raw_label = label.encode("ascii")
    else:
        raw_label = struct.pack("<I", label)
    assert len(raw_label) == 4
    body = b"".join(children)
    return (
        b"GRUP"
        + struct.pack("<I", 24 + len(body))
        + raw_label
        + struct.pack("<iHHHH", group_type, 0, 0, 0, 0)
        + body
    )


def tes4(
    *,
    esl: bool,
    next_form_id: int,
    record_count: int,
    lux_master: bool = False,
) -> bytes:
    masters = ["Skyrim.esm"]
    if lux_master:
        masters.append("Lux.esp")
    rows = [
        subrecord(
            "HEDR",
            struct.pack("<fII", 1.7, record_count, next_form_id),
        ),
    ]
    for master in masters:
        rows.extend(
            (
                subrecord("MAST", master.encode("ascii") + b"\0"),
                subrecord("DATA", b"\0" * 8),
            )
        )
    return record("TES4", 0, *rows, flags=0x200 if esl else 0)


def build_plugin(
    *,
    finished: bool,
    hair_rgb: int = 0xD6BE83,
    next_form_id: int = 0x808,
    include_outfit: bool = False,
    forbidden_signature: str | None = None,
    extra_actor: bool = False,
    lux_master: bool = False,
    schedule_hour: int = 0,
    condition_faction: int = 0x5C84E,
    npc_extra_subrecord: bool = False,
    color_extra_subrecord: bool = False,
    world_payload: bool = False,
    cell_payload: bool = False,
    temporary_actor_group: bool = False,
) -> bytes:
    npc_rows = [
        subrecord("EDID", b"GenericFollower\0"),
        subrecord("HCLF", struct.pack("<I", 0x801)),
    ]
    if finished:
        npc_rows.append(subrecord("PKID", struct.pack("<I", 0x805)))
    if include_outfit:
        npc_rows.append(subrecord("DOFT", struct.pack("<I", 0x123)))
    if npc_extra_subrecord:
        npc_rows.append(subrecord("FULL", b"Drift\0"))
    npc = record("NPC_", 0x800, *npc_rows)
    color_rows = [
        subrecord(
            "CNAM",
            bytes(
                (
                    (hair_rgb >> 16) & 0xFF,
                    (hair_rgb >> 8) & 0xFF,
                    hair_rgb & 0xFF,
                    0,
                )
            ),
        )
    ]
    if color_extra_subrecord:
        color_rows.append(subrecord("EDID", b"Drift\0"))
    color = record("CLFM", 0x801, *color_rows)
    fixed = (
        record("TXST", 0x802, subrecord("EDID", b"PrivateHeadTextures\0")),
        record("HDPT", 0x803, subrecord("EDID", b"PrivateFaceHead\0")),
        record("RELA", 0x804, subrecord("DATA", b"\0" * 12)),
    )
    groups = [
        group("NPC_", 0, npc),
        group("CLFM", 0, color),
        group("TXST", 0, fixed[0]),
        group("HDPT", 0, fixed[1]),
        group("RELA", 0, fixed[2]),
    ]
    if finished:
        package = record(
            "PACK",
            0x805,
            subrecord("EDID", b"GenericStableSandbox\0"),
            subrecord("PKDT", struct.pack("<III", 0, 0, 0)),
            subrecord(
                "PSDT",
                struct.pack("<bbbbII", -1, 0, schedule_hour, 24, 0, 0),
            ),
            subrecord("PLDT", struct.pack("<iIi", 0, 0x806, 768)),
            subrecord(
                "CTDA",
                struct.pack(
                    "<IfIIIII", 0, 0.0, condition_faction, 0, 0, 0, 0
                ),
            ),
        )
        marker = record(
            "REFR",
            0x806,
            subrecord("NAME", struct.pack("<I", 0x3B)),
            subrecord("DATA", struct.pack("<6f", 4, 5, 6, 0, 0, 0)),
            flags=0x400,
        )
        actor = record(
            "ACHR",
            0x807,
            subrecord("NAME", struct.pack("<I", 0x800)),
            subrecord("DATA", struct.pack("<6f", 1, 2, 3, 0, 0, 90)),
            flags=0x400,
        )
        actor_rows = [actor]
        if extra_actor:
            actor_rows.append(
                record(
                    "ACHR",
                    0x808,
                    subrecord("NAME", struct.pack("<I", 0x800)),
                    subrecord("DATA", struct.pack("<6f", 9, 9, 9, 0, 0, 0)),
                    flags=0x400,
                )
            )
        persistent = group(0xA16A, 8, marker, *actor_rows)
        if temporary_actor_group:
            persistent = group(
                0xA16A,
                8,
                marker,
                group(0xA16A, 9, *actor_rows),
            )
        cell_children = group(
            0xA16A,
            6,
            persistent,
        )
        world_children = group(
            0x3C,
            1,
            record(
                "CELL",
                0xA16A,
                *(
                    (subrecord("XCLL", b"\0" * 36),)
                    if cell_payload
                    else ()
                ),
            ),
            cell_children,
        )
        groups.extend(
            (
                group("PACK", 0, package),
                group(
                    "WRLD",
                    0,
                    record(
                        "WRLD",
                        0x3C,
                        *(
                            (subrecord("EDID", b"DriftWorld\0"),)
                            if world_payload
                            else ()
                        ),
                    ),
                    world_children,
                ),
            )
        )
    if forbidden_signature is not None:
        groups.append(
            group(
                forbidden_signature,
                0,
                record(forbidden_signature, 0x809),
            )
        )
    return tes4(
        esl=finished,
        next_form_id=next_form_id,
        record_count=21 if finished else 10,
        lux_master=lux_master,
    ) + b"".join(groups)


def write_zip(path: Path, entries: dict[str, bytes]) -> None:
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        for name in sorted(entries):
            info = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(info, entries[name])


def artifact(kind: str, path: str, data: bytes) -> dict[str, object]:
    return {
        "kind": kind,
        "relativePath": path,
        "byteLength": len(data),
        "sha256": sha256(data).lower(),
    }


def expected_diagnostic() -> bytes:
    return (
        "; NPC Manager follower-finish diagnostic\n"
        "; status=STATIC_PASS_RUNTIME_REQUIRED\n"
        "; plugin=GenericFollower.esp\n"
        "; editorId=GenericFollower\n"
        "; formId=0x00000800\n"
        'help "GenericFollower" 4\n'
    ).encode("utf-8")


def make_fixture(root: Path, mutation: str | None = None) -> tuple[Path, ...]:
    plugin_name = "GenericFollower.esp"
    source_plugin = build_plugin(finished=False, hair_rgb=0x94876A, next_form_id=0x805)
    output_plugin = build_plugin(
        finished=True,
        hair_rgb=0xD6BE83 if mutation != "hair" else 0x010203,
        next_form_id=0x809 if mutation == "next-form-id" else 0x808,
        include_outfit=mutation == "outfit",
        forbidden_signature="LAND" if mutation == "forbidden-world" else None,
        extra_actor=mutation == "extra-actor",
        lux_master=mutation == "lux-master",
        schedule_hour=1 if mutation == "schedule" else 0,
        condition_faction=(
            0x123 if mutation == "condition" else 0x5C84E
        ),
        npc_extra_subrecord=mutation == "npc-drift",
        color_extra_subrecord=mutation == "color-drift",
        world_payload=mutation == "world-payload",
        cell_payload=mutation == "cell-payload",
        temporary_actor_group=mutation == "group-tree",
    )
    face_geom = b"NIF-preserved-fixture"
    face_tint = b"DDS preserved fixture"
    preserved = b"provider evidence preserved"
    source_diag = b"source diagnostic"
    source_entries = {
        "BUILD_INFO.txt": b"source build info",
        plugin_name: source_plugin,
        "NPCManager/Evidence/provider.json": preserved,
        "README-NPCMANAGER-RUNTIME-TEST.txt": b"source readme",
        "RUNTIME-TEST-INSTRUCTIONS.md": b"source runtime instructions",
        "diag-genericfollower.txt": source_diag,
        f"meshes/actors/character/FaceGenData/FaceGeom/{plugin_name}/00000800.nif": face_geom,
        f"textures/actors/character/FaceGenData/FaceTint/{plugin_name}/00000800.dds": face_tint,
    }
    if mutation == "unsafe-source-path":
        source_entries["../escape.txt"] = b"escape"
    source_zip = root / "source.zip"
    write_zip(source_zip, source_entries)

    source_manifest_rows = []
    for name, data in source_entries.items():
        if name in ROOT_DOCUMENTS or name.startswith("../"):
            continue
        source_manifest_rows.append(
            artifact(
                (
                    "plugin"
                    if name == plugin_name
                    else "facegeom"
                    if "/FaceGeom/" in name
                    else "facetint"
                    if "/FaceTint/" in name
                    else "source-payload"
                ),
                "Data/" + name,
                data,
            )
        )
    source_manifest = {
        "schemaVersion": 1,
        "edition": "skyrimse",
        "presetFormat": "blank-npc-creation-proposal",
        "sourcePreset": "evidence/source.json",
        "sourcePresetSha256": "1" * 64,
        "sourcePlugin": r"K:\source\carrier.esp",
        "sourcePluginSha256": "2" * 64,
        "outputPlugin": plugin_name,
        "targetFormId": "0x00000800",
        "artifacts": source_manifest_rows,
    }
    source_manifest_path = root / "source-manifest.json"
    source_manifest_bytes = canonical(source_manifest)
    source_manifest_path.write_bytes(source_manifest_bytes)

    package_root = root / "package"
    (package_root / "Data").mkdir(parents=True)
    for name, data in source_entries.items():
        if name.startswith("../"):
            continue
        destination = (
            package_root / name
            if name in ROOT_DOCUMENTS
            else package_root / "Data" / Path(name)
        )
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(data)
    (package_root / "Data" / plugin_name).write_bytes(output_plugin)
    (package_root / "Data" / "diag-genericfollower.txt").write_bytes(
        expected_diagnostic()
    )
    if mutation == "source-payload":
        (
            package_root / "Data" / "NPCManager" / "Evidence" / "provider.json"
        ).write_bytes(b"mutated")

    request = {
        "schemaVersion": 1,
        "operation": "skyrim-simple-follower-finish",
        "source": {
            "zip": str(source_zip),
            "zipByteLength": source_zip.stat().st_size,
            "zipSha256": sha256(source_zip.read_bytes()),
            "packageManifest": str(source_manifest_path),
            "packageManifestSha256": sha256(source_manifest_bytes),
            "plugin": plugin_name,
            "pluginSha256": sha256(source_plugin),
            "faceGeomSha256": sha256(face_geom),
            "faceTintSha256": sha256(face_tint),
        },
        "npcEditorId": "GenericFollower",
        "npcFormId": "0x00000800",
        "occupiedLocalFormIds": [
            "0x00000800",
            "0x00000801",
            "0x00000802",
            "0x00000803",
            "0x00000804",
        ],
        "expectedDefaultOutfitNull": True,
        "hair": {
            "colorFormId": "0x00000801",
            "oldPackedRgb": 0x94876A,
            "newPackedRgb": 0xD6BE83,
        },
        "sandbox": {
            "procedure": (
                "Travel" if mutation == "procedure" else "Sandbox"
            ),
            "radius": 768,
            "schedule": "continuous",
            "target": f"{plugin_name}|0x00000806",
            "condition": "GetFactionRank(Skyrim.esm|0x0005C84E) < 0",
        },
        "placement": {
            "worldspace": "Skyrim.esm|0x0000003C",
            "cell": "Skyrim.esm|0x0000A16A",
            "markerBase": "Skyrim.esm|0x0000003B",
            "actor": {
                "x": 1,
                "y": 2,
                "z": 3,
                "rotationX": 0,
                "rotationY": 0,
                "rotationZ": 90,
            },
            "anchor": {
                "x": 4,
                "y": 5,
                "z": 6,
                "rotationX": 0,
                "rotationY": 0,
                "rotationZ": 0,
            },
        },
        "allocation": {
            "package": "0x00000805",
            "anchor": "0x00000806",
            "actor": "0x00000807",
            "nextFormId": "0x00000808",
        },
        "allowedNewRecords": [
            "PACK 0x00000805",
            "REFR 0x00000806",
            "ACHR 0x00000807",
        ],
        "allowedExistingRecordChanges": [
            "TES4: set ESL flag and mechanical header metadata",
            "CLFM 0x00000801: RGB change",
            "NPC_ 0x00000800: add PKID 0x00000805",
        ],
        "allowedPackageFiles": [
            row["relativePath"] for row in source_manifest_rows
        ]
        + ["npcmanager-package.json"],
        "outputRoot": str(package_root),
        "outputZip": str(root / "candidate.zip"),
        "narrative": "Unnamed follower fixture.",
    }
    if mutation == "facegeom-binding":
        request["source"]["faceGeomSha256"] = "A" * 64
    if mutation == "facetint-binding":
        request["source"]["faceTintSha256"] = "B" * 64
    request_bytes = canonical(request)
    request_path = root / "request.json"
    request_path.write_bytes(request_bytes)
    proposal = {
        "schemaVersion": 1,
        "operation": "skyrim-simple-follower-finish",
        "requestSha256": sha256(request_bytes),
        "request": request,
        "existingRecordChanges": request["allowedExistingRecordChanges"],
        "newRecords": request["allowedNewRecords"],
        "nextFormId": "0x00000808",
        "rawGroupTreeSurface": [
            "TES4",
            "GRUP/NPC_",
            "GRUP/PACK",
            "GRUP/WRLD/CELL/REFR",
            "GRUP/WRLD/CELL/ACHR",
        ],
        "allowedPackageFiles": request["allowedPackageFiles"],
        "runtimeAuthority": mutation == "runtime-authority",
    }
    proposal_bytes = canonical(proposal)
    proposal_path = root / "proposal.json"
    proposal_path.write_bytes(proposal_bytes)

    evidence_payloads = {
        "evidence/follower-finish-request.json": request_bytes,
        "evidence/follower-finish-proposal.json": proposal_bytes,
        "evidence/follower-finish-verification.json": canonical(
            {
                "schemaVersion": 1,
                "verdict": "STATIC_PASS_RUNTIME_REQUIRED",
                "runtimeAuthority": False,
                "sourcePluginSha256": sha256(source_plugin),
                "outputPluginSha256": sha256(output_plugin),
            }
        ),
        "evidence/source-package-binding.json": canonical(
            {
                "schemaVersion": 1,
                "sourceZipSha256": sha256(source_zip.read_bytes()),
                "sourceManifestSha256": sha256(source_manifest_bytes),
                "allowedChangedPaths": [
                    "Data/" + plugin_name,
                    "Data/diag-genericfollower.txt",
                ],
                "runtimeAuthority": False,
            }
        ),
        "evidence/world-conflict-audit.json": canonical(
            {
                "schemaVersion": 1,
                "forbiddenSignatures": [
                    "LAND",
                    "NAVM",
                    "NAVI",
                    "WATR",
                    "LTEX",
                    "LCTN",
                    "REGN",
                    "CLMT",
                    "MUSC",
                    "IMGS",
                ],
                "observedForbiddenSignatures": [],
                "verdict": "PASS_STATIC_RUNTIME_REQUIRED",
            }
        ),
        "evidence/runtime-test-instructions.json": canonical(
            {
                "schemaVersion": 1,
                "verdict": "STATIC_PASS_RUNTIME_REQUIRED",
                "runtimeAuthority": False,
            }
        ),
    }
    for relative, data in evidence_payloads.items():
        destination = package_root / Path(relative)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(data)
    if mutation == "diagnostic":
        (package_root / "Data" / "diag-genericfollower.txt").write_bytes(
            b"unbound diagnostic mutation"
        )

    artifact_rows = []
    for path in sorted(
        p for p in package_root.rglob("*") if p.is_file()
    ):
        relative = path.relative_to(package_root).as_posix()
        if relative == "npcmanager-package.json":
            continue
        kind = (
            "plugin"
            if relative == f"Data/{plugin_name}"
            else "source-root-document"
            if relative in ROOT_DOCUMENTS
            else "follower-finish-evidence"
            if relative.startswith("evidence/")
            else "payload"
        )
        artifact_rows.append(artifact(kind, relative, path.read_bytes()))
    manifest = {
        "schemaVersion": 1,
        "edition": "skyrimse",
        "presetFormat": "skyrim-simple-follower-finish",
        "sourcePreset": "evidence/follower-finish-request.json",
        "sourcePresetSha256": sha256(request_bytes),
        "sourcePlugin": str(source_zip),
        "sourcePluginSha256": sha256(source_plugin),
        "outputPlugin": plugin_name,
        "targetFormId": "0x00000800",
        "artifacts": artifact_rows,
    }
    (package_root / "npcmanager-package.json").write_bytes(canonical(manifest))

    archive_entries = {}
    for path in package_root.rglob("*"):
        if not path.is_file():
            continue
        relative = path.relative_to(package_root).as_posix()
        if relative.startswith("Data/"):
            archive_entries[relative[5:]] = path.read_bytes()
        elif relative in ROOT_DOCUMENTS:
            archive_entries[relative] = path.read_bytes()
    archive_path = root / "candidate.zip"
    write_zip(archive_path, archive_entries)
    report_path = root / "report.json"
    return (
        request_path,
        proposal_path,
        source_zip,
        package_root,
        archive_path,
        report_path,
    )


def execute_oracle(
    args: tuple[Path, ...], *, expect_report: bool = True
) -> tuple[int, dict | None]:
    command = [
        sys.executable,
        str(VERIFIER),
        "--request",
        str(args[0]),
        "--proposal",
        str(args[1]),
        "--source-zip",
        str(args[2]),
        "--package-root",
        str(args[3]),
        "--archive",
        str(args[4]),
        "--report",
        str(args[5]),
    ]
    completed = subprocess.run(
        command,
        cwd=PROJECT,
        text=True,
        capture_output=True,
        check=False,
    )
    if expect_report and not args[5].is_file():
        raise AssertionError(
            f"oracle did not write a report\nstdout={completed.stdout}\n"
            f"stderr={completed.stderr}"
        )
    report = (
        json.loads(args[5].read_text(encoding="utf-8"))
        if expect_report
        else None
    )
    return completed.returncode, report


def run_oracle(root: Path, mutation: str | None = None) -> tuple[int, dict]:
    code, report = execute_oracle(make_fixture(root, mutation))
    assert report is not None
    return code, report


def test_accepts_complete_independent_fixture(tmp: Path) -> None:
    code, report = run_oracle(tmp)
    assert code == 0, report
    assert report["verdict"] == "STATIC_PASS_RUNTIME_REQUIRED"
    assert report["runtimeAuthority"] is False
    assert report["plugin"]["recordCounts"] == {
        "PACK": 1,
        "REFR": 1,
        "ACHR": 1,
    }


def test_rejects_targeted_mutations(tmp: Path) -> None:
    cases = {
        "hair": "plugin-hair-rgb",
        "next-form-id": "plugin-next-form-id",
        "outfit": "plugin-outfit",
        "forbidden-world": "plugin-forbidden-signature",
        "extra-actor": "plugin-record-count",
        "lux-master": "plugin-lux-master",
        "source-payload": "source-preservation",
        "unsafe-source-path": "source-zip-path",
        "runtime-authority": "runtime-authority",
        "facegeom-binding": "source-facegeom-binding",
        "facetint-binding": "source-facetint-binding",
        "procedure": "plugin-procedure",
        "schedule": "plugin-schedule",
        "condition": "plugin-condition",
        "npc-drift": "plugin-npc-preservation",
        "color-drift": "plugin-color-preservation",
        "world-payload": "plugin-world-payload",
        "cell-payload": "plugin-cell-payload",
        "group-tree": "plugin-group-tree",
        "diagnostic": "diagnostic-binding",
    }
    for mutation, expected in cases.items():
        case_root = tmp / mutation
        case_root.mkdir()
        code, report = run_oracle(case_root, mutation)
        assert code != 0, mutation
        assert any(
            item["code"] == expected for item in report["diagnostics"]
        ), (mutation, report)


def test_rejects_path_and_report_boundary_attacks(tmp: Path) -> None:
    outside = tmp / "outside-request"
    outside.mkdir()
    args = list(make_fixture(outside))
    args[0] = Path(r"C:\Windows\win.ini")
    code, report = execute_oracle(tuple(args))
    assert code != 0 and report is not None
    assert any(
        item["code"] == "path-authority"
        for item in report["diagnostics"]
    ), report

    protected = tmp / "protected-source"
    protected.mkdir()
    args = list(make_fixture(protected))
    args[2] = Path(r"F:\ExampleGame")
    code, report = execute_oracle(tuple(args))
    assert code != 0 and report is not None
    assert any(
        item["code"] == "path-authority"
        for item in report["diagnostics"]
    ), report

    existing = tmp / "existing-report"
    existing.mkdir()
    args = make_fixture(existing)
    sentinel = b"foreign report must survive"
    args[5].write_bytes(sentinel)
    code, _ = execute_oracle(args, expect_report=False)
    assert code != 0
    assert args[5].read_bytes() == sentinel

    missing_parent = tmp / "missing-report-parent"
    missing_parent.mkdir()
    args = list(make_fixture(missing_parent))
    missing = missing_parent / "not-created" / "report.json"
    args[5] = missing
    code, _ = execute_oracle(tuple(args), expect_report=False)
    assert code != 0
    assert not missing.parent.exists()

    collision = tmp / "report-collision"
    collision.mkdir()
    args = list(make_fixture(collision))
    args[5] = args[3] / "report.json"
    code, _ = execute_oracle(tuple(args), expect_report=False)
    assert code != 0
    assert not args[5].exists()

    alias = tmp / "case-alias"
    alias.mkdir()
    args = list(make_fixture(alias))
    args[1] = args[0].with_name(args[0].name.upper())
    code, report = execute_oracle(tuple(args))
    assert code != 0 and report is not None
    assert any(
        item["code"] == "path-collision"
        for item in report["diagnostics"]
    ), report


def test_rejects_windows_reparse_package_root(tmp: Path) -> None:
    case = tmp / "reparse-package"
    case.mkdir()
    args = list(make_fixture(case))
    target = case / "real-package"
    args[3].rename(target)
    created = subprocess.run(
        ["cmd", "/c", "mklink", "/J", str(args[3]), str(target)],
        capture_output=True,
        text=True,
        check=False,
    )
    if created.returncode != 0:
        specification = importlib.util.spec_from_file_location(
            "follower_finish_oracle_reparse_test",
            VERIFIER,
        )
        assert specification is not None and specification.loader is not None
        oracle = importlib.util.module_from_spec(specification)
        sys.modules[specification.name] = oracle
        specification.loader.exec_module(oracle)
        namespace = oracle.argparse.Namespace(
            request=args[0],
            proposal=args[1],
            source_zip=args[2],
            package_root=args[3],
            archive=args[4],
            report=args[5],
        )
        original = oracle.first_reparse_component
        oracle.first_reparse_component = (
            lambda path: args[3]
            if path == args[3]
            else original(path)
        )
        diagnostics = oracle.validate_cli_inputs(namespace)
        assert any(
            item["code"] == "path-reparse"
            for item in diagnostics
        ), diagnostics
        return
    try:
        code, report = execute_oracle(tuple(args))
        assert code != 0 and report is not None
        assert any(
            item["code"] == "path-reparse"
            for item in report["diagnostics"]
        ), report
    finally:
        os.rmdir(args[3])


def main() -> int:
    root = Path(
        tempfile.mkdtemp(
            prefix="follower-finish-oracle-tests-",
            dir=PROJECT / "03-builds" / "tests",
        )
    )
    try:
        success = root / "success"
        success.mkdir()
        test_accepts_complete_independent_fixture(success)
        mutations = root / "mutations"
        mutations.mkdir()
        test_rejects_targeted_mutations(mutations)
        boundaries = root / "boundaries"
        boundaries.mkdir()
        test_rejects_path_and_report_boundary_attacks(boundaries)
        reparses = root / "reparses"
        reparses.mkdir()
        test_rejects_windows_reparse_package_root(reparses)
    finally:
        shutil.rmtree(root)
    print("PASS verify_skyrim_follower_finish_package")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
