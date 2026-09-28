#!/usr/bin/env python3
"""Build Chel as a schema-7 UBE Redguard NPC package.

This script is intentionally project-local and K-only for outputs. It copies
live providers out of F: into a K-local Data root, stages Zenithar's BodySlide
meshes in a K-local BodySlide sandbox, writes the schema-7 authorities, invokes
`npc create-from-jslot`, verifies the package, and creates the install ZIP.
"""

from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any


ROOT = Path(r"K:\ExampleWorkspace")
PROJECT = ROOT / "projects" / "NpcManagerReimplementation"
REPORTS = PROJECT / "05-reports"
SOURCE_COPIES = PROJECT / "01-source-copies"
WORK_BUILDS = PROJECT / "03-builds" / "work"

DOTNET = ROOT / "tools" / "external" / "dotnet-sdk-10.0.301-win-x64" / "dotnet.exe"
NPCM = PROJECT / "src" / "NpcManager.Cli" / "bin" / "Debug" / "net10.0" / "npcm.dll"
BODYSLIDE_TOOL = (
    ROOT
    / "tools"
    / "external"
    / "bodyslide-outfit-studio-5.8.2"
    / "CalienteTools"
    / "BodySlide"
)

LIVE_GAME_DATA = Path(r"F:\ExampleGame\Game Root\Data")
LIVE_UBE = Path(r"F:\ExampleGame\mods\UBE 2.0 -- Ultimate Body Enhancer 2.0")
SOPHIA_DATA = (
    PROJECT / "01-source-copies" / "sophia-live-closure-20260723" / "Data"
)

CHEL_PRESET = (
    ROOT
    / "Resources"
    / "Ube Chel Preset-171120-1-0-1769714172"
    / "UBE Chel Preset"
    / "SKSE"
    / "Plugins"
    / "CharGen"
    / "Presets"
    / "UBE_Chel.jslot"
)
CHEL_RACEMENU_FACEGEOM = (
    PROJECT
    / "01-source-copies"
    / "chel-racemenu-export-20260725"
    / "Chelaccepted.nif"
)
CHEL_RACEMENU_FACETINT = (
    PROJECT
    / "01-source-copies"
    / "chel-racemenu-export-20260725"
    / "Chelaccepted.dds"
)
ZENITHAR_XML = (
    PROJECT
    / "01-source-copies"
    / "chel-ube-provider-probe-20260725"
    / "BodySlide"
    / "[DevonixS] - Zenithar's Masterpiece.xml"
)
UBE_BODY_PROFILE = (
    ROOT / "tools" / "profiles" / "npc-body" / "ube-female-v1.json"
)

RACE = "UBE_AllRace.esp|0x0005A18E"
VOICE = "Skyrim.esm|0x013ADD"
ACTOR_CLASS = "Skyrim.esm|0x013176"
COMBAT_STYLE = "Skyrim.esm|0x03BE1B"

PLUGIN_ORDER = [
    "Skyrim.esm",
    "Update.esm",
    "Dawnguard.esm",
    "HearthFires.esm",
    "Dragonborn.esm",
    "RaceCompatibility.esm",
    "RaceMenu.esp",
    "KS Hairdo's.esp",
    "UBE_AllRace.esp",
]

UBE_BODYSLIDE_GROUP = "Chel UBE Naked Body"
UBE_BODYSLIDE_MEMBERS = [
    "UBE SE 2.0 Release Body",
    "UBE SE 2.0 Release Hands",
    "UBE SE 2.0 Release Feet",
]
BODYSLIDE_EXPECTED = {
    "body0": Path(r"Meshes\!UBE\Body\femalebody_tangent_0.nif"),
    "body1": Path(r"Meshes\!UBE\Body\femalebody_tangent_1.nif"),
    "hands0": Path(r"Meshes\!UBE\Hands\femalehands_tangent_0.nif"),
    "hands1": Path(r"Meshes\!UBE\Hands\femalehands_tangent_1.nif"),
    "feet0": Path(r"Meshes\!UBE\Feet\femalefeet_tangent_0.nif"),
    "feet1": Path(r"Meshes\!UBE\Feet\femalefeet_tangent_1.nif"),
}
BODY_MESH_DESTINATIONS = {
    "body0": "Meshes/Actors/Character/Chel/Body/body_0.nif",
    "body1": "Meshes/Actors/Character/Chel/Body/body_1.nif",
    "hands0": "Meshes/Actors/Character/Chel/Body/hands_0.nif",
    "hands1": "Meshes/Actors/Character/Chel/Body/hands_1.nif",
    "feet0": "Meshes/Actors/Character/Chel/Body/feet_0.nif",
    "feet1": "Meshes/Actors/Character/Chel/Body/feet_1.nif",
}

