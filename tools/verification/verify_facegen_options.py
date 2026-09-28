"""Independent verifier for the typed CharGen/FaceGen options artifact."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


def read_json(path: Path) -> object:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def output_hash(evidence: dict) -> str:
    value = evidence.get("outputSha256")
    if isinstance(value, dict):
        value = value.get("value")
    if not isinstance(value, str):
        raise ValueError("evidence has no outputSha256")
    return value.casefold()


def verify(options: dict, game: str) -> None:
    if options.get("schemaVersion") != "1":
        raise ValueError("unsupported schema")
    if options.get("edition") != game:
        raise ValueError("game mismatch")
    if game == "fallout4":
        if options.get("bakeSseRaceMenuOverlays"):
            raise ValueError("SSE-only overlay baking enabled for FO4")
        expected_normal = "uncompressed" if options.get("diffuseCompression") == "uncompressed" else "bc5"
        if options.get("normalCompression") != expected_normal:
            raise ValueError("FO4 all-mode normal derivation mismatch")
        if options.get("specularCompression") != expected_normal:
            raise ValueError("FO4 all-mode specular derivation mismatch")
    else:
        if any(options.get(name) for name in ("applyGhoulHeadRearFix", "applyEyebrowsFixedColor", "applyMouthVanillaFix")):
            raise ValueError("FO4-only fix enabled for SSE")
        if options.get("convention", {}).get("diffuse", {}).get("maskChannel") != "r":
            raise ValueError("SSE diffuse mask channel is not red")
        if options.get("normalCompression") != "uncompressed":
            raise ValueError("SSE all-mode normal is not uncompressed")
    if not options.get("perLayerResolution"):
        diffuse = options.get("diffuseResolution")
        if options.get("normalResolution") != diffuse or options.get("specularResolution") != diffuse:
            raise ValueError("all-mode resolutions do not follow diffuse")
    seed = options.get("convention", {}).get("seedConstant")
    if not isinstance(seed, list) or len(seed) != 3 or any(not isinstance(value, (int, float)) or not 0 <= value <= 1 for value in seed):
        raise ValueError("invalid seed constant")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--evidence", required=True, type=Path)
    parser.add_argument("--game", required=True, choices=("fallout4", "skyrimse"))
    args = parser.parse_args()
    evidence = read_json(args.evidence)
    if not evidence.get("isValid") or not evidence.get("applied"):
        raise SystemExit("options evidence is not an applied valid result")
    if hashlib.sha256(args.output.read_bytes()).hexdigest().casefold() != output_hash(evidence):
        raise SystemExit("output hash evidence mismatch")
    source = read_json(args.input)
    written = read_json(args.output)
    if source != written:
        raise SystemExit("canonical output changed the typed option values")
    verify(written, args.game)
    print("RESULT PASS canonical CharGen options and game-aware rules verified")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
