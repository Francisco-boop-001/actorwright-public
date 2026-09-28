#!/usr/bin/env python3
"""Focused Desktop renderer-script packaging and authority validator."""

from __future__ import annotations

import argparse
import hashlib
import re
import sys
from pathlib import Path


def fail(message: str) -> None:
    print(f"RESULT FAIL 0/1: {message}")
    raise SystemExit(1)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--desktop-output",
        required=True,
        type=Path,
        help="NpcManager.Desktop build or publish output directory",
    )
    args = parser.parse_args()

    workspace_root = Path(__file__).resolve().parents[4]
    project_root = workspace_root / "projects" / "NpcManagerReimplementation"
    script = workspace_root / "tools" / "rendering" / "render_npc_preview_bundle.py"
    csproj = (
        project_root
        / "src"
        / "NpcManager.Desktop"
        / "NpcManager.Desktop.csproj"
    )
    composition = (
        project_root
        / "src"
        / "NpcManager.Desktop"
        / "SkyrimMainWorkspaceDesktopComposition.cs"
    )
    output_script = (
        args.desktop_output.resolve()
        / "runtime"
        / "rendering"
        / "render_npc_preview_bundle.py"
    )

    source_bytes = script.read_bytes()
    source_hash = hashlib.sha256(source_bytes).hexdigest().upper()
    csproj_text = csproj.read_text(encoding="utf-8")
    composition_text = composition.read_text(encoding="utf-8")

    required_project_markers = (
        r'Include="..\..\..\..\tools\rendering\render_npc_preview_bundle.py"',
        r'Link="runtime\rendering\render_npc_preview_bundle.py"',
        'CopyToOutputDirectory="PreserveNewest"',
        'CopyToPublishDirectory="PreserveNewest"',
    )
    missing_markers = [
        marker for marker in required_project_markers if marker not in csproj_text
    ]
    if missing_markers:
        fail(f"Desktop project is missing packaging markers: {missing_markers}")

    if "ResolveNpcVisualRendererScript()" not in composition_text:
        fail("Desktop composition does not call its output-local script resolver.")
    resolver_pattern = re.compile(
        r"ResolveNpcVisualRendererScript\(\)\s*=>\s*"
        r"new\(Path\.Combine\(\s*"
        r"AppContext\.BaseDirectory,\s*"
        r'"runtime",\s*"rendering",\s*'
        r'"render_npc_preview_bundle\.py"\s*\)\s*\);',
        re.DOTALL,
    )
    if resolver_pattern.search(composition_text) is None:
        fail("Desktop script resolver is not rooted only at AppContext.BaseDirectory.")

    hash_pattern = re.compile(
        r"NpcVisualRendererScriptSha256\s*=\s*"
        r'new\(\s*"([0-9A-F]{64})"\s*\);',
        re.DOTALL,
    )
    hash_match = hash_pattern.search(composition_text)
    if hash_match is None:
        fail("Desktop composition has no exact renderer-script SHA-256 pin.")
    if hash_match.group(1) != source_hash:
        fail(
            "Desktop renderer-script pin does not match the shared source: "
            f"{hash_match.group(1)} != {source_hash}"
        )

    if not output_script.is_file():
        fail(f"Desktop output is missing packaged script: {output_script}")
    output_bytes = output_script.read_bytes()
    if output_bytes != source_bytes:
        fail("Desktop output renderer script is not byte-identical to shared source.")

    print(
        "RESULT PASS 1/1: Desktop resolves and packages the exact renderer script "
        f"{source_hash}."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
