#!/usr/bin/env python3
"""Independently verify PSC/PEX evidence and the pinned Caprica inspection output."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


def fail(message: str) -> None:
    raise SystemExit(f"RESULT FAIL {message}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--evidence", required=True, type=Path)
    parser.add_argument("--edition", required=True, choices=("fallout4", "skyrimse"))
    args = parser.parse_args()
    evidence = args.evidence.resolve()
    if not str(evidence).lower().startswith("k:\\exampleworkspace"):
        fail(f"outside-k={evidence}")
    artifact = json.loads(evidence.read_text(encoding="utf-8"))
    if artifact.get("artifactKind") != "npc-apply-script-build-evidence" or artifact.get("binaryMutation") is not False or artifact.get("runtimeProof") is not False:
        fail("artifact-kind-safety")
    script = "NPCM_Manolov_ApplyFO4" if args.edition == "fallout4" else "NPCM_Manolov_ApplySSE"
    if artifact.get("scriptName") != script or artifact.get("edition") != args.edition or not artifact.get("validPex"):
        fail("identity-or-pex-validity")
    for path_key, hash_key in (("sourcePath", "sourceSha256"), ("pexPath", "pexSha256"), ("pexInspectionPath", "pexInspectionSha256"), ("compilerManifestPath", "compilerManifestSha256")):
        path = Path(artifact[path_key])
        if not path.is_file() or not str(path).lower().startswith("k:\\exampleworkspace"):
            fail(f"missing-{path_key}")
        if hashlib.sha256(path.read_bytes()).hexdigest() != artifact[hash_key].lower():
            fail(f"hash-{path_key}")
    inspection = json.loads(Path(artifact["pexInspectionPath"]).read_text(encoding="utf-8"))
    expected_game = "Fallout4" if args.edition == "fallout4" else "Skyrim"
    if inspection.get("valid") is not True or inspection.get("game") != expected_game:
        fail("inspection-validity")
    objects = [obj for obj in inspection.get("objects", []) if obj.get("name") == script]
    if len(objects) != 1 or objects[0].get("parent", "").lower() != "actor":
        fail("inspection-script-object")
    if len(artifact.get("properties", [])) != (13 if args.edition == "fallout4" else 37):
        fail("property-count")
    if not artifact.get("apiDependencies"):
        fail("api-dependencies")
    if not artifact.get("compilationMode") == "precompiled-pex-inspection":
        fail("compilation-mode")
    print(f"RESULT PASS edition={args.edition} properties={len(artifact['properties'])} pex_sha256={artifact['pexSha256']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
