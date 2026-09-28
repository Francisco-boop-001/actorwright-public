"""Independent verifier for the typed preview animation tree artifact."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


def load(path: Path) -> dict:
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict):
        raise ValueError(f"{path} must contain an object")
    return value


def leaves(branch: dict) -> list[dict]:
    result = list(branch.get("leaves", []))
    for child in branch.get("children", []):
        result.extend(leaves(child))
    return result


def find_group(groups: list[dict], kind: str, name: str) -> dict:
    for group in groups:
        if group.get("kind") == kind and group.get("name") == name:
            return group
    raise ValueError(f"missing {kind}:{name}")


def verify(path: Path, manifest_hash: str, expected_visible: int, expect_full: bool) -> None:
    response = load(path)
    if response.get("succeeded") is not True:
        raise ValueError(f"{path} did not succeed")
    artifact = response.get("artifact")
    if not isinstance(artifact, dict) or artifact.get("artifactKind") != "preview-animation-tree":
        raise ValueError(f"{path} has the wrong artifact kind")
    if artifact.get("schemaVersion") != "1" or artifact.get("inputManifestSha256") != manifest_hash:
        raise ValueError(f"{path} has an invalid schema or manifest binding")
    if artifact.get("visibleCount") != expected_visible:
        raise ValueError(f"{path} visible count is not {expected_visible}")
    groups = artifact.get("groups")
    if not isinstance(groups, list):
        raise ValueError(f"{path} groups are not an array")
    mt = find_group(groups, "role", "Locomotion (MT)")
    if not any(leaf.get("clip", {}).get("id") == "walk" for leaf in leaves(mt)):
        raise ValueError(f"{path} did not preserve the walk leaf under MT")
    gestures = find_group(groups, "gestures", "Gestures & Dialogue (IDLE)")
    talk = find_group(gestures.get("children", []), "category", "Talk")
    talk_leaf = next((leaf for leaf in talk.get("leaves", []) if leaf.get("clip", {}).get("id") == "talk"), None)
    if talk_leaf is None or not talk_leaf.get("label", "").startswith("° "):
        raise ValueError(f"{path} did not preserve the non-behavior-graph gesture marker")
    if expect_full:
        weapon = find_group(groups, "role", "Weapon / combat")
        attack = next((leaf for leaf in leaves(weapon) if leaf.get("clip", {}).get("id") == "attack"), None)
        if attack is None or not attack.get("label", "").startswith("⊕ "):
            raise ValueError(f"{path} did not preserve the additive attack marker")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--default", type=Path, required=True)
    parser.add_argument("--full", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    args = parser.parse_args()
    manifest_hash = hashlib.sha256(args.manifest.read_bytes()).hexdigest()
    verify(args.default, manifest_hash, 3, False)
    verify(args.full, manifest_hash, 4, True)
    print(f"PREVIEW ANIMATION-TREE INDEPENDENT PASS default={args.default}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
