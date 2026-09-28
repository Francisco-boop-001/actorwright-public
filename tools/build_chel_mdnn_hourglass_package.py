#!/usr/bin/env python3
"""Build the Chel MDNR + Hourglass UBE runtime-test package.

The Manager creates a schema-6 UBE Redguard actor host. This driver then adds
the exact RaceMenu JSlot, Manager-composed FaceTint, MDNR configuration, and
Hourglass UBE SliderPreset. MDNR itself remains an external dependency.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Any


ROOT = Path(r"K:\ExampleWorkspace")
PROJECT = ROOT / "projects" / "NpcManagerReimplementation"
REPORTS = PROJECT / "05-reports"
WORK_BUILDS = PROJECT / "03-builds" / "work"
PACKAGES = PROJECT / "04-packages"

DOTNET = (
    ROOT
    / "tools"
    / "external"
    / "dotnet-sdk-10.0.301-win-x64"
    / "dotnet.exe"
)
NPCM = (
    PROJECT
    / "src"
    / "NpcManager.Cli"
    / "bin"
    / "Debug"
    / "net10.0"
    / "npcm.dll"
)

DATA_ROOT = (
    PROJECT
    / "01-source-copies"
    / "chel-mdnr-provider-20260726-3"
    / "Data"
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
HOURGLASS_XML = (
    PROJECT
    / "01-source-copies"
    / "chel-hourglass-ube-20260726"
    / "CalienteTools"
    / "BodySlide"
    / "SliderPresets"
    / "Hourglass Body UBE.xml"
)
REJECTED_EXPORT_DDS = (
    PROJECT
    / "01-source-copies"
    / "chel-racemenu-export-20260725"
    / "Chelaccepted.dds"
)
BASE_REQUEST = (
    WORK_BUILDS
    / "chel-ube-redguard-schema7-20260725-8-hdpt-identity"
    / "chel-schema7-execution-request.json"
)
BASE_STANDALONE = (
    WORK_BUILDS
    / "chel-ube-redguard-schema7-20260725-8-hdpt-identity"
    / "authorities"
    / "standalone-assets-schema7-seed.json"
)

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

EXPECTED_PRESET_SHA256 = (
    "4f312633973b70df356c03b96bc85b4b32345ec178c13761c3131836b751e0ce"
)
EXPECTED_HOURGLASS_SHA256 = (
    "95f68cf553191a8fdcfbbc748848efe71f829dc261027f50397bdef78a898147"
)
EXPECTED_REJECTED_DDS_SHA256 = (
    "98df46e531e1ebb15d2c6850fcfa159302e0a2c6ebbbb17b853d2da6690fe7fd"
)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def rel(path: Path) -> str:
    return path.resolve().relative_to(ROOT.resolve()).as_posix()


def write_text(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")


def write_json(path: Path, value: Any) -> str:
    write_text(path, json.dumps(value, indent=2, ensure_ascii=False) + "\n")
    return sha256(path)


def run_cli(
    arguments: list[str],
    stdout_path: Path,
    stderr_path: Path,
    timeout: int = 1200,
) -> subprocess.CompletedProcess[str]:
    stdout_path.parent.mkdir(parents=True, exist_ok=True)
    command = [str(DOTNET), str(NPCM), *arguments]
    with stdout_path.open("w", encoding="utf-8", newline="\n") as stdout:
        with stderr_path.open("w", encoding="utf-8", newline="\n") as stderr:
            return subprocess.run(
                command,
                cwd=PROJECT,
                check=False,
                text=True,
                stdout=stdout,
                stderr=stderr,
                timeout=timeout,
            )


def require_file(path: Path, expected_hash: str | None = None) -> None:
    if not path.is_file():
        raise FileNotFoundError(path)
    if expected_hash is not None and sha256(path) != expected_hash.lower():
        raise RuntimeError(f"Hash drift: {path}")


def ensure_under_project(path: Path) -> None:
    resolved = path.resolve()
    project = PROJECT.resolve()
    if resolved != project and project not in resolved.parents:
        raise RuntimeError(f"Output escaped the project: {path}")


def write_schema6_authority(build_root: Path) -> tuple[Path, str]:
    authority = json.loads(BASE_STANDALONE.read_text(encoding="utf-8"))
    authority["schemaVersion"] = 6
    authority["assetSetId"] = "chel-ube-mdnr-hourglass-schema6-host-20260726"
    authority["faceTint"] = {"width": 512, "height": 512}
    authority["packageAssets"] = []
    authority["overlayDecisions"] = None
    authority["externalTextureAuthorities"] = []
    authority["finalOutputAuthority"] = None
    authority.pop("bodySlidePresetAuthority", None)
    authority.pop("bodyMeshAuthority", None)
    authority.pop("externalCharGenExportAuthority", None)
    path = build_root / "authorities" / "standalone-assets-schema6-seed.json"
    return path, write_json(path, authority)


def write_execution_request(
    build_root: Path,
    authority_path: Path,
    authority_hash: str,
) -> tuple[Path, str]:
    request = json.loads(BASE_REQUEST.read_text(encoding="utf-8"))
    request["standaloneAssets"] = {
        "manifestPath": rel(authority_path),
        "manifestSha256": authority_hash,
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
        "race": "UBE_AllRace.esp|0x0005A18E",
        "voice": "Skyrim.esm|0x013ADD",
        "class": "Skyrim.esm|0x013176",
        "combatStyle": "Skyrim.esm|0x03BE1B",
        "defaultOutfit": None,
    }
    request["stats"]["weight"] = 0.0
    request["applyBodySlide"] = False
    request["allowInheritedMeshEmbeddedSkinTextureRoute"] = True
    request["faceGeomSkeletonAuthority"] = "identityFaceGenBones"
    path = build_root / "chel-schema6-mdnr-execution-request.json"
    return path, write_json(path, request)


def run_manager(
    run_id: str,
    request_path: Path,
    request_hash: str,
    build_root: Path,
) -> dict[str, Any]:
    stdout = REPORTS / f"chel-mdnr-manager-{run_id}.stdout.json"
    stderr = REPORTS / f"chel-mdnr-manager-{run_id}.stderr.log"
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
            EXPECTED_PRESET_SHA256,
            "--data-root",
            str(DATA_ROOT),
            "--plugins",
            ",".join(PLUGIN_ORDER),
            "--companion-root",
            str(build_root / "companions"),
            "--json",
        ],
        stdout,
        stderr,
    )
    if completed.returncode != 0:
        raise RuntimeError(f"Manager build failed; inspect {stdout} and {stderr}")
    return json.loads(stdout.read_text(encoding="utf-8"))


def mdnr_config() -> dict[str, Any]:
    return {
        "actors": [
            {
                "condition": "IsActorBase(ChelNpcManager.esp|0x800)",
                "priority": 100,
                "inserttype": "unique",
                "volatile": False,
                "presets": [
                    {
                        "race": {
                            "formid": "0x05A18E",
                            "plugin": "UBE_AllRace.esp",
                        },
                        "gender": "female",
                        "applytype": {
                            "overrides": False,
                            "bodymorphs": False,
                            "transforms": False,
                            "skinoverrides": False,
                        },
                        "presetfile": "CharGen\\Presets\\UBE_Chel.jslot",
                        "tintfile": "CharGen\\ChelNpcManager.dds",
                        "bodypresetfile": (
                            "CalienteTools\\BodySlide\\SliderPresets\\"
                            "Hourglass Body UBE.xml"
                        ),
                    }
                ],
            }
        ]
    }


def add_runtime_layer(run_id: str, package_root: Path) -> dict[str, Any]:
    data = package_root / "Data"
    static_tint = (
        data
        / "textures"
        / "actors"
        / "character"
        / "FaceGenData"
        / "FaceTint"
        / "ChelNpcManager.esp"
        / "00000800.dds"
    )
    require_file(static_tint)

    runtime_preset = (
        data / "SKSE" / "Plugins" / "CharGen" / "Presets" / "UBE_Chel.jslot"
    )
    runtime_tint = (
        data / "SKSE" / "Plugins" / "CharGen" / "ChelNpcManager.dds"
    )
    runtime_xml = (
        data
        / "CalienteTools"
        / "BodySlide"
        / "SliderPresets"
        / "Hourglass Body UBE.xml"
    )
    config = (
        data
        / "SKSE"
        / "Plugins"
        / "MuDynamicNPCReplacer"
        / "ChelNpcManager.json"
    )
    diagnostic = data / "diag-chel-mdnr.txt"
    readme = data / "Chel-MDNR-README.txt"

    destinations = [runtime_preset, runtime_tint, runtime_xml, config, diagnostic, readme]
    if any(path.exists() for path in destinations):
        raise FileExistsError("MDNR runtime destination already exists")

    runtime_preset.parent.mkdir(parents=True, exist_ok=True)
    runtime_tint.parent.mkdir(parents=True, exist_ok=True)
    runtime_xml.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(CHEL_PRESET, runtime_preset)
    shutil.copy2(static_tint, runtime_tint)
    shutil.copy2(HOURGLASS_XML, runtime_xml)
    write_json(config, mdnr_config())
    write_text(
        diagnostic,
        "\n".join(
            [
                "; Chel MDNR identity diagnostic.",
                "; First spawn a NEW actor:",
                ';   help "ChelNpcManager" 4 NPC_',
                ";   player.placeatme <resolved-prefix>000800 1",
                "; Click Chel, then run: bat diag-chel-mdnr",
                "; Do NOT run setnpcweight before the first likeness capture.",
                "; Keep a known-good control NPC in the same frame and lighting.",
                "getavinfo aggression",
                "showinventory",
                "",
            ]
        ),
    )
    write_text(
        readme,
        "\n".join(
            [
                "Chel v0.9 - MDNR + Hourglass Body UBE runtime test",
                "",
                "REQUIRED, NOT BUNDLED:",
                "  MDNR - Mu Dynamic NPC Replacer 2.1.4",
                "  https://www.nexusmods.com/skyrimspecialedition/mods/150931",
                "",
                "Already present in the selected profile at build intake:",
                "  RaceMenu 0.4.16",
                "  UBE 2.0 U.0.7",
                "  OBody Next Generation 4.2.0",
                "  Hourglass Body UBE 1.0",
                "",
                "Disable every older Chel NPC Manager package before this test.",
                "Install MDNR, then install this ZIP with the mod manager.",
                "Start from a save that has not loaded an older Chel actor.",
                "",
                "Console spawn:",
                '  help "ChelNpcManager" 4 NPC_',
                "  player.placeatme <resolved-load-order-prefix>000800 1",
                "",
                "The MDNR rule targets ChelNpcManager.esp|0x800.",
                "It loads UBE_Chel.jslot and ChelNpcManager.dds.",
                "OBody receives Hourglass Body UBE.xml.",
                "JSlot body morphs are disabled so they cannot stack with Hourglass.",
                "",
                "Before any setnpcweight command, capture the first face and body.",
                "Use: bat diag-chel-mdnr",
                "Check SKSE/Plugins/MuDynamicNPCReplacer.log if the rule does not apply.",
                "",
                "This archive has static staging authority only. Runtime likeness",
                "remains unproven until the controlled in-game capture.",
                "",
            ]
        ),
    )

    config_hash = sha256(config)
    authority = {
        "schema": "npcmanager-mdnr-runtime-authority/1",
        "runId": run_id,
        "target": {
            "plugin": "ChelNpcManager.esp",
            "localFormId": "0x00000800",
            "editorId": "ChelNpcManager",
            "race": "UBE_AllRace.esp|0x0005A18E",
            "sex": "female",
        },
        "face": {
            "runtimeMechanism": "MDNR RaceMenu JSlot application",
            "presetPath": "SKSE/Plugins/CharGen/Presets/UBE_Chel.jslot",
            "presetSha256": sha256(runtime_preset),
            "tintPath": "SKSE/Plugins/CharGen/ChelNpcManager.dds",
            "tintSha256": sha256(runtime_tint),
            "tintMatchesManagerFaceTint": sha256(runtime_tint) == sha256(static_tint),
            "rejectedExportDds": {
                "path": rel(REJECTED_EXPORT_DDS),
                "sha256": sha256(REJECTED_EXPORT_DDS),
                "byteLength": REJECTED_EXPORT_DDS.stat().st_size,
                "dimensions": "1x1",
                "reason": "neutral DDS is not qualified as UBE sculpt-export tint",
            },
        },
        "body": {
            "runtimeMechanism": "MDNR bodypresetfile through OBody",
            "presetPath": (
                "CalienteTools/BodySlide/SliderPresets/Hourglass Body UBE.xml"
            ),
            "presetSha256": sha256(runtime_xml),
            "presetName": "Hourglass Body UBE",
            "targetSet": "UBE SE 2.0 Release Body",
            "bodyGenEmitted": False,
            "privateBodyMeshes": False,
            "privateSkinWnam": False,
        },
        "mdnr": {
            "configPath": (
                "SKSE/Plugins/MuDynamicNPCReplacer/ChelNpcManager.json"
            ),
            "configSha256": config_hash,
            "condition": "IsActorBase(ChelNpcManager.esp|0x800)",
            "applytype": {
                "overrides": False,
                "bodymorphs": False,
                "transforms": False,
                "skinoverrides": False,
            },
            "externalDependency": {
                "name": "MDNR - Mu Dynamic NPC Replacer",
                "version": "2.1.4",
                "bundled": False,
                "enabledAtBuildIntake": False,
            },
        },
        "routeOwnership": {
            "managerVmad": ["JSlot overlays", "1.04 NPC Head transform"],
            "mdnr": ["runtime JSlot face/headparts", "FaceTint", "body preset handoff"],
            "obody": ["Hourglass Body UBE morph application"],
        },
        "runtimeAuthority": False,
    }
    authority_path = (
        data
        / "NPCManager"
        / "Evidence"
        / "RuntimeAppearance"
        / "mdnr-hourglass-authority.json"
    )
    authority_hash = write_json(authority_path, authority)
    return {
        "authorityPath": authority_path,
        "authoritySha256": authority_hash,
        "configPath": config,
        "configSha256": config_hash,
        "runtimePreset": runtime_preset,
        "runtimeTint": runtime_tint,
        "runtimeXml": runtime_xml,
        "diagnostic": diagnostic,
        "readme": readme,
    }


def rewrite_manifest(package_root: Path, runtime: dict[str, Any]) -> str:
    manifest_path = package_root / "npcmanager-package.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    prior_kinds = {
        row["relativePath"].replace("\\", "/").lower(): row["kind"]
        for row in manifest["artifacts"]
    }
    new_kinds = {
        rel(runtime["runtimePreset"]).lower(): "mdnr-racemenu-preset",
        rel(runtime["runtimeTint"]).lower(): "mdnr-facetint",
        rel(runtime["runtimeXml"]).lower(): "mdnr-bodyslide-preset",
        rel(runtime["configPath"]).lower(): "mdnr-config",
        rel(runtime["authorityPath"]).lower(): "mdnr-runtime-authority",
        rel(runtime["diagnostic"]).lower(): "mdnr-runtime-diagnostic",
        rel(runtime["readme"]).lower(): "mdnr-runtime-readme",
    }
    root_prefix = rel(package_root).rstrip("/") + "/"
    normalized_new_kinds = {
        path.removeprefix(root_prefix.lower()): kind
        for path, kind in new_kinds.items()
    }
    artifacts: list[dict[str, Any]] = []
    for path in sorted(
        (item for item in package_root.rglob("*") if item.is_file()),
        key=lambda item: item.relative_to(package_root).as_posix().lower(),
    ):
        if path == manifest_path:
            continue
        relative = path.relative_to(package_root).as_posix()
        key = relative.lower()
        kind = prior_kinds.get(key) or normalized_new_kinds.get(key)
        if kind is None:
            raise RuntimeError(f"Undeclared post-build file has no kind: {relative}")
        artifacts.append(
            {
                "kind": kind,
                "relativePath": relative,
                "byteLength": path.stat().st_size,
                "sha256": sha256(path),
            }
        )
    manifest["artifacts"] = artifacts
    return write_json(manifest_path, manifest)


def assert_runtime_split(package_root: Path) -> dict[str, Any]:
    data = package_root / "Data"
    forbidden = []
    for path in data.rglob("*"):
        if not path.is_file():
            continue
        relative = path.relative_to(data).as_posix().lower()
        if "bodygendata/" in relative:
            forbidden.append(relative)
        if relative.startswith("meshes/actors/character/chel/body/"):
            forbidden.append(relative)
        if "zenithar" in relative:
            forbidden.append(relative)
    if forbidden:
        raise RuntimeError(f"Forbidden body carryover: {forbidden}")

    config_path = (
        data
        / "SKSE"
        / "Plugins"
        / "MuDynamicNPCReplacer"
        / "ChelNpcManager.json"
    )
    config = json.loads(config_path.read_text(encoding="utf-8"))
    expected = mdnr_config()
    if config != expected:
        raise RuntimeError("Written MDNR configuration drifted from the proposal")
    standalone_path = data / "NPCManager" / "Evidence" / "standalone-assets.json"
    standalone = json.loads(standalone_path.read_text(encoding="utf-8"))
    if standalone.get("schemaVersion") != 6:
        raise RuntimeError("Manager host is not schemaVersion 6")
    if any(
        key in standalone
        for key in (
            "bodySlidePresetAuthority",
            "bodyMeshAuthority",
            "externalCharGenExportAuthority",
        )
    ):
        raise RuntimeError("Schema 7 authority survived into the MDNR host")
    return {
        "schema6Host": True,
        "noBodyGen": True,
        "noPrivateChelBodyMeshes": True,
        "noZenitharArtifacts": True,
        "mdnrConfigExact": True,
    }


def verify_package(run_id: str, package_root: Path) -> dict[str, Any]:
    stdout = REPORTS / f"chel-mdnr-package-verify-{run_id}.json"
    stderr = REPORTS / f"chel-mdnr-package-verify-{run_id}.stderr.log"
    completed = run_cli(
        [
            "package",
            "verify",
            "--manifest",
            str(package_root / "npcmanager-package.json"),
            "--json",
        ],
        stdout,
        stderr,
        timeout=600,
    )
    if completed.returncode != 0:
        raise RuntimeError(f"Package verification failed; inspect {stdout}")
    return json.loads(stdout.read_text(encoding="utf-8"))


def archive_package(
    run_id: str,
    package_root: Path,
    archive: Path,
) -> dict[str, Any]:
    stdout = REPORTS / f"chel-mdnr-package-archive-{run_id}.json"
    stderr = REPORTS / f"chel-mdnr-package-archive-{run_id}.stderr.log"
    completed = run_cli(
        [
            "package",
            "archive",
            "--source-root",
            str(package_root),
            "--output",
            str(archive),
            "--json",
        ],
        stdout,
        stderr,
        timeout=600,
    )
    if completed.returncode != 0:
        raise RuntimeError(f"Package archive failed; inspect {stdout}")
    return json.loads(stdout.read_text(encoding="utf-8"))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--run-id", default="20260726-1")
    args = parser.parse_args()
    run_id = args.run_id
    if not run_id.replace("-", "").isalnum():
        raise ValueError("Unsafe run id")

    build_root = WORK_BUILDS / f"chel-ube-mdnr-hourglass-{run_id}"
    package_root = build_root / "manager-package"
    archive = (
        PACKAGES
        / "NpcManagerReimplementation-Chel-v0.9-mdnr-hourglass-runtime-test.zip"
    )
    for path in (build_root, package_root, archive):
        ensure_under_project(path)
        if path.exists():
            raise FileExistsError(path)

    require_file(DOTNET)
    require_file(NPCM)
    require_file(BASE_REQUEST)
    require_file(BASE_STANDALONE)
    require_file(CHEL_PRESET, EXPECTED_PRESET_SHA256)
    require_file(HOURGLASS_XML, EXPECTED_HOURGLASS_SHA256)
    require_file(REJECTED_EXPORT_DDS, EXPECTED_REJECTED_DDS_SHA256)
    if REJECTED_EXPORT_DDS.stat().st_size != 132:
        raise RuntimeError("Rejected export DDS size changed")
    if not DATA_ROOT.is_dir():
        raise FileNotFoundError(DATA_ROOT)

    build_root.mkdir(parents=True)
    authority_path, authority_hash = write_schema6_authority(build_root)
    request_path, request_hash = write_execution_request(
        build_root, authority_path, authority_hash
    )
    manager = run_manager(run_id, request_path, request_hash, build_root)
    if not manager.get("completed"):
        raise RuntimeError("Manager returned without completed=true")

    runtime = add_runtime_layer(run_id, package_root)
    manifest_hash = rewrite_manifest(package_root, runtime)
    split_checks = assert_runtime_split(package_root)
    verified = verify_package(run_id, package_root)
    if not verified.get("verified"):
        raise RuntimeError("Package verifier returned verified=false")
    archive_result = archive_package(run_id, package_root, archive)

    result = {
        "schema": "npcmanager-chel-mdnr-hourglass-build/1",
        "runId": run_id,
        "verdict": "STATIC_PASS_RUNTIME_REQUIRED",
        "packageRoot": str(package_root),
        "packageManifest": str(package_root / "npcmanager-package.json"),
        "packageManifestSha256": manifest_hash,
        "archive": str(archive),
        "archiveSha256": sha256(archive),
        "managerCompleted": True,
        "packageVerified": True,
        "runtimeSplitChecks": split_checks,
        "mdnrBundled": False,
        "mdnrRequiredVersion": "2.1.4",
        "runtimeAuthority": False,
        "managerResponse": manager,
        "packageVerify": verified,
        "archiveResult": archive_result,
    }
    result_path = REPORTS / f"chel-mdnr-hourglass-build-{run_id}.json"
    write_json(result_path, result)
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"ERROR: {error}", file=sys.stderr)
        raise