COMMON_FILES = [
    "Skyrim.esm",
    "Update.esm",
    "Dawnguard.esm",
    "HearthFires.esm",
    "Dragonborn.esm",
    "RaceCompatibility.esm",
    "RaceMenu.esp",
    "RaceMenu.bsa",
    "KS Hairdo's.esp",
]
KS_MESHES = [
    r"meshes\KS Hairdo's\Rose.nif",
    r"meshes\KS Hairdo's\Rose.tri",
    r"meshes\KS Hairdo's\RoseHl.nif",
    r"meshes\KS Hairdo's\hairline\straightscalpHUMAN.nif",
    r"meshes\KS Hairdo's\hairline\straightscalpHUMAN.tri",
]
KS_TEXTURES = [
    r"textures\KS Hairdo's\rose.dds",
    r"textures\KS Hairdo's\rose_n.dds",
    r"textures\KS Hairdo's\hairline\hairline01.dds",
    r"textures\KS Hairdo's\hairline\hairline01_n.dds",
]
EXTRA_COMMON_ASSETS = [
    r"textures\Actors\Character\Male\BlankDetailmap.dds",
    r"textures\Actors\Character\Character Assets\TintMasks\SkinTone.dds",
]


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def wire_value(value: Any) -> Any:
    return value.get("value") if isinstance(value, dict) else value


def write_text(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")


def write_json(path: Path, value: Any) -> str:
    write_text(path, json.dumps(value, indent=2, ensure_ascii=False) + "\n")
    return sha256(path)


def rel(path: Path) -> str:
    return path.resolve().relative_to(ROOT.resolve()).as_posix()


def assert_under(path: Path, root: Path, role: str) -> None:
    resolved = path.resolve()
    root_resolved = root.resolve()
    if resolved != root_resolved and root_resolved not in resolved.parents:
        raise RuntimeError(f"{role} escaped {root}: {path}")


def copy_file(src: Path, dst: Path) -> None:
    if not src.is_file():
        raise FileNotFoundError(src)
    dst.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(src, dst)


def copy_relative(src_root: Path, dst_root: Path, relative: str) -> None:
    copy_file(src_root / Path(relative), dst_root / Path(relative))


def find_common_file(name: str) -> Path:
    for root in (SOPHIA_DATA, LIVE_GAME_DATA):
        candidate = root / name
        if candidate.is_file():
            return candidate
    raise FileNotFoundError(name)


def copy_provider_data(data_root: Path) -> dict[str, Any]:
    data_root.mkdir(parents=True, exist_ok=True)
    copied: list[str] = []

    for name in COMMON_FILES:
        source = find_common_file(name)
        copy_file(source, data_root / name)
        copied.append(name)

    for relative in EXTRA_COMMON_ASSETS + KS_MESHES + KS_TEXTURES:
        copy_relative(SOPHIA_DATA, data_root, relative)
        copied.append(relative.replace("\\", "/"))

    for name in ["UBE_AllRace.esp", "UBE_RaceMenuMorphs.esp", "UBE_headparts_patch.esp", "3BBB UBE patch.esp"]:
        source = LIVE_UBE / name
        if source.is_file():
            copy_file(source, data_root / name)
            copied.append(name)

    for directory in ["meshes", "textures", "Scripts", "SKSE", "interface"]:
        source = LIVE_UBE / directory
        if source.is_dir():
            shutil.copytree(source, data_root / directory, dirs_exist_ok=True)
            copied.append(directory + "/")

    return {
        "dataRoot": str(data_root),
        "pluginOrder": PLUGIN_ORDER,
        "copiedEntries": copied,
        "ubeSource": str(LIVE_UBE),
        "commonSource": str(SOPHIA_DATA),
        "boundary": "F: was used only as read-only copy-out source; all provider writes landed under K:",
    }


def configure_bodyslide(config: Path, output_data: Path, project_path: Path) -> None:
    tree = ET.parse(config)
    root = tree.getroot()
    output_text = str(output_data.resolve()) + "\\"
    project_text = str(project_path.resolve()) + "\\"
    for element_name, value in [
        ("WarnMissingGamePath", "false"),
        ("GameDataPath", output_text),
        ("OutputDataPath", output_text),
        ("ProjectPath", project_text),
    ]:
        found = root.find(element_name)
        if found is not None:
            found.text = value
    found = root.find("GameDataPaths/SkyrimSpecialEdition")
    if found is not None:
        found.text = output_text
    tree.write(config, encoding="utf-8", xml_declaration=False)


def stage_bodyslide_sandbox(sandbox_root: Path, mesh_output_root: Path) -> Path:
    app = sandbox_root / "CalienteTools" / "BodySlide"
    shutil.copytree(BODYSLIDE_TOOL, app)
    ube_body_slide = LIVE_UBE / "CalienteTools" / "BodySlide"
    for directory in ["ShapeData", "SliderSets", "SliderPresets", "SliderGroups", "SliderCategories"]:
        source = ube_body_slide / directory
        if source.is_dir():
            shutil.copytree(source, app / directory, dirs_exist_ok=True)
    copy_file(ZENITHAR_XML, app / "SliderPresets" / ZENITHAR_XML.name)

    groups = app / "SliderGroups"
    groups.mkdir(parents=True, exist_ok=True)
    group_root = ET.Element("SliderGroups")
    group = ET.SubElement(group_root, "Group", {"name": UBE_BODYSLIDE_GROUP})
    for member in UBE_BODYSLIDE_MEMBERS:
        ET.SubElement(group, "Member", {"name": member})
    ET.ElementTree(group_root).write(
        groups / "Chel UBE Naked Body.xml",
        encoding="utf-8",
        xml_declaration=True,
    )
    configure_bodyslide(app / "Config.xml", mesh_output_root, app)
    return app


def run_command(
    args: list[str],
    cwd: Path,
    stdout_path: Path,
    stderr_path: Path,
    timeout: int = 600,
    hide_window: bool = True,
) -> subprocess.CompletedProcess[str]:
    stdout_path.parent.mkdir(parents=True, exist_ok=True)
    creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0) if hide_window else 0
    completed = subprocess.run(
        args,
        cwd=cwd,
        text=True,
        capture_output=True,
        timeout=timeout,
        creationflags=creationflags,
    )
    write_text(stdout_path, completed.stdout)
    write_text(stderr_path, completed.stderr)
    write_text(stdout_path.with_suffix(stdout_path.suffix + ".exit.txt"), str(completed.returncode) + "\n")
    return completed


