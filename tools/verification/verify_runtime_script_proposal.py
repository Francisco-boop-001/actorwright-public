#!/usr/bin/env python3
"""Independently verify the typed VMAD proposal contract."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


FO4 = {
    "IsFemale": "bool", "SchemaVersion": "int", "OvlTemplate": "string[]", "OvlPriority": "int[]",
    "OvlRed": "float[]", "OvlGreen": "float[]", "OvlBlue": "float[]", "OvlAlpha": "float[]",
    "OvlOffsetU": "float[]", "OvlOffsetV": "float[]", "OvlScaleU": "float[]", "OvlScaleV": "float[]",
    "SkinTemplate": "string",
}
SSE = {
    "IsFemale": "bool", "SchemaVersion": "int", "OvlNode": "string[]", "OvlDiffuse": "string[]", "OvlNormal": "string[]",
    "OvlHasTint": "bool[]", "OvlTint": "int[]", "OvlHasAlpha": "bool[]", "OvlAlpha": "float[]", "SkinSlot": "int[]",
    "SkinDiffuse": "string[]", "SkinNormal": "string[]", "SkinHasTint": "bool[]", "SkinTint": "int[]", "NodeName": "string[]",
    "NodeHasScale": "bool[]", "NodeScale": "float[]", "NodeHasPos": "bool[]", "NodePosX": "float[]", "NodePosY": "float[]",
    "NodePosZ": "float[]", "NodeHasRot": "bool[]", **{f"NodeRotM{i}": "float[]" for i in range(9)}, "NodeScaleMode": "int[]",
}


def fail(message: str) -> None:
    raise SystemExit(f"RESULT FAIL {message}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--proposal", required=True, type=Path)
    parser.add_argument("--edition", required=True, choices=("fallout4", "skyrimse"))
    args = parser.parse_args()
    proposal = args.proposal.resolve()
    if not str(proposal).lower().startswith("k:\\exampleworkspace"):
        fail(f"outside-k={proposal}")
    document = json.loads(proposal.read_text(encoding="utf-8"))
    if document.get("artifactKind") != "npc-apply-script-vmad-proposal" or document.get("binaryMutation") is not False:
        fail("artifact-kind-or-binary-mutation")
    script = "NPCM_Manolov_ApplyFO4" if args.edition == "fallout4" else "NPCM_Manolov_ApplySSE"
    if document.get("scriptName") != script or document.get("edition") != args.edition:
        fail("script-or-edition")
    expected = FO4 if args.edition == "fallout4" else SSE
    properties = document.get("properties")
    if not isinstance(properties, list) or {item.get("name") for item in properties} != set(expected) or len(properties) != len(expected):
        fail("property-set")
    values = {}
    for item in properties:
        name, typ, value = item.get("name"), item.get("type"), item.get("value")
        if typ != expected.get(name):
            fail(f"property-type={name}")
        if typ.endswith("[]"):
            if not isinstance(value, list) or not value:
                fail(f"array-shape={name}")
            values[name] = len(value)
        elif typ == "bool" and not isinstance(value, bool):
            fail(f"bool-shape={name}")
        elif typ == "int" and (not isinstance(value, int) or isinstance(value, bool)):
            fail(f"int-shape={name}")
        elif typ == "string" and not isinstance(value, str):
            fail(f"string-shape={name}")
    groups = [["OvlTemplate", "OvlPriority", "OvlRed", "OvlGreen", "OvlBlue", "OvlAlpha", "OvlOffsetU", "OvlOffsetV", "OvlScaleU", "OvlScaleV"]] if args.edition == "fallout4" else [["OvlNode", "OvlDiffuse", "OvlNormal", "OvlHasTint", "OvlTint", "OvlHasAlpha", "OvlAlpha"], ["SkinSlot", "SkinDiffuse", "SkinNormal", "SkinHasTint", "SkinTint"], ["NodeName", "NodeHasScale", "NodeScale", "NodeHasPos", "NodePosX", "NodePosY", "NodePosZ", "NodeHasRot", *[f"NodeRotM{i}" for i in range(9)], "NodeScaleMode"]]
    for group in groups:
        if len({values[name] for name in group}) != 1:
            fail("parallel-array-length")
    emitter = Path(document["emitterSource"])
    if not emitter.is_file() or not str(emitter).lower().startswith("k:\\exampleworkspace"):
        fail("emitter-source")
    digest = hashlib.sha256(emitter.read_bytes()).hexdigest()
    if digest != document.get("emitterSourceSha256", "").lower():
        fail("emitter-hash")
    for fragment in document.get("fragments", []):
        if fragment["index"] < 0 or fragment["startInstruction"] < 0 or fragment["endInstruction"] <= fragment["startInstruction"]:
            fail("fragment-boundary")
    print(f"RESULT PASS edition={args.edition} properties={len(properties)} emitter_sha256={digest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
