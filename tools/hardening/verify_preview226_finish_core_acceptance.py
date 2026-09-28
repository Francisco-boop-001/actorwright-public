#!/usr/bin/env python3
"""Run the bounded static acceptance matrix for the preview.226 Finish Core slice."""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
from pathlib import Path


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run_selector(root: Path, dotnet: Path, dll: Path, selector: str, timeout: int = 180) -> dict[str, object]:
    result = subprocess.run(
        [str(dotnet), "exec", str(dll), selector],
        cwd=root,
        capture_output=True,
        text=True,
        timeout=timeout,
        check=False,
    )
    return {
        "selector": selector,
        "exitCode": result.returncode,
        "stdout": result.stdout[-4000:],
        "stderr": result.stderr[-4000:],
        "outcome": "PASS" if result.returncode == 0 else "FAIL",
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo-root", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    root = args.repo_root.resolve()
    dotnet = root / "tools/external/dotnet-sdk-10.0.301-win-x64/dotnet.exe"
    architecture = root / "projects/NpcManagerReimplementation/tests/NpcManager.Architecture.Tests/bin/Debug/net10.0/NpcManager.Architecture.Tests.dll"
    cli = root / "projects/NpcManagerReimplementation/tests/NpcManager.Cli.Tests/bin/Debug/net10.0/NpcManager.Cli.Tests.dll"
    desktop = root / "projects/NpcManagerReimplementation/tests/NpcManager.Desktop.Smoke/bin/Debug/net10.0-windows/NpcManager.Desktop.Smoke.dll"
    selectors = [
        (architecture, "--test-npc-finish-core-contracts"),
        (architecture, "--test-npc-finish-core-authority"),
        (architecture, "--test-npc-finish-core-proposal"),
        (architecture, "--test-npc-finish-core-binary"),
        (architecture, "--test-npc-finish-core-transaction"),
        (cli, "--test-npc-finish-core-cli"),
        (desktop, "--test-npc-finish-wizard"),
    ]
    checks: list[dict[str, object]] = []
    errors: list[str] = []
    for dll, selector in selectors:
        if not dll.is_file():
            checks.append({"selector": selector, "dll": str(dll), "outcome": "BLOCKED"})
            errors.append(f"missing test executable: {dll}")
            continue
        try:
            row = run_selector(root, dotnet, dll, selector)
        except (OSError, subprocess.TimeoutExpired) as exc:
            row = {"selector": selector, "outcome": "FAIL", "error": str(exc)}
        row["dll"] = str(dll.relative_to(root)).replace("\\", "/")
        checks.append(row)
        if row.get("outcome") != "PASS":
            errors.append(f"selector failed: {selector}")

    contracts = root / "projects/NpcManagerReimplementation/src/NpcManager.Application/SkyrimNpcFinishCoreContracts.cs"
    codec = root / "projects/NpcManagerReimplementation/src/NpcManager.Infrastructure/SkyrimNpcFinishCoreDocumentCodec.cs"
    desktop_xaml = root / "projects/NpcManagerReimplementation/src/NpcManager.Desktop/SkyrimNpcFinishWizardPanel.xaml"
    forbidden = ["cell", "worldspace", "placement", "anchor", "transform", "achr"]
    request_text = contracts.read_text(encoding="utf-8") if contracts.is_file() else ""
    codec_text = codec.read_text(encoding="utf-8") if codec.is_file() else ""
    xaml_text = desktop_xaml.read_text(encoding="utf-8") if desktop_xaml.is_file() else ""
    request_forbidden = [name for name in forbidden if f'"{name}"' in codec_text.lower() and name not in {"placement"}]
    checks.append({
        "id": "closed-request-and-desktop-boundary",
        "outcome": "PASS" if request_text and xaml_text and not request_forbidden else "FAIL",
        "requestForbiddenWireMembers": request_forbidden,
        "placementTextExplicit": "Placement is not included" in xaml_text,
    })
    if request_forbidden:
        errors.append("forbidden placement member appears in the canonical codec")

    mirella_report = root / "projects/MirellaFreshBuild/05-reports/mirella-preview226-finish-core-candidate-20260802.json"
    candidate_root = root / "projects/MirellaFreshBuild/03-builds/work/preview226-finish-core-candidate"
    mirella = json.loads(mirella_report.read_text(encoding="utf-8")) if mirella_report.is_file() else {}
    checks.append({
        "id": "mirella-candidate-boundary",
        "outcome": "PASS" if mirella.get("outcome") == "BLOCKED" and not candidate_root.exists() else "FAIL",
        "mirellaOutcome": mirella.get("outcome"),
        "candidateRootExists": candidate_root.exists(),
    })
    if mirella.get("outcome") != "BLOCKED" or candidate_root.exists():
        errors.append("Mirella must remain blocked until missing authorities are recovered")

    report = {
        "schemaVersion": 1,
        "artifactKind": "npc-manager-preview226-finish-core-acceptance",
        "outcome": "PASS" if not errors else "FAIL",
        "runtimeAuthority": False,
        "visualAuthority": False,
        "placementIncluded": False,
        "checks": checks,
        "errors": errors,
        "inputs": {
            "dotnet": str(dotnet.relative_to(root)).replace("\\", "/"),
            "dotnetSha256": sha256(dotnet) if dotnet.is_file() else None,
        },
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    if args.output.exists():
        raise SystemExit(f"refusing to overwrite existing report: {args.output}")
    args.output.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2, sort_keys=True))
    return 0 if report["outcome"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