def run_cli(
    cli_args: list[str],
    cwd: Path,
    stdout_path: Path,
    stderr_path: Path,
    timeout: int = 600,
) -> subprocess.CompletedProcess[str]:
    return run_command(
        [str(DOTNET), str(NPCM), *cli_args],
        cwd,
        stdout_path,
        stderr_path,
        timeout,
    )


def parse_json_from(path: Path) -> Any:
    text = path.read_text(encoding="utf-8")
    return json.loads(text)


def write_prewrite_reports(run_id: str, build_root: Path, data_root: Path, manager_root: Path, zip_path: Path) -> None:
    analyze = REPORTS / f"chel-schema7-prewrite-analysis-{run_id}.md"
    proposed = REPORTS / f"chel-schema7-proposed-change-{run_id}.md"
    write_text(
        analyze,
        "\n".join(
            [
                "# Chel UBE Redguard Schema 7 Prewrite Analysis",
                "",
                f"- Target: Chel, female UBE Redguard, race `{RACE}`.",
                f"- Face source: `{CHEL_PRESET}`.",
                f"- Genuine RaceMenu FaceGeom: `{CHEL_RACEMENU_FACEGEOM}`.",
                f"- RaceMenu export companion evidence: `{CHEL_RACEMENU_FACETINT}`.",
                f"- Body source: Zenithar BodySlide XML `{ZENITHAR_XML}`.",
                "- Role: visual naked NPC first; no follower, combat, dialogue, outfit, or placement polish.",
                "- BodySlide execution model: K-local sandbox invocation only; live modlist is copy-out/read-only.",
                "- Expected static status: `STATIC_PASS_RUNTIME_REQUIRED` until in-game visual authority lands.",
                "",
                "Reviewed output roots:",
                f"- Provider copy: `{data_root}`",
                f"- BodySlide/intermediate work: `{build_root}`",
                f"- Manager package root: `{manager_root}`",
                f"- Install ZIP: `{zip_path}`",
                "",
                "Allowed game-facing surfaces:",
                "- `Data/ChelNpcManager.esp`",
                "- `Data/meshes/actors/character/FaceGenData/FaceGeom/ChelNpcManager.esp/00000800.nif`",
                "- `Data/textures/actors/character/FaceGenData/FaceTint/ChelNpcManager.esp/00000800.dds`",
                "- `Data/Meshes/Actors/Character/Chel/Body/*.nif`",
                "- manager evidence, runtime diagnostic batch, and product-owned apply PEX.",
            ]
        )
        + "\n",
    )
    write_text(
        proposed,
        "\n".join(
            [
                "# Chel UBE Redguard Schema 7 Proposed Change",
                "",
                "Build one installable no-wrapper ZIP containing a static visual-test NPC:",
                "",
                "- NPC EditorID/name: `ChelNpcManager` / `Chel`.",
                "- Race/sex: `UBE_AllRace.esp|0x0005A18E`, female.",
                "- Private naked skin route: manager-owned ARMO/ARMA skin records bound to staged UBE body/hands/feet meshes.",
                "- FaceGeom skeleton authority: explicit `identityFaceGenBones`; UBE skin-to-bone transforms retain bind authority.",
                "- FaceGeom carrier: exact user-confirmed RaceMenu CharGen export; all 12 UBE shapes must survive with the exported profile.",
                "- BodyGen: intentionally suppressed because schema 7 uses external BodySlide mesh authority.",
                "- Runtime authority: false; package requires in-game visual proof before release/beauty claims.",
                "",
                "The manager may write only under the reviewed K-local roots above and must not mutate `F:\\ExampleGame`.",
            ]
        )
        + "\n",
    )


def inspect_inputs(run_id: str) -> tuple[dict[str, Any], dict[str, Any]]:
    jslot_stdout = REPORTS / f"chel-jslot-inspect-{run_id}.json"
    jslot_stderr = REPORTS / f"chel-jslot-inspect-{run_id}.stderr.log"
    completed = run_cli(
        [
            "preset",
            "inspect",
            "--edition",
            "skyrimse",
            "--format",
            "racemenu-jslot",
            "--input",
            str(CHEL_PRESET),
            "--json",
        ],
        PROJECT,
        jslot_stdout,
        jslot_stderr,
    )
    if completed.returncode != 0:
        raise RuntimeError(f"JSlot inspect failed; see {jslot_stderr}")

    slider_stdout = REPORTS / f"chel-zenithar-inspect-{run_id}.json"
    slider_stderr = REPORTS / f"chel-zenithar-inspect-{run_id}.stderr.log"
    completed = run_cli(
        [
            "body",
            "sliders",
            "inspect-preset",
            "--game",
            "skyrimse",
            "--preset-xml",
            str(ZENITHAR_XML),
            "--json",
        ],
        PROJECT,
        slider_stdout,
        slider_stderr,
    )
    if completed.returncode != 0:
        raise RuntimeError(f"BodySlide preset inspect failed; see {slider_stderr}")

    return parse_json_from(jslot_stdout), parse_json_from(slider_stdout)


