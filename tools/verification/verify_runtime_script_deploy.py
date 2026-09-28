#!/usr/bin/env python3
"""Independently verify a packaged apply PEX deployed into a copied Data root."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


LAB_PREFIX = "k:\\exampleworkspace"


def fail(message: str) -> None:
    raise SystemExit(f"RESULT FAIL {message}")


def safe_file(path: Path, label: str) -> bytes:
    resolved = path.resolve()
    if not str(resolved).lower().startswith(LAB_PREFIX) or not resolved.is_file() or resolved.is_symlink():
        fail(f"unsafe-or-missing-{label}={resolved}")
    return resolved.read_bytes()


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--data-root", required=True, type=Path)
    parser.add_argument("--edition", required=True, choices=("fallout4", "skyrimse"))
    args = parser.parse_args()
    manifest = args.manifest.resolve()
    data_root = args.data_root.resolve()
    if not str(manifest).lower().startswith(LAB_PREFIX) or not manifest.is_file():
        fail("manifest-outside-k-or-missing")
    if not str(data_root).lower().startswith(LAB_PREFIX) or not data_root.is_dir() or data_root.is_symlink():
        fail("data-root-outside-k-or-missing")
    try:
        artifact = json.loads(manifest.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        fail(f"manifest-invalid={exc}")
    script = "NPCM_Manolov_ApplyFO4" if args.edition == "fallout4" else "NPCM_Manolov_ApplySSE"
    if artifact.get("schemaVersion") != "1" or artifact.get("artifactKind") != "npc-apply-script-package":
        fail("artifact-kind")
    if artifact.get("edition") != args.edition or artifact.get("scriptName") != script:
        fail("edition-or-script")
    package_source = (manifest.parent / "Data" / "Scripts" / f"{script}.pex").resolve()
    deployed = (data_root / "Scripts" / f"{script}.pex").resolve()
    if package_source != Path(artifact.get("installedPath", "")).resolve():
        fail("package-path-binding")
    source = safe_file(package_source, "package-pex")
    output = safe_file(deployed, "deployed-pex")
    expected_hash = str(artifact.get("installedSha256", "")).lower()
    if sha256(source) != expected_hash or sha256(output) != expected_hash or source != output:
        fail("deployed-bytes-or-hash")
    if len(output) != artifact.get("installedByteLength"):
        fail("deployed-length")
    scripts = data_root / "Scripts"
    if scripts.is_symlink() or any(path.is_symlink() for path in scripts.rglob("*")):
        fail("reparse-or-symlink-data-script")
    print(f"RESULT PASS edition={args.edition} script={script} sha256={expected_hash}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
