#!/usr/bin/env python3
"""Independently verify package hashes, deterministic archive metadata, and CLI smoke."""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import io
import json
import os
import subprocess
import time
import zipfile
from pathlib import Path
from typing import Any


def _hash(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _canonical_archive(package_root: Path) -> bytes:
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        paths = sorted(
            (path for path in package_root.rglob("*") if path.is_file()),
            key=lambda path: path.relative_to(package_root).as_posix(),
        )
        for path in paths:
            info = zipfile.ZipInfo(path.relative_to(package_root).as_posix(), date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 0
            info.external_attr = 0o100644 << 16
            archive.writestr(info, path.read_bytes())
    return output.getvalue()


def _visible_windows_for_process(process_id: int, title: str) -> list[tuple[int, str]]:
    if os.name != "nt":
        return []
    from ctypes import wintypes

    user32 = ctypes.WinDLL("user32", use_last_error=True)
    user32.FindWindowExW.argtypes = (
        wintypes.HWND,
        wintypes.HWND,
        wintypes.LPCWSTR,
        wintypes.LPCWSTR,
    )
    user32.FindWindowExW.restype = wintypes.HWND
    user32.GetWindowThreadProcessId.argtypes = (wintypes.HWND, ctypes.POINTER(wintypes.DWORD))
    user32.GetWindowThreadProcessId.restype = wintypes.DWORD
    user32.IsWindowVisible.argtypes = (wintypes.HWND,)
    user32.IsWindowVisible.restype = wintypes.BOOL
    windows: list[tuple[int, str]] = []
    previous = None
    while True:
        window = user32.FindWindowExW(None, previous, None, title)
        if not window:
            break
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(window, ctypes.byref(owner))
        if owner.value == process_id and user32.IsWindowVisible(window):
            windows.append((int(window), title))
        previous = window
    return windows


def _smoke_desktop(executable: Path) -> list[str]:
    errors: list[str] = []
    if os.name != "nt":
        return ["packaged desktop smoke requires Windows"]
    environment = os.environ.copy()
    for name in ("DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_HOST_PATH"):
        environment.pop(name, None)
    process = subprocess.Popen(
        [str(executable.resolve())],
        cwd=executable.parent,
        env=environment,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        text=True,
    )
    matching: list[tuple[int, str]] = []
    try:
        deadline = time.monotonic() + 20.0
        while time.monotonic() < deadline:
            exit_code = process.poll()
            if exit_code is not None:
                detail = process.stderr.read().strip() if process.stderr is not None else ""
                errors.append(f"packaged desktop exited before first render: {exit_code} {detail}".strip())
                return errors
            matching = _visible_windows_for_process(
                process.pid,
                "NPC Studio — Skyrim NPC Manager",
            )
            if matching:
                break
            time.sleep(0.1)
        if len(matching) != 1:
            errors.append(f"packaged desktop did not expose exactly one expected window: {matching}")
            return errors

        user32 = ctypes.WinDLL("user32", use_last_error=True)
        from ctypes import wintypes

        user32.PostMessageW.argtypes = (
            wintypes.HWND,
            wintypes.UINT,
            wintypes.WPARAM,
            wintypes.LPARAM,
        )
        user32.PostMessageW.restype = wintypes.BOOL
        if not user32.PostMessageW(matching[0][0], 0x0010, 0, 0):
            errors.append("packaged desktop window refused WM_CLOSE")
            return errors
        try:
            exit_code = process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            errors.append("packaged desktop did not exit after its window was closed")
            return errors
        if exit_code != 0:
            errors.append(f"packaged desktop exited nonzero after first-render smoke: {exit_code}")
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
    return errors


def verify(project_root: Path, package_root: Path, archive_path: Path, report_path: Path) -> dict[str, Any]:
    errors: list[str] = []
    manifest_path = package_root / "package-manifest.json"
    hashes_path = package_root / "package.hashes.sha256"
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        report = json.loads(report_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        return {"result": "FAIL", "errors": [str(exc)]}

    entries = manifest.get("files", [])
    if not isinstance(entries, list) or not entries:
        errors.append("package manifest has no file entries")
        entries = []
    listed = {entry.get("path"): entry for entry in entries if isinstance(entry, dict)}
    for path_text, entry in sorted(listed.items()):
        if not isinstance(path_text, str) or Path(path_text).is_absolute() or ".." in Path(path_text).parts:
            errors.append(f"unsafe manifest path: {path_text}")
            continue
        path = package_root / Path(path_text)
        if not path.is_file():
            errors.append(f"missing package file: {path_text}")
            continue
        if entry.get("bytes") != path.stat().st_size or entry.get("sha256") != _hash(path):
            errors.append(f"hash/length mismatch: {path_text}")

    hash_lines = {}
    try:
        for line in hashes_path.read_text(encoding="utf-8").splitlines():
            digest, path_text = line.split("  ", 1)
            hash_lines[path_text] = digest
    except (OSError, ValueError) as exc:
        errors.append(f"invalid hash list: {exc}")
    if hash_lines != {path: entry.get("sha256") for path, entry in listed.items()}:
        errors.append("package.hashes.sha256 does not match package-manifest.json")

    try:
        actual_archive = archive_path.read_bytes()
        with zipfile.ZipFile(io.BytesIO(actual_archive)) as archive:
            names = archive.namelist()
            expected_names = sorted(
                (path.relative_to(package_root).as_posix() for path in package_root.rglob("*") if path.is_file())
            )
            if names != expected_names:
                errors.append("archive entries are not the sorted package file set")
            for info in archive.infolist():
                if info.date_time != (1980, 1, 1, 0, 0, 0):
                    errors.append(f"non-reproducible archive timestamp: {info.filename}")
                if archive.read(info.filename) != (package_root / Path(info.filename)).read_bytes():
                    errors.append(f"archive content mismatch: {info.filename}")
        if hashlib.sha256(actual_archive).hexdigest() != report.get("packageArchiveSha256"):
            errors.append("archive hash does not match acceptance report")
        if actual_archive != _canonical_archive(package_root):
            errors.append("archive is not reproducible from package contents")
    except (OSError, zipfile.BadZipFile) as exc:
        errors.append(f"archive verification failed: {exc}")

    required = [
        "cli/npcm.exe", "cli/npcm.dll", "desktop/NpcManager.Desktop.exe", "schemas/cli.schema.json",
        "README.md", "licenses/LICENSE", "licenses/NOTICE",
        "licenses/third-party-licenses.md",
        "licenses/Reloaded.Memory-9.4.1-LICENSE.md",
        "licenses/Microsoft.NETCore.App.Runtime-10.0.9-LICENSE.txt",
        "licenses/Microsoft.NETCore.App.Runtime-10.0.9-THIRD-PARTY-NOTICES.txt",
        "licenses/Microsoft.WindowsDesktop.App.Runtime-10.0.9-LICENSE.txt",
        "licenses/Microsoft.AspNetCore.App.Runtime-10.0.9-LICENSE.txt",
        "licenses/Microsoft.AspNetCore.App.Runtime-10.0.9-THIRD-PARTY-NOTICES.txt",
        "licenses/m8-license-closure.json", "licenses/m8-license-closure.md",
        "evidence/feature-ledger.json", "evidence/m8-runtime-smoke-readiness-2026-07-17.json",
        "evidence/m8-dependency-vulnerability-2026-07-17.json",
        "evidence/m8-dependency-vulnerability-public-2026-07-17.json",
        "evidence/m9-open-gaps-audit-2026-07-17.json",
        "source/tools/rendering/render_preview_scene.py",
        "source/tools/rendering/export_preview_nif.py",
        "source/tools/rendering/export_facegeom_nif.py",
        "source/tools/rendering/nif_geometry_readback.py",
    ]
    for path_text in required:
        if not (package_root / Path(path_text)).is_file():
            errors.append(f"required package surface missing: {path_text}")

    environment = os.environ.copy()
    for name in ("DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_HOST_PATH"):
        environment.pop(name, None)

    cli_executable = package_root / "cli/npcm.exe"
    if cli_executable.is_file():
        result = subprocess.run(
            [str(cli_executable.resolve()), "capabilities", "--json"],
            cwd=package_root,
            env=environment,
            capture_output=True,
            text=True,
            check=False,
        )
        if result.returncode != 0:
            errors.append(f"packaged CLI smoke failed: {result.returncode}")
        else:
            try:
                capabilities = json.loads(result.stdout)
                if capabilities.get("protocol") != "1" or not isinstance(capabilities.get("commands"), list):
                    errors.append("packaged CLI capabilities protocol mismatch")
            except json.JSONDecodeError:
                errors.append("packaged CLI capabilities was not JSON")

    desktop_executable = package_root / "desktop/NpcManager.Desktop.exe"
    if desktop_executable.is_file():
        errors.extend(_smoke_desktop(desktop_executable))

    return {"result": "PASS" if not errors else "FAIL", "fileCount": len(entries), "errors": errors}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--package-root", type=Path, required=True)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    project_root = Path(__file__).resolve().parents[2]
    result = verify(project_root, args.package_root.resolve(), args.archive.resolve(), args.report.resolve())
    print(json.dumps(result))
    return 0 if result["result"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