def run_bodyslide(run_id: str, build_root: Path) -> tuple[dict[str, Any], dict[str, Path]]:
    sandbox_root = build_root / "bodyslide-sandbox"
    mesh_output_root = build_root / "bodyslide-generated-data"
    app = stage_bodyslide_sandbox(sandbox_root, mesh_output_root)
    exe = app / "BodySlide.exe"
    stdout_path = REPORTS / f"chel-bodyslide-{run_id}.stdout.log"
    stderr_path = REPORTS / f"chel-bodyslide-{run_id}.stderr.log"
    command = [
        str(exe),
        "-gbuild",
        UBE_BODYSLIDE_GROUP,
        "-t",
        str(mesh_output_root),
        "-p",
        "[DevonixS] - Zenithar's Masterpiece",
        "-tri",
    ]
    completed = run_command(
        command,
        app,
        stdout_path,
        stderr_path,
        timeout=900,
        hide_window=False,
    )
    outputs = [
        path.relative_to(mesh_output_root).as_posix()
        for path in sorted(mesh_output_root.rglob("*"))
        if path.is_file()
    ]
    expected_sources: dict[str, Path] = {
        role: mesh_output_root / relative for role, relative in BODYSLIDE_EXPECTED.items()
    }
    missing = [str(path) for path in expected_sources.values() if not path.is_file()]
    evidence = {
        "schema": "npcmanager-chel-bodyslide-run/1",
        "runId": run_id,
        "tool": {
            "path": str(exe),
            "sha256": sha256(exe),
            "version": "5.8.2",
        },
        "command": command,
        "cwd": str(app),
        "returnCode": completed.returncode,
        "stdoutLog": str(stdout_path),
        "stderrLog": str(stderr_path),
        "group": UBE_BODYSLIDE_GROUP,
        "members": UBE_BODYSLIDE_MEMBERS,
        "preset": "[DevonixS] - Zenithar's Masterpiece",
        "outputs": outputs,
        "expectedMissing": missing,
        "boundary": "BodySlide was invoked only from a K-local sandbox and wrote only to K-local output.",
    }
    write_json(REPORTS / f"chel-bodyslide-run-{run_id}.json", evidence)
    if completed.returncode != 0 or missing:
        raise RuntimeError(f"BodySlide did not produce expected meshes; see {REPORTS / f'chel-bodyslide-run-{run_id}.json'}")
    return evidence, expected_sources


def adopt_existing_bodyslide(
    run_id: str,
    build_root: Path,
    source_run_id: str | None = None,
) -> tuple[dict[str, Any], dict[str, Path]]:
    source_build_root = (
        WORK_BUILDS / f"chel-ube-redguard-schema7-{source_run_id}"
        if source_run_id
        else build_root
    )
    mesh_output_root = source_build_root / "bodyslide-generated-data"
    expected_sources: dict[str, Path] = {
        role: mesh_output_root / relative for role, relative in BODYSLIDE_EXPECTED.items()
    }
    missing = [str(path) for path in expected_sources.values() if not path.is_file()]
    outputs = [
        path.relative_to(mesh_output_root).as_posix()
        for path in sorted(mesh_output_root.rglob("*"))
        if path.is_file()
    ] if mesh_output_root.is_dir() else []
    evidence = {
        "schema": "npcmanager-chel-bodyslide-run/1",
        "runId": run_id,
        "execution": (
            "reused-hash-bound-k-local-generated-output"
            if source_run_id
            else "adopted-existing-visible-start-process-output"
        ),
        "reusedFromRunId": source_run_id,
        "tool": {
            "path": str(source_build_root / "bodyslide-sandbox" / "CalienteTools" / "BodySlide" / "BodySlide.exe"),
            "version": "5.8.2",
        },
        "group": UBE_BODYSLIDE_GROUP,
        "members": UBE_BODYSLIDE_MEMBERS,
        "preset": "[DevonixS] - Zenithar's Masterpiece",
        "outputs": outputs,
        "expectedMissing": missing,
        "log": str(
            source_build_root
            / "bodyslide-sandbox"
            / "CalienteTools"
            / "BodySlide"
            / "Log_BS.txt"
        ),
        "boundary": (
            "Exact generated meshes were reused from a prior K-local Chel build; no live-root read or BodySlide execution occurred in this correction pass."
            if source_run_id
            else "BodySlide was invoked from the K-local sandbox through visible Start-Process after direct subprocess launch timed out."
        ),
    }
    write_json(REPORTS / f"chel-bodyslide-run-{run_id}.json", evidence)
    if missing:
        raise RuntimeError(f"Existing BodySlide output is missing expected meshes: {missing}")
    return evidence, expected_sources


def texture_strings(path: Path) -> list[str]:
    data = path.read_bytes()
    return sorted(
        {
            match.decode("cp1252", errors="ignore").replace("/", "\\")
            for match in re.findall(rb"[A-Za-z0-9_! '\\/.()\[\]-]{3,}\.dds", data, re.I)
        },
        key=str.lower,
    )


