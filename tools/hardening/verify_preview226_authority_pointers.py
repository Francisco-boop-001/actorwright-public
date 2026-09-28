#!/usr/bin/env python3
"""Independently verify the preview.226 provisional-authoring cutover."""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path
from typing import Any


VERSION = "1.0.0-preview.226"
PREVIOUS = "1.0.0-preview.225"
ARCHIVE_SHA256 = "528B6361FF1443D4CCF7F1AE2FBA891E20945BF5BF010917EADD7DC02C9323E0"
DESKTOP_SHA256 = "0CFA1BF89CD431A3BB57883431E6E81E2F24DD98CAB467A23B7CF202E9663E9A"
CLI_SHA256 = "762A882DFCB389103CA7A12C6D0E83D6648D457378BB0656906FB28EC68D87A9"
PACKAGE_MANIFEST_SHA256 = "0ED93BB0D12EDAD576B645C8172D275EB8CDDD9923F9185806834A8394003CE6"
HASH_MANIFEST_SHA256 = "E6A1CED7879972F2A8D5752D8B4CE60C868396A936CEDEF69E4C5F0D9DDD4B47"
COMMANDS = {
    "npc finish analyze",
    "npc finish apply",
    "npc finish verify",
}


def read_json(root: Path, relative: str) -> Any:
    with (root / relative).open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


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
        outcome = "PASS" if actual == expected else "FAIL"
        checks.append(
            {
                "id": identifier,
                "outcome": outcome,
                "actual": actual,
                "expected": expected,
            }
        )

    package_root = root / "projects/NpcManagerReimplementation/04-packages/npcmanager-1.0.0-preview.226"
    package_manifest = read_json(root, "projects/NpcManagerReimplementation/04-packages/npcmanager-1.0.0-preview.226/package-manifest.json")
    report = read_json(root, "projects/NpcManagerReimplementation/05-reports/npc-manager-preview226-provisional-authoring-registration-20260803.json")
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
    release_entry = release_gate["preview226_provisional_authoring_registration"]

    check("tool-index-version", registered_tool["version"], VERSION)
    check("tool-index-package", registered_tool["package"], "projects/NpcManagerReimplementation/04-packages/npcmanager-1.0.0-preview.226.zip")
    check("tool-index-archive-hash", registered_tool["package_sha256"], ARCHIVE_SHA256)
    check("tool-index-command-count", registered_tool["capability_count"], 133)
    check("tool-index-runtime-boundary", [registered_tool["provisional_authoring_authority"], registered_tool["visual_authority"], registered_tool["runtime_authority"]], [True, False, False])

    check("tool-manifest-version", tool_manifest["version"], VERSION)
    check("tool-manifest-package-root", tool_manifest["package_root"], "projects/NpcManagerReimplementation/04-packages/npcmanager-1.0.0-preview.226")
    check("tool-manifest-entry-points", [tool_manifest["entry_points"]["desktop"]["sha256"], tool_manifest["entry_points"]["cli"]["sha256"]], [DESKTOP_SHA256, CLI_SHA256])
    check("tool-manifest-command-count", tool_manifest["capability_authority"]["registered_catalogued_command_count"], 133)
    check("tool-manifest-finish-core", tool_manifest["registered_finish_core"]["commands"], sorted(COMMANDS))
    check("tool-manifest-finish-core-boundary", [tool_manifest["registered_finish_core"]["registered"], tool_manifest["registered_finish_core"]["runtimeAuthority"], tool_manifest["registered_finish_core"]["visualAuthority"]], [True, False, False])
    check("tool-manifest-rollback", tool_manifest["previous_registered_product"]["version"], PREVIOUS)

    check("project-registered-version", registered_product["version"], VERSION)
    check("project-registered-package", registered_product["package_archive"], "04-packages/npcmanager-1.0.0-preview.226.zip")
    check("project-registered-hashes", [registered_product["package_archive_sha256"], registered_product["desktop_sha256"], registered_product["cli_sha256"]], [ARCHIVE_SHA256, DESKTOP_SHA256, CLI_SHA256])
    check("project-registered-command-count", registered_product["capability_count"], 133)
    check("project-registered-boundary", [registered_product["visual_authority"], registered_product["runtime_authority"], registered_product["finish_core_placement_included"]], [False, False, False])
    check("project-source-authority", source_authority["registeredPackageVersion"], VERSION)
    check("project-source-candidate", [source_candidate["version"], source_candidate["registered"], source_candidate["capability_count"]], [VERSION, True, 133])
    check("project-deliverable-registration", "05-reports/npc-manager-preview226-provisional-authoring-registration-20260803.json" in project_manifest["deliverables"], True)

    check("workspace-tool-version", workspace_tool["version"], VERSION)
    check("workspace-tool-paths", [workspace_tool["desktop"], workspace_tool["cli"]], ["projects/NpcManagerReimplementation/04-packages/npcmanager-1.0.0-preview.226/desktop/NpcManager.Desktop.exe", "projects/NpcManagerReimplementation/04-packages/npcmanager-1.0.0-preview.226/cli/npcm.exe"])
    check("workspace-tool-boundary", workspace_tool["registration_boundary"].startswith("Preview.226 is provisional authority"), True)
    check("workspace-tool-rollback", workspace_tool["previous_registered_product"], "1.0.0-preview.225-preserved-130-commands")

    registered_binary = capability_registry["product"]["registeredAuthoringBinary"]
    check("capability-registry-version", registered_binary["version"], VERSION)
    check("capability-registry-archive", registered_binary["archiveSha256"], ARCHIVE_SHA256)
    check("capability-registry-boundary", [registered_binary["provisionalAuthoringAuthority"], registered_binary["runtimeAuthority"], registered_binary["visualAuthority"]], [True, False, False])

    check("release-gate-version", release_entry["registeredVersion"], VERSION)
    check("release-gate-archive", release_entry["packageArchiveSha256"], ARCHIVE_SHA256)
    check("release-gate-boundary", [release_entry["registered"], release_entry["runtimeAuthority"], release_entry["visualAuthority"]], [True, False, False])
    check("release-gate-rollback", release_entry["previousRollbackAuthority"], PREVIOUS)
    check("release-gate-source-authority", release_gate["source_authority"]["registeredPackageVersion"], VERSION)

    check("registration-report-version", report["version"], VERSION)
    check("registration-report-boundary", [report["registrationDecision"]["provisionalAuthoringAuthority"], report["registrationDecision"]["runtimeAuthority"], report["registrationDecision"]["visualAuthority"]], [True, False, False])
    check("registration-report-rollback", report["previousRegisteredProduct"]["version"], PREVIOUS)

    actual_hashes = {
        "archive": sha256(root / "projects/NpcManagerReimplementation/04-packages/npcmanager-1.0.0-preview.226.zip"),
        "desktop": sha256(package_root / "desktop/NpcManager.Desktop.exe"),
        "cli": sha256(package_root / "cli/npcm.exe"),
        "packageManifest": sha256(package_root / "package-manifest.json"),
        "hashManifest": sha256(package_root / "package.hashes.sha256"),
    }
    check("package-bytes", actual_hashes, {"archive": ARCHIVE_SHA256, "desktop": DESKTOP_SHA256, "cli": CLI_SHA256, "packageManifest": PACKAGE_MANIFEST_SHA256, "hashManifest": HASH_MANIFEST_SHA256})
    check("package-file-count", len(package_manifest["files"]), 2723)
    check("package-runtime-claim", [package_manifest["runtimeReleaseClaim"], report["registeredPackage"]["manifestedFileCount"]], [False, 2723])

    outcome = "PASS" if all(item["outcome"] == "PASS" for item in checks) else "FAIL"
    result = {
        "schemaVersion": 1,
        "artifactKind": "npc-manager-preview226-authority-pointer-verification",
        "version": VERSION,
        "previousRollbackAuthority": PREVIOUS,
        "outcome": outcome,
        "checks": checks,
        "errors": [] if outcome == "PASS" else [item["id"] for item in checks if item["outcome"] != "PASS"],
    }
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2))
    return 0 if outcome == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
