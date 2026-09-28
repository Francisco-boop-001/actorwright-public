#!/usr/bin/env python3
"""Independently verify the preview.227 repository authority pointers."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Any


VERSION = "1.0.0-preview.227"
PREVIOUS_PROVISIONAL = "1.0.0-preview.226"
IMMUTABLE_ROLLBACK = "1.0.0-preview.225"
ARCHIVE_SHA256 = "3738DB221C96E8D171BD9E5F704A5A2D2A158782C5B551963B84343D4B93C22A"
DESKTOP_SHA256 = "2CDD575D40C638D929BAD9C1CCDCDF4999532D0DB9E21C34A0AEFCE517B6353D"
CLI_SHA256 = "D68685F833D83921AE2A8ACB7A80E3D0B90B41141229B5D3EDF8835779004DEB"
PACKAGE_MANIFEST_SHA256 = "CB8662295EF86109D08B217A7A7A82EC880625B3CD3BA99DC813520C6BFBCD95"
HASH_MANIFEST_SHA256 = "5D506FDD48FF85ECF92DAC47F9A57B342DD8A134E7BE2EE49AB5AD941E818BB5"
PLACEMENT_COMMANDS = [
    "npc placement interior analyze",
    "npc placement interior apply",
    "npc placement interior verify",
]


def read_json(root: Path, relative: str) -> Any:
    return json.loads((root / relative).read_text(encoding="utf-8-sig"))


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def find_named(items: list[dict[str, Any]], name: str) -> dict[str, Any]:
    for item in items:
        if item.get("name") == name:
            return item
    raise KeyError(name)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    root = Path(args.repo_root).resolve()
    checks: list[dict[str, Any]] = []

    def check(identifier: str, actual: Any, expected: Any) -> None:
        checks.append(
            {
                "id": identifier,
                "outcome": "PASS" if actual == expected else "FAIL",
                "actual": actual,
                "expected": expected,
            }
        )

    package_root = root / "projects/NpcManagerReimplementation/04-packages/npcmanager-1.0.0-preview.227"
    package_manifest = read_json(root, "projects/NpcManagerReimplementation/04-packages/npcmanager-1.0.0-preview.227/package-manifest.json")
    report = read_json(root, "projects/NpcManagerReimplementation/05-reports/npc-manager-preview227-provisional-registration-20260803.json")
    continuity = read_json(root, "projects/NpcManagerReimplementation/05-reports/npc-manager-preview227-candidate-continuity-20260803.json")
    source_delta = read_json(root, "projects/NpcManagerReimplementation/05-reports/npc-manager-preview227-source-delta-20260803.json")
    tool_index = read_json(root, "tools/manifests/tool-acquisition.json")
    tool_manifest = read_json(root, "tools/manifests/skyrim-npc-manager-reimplementation.json")
    project_manifest = read_json(root, "projects/NpcManagerReimplementation/PROJECT_MANIFEST.json")
    workspace_manifest = read_json(root, "WORKSPACE_MANIFEST.json")
    capability_registry = read_json(root, "projects/NpcManagerReimplementation/02-normalized-resources/accepted-preset-to-npc-capabilities-v1.json")
    release_gate = read_json(root, "projects/NpcManagerReimplementation/05-reports/release-gate.json")

    registered_tool = find_named(tool_index["tools"], "skyrim-npc-manager-reimplementation")
    workspace_tool = find_named(workspace_manifest["known_staged_tools"], "skyrim-npc-manager-reimplementation")
    registered_product = project_manifest["registered_provisional_product"]
    source_authority = project_manifest["source_authority"]
    source_candidate = project_manifest["current_source_candidate"]
    release_entry = release_gate["preview227_provisional_authoring_registration"]
    registered_binary = capability_registry["product"]["registeredAuthoringBinary"]

    check("tool-index-version", registered_tool["version"], VERSION)
    check("tool-index-archive", [registered_tool["package"], registered_tool["package_sha256"]], ["projects/NpcManagerReimplementation/04-packages/npcmanager-1.0.0-preview.227.zip", ARCHIVE_SHA256])
    check("tool-index-count", registered_tool["capability_count"], 136)
    check("tool-index-boundary", [registered_tool["provisional_authoring_authority"], registered_tool["visual_authority"], registered_tool["runtime_authority"]], [True, False, False])
    check("tool-index-placement", registered_tool["interior_placement_commands"], PLACEMENT_COMMANDS)

    check("tool-manifest-version", tool_manifest["version"], VERSION)
    check("tool-manifest-entry-points", [tool_manifest["entry_points"]["desktop"]["sha256"], tool_manifest["entry_points"]["cli"]["sha256"]], [DESKTOP_SHA256, CLI_SHA256])
    check("tool-manifest-count", tool_manifest["capability_authority"]["registered_catalogued_command_count"], 136)
    check("tool-manifest-placement", tool_manifest["registered_interior_placement"]["commands"], PLACEMENT_COMMANDS)
    check("tool-manifest-placement-boundary", [tool_manifest["registered_interior_placement"]["conflictContained"], tool_manifest["registered_interior_placement"]["conflictFree"], tool_manifest["registered_interior_placement"]["pathingAuthority"], tool_manifest["registered_interior_placement"]["runtimeAuthority"]], [True, False, False, False])
    check("tool-manifest-rollback", tool_manifest["previous_provisional_product"]["version"], PREVIOUS_PROVISIONAL)

    check("project-registered-version", registered_product["version"], VERSION)
    check("project-registered-hashes", [registered_product["package_archive_sha256"], registered_product["desktop_sha256"], registered_product["cli_sha256"]], [ARCHIVE_SHA256, DESKTOP_SHA256, CLI_SHA256])
    check("project-registered-count", registered_product["capability_count"], 136)
    check("project-registered-boundary", [registered_product["visual_authority"], registered_product["runtime_authority"], registered_product["interior_placement_conflict_free"], registered_product["interior_placement_pathing_authority"]], [False, False, False, False])
    check("project-source-authority", source_authority["registeredPackageVersion"], VERSION)
    check("project-source-candidate", [source_candidate["version"], source_candidate["registered"], source_candidate["capability_count"]], [VERSION, True, 136])
    check("project-deliverable-registration", "05-reports/npc-manager-preview227-provisional-registration-20260803.json" in project_manifest["deliverables"], True)

    check("workspace-tool-version", workspace_tool["version"], VERSION)
    check("workspace-tool-count", workspace_tool["capability_count"], 136)
    check("workspace-tool-rollback", workspace_tool["previous_registered_product"], "1.0.0-preview.226-preserved-133-commands")

    check("capability-registry-version", registered_binary["version"], VERSION)
    check("capability-registry-archive", registered_binary["archiveSha256"], ARCHIVE_SHA256)
    check("capability-registry-placement", registered_binary["interiorPlacement"]["commands"], PLACEMENT_COMMANDS)
    check("capability-registry-boundary", [registered_binary["provisionalAuthoringAuthority"], registered_binary["runtimeAuthority"], registered_binary["visualAuthority"], registered_binary["interiorPlacement"]["conflictFree"]], [True, False, False, False])

    check("release-gate-version", release_entry["registeredVersion"], VERSION)
    check("release-gate-archive", release_entry["packageArchiveSha256"], ARCHIVE_SHA256)
    check("release-gate-boundary", [release_entry["registered"], release_entry["runtimeAuthority"], release_entry["visualAuthority"], release_entry["pathingAuthority"], release_entry["conflictFree"]], [True, False, False, False, False])
    check("release-gate-rollback", release_entry["previousProvisionalRollbackAuthority"], PREVIOUS_PROVISIONAL)
    check("release-gate-source-authority", release_gate["source_authority"]["registeredPackageVersion"], VERSION)

    check("registration-report-authority", [report["authority"]["version"], report["outcome"], report["boundaries"]["provisionalAuthoringAuthority"]], [VERSION, "PASS_STATIC_PROVISIONAL_AUTHORITY", True])
    check("registration-report-placement", report["registeredFeatures"]["interiorPlacement"]["commands"], PLACEMENT_COMMANDS)
    check("registration-report-boundary", [report["boundaries"]["runtimeAuthority"], report["boundaries"]["visualAuthority"], report["boundaries"]["pathingAuthority"], report["boundaries"]["conflictFree"]], [False, False, False, False])
    check("candidate-continuity", continuity["outcome"], "PASS")
    check("source-delta", [source_delta["outcome"], source_delta["unclassified"]], ["PASS", []])

    actual_hashes = {
        "archive": sha256(root / "projects/NpcManagerReimplementation/04-packages/npcmanager-1.0.0-preview.227.zip"),
        "desktop": sha256(package_root / "desktop/NpcManager.Desktop.exe"),
        "cli": sha256(package_root / "cli/npcm.exe"),
        "packageManifest": sha256(package_root / "package-manifest.json"),
        "hashManifest": sha256(package_root / "package.hashes.sha256"),
    }
    check("package-bytes", actual_hashes, {"archive": ARCHIVE_SHA256, "desktop": DESKTOP_SHA256, "cli": CLI_SHA256, "packageManifest": PACKAGE_MANIFEST_SHA256, "hashManifest": HASH_MANIFEST_SHA256})
    check("package-file-count", len(package_manifest["files"]), 2734)
    check("package-runtime-claim", package_manifest["runtimeReleaseClaim"], False)

    outcome = "PASS" if all(item["outcome"] == "PASS" for item in checks) else "FAIL"
    result = {
        "schemaVersion": 1,
        "artifactKind": "npc-manager-preview227-authority-pointer-verification",
        "version": VERSION,
        "previousProvisionalRollbackAuthority": PREVIOUS_PROVISIONAL,
        "immutableRollbackAuthority": IMMUTABLE_ROLLBACK,
        "outcome": outcome,
        "checks": checks,
        "errors": [] if outcome == "PASS" else [item["id"] for item in checks if item["outcome"] != "PASS"],
    }
    output = Path(args.output)
    if output.exists():
        raise SystemExit(f"refusing to overwrite existing output: {output}")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2))
    return 0 if outcome == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