def write_body_authorities(
    run_id: str,
    authority_root: Path,
    slider_inspect: dict[str, Any],
    body_sources: dict[str, Path],
) -> tuple[Path, str, Path, str, dict[str, Any]]:
    authority_root.mkdir(parents=True, exist_ok=True)
    preset_name = slider_inspect["presetName"]
    slider_set = slider_inspect["sliderSet"]
    groups = slider_inspect["groups"]
    sliders = slider_inspect["sliders"]
    preset_authority = {
        "schemaVersion": 1,
        "authorityId": f"chel-ube-redguard-zenithar-preset-{run_id}",
        "edition": "skyrimse",
        "sourceKind": "bodyslide-sliderpreset-xml",
        "presetXmlPath": rel(ZENITHAR_XML),
        "presetXmlSha256": slider_inspect["sourceSha256"].lower(),
        "presetName": preset_name,
        "sliderSet": slider_set,
        "groups": groups,
        "sliderCount": len(sliders),
        "runtimeAuthority": False,
    }
    preset_manifest = authority_root / "body-slide-preset-authority.json"
    preset_manifest_hash = write_json(preset_manifest, preset_authority)

    meshes = []
    for role in ["body0", "body1", "hands0", "hands1", "feet0", "feet1"]:
        source = body_sources[role]
        meshes.append(
            {
                "role": role,
                "sourcePath": rel(source),
                "sha256": sha256(source),
                "destination": BODY_MESH_DESTINATIONS[role],
            }
        )
    mesh_authority = {
        "schemaVersion": 1,
        "authorityId": f"chel-ube-redguard-zenithar-meshes-{run_id}",
        "edition": "skyrimse",
        "sourceKind": "external-bodyslide-generated-meshes",
        "bodySlidePresetAuthority": {
            "manifestPath": rel(preset_manifest),
            "manifestSha256": preset_manifest_hash,
        },
        "meshes": meshes,
        "runtimeAuthority": False,
    }
    mesh_manifest = authority_root / "body-mesh-authority.json"
    mesh_manifest_hash = write_json(mesh_manifest, mesh_authority)
    mesh_details = {
        role: {
            "source": str(source),
            "sha256": sha256(source),
            "bytes": source.stat().st_size,
            "textures": texture_strings(source),
        }
        for role, source in body_sources.items()
    }
    return preset_manifest, preset_manifest_hash, mesh_manifest, mesh_manifest_hash, mesh_details


def write_external_chargen_authority(
    run_id: str,
    authority_root: Path,
) -> tuple[Path, str]:
    authority = {
        "schemaVersion": 1,
        "authorityId": f"chel-racemenu-export-{run_id}",
        "edition": "skyrimse",
        "sourceKind": "racemenu-chargen-export",
        "presetPath": rel(CHEL_PRESET),
        "presetSha256": sha256(CHEL_PRESET),
        "faceGeomPath": rel(CHEL_RACEMENU_FACEGEOM),
        "faceGeomSha256": sha256(CHEL_RACEMENU_FACEGEOM),
        "faceTintPath": rel(CHEL_RACEMENU_FACETINT),
        "faceTintSha256": sha256(CHEL_RACEMENU_FACETINT),
        "race": RACE,
        "sex": "female",
        "userConfirmedVisualMatch": True,
        "runtimeAuthority": False,
    }
    path = authority_root / "external-chargen-export-authority.json"
    return path, write_json(path, authority)


def write_appearance_profile(
    run_id: str,
    authority_root: Path,
) -> tuple[Path, str]:
    face_profile = {
        "schemaVersion": 1,
        "profileId": f"chel-manager-schema7-{run_id}",
        "topology": "ube-hph-female-v1",
        "sex": "Female",
        "assemblyPolicy": "user-confirmed-racemenu-exported-complete",
        "expectedOutput": {
            "blocks": 80,
            "reachable": 80,
            "shapes": 12,
            "nullChildren": 0,
        },
    }
    face_path = authority_root / "face-profile.json"
    face_hash = write_json(face_path, face_profile)
    appearance = {
        "schemaVersion": 1,
        "id": f"chel-ube-redguard-schema7-{run_id}",
        "faceProfile": rel(face_path),
        "faceProfileSha256": face_hash,
        "bodyProfile": rel(UBE_BODY_PROFILE),
        "bodyProfileSha256": sha256(UBE_BODY_PROFILE),
    }
    appearance_path = authority_root / "appearance-profile.json"
    return appearance_path, write_json(appearance_path, appearance)


