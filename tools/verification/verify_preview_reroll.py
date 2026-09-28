"""Independent oracle for seed-stable preview variant selection."""

import argparse
import hashlib
import json
from pathlib import Path


MASK = (1 << 64) - 1


def load(path: Path):
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def select_index(seed: int, count: int) -> int:
    value = ((seed & MASK) + 0x9E3779B97F4A7C15) & MASK
    value = ((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9) & MASK
    value = ((value ^ (value >> 27)) * 0x94D049BB133111EB) & MASK
    value ^= value >> 31
    return value % count


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--repeat", required=True, type=Path)
    parser.add_argument("--response", required=True, type=Path)
    parser.add_argument("--seed", required=True, type=int)
    args = parser.parse_args()

    manifest_bytes = args.manifest.read_bytes()
    manifest = load(args.manifest)
    artifact = load(args.output)
    repeat = load(args.repeat)
    response = load(args.response)
    assert artifact == repeat, "Repeated preview reroll artifact changed"
    assert hashlib.sha256(manifest_bytes).hexdigest().casefold() == artifact["inputManifestSha256"].casefold()
    assert artifact["artifactKind"] == "preview-reroll-semantic-build"
    assert artifact["edition"] == manifest["edition"]
    assert artifact["npcFormId"] == "0x00000900"
    assert artifact["seed"] == args.seed == 42
    assert artifact["candidateCount"] == artifact["eligibleCount"] == len(manifest["variants"]) == 3
    expected_index = select_index(args.seed, artifact["candidateCount"])
    assert artifact["selectedIndex"] == expected_index
    assert artifact["selectedVariantId"] == artifact["candidates"][expected_index]["id"]
    assert all(candidate["eligible"] for candidate in artifact["candidates"])
    assert response["written"] is True
    assert response["selectedVariantId"] == artifact["selectedVariantId"]
    assert response["selectedIndex"] == expected_index
    assert all(item["severity"] != "error" for item in response["diagnostics"])
    print(f"PREVIEW REROLL INDEPENDENT PASS output={args.output}")


if __name__ == "__main__":
    main()
