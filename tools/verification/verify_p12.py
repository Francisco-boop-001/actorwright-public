"""Independent P12 CLI/GUI completeness oracle."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


def read_json(path: Path) -> dict:
    text = path.read_text(encoding="utf-8-sig")
    return json.loads(text)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project-root", type=Path, required=True)
    parser.add_argument("--work-root", type=Path, required=True)
    parser.add_argument("--gui-xaml", type=Path, required=True)
    parser.add_argument("--gui-project", type=Path, required=True)
    args = parser.parse_args()

    caps = read_json(args.work_root / "capabilities.json")
    assert caps["protocol"] == "1"
    mappings = {row["command"]: row["ledgerIds"] for row in caps["ledgerMappings"]}
    expected = {
        "gui": "P12-001",
        "facegen bake-all": "P12-002",
        "facegen build-geom": "P12-003",
        "facegen diagnose": "P12-004",
        "capabilities": "P12-005",
        "schema export": "P12-006",
        "pipeline preset-to-npc": "P12-007",
    }
    for command, ledger_id in expected.items():
        assert ledger_id in mappings.get(command, []), (command, ledger_id)

    schema = read_json(args.work_root / "schema-export.json")
    assert schema["written"] is True
    schema_path = args.work_root / "all.schema.json"
    schema_document = read_json(schema_path)
    assert schema_document["schemaVersion"] == "1"
    assert len(schema_document["exitCodes"]) == 6
    assert any(row["name"] == "pipeline preset-to-npc" for row in schema_document["commands"])

    gui = read_json(args.work_root / "gui.json")
    assert gui["status"] == "available"
    xaml = args.gui_xaml.read_text(encoding="utf-8")
    project = args.gui_project.read_text(encoding="utf-8")
    for marker in ("AutomationProperties.Name", "MinWidth=\"720\"", "MinHeight=\"480\"", "Show capabilities"):
        assert marker in xaml, marker
    assert 'OutputType>WinExe<' in project and 'UseWPF>true<' in project

    diagnose = read_json(args.work_root / "facegen-diagnose.json")
    assert diagnose["isApplicable"] is True
    geom = read_json(args.work_root / "facegen-build-geom.json")
    assert geom["written"] is True and (args.work_root / "facegeom.json").exists()
    batch = read_json(args.work_root / "facegen-bake-all.json")
    assert batch["written"] is True and batch["hasFailures"] is False and batch["attempted"] == 2
    pipeline = read_json(args.work_root / "pipeline.json")
    assert pipeline["completed"] is True
    assert (args.work_root / "pipeline" / "npcmanager-package.json").exists()
    package_kinds = {item["kind"] for item in pipeline["packageArtifacts"]}
    assert {"plugin", "bodygen", "bodyslide", "facegeom", "facetint", "runtime-script-build", "runtime-script", "runtime-kit"}.issubset(package_kinds)
    runtime_script = next(item for item in pipeline["packageArtifacts"] if item["kind"] == "runtime-script")
    assert runtime_script["relativePath"] == "Data/Scripts/NPCM_Manolov_ApplyFO4.pex"
    for item in pipeline["packageArtifacts"]:
        artifact_path = Path(item["absolutePath"])
        assert artifact_path.exists() and artifact_path.is_file(), item["absolutePath"]
        artifact_bytes = artifact_path.read_bytes()
        assert len(artifact_bytes) == item["byteLength"], item["relativePath"]
        assert hashlib.sha256(artifact_bytes).hexdigest().upper() == item["sha256"].upper(), item["relativePath"]
    print("P12 ORACLE PASS commands=7 gui=static-smoke facegen=3 pipeline=1")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
