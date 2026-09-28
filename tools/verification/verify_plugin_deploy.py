#!/usr/bin/env python3
"""Independently verify a plugin deployment without loading the application assemblies."""
from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path


LAB_PREFIX = "k:\\exampleworkspace"


def under_lab(path: Path) -> bool:
    try:
        return os.path.commonpath((str(path).lower(), LAB_PREFIX)) == LAB_PREFIX
    except ValueError:
        return False


def fail(message: str) -> None:
    raise SystemExit(f"RESULT FAIL {message}")


def resolved(path: Path) -> Path:
    return path.resolve()


def safe_file(path: Path, label: str) -> bytes:
    candidate = resolved(path)
    if not under_lab(candidate) or not candidate.is_file() or candidate.is_symlink():
        fail(f"unsafe-or-missing-{label}={candidate}")
    return candidate.read_bytes()


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--destination", required=True, type=Path)
    parser.add_argument("--expected-sha256", required=True)
    parser.add_argument("--mode", choices=("deployed", "conflict", "unsafe"), default="deployed")
    args = parser.parse_args()
    expected = args.expected_sha256.lower()
    if len(expected) != 64 or any(character not in "0123456789abcdef" for character in expected):
        fail("expected-sha256-invalid")

    source_path = resolved(args.source)
    destination_path = resolved(args.destination)
    if args.mode == "unsafe":
        if under_lab(destination_path):
            fail("unsafe-destination-is-under-k")
        print("RESULT PASS mode=unsafe destination-outside-k")
        return 0

    source = safe_file(source_path, "source")
    destination = safe_file(destination_path, "destination")
    source_hash = digest(source)
    destination_hash = digest(destination)
    if source_hash != expected:
        fail("source-hash-mismatch")

    if args.mode == "deployed":
        if source_hash != destination_hash or source_path == destination_path:
            fail("deployed-bytes-or-hash-mismatch")
        print(f"RESULT PASS mode=deployed sha256={expected} bytes={len(destination)}")
        return 0

    if source_hash == destination_hash or source_path == destination_path:
        fail("conflict-destination-is-identical")
    print(f"RESULT PASS mode=conflict source-sha256={source_hash} destination-sha256={destination_hash}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