def write_initial_standalone_manifest(
    run_id: str,
    authority_root: Path,
    preset_manifest: Path,
    preset_manifest_hash: str,
    mesh_manifest: Path,
    mesh_manifest_hash: str,
    external_chargen_manifest: Path,
    external_chargen_manifest_hash: str,
) -> tuple[Path, str]:
    seed = json.loads(
        (PROJECT / "01-source-copies" / "gate2-emi2" / "standalone-assets-v5.json").read_text(encoding="utf-8")
    )
    root = {
        "schemaVersion": 7,
        "assetSetId": f"chel-ube-redguard-schema7-seed-{run_id}",
        "edition": "skyrimse",
        "nam9Authority": seed["nam9Authority"],
        "faceTint": {"width": 512, "height": 512},
        "privateHeadTextures": seed["privateHeadTextures"],
        "packageAssets": [],
        "overlayDecisions": None,
        "externalTextureAuthorities": [],
        "bodySlidePresetAuthority": {
            "manifestPath": rel(preset_manifest),
            "manifestSha256": preset_manifest_hash,
        },
        "bodyMeshAuthority": {
            "manifestPath": rel(mesh_manifest),
            "manifestSha256": mesh_manifest_hash,
        },
        "externalCharGenExportAuthority": {
            "manifestPath": rel(external_chargen_manifest),
            "manifestSha256": external_chargen_manifest_hash,
        },
        "finalOutputAuthority": None,
    }
    path = authority_root / "standalone-assets-schema7-seed.json"
    return path, write_json(path, root)


def write_execution_request(run_id: str, build_root: Path, standalone_manifest: Path, standalone_hash: str) -> tuple[Path, str]:
    request = json.loads(
        (PROJECT / "02-normalized-resources" / "sophia-execution-request-v0.4-slot0-hardened-naked.json").read_text(
            encoding="utf-8"
        )
    )
    request["standaloneAssets"] = {
        "manifestPath": rel(standalone_manifest),
        "manifestSha256": standalone_hash,
    }
    request["output"] = {
        "root": rel(build_root / "manager-package"),
        "plugin": "ChelNpcManager.esp",
    }
    request["identity"] = {
        "editorId": "ChelNpcManager",
        "name": "Chel",
    }
    request["traits"] = {
        "sex": "female",
        "role": "static-validation",
        "unique": True,
        "essential": False,
        "protected": False,
        "respawns": False,
        "autoCalcStats": True,
    }
    request["references"] = {
        "race": RACE,
        "voice": VOICE,
        "class": ACTOR_CLASS,
        "combatStyle": COMBAT_STYLE,
        "defaultOutfit": None,
    }
    request["stats"]["weight"] = 0.0
    request["applyBodySlide"] = True
    request["faceGeomSkeletonAuthority"] = "identityFaceGenBones"
    path = build_root / "chel-schema7-execution-request.json"
    return path, write_json(path, request)


def run_manager(
    run_id: str,
    request_path: Path,
    request_hash: str,
    data_root: Path,
    companion_root: Path,
) -> dict[str, Any]:
    preset_hash = sha256(CHEL_PRESET)
    stdout_path = REPORTS / f"chel-manager-command-{run_id}.stdout.json"
    stderr_path = REPORTS / f"chel-manager-command-{run_id}.stderr.log"
    completed = run_cli(
        [
            "npc",
            "create-from-jslot",
            "--request",
            str(request_path),
            "--request-sha256",
            request_hash,
            "--preset",
            str(CHEL_PRESET),
            "--preset-sha256",
            preset_hash,
            "--data-root",
            str(data_root),
            "--plugins",
            ",".join(PLUGIN_ORDER),
            "--companion-root",
            str(companion_root),
            "--json",
        ],
        PROJECT,
        stdout_path,
        stderr_path,
        timeout=1200,
    )
    if completed.returncode != 0:
        raise RuntimeError(f"npc create-from-jslot failed; see {stdout_path} and {stderr_path}")
    return parse_json_from(stdout_path)


def verify_and_archive(run_id: str, package_root: Path, zip_path: Path) -> tuple[dict[str, Any], dict[str, Any]]:
    manifest = package_root / "npcmanager-package.json"
    verify_stdout = REPORTS / f"chel-package-verify-{run_id}.json"
    verify_stderr = REPORTS / f"chel-package-verify-{run_id}.stderr.log"
    completed = run_cli(
        ["package", "verify", "--manifest", str(manifest), "--json"],
        PROJECT,
        verify_stdout,
        verify_stderr,
        timeout=600,
    )
    if completed.returncode != 0:
        raise RuntimeError(f"package verify failed; see {verify_stdout} and {verify_stderr}")
    archive_stdout = REPORTS / f"chel-package-archive-{run_id}.json"
    archive_stderr = REPORTS / f"chel-package-archive-{run_id}.stderr.log"
    completed = run_cli(
        ["package", "archive", "--source-root", str(package_root), "--output", str(zip_path), "--json"],
        PROJECT,
        archive_stdout,
        archive_stderr,
        timeout=600,
    )
    if completed.returncode != 0:
        raise RuntimeError(f"package archive failed; see {archive_stdout} and {archive_stderr}")
    return parse_json_from(verify_stdout), parse_json_from(archive_stdout)


