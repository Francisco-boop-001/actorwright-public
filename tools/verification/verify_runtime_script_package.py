#!/usr/bin/env python3
"""Independently verify the K-local apply-PEX package and source/hash binding."""
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
    parser.add_argument("--edition", required=True, choices=("fallout4", "skyrimse"))
    args = parser.parse_args()
    manifest = args.manifest.resolve()
    if not str(manifest).lower().startswith(LAB_PREFIX) or not manifest.is_file():
        fail("manifest-outside-k-or-missing")
    try:
        artifact = json.loads(manifest.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        fail(f"manifest-invalid={exc}")
    if artifact.get("schemaVersion") != "1" or artifact.get("artifactKind") != "npc-apply-script-package":
        fail("artifact-kind")
    if artifact.get("edition") != args.edition or artifact.get("binaryMutation") is not False or artifact.get("runtimeProof") is not False:
        fail("edition-or-safety-flags")
    script = "NPCM_Manolov_ApplyFO4" if args.edition == "fallout4" else "NPCM_Manolov_ApplySSE"
    if artifact.get("scriptName") != script:
        fail("script-name")

    source_root = Path(artifact.get("sourceRoot", "")).resolve()
    if not str(source_root).lower().startswith(LAB_PREFIX) or not source_root.is_dir() or source_root.is_symlink():
        fail("source-root")
    psc = safe_file(Path(artifact.get("sourcePscPath", "")), "source-psc")
    pex = safe_file(Path(artifact.get("sourcePexPath", "")), "source-pex")
    psc_path = Path(artifact["sourcePscPath"]).resolve()
    pex_path = Path(artifact["sourcePexPath"]).resolve()
    if not psc_path.is_relative_to(source_root) or not pex_path.is_relative_to(source_root):
        fail("source-file-outside-root")
    if psc_path.name != f"{script}.psc" or pex_path.name != f"{script}.pex":
        fail("source-file-name")
    if sha256(psc) != str(artifact.get("sourcePscSha256", "")).lower():
        fail("source-psc-hash")
    if sha256(pex) != str(artifact.get("sourcePexSha256", "")).lower():
        fail("source-pex-hash")

    root = manifest.parent
    relative = artifact.get("installedRelativePath")
    if relative != f"Data/Scripts/{script}.pex" or Path(relative).is_absolute() or ".." in Path(relative).parts:
        fail("installed-relative-path")
    installed_path = (root / Path(relative)).resolve()
    if installed_path != Path(artifact.get("installedPath", "")).resolve():
        fail("installed-path-binding")
    installed = safe_file(installed_path, "installed-pex")
    if installed != pex or sha256(installed) != str(artifact.get("installedSha256", "")).lower():
        fail("installed-pex-content-or-hash")
    if len(installed) != artifact.get("installedByteLength"):
        fail("installed-pex-length")
    scripts = root / "Data" / "Scripts"
    if not scripts.is_dir() or scripts.is_symlink():
        fail("scripts-directory")
    files = sorted(path.relative_to(scripts).as_posix() for path in scripts.rglob("*") if path.is_file())
    if files != [f"{script}.pex"]:
        fail(f"unexpected-script-sidecars={files}")
    if any(path.is_symlink() for path in scripts.rglob("*")):
        fail("reparse-or-symlink-sidecar")
    print(f"RESULT PASS edition={args.edition} script={script} sha256={sha256(installed)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
