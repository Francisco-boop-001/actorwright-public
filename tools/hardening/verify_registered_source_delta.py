"""Reconcile a working source tree with a registered package snapshot.

The comparer is deliberately read-only.  It reports every file in the selected
source owners, refuses path aliases/reparse points, and requires an explicit
allowlist classification for every non-identical row before returning PASS.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import stat
import subprocess
import sys
from pathlib import Path, PurePosixPath
from typing import Any, Iterable, Mapping, Sequence


ALLOWED_DISPOSITIONS = {
    "registered-preview225-byte-identity",
    "approved-registration-document",
    "unrelated-existing-drift",
}

# Build products are not source inputs.  They are recorded as exclusions so a
# dirty build cannot masquerade as a source delta, while avoiding thousands of
# generated DLL/cache rows in the reviewed allowlist.
GENERATED_DIRECTORY_NAMES = {
    "bin",
    "obj",
    "__pycache__",
    ".pytest_cache",
    "testresults",
}


class SourceBoundaryError(RuntimeError):
    """Raised when the source boundary cannot be inspected safely."""


def _is_reparse(path: Path) -> bool:
    info = path.lstat()
    if stat.S_ISLNK(info.st_mode):
        return True
    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    return bool(getattr(info, "st_file_attributes", 0) & reparse_flag)


def _assert_safe_tree(root: Path) -> Path:
    try:
        root = root.resolve(strict=True)
    except FileNotFoundError as exc:
        raise ValueError(f"source root does not exist: {root}") from exc
    if not root.is_dir():
        raise ValueError(f"source root is not a directory: {root}")
    if _is_reparse(root):
        raise SourceBoundaryError(f"reparse source root: {root}")
    for item in root.rglob("*"):
        try:
            if _is_reparse(item):
                raise SourceBoundaryError(f"reparse source path: {item}")
        except FileNotFoundError as exc:
            raise SourceBoundaryError(f"source path disappeared during inspection: {item}") from exc
    return root


def _normalise_relative(value: str) -> str:
    if not isinstance(value, str) or not value:
        raise ValueError("relative path must be a non-empty string")
    if "\\" in value:
        raise ValueError(f"path must use slash separators: {value!r}")
    path = PurePosixPath(value)
    if path.is_absolute() or any(part in ("", ".", "..") for part in path.parts):
        raise ValueError(f"path is not canonical and project-relative: {value!r}")
    return path.as_posix()


def _normalise_include(value: str) -> str:
    value = _normalise_relative(value)
    if "/" in value:
        raise ValueError(f"include must name a top-level source owner: {value!r}")
    return value


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _find_git_root(path: Path) -> Path | None:
    current = path
    for candidate in (current, *current.parents):
        git_marker = candidate / ".git"
        if git_marker.is_dir() or git_marker.is_file():
            return candidate
    return None


class _GitState:
    def __init__(self, working_project: Path) -> None:
        self.repo_root = _find_git_root(working_project)
        self.project_prefix = ""
        self.tracked: set[str] = set()
        self.untracked: set[str] = set()
        self.dirty: set[str] = set()
        if self.repo_root is None:
            return
        self.project_prefix = working_project.relative_to(self.repo_root).as_posix()
        self.tracked = self._names("git", "ls-files", "--", self.project_prefix)
        self.untracked = self._names(
            "git", "ls-files", "--others", "--exclude-standard", "--", self.project_prefix
        )
        self.dirty = self._names("git", "diff", "--name-only", "--", self.project_prefix)
        self.dirty.update(
            self._names("git", "diff", "--cached", "--name-only", "--", self.project_prefix)
        )

    def _names(self, *args: str) -> set[str]:
        assert self.repo_root is not None
        completed = subprocess.run(
            args,
            cwd=self.repo_root,
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        names: set[str] = set()
        prefix = self.project_prefix.rstrip("/") + "/"
        for raw in completed.stdout.decode("utf-8", errors="strict").splitlines():
            name = raw.replace("\\", "/")
            if name.startswith(prefix):
                names.add(name[len(prefix) :])
        return names

    def state(self, relative: str) -> str:
        if self.repo_root is None:
            return "untracked"
        if relative in self.untracked:
            return "untracked"
        if relative in self.tracked:
            return "tracked"
        return "unknown"

    def is_stageable_exact(self, relative: str) -> bool:
        return self.repo_root is not None and (
            relative in self.untracked or relative in self.dirty
        )


def _files(root: Path, includes: Sequence[str]) -> tuple[dict[str, Path], list[str]]:
    result: dict[str, Path] = {}
    excluded: list[str] = []
    folded: dict[str, str] = {}
    for owner in includes:
        owner_path = root / owner
        if not owner_path.exists():
            continue
        if _is_reparse(owner_path):
            raise SourceBoundaryError(f"reparse source owner: {owner_path}")
        if not owner_path.is_dir():
            raise ValueError(f"source owner is not a directory: {owner_path}")
        for path in owner_path.rglob("*"):
            if _is_reparse(path):
                raise SourceBoundaryError(f"reparse source path: {path}")
            if not path.is_file():
                continue
            relative = path.relative_to(root).as_posix()
            if any(part.casefold() in GENERATED_DIRECTORY_NAMES for part in PurePosixPath(relative).parts):
                excluded.append(relative)
                continue
            key = relative.casefold()
            previous = folded.get(key)
            if previous is not None and previous != relative:
                raise SourceBoundaryError(
                    f"case-folded duplicate source paths: {previous!r} and {relative!r}"
                )
            folded[key] = relative
            result[relative] = path
    return result, sorted(excluded)


def _allowlist_map(allowlist: Mapping[str, Any]) -> dict[str, Mapping[str, Any]]:
    if allowlist.get("schemaVersion") != 1:
        raise ValueError("allowlist schemaVersion must be 1")
    rows = allowlist.get("classifications")
    if not isinstance(rows, list):
        raise ValueError("allowlist classifications must be an array")
    result: dict[str, Mapping[str, Any]] = {}
    for row in rows:
        if not isinstance(row, Mapping):
            raise ValueError("allowlist classification must be an object")
        relative = _normalise_relative(row.get("path"))
        disposition = row.get("disposition")
        reason = row.get("reason")
        if disposition not in ALLOWED_DISPOSITIONS:
            raise ValueError(f"unsupported allowlist disposition for {relative}: {disposition!r}")
        if not isinstance(reason, str) or not reason.strip():
            raise ValueError(f"allowlist reason is required for {relative}")
        if relative in result:
            raise ValueError(f"duplicate allowlist path: {relative}")
        result[relative] = row
    return result


def verify(
    previous_source: Path | str,
    working_project: Path | str,
    allowlist: Mapping[str, Any],
    *,
    include: Sequence[str] = ("src", "tests", "tools"),
) -> dict[str, Any]:
    """Return a deterministic source reconciliation report."""

    previous = _assert_safe_tree(Path(previous_source))
    working = _assert_safe_tree(Path(working_project))
    includes = tuple(_normalise_include(value) for value in include)
    if len(set(item.casefold() for item in includes)) != len(includes):
        raise SourceBoundaryError("case-folded duplicate include owners")
    classifications = _allowlist_map(allowlist)
    for relative in classifications:
        if relative.split("/", 1)[0].casefold() not in {item.casefold() for item in includes}:
            raise ValueError(f"allowlist path is outside selected source owners: {relative}")

    previous_files, previous_excluded = _files(previous, includes)
    working_files, working_excluded = _files(working, includes)
    git_state = _GitState(working)
    rows: list[dict[str, Any]] = []
    unclassified: list[str] = []
    stageable: list[str] = []
    for relative in sorted(set(previous_files) | set(working_files)):
        previous_path = previous_files.get(relative)
        working_path = working_files.get(relative)
        previous_hash = _sha256(previous_path) if previous_path else None
        working_hash = _sha256(working_path) if working_path else None
        state = git_state.state(relative) if working_path else None
        if previous_hash is not None and previous_hash == working_hash:
            disposition = "byte-identical"
            if working_path and git_state.is_stageable_exact(relative):
                stageable.append(relative)
        elif previous_path is None:
            disposition = "added"
        elif working_path is None:
            disposition = "missing"
        elif state == "untracked":
            disposition = "collision"
        else:
            disposition = "changed"
        classification = classifications.get(relative)
        row: dict[str, Any] = {
            "path": relative,
            "previousSha256": previous_hash,
            "workingSha256": working_hash,
            "disposition": disposition,
            "workingState": state,
        }
        if classification is not None:
            row["classification"] = {
                "disposition": classification["disposition"],
                "reason": classification["reason"],
            }
        rows.append(row)
        if disposition != "byte-identical" and classification is None:
            unclassified.append(relative)

    # A classification for a file that is not part of the compared trees is
    # suspicious: it would otherwise permit a typo to hide a real delta.
    stale = sorted(set(classifications) - set(previous_files) - set(working_files))
    if stale:
        raise ValueError(f"allowlist paths are absent from both trees: {stale}")

    return {
        "schemaVersion": 1,
        "outcome": "PASS" if not unclassified else "FAIL",
        "previousSource": previous.as_posix(),
        "workingProject": working.as_posix(),
        "include": list(includes),
        "delta": rows,
        "unclassified": unclassified,
        "stageableExactPaths": stageable,
        "excludedGeneratedPaths": sorted(set(previous_excluded) | set(working_excluded)),
        "summary": {
            "filesCompared": len(rows),
            "byteIdentical": sum(row["disposition"] == "byte-identical" for row in rows),
            "classified": sum("classification" in row for row in rows),
            "unclassified": len(unclassified),
            "excludedGenerated": len(set(previous_excluded) | set(working_excluded)),
        },
    }


def _load_json(path: Path) -> Any:
    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        return json.load(handle)


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--previous-source", required=True, type=Path)
    parser.add_argument("--working-project", required=True, type=Path)
    parser.add_argument("--allowlist", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--include", action="append", dest="includes")
    args = parser.parse_args(argv)
    try:
        allowlist = _load_json(args.allowlist)
        report = verify(
            args.previous_source,
            args.working_project,
            allowlist,
            include=tuple(args.includes or ("src", "tests", "tools")),
        )
    except (OSError, ValueError, SourceBoundaryError, subprocess.CalledProcessError) as exc:
        report = {
            "schemaVersion": 1,
            "outcome": "BLOCKED",
            "error": str(exc),
        }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(report, handle, indent=2, sort_keys=True)
        handle.write("\n")
    print(json.dumps(report, indent=2, sort_keys=True))
    return 0 if report.get("outcome") == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