def static_acceptance(
    run_id: str,
    build_root: Path,
    data_root: Path,
    manager_response: dict[str, Any],
    package_verify: dict[str, Any],
    archive_result: dict[str, Any],
    body_mesh_details: dict[str, Any],
) -> dict[str, Any]:
    package_root = build_root / "manager-package"
    data_package = package_root / "Data"
    bodygen_files = [
        path.relative_to(package_root).as_posix()
        for path in sorted(data_package.rglob("*"))
        if path.is_file() and "BodyGenData" in path.as_posix()
    ]
    body_mesh_files = [
        dest
        for dest in BODY_MESH_DESTINATIONS.values()
        if (data_package / Path(dest)).is_file()
    ]
    standalone = json.loads(
        (data_package / "NPCManager" / "Evidence" / "standalone-assets.json").read_text(encoding="utf-8")
    )
    selected_dependencies = data_package / "NPCManager" / "Evidence" / "selected-preset-dependencies.json"
    direct_facegeom_evidence = (
        data_package
        / "NPCManager"
        / "Evidence"
        / "FaceGeom"
        / "direct-chargen-merge.json"
    )
    direct_facegeom = (
        json.loads(direct_facegeom_evidence.read_text(encoding="utf-8"))
        if direct_facegeom_evidence.is_file()
        else {}
    )
    plugin = data_package / "ChelNpcManager.esp"
    facegeom = data_package / "meshes" / "actors" / "character" / "FaceGenData" / "FaceGeom" / "ChelNpcManager.esp" / "00000800.nif"
    facetint = data_package / "textures" / "actors" / "character" / "FaceGenData" / "FaceTint" / "ChelNpcManager.esp" / "00000800.dds"
    checks = {
        "managerCompleted": bool(manager_response.get("completed")),
        "externalRaceMenuExportSelected": any(
            diagnostic.get("code")
            == "jslot-npc-external-chargen-selected"
            for diagnostic in manager_response.get("diagnostics", [])
        ),
        "externalRaceMenuCarrierPreserved": (
            direct_facegeom.get("qualificationProfile")
            == "RaceMenuExportedComplete"
            and direct_facegeom.get("routedShapeCount") == 12
            and direct_facegeom.get("changedPositionShapeCount") == 0
        ),
        "packageVerified": bool(package_verify.get("verified")),
        "archiveWritten": bool(archive_result.get("written")),
        "schema7Evidence": standalone.get("schemaVersion") == 7,
        "bodyGenSuppressed": not bodygen_files,
        "allBodyMeshesPresent": len(body_mesh_files) == 6,
        "pluginPresent": plugin.is_file(),
        "faceGeomPresent": facegeom.is_file(),
        "faceTintPresent": facetint.is_file(),
        "selectedDependenciesPresent": selected_dependencies.is_file(),
    }
    verdict = "STATIC_PASS_RUNTIME_REQUIRED" if all(checks.values()) else "STATIC_FAIL"
    result = {
        "schema": "npcmanager-chel-schema7-static-acceptance/1",
        "runId": run_id,
        "verdict": verdict,
        "checks": checks,
        "managerResponse": manager_response,
        "packageVerify": package_verify,
        "archive": archive_result,
        "bodyGenFiles": bodygen_files,
        "bodyMeshFiles": body_mesh_files,
        "bodyMeshDetails": body_mesh_details,
        "packageRoot": str(package_root),
        "providerDataRoot": str(data_root),
        "runtimeAuthority": False,
        "runtimeNextStep": "Install ZIP in a throwaway profile/load order and capture visual authority evidence before any beauty, seam-free, or release-ready claim.",
    }
    write_json(REPORTS / f"chel-schema7-static-acceptance-{run_id}.json", result)
    archive_artifact = archive_result.get("artifact", {})
    write_text(
        REPORTS / f"chel-schema7-postwrite-verification-{run_id}.md",
        "\n".join(
            [
                "# Chel UBE Redguard Schema 7 Postwrite Verification",
                "",
                f"- Verdict: `{verdict}`",
                f"- Package root: `{package_root}`",
                f"- ZIP: `{wire_value(archive_artifact.get('archive'))}`",
                f"- ZIP SHA-256: `{wire_value(archive_artifact.get('archiveSha256'))}`",
                f"- BodyGen files emitted: `{len(bodygen_files)}`",
                f"- Private BodySlide mesh files present: `{len(body_mesh_files)}/6`",
                f"- External RaceMenu carrier selected: `{checks['externalRaceMenuExportSelected']}`",
                f"- Exact 12-shape RaceMenu carrier preserved: `{checks['externalRaceMenuCarrierPreserved']}`",
                f"- Runtime authority: `false`",
                "",
                "Runtime visual proof is still required before release/appearance claims.",
            ]
        )
        + "\n",
    )
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--run-id", default=None)
    parser.add_argument("--resume-after-bodyslide", action="store_true")
    parser.add_argument("--reuse-k-inputs-from", default=None)
    args = parser.parse_args()
    run_id = args.run_id or "20260725-" + dt.datetime.now().strftime("%H%M%S")
    if args.resume_after_bodyslide and args.reuse_k_inputs_from:
        raise ValueError(
            "--resume-after-bodyslide and --reuse-k-inputs-from are mutually exclusive"
        )

    for path in [
        DOTNET,
        NPCM,
        BODYSLIDE_TOOL / "BodySlide.exe",
        CHEL_PRESET,
        CHEL_RACEMENU_FACEGEOM,
        CHEL_RACEMENU_FACETINT,
        ZENITHAR_XML,
        UBE_BODY_PROFILE,
    ]:
        if not path.exists():
            raise FileNotFoundError(path)

    build_root = WORK_BUILDS / f"chel-ube-redguard-schema7-{run_id}"
    source_root = SOURCE_COPIES / f"chel-schema7-provider-{run_id}"
    data_root = (
        SOURCE_COPIES
        / f"chel-schema7-provider-{args.reuse_k_inputs_from}"
        / "Data"
        if args.reuse_k_inputs_from
        else source_root / "Data"
    )
    manager_root = build_root / "manager-package"
    companion_root = build_root / "companions"
    zip_path = PROJECT / "03-builds" / "packages" / f"ChelNpcManager-schema7-{run_id}.zip"
    authority_root = build_root / "authorities"

    checked_paths = [manager_root, companion_root, zip_path]
    if not args.resume_after_bodyslide and not args.reuse_k_inputs_from:
        checked_paths.extend([build_root, source_root])
    elif args.reuse_k_inputs_from:
        checked_paths.append(build_root)
    for path in checked_paths:
        assert_under(path, PROJECT, "Chel build path")
        if path.exists():
            raise FileExistsError(path)
    if args.resume_after_bodyslide:
        if not build_root.is_dir():
            raise FileNotFoundError(build_root)
        if not data_root.is_dir():
            raise FileNotFoundError(data_root)
    elif args.reuse_k_inputs_from:
        if not re.fullmatch(r"[A-Za-z0-9._-]+", args.reuse_k_inputs_from):
            raise ValueError("--reuse-k-inputs-from contains unsafe characters")
        if not data_root.is_dir():
            raise FileNotFoundError(data_root)
        source_build_root = (
            WORK_BUILDS
            / f"chel-ube-redguard-schema7-{args.reuse_k_inputs_from}"
        )
        if not (source_build_root / "bodyslide-generated-data").is_dir():
            raise FileNotFoundError(
                source_build_root / "bodyslide-generated-data"
            )
        build_root.mkdir(parents=True)
    else:
        build_root.mkdir(parents=True)
        source_root.mkdir(parents=True)
    zip_path.parent.mkdir(parents=True, exist_ok=True)

    if args.resume_after_bodyslide:
        write_prewrite_reports(
            run_id, build_root, data_root, manager_root, zip_path
        )
        jslot_inspect, slider_inspect = inspect_inputs(run_id)
        body_run, body_sources = adopt_existing_bodyslide(run_id, build_root)
    elif args.reuse_k_inputs_from:
        write_prewrite_reports(
            run_id, build_root, data_root, manager_root, zip_path
        )
        jslot_inspect, slider_inspect = inspect_inputs(run_id)
        write_json(
            REPORTS / f"chel-provider-copy-{run_id}.json",
            {
                "schema": "npcmanager-chel-provider-reuse/1",
                "runId": run_id,
                "reusedFromRunId": args.reuse_k_inputs_from,
                "dataRoot": str(data_root),
                "pluginOrder": PLUGIN_ORDER,
                "boundary": "Reused an existing K-local copied provider root; F: was not read or written in this correction pass.",
            },
        )
        body_run, body_sources = adopt_existing_bodyslide(
            run_id, build_root, args.reuse_k_inputs_from
        )
    else:
        write_prewrite_reports(run_id, build_root, data_root, manager_root, zip_path)
        jslot_inspect, slider_inspect = inspect_inputs(run_id)
        provider_report = copy_provider_data(data_root)
        write_json(REPORTS / f"chel-provider-copy-{run_id}.json", provider_report)
        body_run, body_sources = run_bodyslide(run_id, build_root)
    preset_manifest, preset_manifest_hash, mesh_manifest, mesh_manifest_hash, mesh_details = write_body_authorities(
        run_id, authority_root, slider_inspect, body_sources
    )
    external_chargen_manifest, external_chargen_manifest_hash = (
        write_external_chargen_authority(run_id, authority_root)
    )
    appearance_profile, appearance_profile_hash = write_appearance_profile(
        run_id, authority_root
    )
    standalone_manifest, standalone_hash = write_initial_standalone_manifest(
        run_id,
        authority_root,
        preset_manifest,
        preset_manifest_hash,
        mesh_manifest,
        mesh_manifest_hash,
        external_chargen_manifest,
        external_chargen_manifest_hash,
    )
    request_path, request_hash = write_execution_request(run_id, build_root, standalone_manifest, standalone_hash)

    manager_response = run_manager(run_id, request_path, request_hash, data_root, companion_root)
    package_verify, archive_result = verify_and_archive(run_id, manager_root, zip_path)
    acceptance = static_acceptance(
        run_id, build_root, data_root, manager_response, package_verify, archive_result, mesh_details
    )
    final = {
        "runId": run_id,
        "verdict": acceptance["verdict"],
        "packageRoot": str(manager_root),
        "zip": wire_value(archive_result.get("artifact", {}).get("archive")),
        "zipSha256": wire_value(
            archive_result.get("artifact", {}).get("archiveSha256")
        ),
        "jslotSha256": jslot_inspect.get("sourceSha256"),
        "bodySlideRunReport": str(REPORTS / f"chel-bodyslide-run-{run_id}.json"),
        "staticAcceptance": str(REPORTS / f"chel-schema7-static-acceptance-{run_id}.json"),
        "appearanceProfile": str(appearance_profile),
        "appearanceProfileSha256": appearance_profile_hash,
        "externalCharGenAuthority": str(external_chargen_manifest),
        "externalCharGenAuthoritySha256": external_chargen_manifest_hash,
    }
    print(json.dumps(final, indent=2))
    return 0 if acceptance["verdict"] == "STATIC_PASS_RUNTIME_REQUIRED" else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise
