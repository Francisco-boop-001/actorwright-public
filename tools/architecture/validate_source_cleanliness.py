#!/usr/bin/env python3
"""Reject effective SDK Compile inputs under src that are not Git source."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
from typing import Any


def _run(
    args: list[str],
    *,
    cwd: Path,
) -> subprocess.CompletedProcess[str]:
    environment = os.environ.copy()
    environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "0"
    environment["DOTNET_CLI_DISABLE_BUILD_SERVERS"] = "1"
    environment["MSBUILDDISABLENODEREUSE"] = "1"
    environment["UseSharedCompilation"] = "false"
    return subprocess.run(
        args,
        cwd=cwd,
        env=environment,
        text=True,
        capture_output=True,
        check=False,
    )


_MSBUILD_LOGGER_BOUNDARY = re.compile(
    r"\AMSBuild version [^\r\n]+ for \.NET(?:\r\n|\n|\r)"
)


def _parse_msbuild_json(output: str) -> dict[str, Any]:
    """Decode one MSBuild JSON payload despite a CLI/logger text boundary.

    ``-getItem`` is a JSON-producing query, but the dotnet/MSBuild host can
    still write a banner to the same stream in a clean release process.  Keep
    the source-integrity checks strict by accepting exactly one JSON object;
    the payload shape and every effective path are validated by the caller.
    """
    # A Windows UTF-8 BOM is a transport marker, not part of the JSON
    # document.  The only permitted non-whitespace prefix is the exact
    # one-line MSBuild host banner observed at the release boundary.
    framed = output[1:] if output.startswith("\ufeff") else output
    framed = framed.lstrip()
    logger_boundary = _MSBUILD_LOGGER_BOUNDARY.match(framed)
    if logger_boundary is not None:
        framed = framed[logger_boundary.end():].lstrip()

    payload, end = json.JSONDecoder().raw_decode(framed)
    if not isinstance(payload, dict):
        raise ValueError("MSBuild JSON payload must be an object")
    if framed[end:].strip():
        raise ValueError("MSBuild output contained more than one payload")
    return payload


def _project_compile_items(
    project: Path,
    *,
    dotnet: Path,
    root: Path,
) -> tuple[list[str], str | None]:
    result = _run(
        [
            str(dotnet),
            "msbuild",
            str(project),
            "-getItem:Compile",
            "-p:Configuration=Release",
            "-p:UseSharedCompilation=false",
            "-nologo",
        ],
        cwd=root,
    )
    if result.returncode != 0:
        detail = (result.stdout + result.stderr).strip()
        return [], (
            f"MSBuild Compile evaluation failed for {project.relative_to(root)}"
            + (f": {detail}" if detail else "")
        )
    try:
        payload: Any = _parse_msbuild_json(result.stdout)
    except (TypeError, ValueError) as error:
        return [], (
            f"MSBuild Compile evaluation was not JSON for "
            f"{project.relative_to(root)}: {error}"
        )
    items_payload = payload.get("Items") if isinstance(payload, dict) else None
    if not isinstance(items_payload, dict) or "Compile" not in items_payload:
        return [], (
            f"MSBuild Compile evaluation returned an invalid item payload for "
            f"{project.relative_to(root)}"
        )
    raw_items = items_payload["Compile"]
    if isinstance(raw_items, dict):
        raw_items = [raw_items]
    if not isinstance(raw_items, list):
        return [], (
            f"MSBuild Compile evaluation returned an invalid item list for "
            f"{project.relative_to(root)}"
        )
    paths: list[str] = []
    for item in raw_items:
        if not isinstance(item, dict) or not isinstance(item.get("FullPath"), str):
            return [], (
                f"MSBuild Compile evaluation returned an item without FullPath "
                f"for {project.relative_to(root)}"
            )
        paths.append(item["FullPath"])
    return paths, None


def _tracked_paths(root: Path) -> set[str]:
    result = _run(["git", "ls-files", "--full-name", "-z", "--", "src"], cwd=root)
    if result.returncode != 0:
        raise RuntimeError(
            "unable to enumerate Git source: " +
            (result.stdout + result.stderr).strip()
        )
    return {
        item.replace("\\", "/").casefold()
        for item in result.stdout.split("\0")
        if item
    }


def _git_state(root: Path, relative: str) -> str:
    result = _run(
        ["git", "check-ignore", "--no-index", "-q", "--", relative],
        cwd=root,
    )
    return "ignored" if result.returncode == 0 else "untracked"


def validate_source_cleanliness(
    root: Path,
    dotnet: Path,
) -> list[str]:
    root = root.resolve(strict=True)
    src = (root / "src").resolve(strict=True)
    projects = sorted(
        (
            path
            for path in src.glob("**/*.csproj")
            if not path.name.endswith("_wpftmp.csproj")
        ),
        key=lambda path: path.relative_to(root).as_posix().casefold(),
    )
    if not projects:
        return ["source cleanliness requires at least one src project"]

    tracked = _tracked_paths(root)
    errors: list[str] = []
    seen: set[tuple[str, str]] = set()
    evaluated = 0
    for project in projects:
        items, evaluation_error = _project_compile_items(
            project,
            dotnet=dotnet,
            root=root,
        )
        if evaluation_error is not None:
            errors.append(evaluation_error)
            continue
        for raw_path in items:
            path = Path(raw_path)
            if not path.is_absolute():
                path = project.parent / path
            path = path.resolve(strict=False)
            try:
                relative_path = path.relative_to(root).as_posix()
                path.relative_to(src)
            except ValueError:
                continue
            evaluated += 1
            key = (
                project.relative_to(root).as_posix().casefold(),
                relative_path.casefold(),
            )
            if key in seen:
                continue
            seen.add(key)
            if not path.is_file():
                errors.append(
                    f"{relative_path}: effective Compile input is missing "
                    f"(project {project.relative_to(root).as_posix()})"
                )
            if relative_path.casefold() not in tracked:
                state = _git_state(root, relative_path)
                errors.append(
                    f"{relative_path}: effective Compile input is {state}; "
                    f"it is not tracked in Git "
                    f"(project {project.relative_to(root).as_posix()})"
                )
    if errors:
        return errors
    return [f"evaluated projects={len(projects)} srcCompileInputs={evaluated}"]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--repository-root",
        type=Path,
        default=Path(__file__).resolve().parents[2],
    )
    parser.add_argument("--dotnet", type=Path)
    arguments = parser.parse_args(argv)
    root = arguments.repository_root.resolve(strict=True)
    dotnet = arguments.dotnet
    if dotnet is None:
        discovered = shutil.which("dotnet")
        if discovered is None:
            print("RESULT FAIL: dotnet SDK is unavailable")
            return 1
        dotnet = Path(discovered)
    dotnet = dotnet.resolve(strict=True)
    try:
        errors = validate_source_cleanliness(root, dotnet)
    except (OSError, RuntimeError, ValueError) as error:
        print(f"RESULT FAIL: {error}")
        return 1
    if errors and not (len(errors) == 1 and errors[0].startswith("evaluated ")):
        print("RESULT FAIL source cleanliness")
        for error in errors:
            print(f"  - {error}")
        return 1
    print("RESULT PASS source cleanliness: " + errors[0])
    return 0


if __name__ == "__main__":
    sys.exit(main())
