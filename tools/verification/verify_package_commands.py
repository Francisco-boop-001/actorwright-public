"""Independent integrity oracle for a typed npcmanager package."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def verify(manifest: Path) -> None:
    document = json.loads(manifest.read_text(encoding="utf-8-sig"))
    assert document["schemaVersion"] == 1
    root = manifest.parent
    artifacts = document["artifacts"]
    declared = {Path(item["relativePath"]) for item in artifacts}
    assert len(declared) == len(artifacts)
    for item in artifacts:
        path = root / Path(item["relativePath"])
        assert path.is_file(), str(path)
        assert path.stat().st_size == item["byteLength"], str(path)
        assert sha256(path) == item["sha256"].upper(), str(path)
    actual = {path.relative_to(root) for path in root.rglob("*") if path.is_file() and path.name != manifest.name}
    assert actual == declared, (actual, declared)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", type=Path, required=True)
    args = parser.parse_args()
    verify(args.manifest)
    print(f"PACKAGE COMMANDS ORACLE PASS artifacts={len(json.loads(args.manifest.read_text(encoding='utf-8-sig'))['artifacts'])}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
