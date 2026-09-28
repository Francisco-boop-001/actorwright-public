"""Independently verify canonical FaceGen provider-path resolution evidence."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


WORKSPACE = Path(r"K:\ExampleWorkspace").resolve()


def fail(message: str) -> None:
    raise SystemExit(f"RESULT FAIL {message}")


def load(path: Path) -> dict:
    resolved = path.resolve()
    try:
        resolved.relative_to(WORKSPACE)
    except ValueError:
        fail("evidence path outside K workspace")
    try:
        value = json.loads(resolved.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as error:
        fail(f"invalid JSON: {error}")
    if not isinstance(value, dict):
        fail("response root must be an object")
    return value


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", required=True, type=Path)
    parser.add_argument("--edition", required=True, choices=("fallout4", "skyrimse"))
    parser.add_argument("--data-root", required=True, type=Path)
    parser.add_argument("--plugin", required=True)
    parser.add_argument("--npc", default="0x00000800")
    args = parser.parse_args()

    value = load(args.json)
    if value.get("resolved") is not True:
        fail("resolver did not report resolved=true")
    artifact = value.get("artifact")
    if not isinstance(artifact, dict) or artifact.get("artifactKind") != "facegen-provider-resolution":
        fail("artifact kind")
    expected_npc = "0x" + args.npc.removeprefix("0x").removeprefix("0X").upper().zfill(8)
    if artifact.get("edition") != args.edition or artifact.get("npcFormId") != expected_npc:
        fail("target identity")
    if artifact.get("originatingPlugin") != args.plugin or artifact.get("winningPlugin") != args.plugin:
        fail("origin/winning plugin")
    if artifact.get("schemaVersion") != "3":
        fail("schema version")
    context = artifact.get("npcContext")
    if not isinstance(context, dict) or context.get("sex") != "female":
        fail("winning NPC sex context")

    def form_value(value: object) -> int | None:
        if not isinstance(value, dict) or not isinstance(value.get("value"), int):
            return None
        return value["value"]

    if form_value(context.get("raceFormId")) != 0x801:
        fail("winning NPC race context")
    head_parts = context.get("headPartFormIds")
    if not isinstance(head_parts, list) or [form_value(item) for item in head_parts] != list(range(0x811, 0x81A)):
        fail("winning NPC headpart context")
    artifacts = artifact.get("artifacts")
    if not isinstance(artifacts, list):
        fail("artifacts")

    expected = {
        "fallout4": [
            ("faceGeom", f"Meshes/Actors/Character/FaceGenData/FaceGeom/{args.plugin}/00000800.nif", "required"),
            ("faceCustomizationDiffuse", f"Textures/Actors/Character/FaceCustomization/{args.plugin}/00000800_d.dds", "required"),
            ("faceCustomizationNormal", f"Textures/Actors/Character/FaceCustomization/{args.plugin}/00000800_msn.dds", "required"),
            ("faceCustomizationSpecular", f"Textures/Actors/Character/FaceCustomization/{args.plugin}/00000800_s.dds", "required"),
        ],
        "skyrimse": [
            ("faceGeom", f"Meshes/Actors/Character/FaceGenData/FaceGeom/{args.plugin}/00000800.nif", "required"),
            ("faceTint", f"Textures/Actors/Character/FaceGenData/FaceTint/{args.plugin}/00000800.dds", "required"),
            ("faceDiffuse", f"Textures/Actors/Character/FaceGenData/FaceDiffuse/{args.plugin}/00000800.dds", "optional"),
            ("faceNormal", f"Textures/Actors/Character/FaceGenData/FaceNormal/{args.plugin}/00000800.dds", "optional"),
        ],
    }[args.edition]
    if len(artifacts) != len(expected):
        fail(f"artifact count {len(artifacts)} != {len(expected)}")

    data_root = args.data_root.resolve()
    for item, (kind, relative, requiredness) in zip(artifacts, expected):
        if not isinstance(item, dict) or item.get("kind") != kind:
            fail(f"artifact kind {kind}")
        path = item.get("canonicalPath", {}).get("value")
        if path != relative or item.get("requiredness") != requiredness:
            fail(f"canonical contract {kind}")
        source = data_root.joinpath(*relative.split("/"))
        if not source.is_file():
            fail(f"fixture provider missing {relative}")
        providers = item.get("providers")
        if not isinstance(providers, list) or not providers:
            fail(f"provider evidence missing {relative}")
        expected_hash = hashlib.sha256(source.read_bytes()).hexdigest()
        if not any(p.get("sha256", {}).get("value", "").lower() == expected_hash for p in providers):
            fail(f"provider hash mismatch {relative}")

    diagnostics = value.get("diagnostics")
    if not isinstance(diagnostics, list) or any(item.get("severity") in {"error", "Error"} for item in diagnostics):
        fail("unexpected error diagnostic")
    print(f"RESULT PASS edition={args.edition} artifacts={len(artifacts)} providerHashes=verified")


if __name__ == "__main__":
    main()
