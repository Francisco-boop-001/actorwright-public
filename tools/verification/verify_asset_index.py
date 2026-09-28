"""Independent verifier for the persisted assets index artifact."""

from __future__ import annotations

import argparse
import json
import os
import re
from pathlib import Path


WORKSPACE = Path(r"K:\ExampleWorkspace").resolve()
SHA256 = re.compile(r"^[0-9a-f]{64}$")


def fail(message: str) -> None:
    raise SystemExit(f"RESULT FAIL {message}")


def is_under(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


def load(path: Path) -> dict:
    resolved = path.resolve()
    if not is_under(resolved, WORKSPACE):
        fail("JSON path outside K workspace")
    if resolved.is_symlink():
        fail("JSON path may not be a reparse point")
    try:
        value = json.loads(resolved.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as error:
        fail(f"invalid JSON: {error}")
    if not isinstance(value, dict):
        fail("root must be an object")
    return value


def provider_key(provider: dict) -> tuple[int, str, str, str]:
    return (
        0 if provider["kind"] == "loose" else 1,
        provider["source"].casefold(),
        provider["source"],
        provider["sha256"],
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", required=True, type=Path)
    parser.add_argument("--count", type=int)
    args = parser.parse_args()
    value = load(args.json)
    if value.get("schemaVersion") != "1":
        fail("schema version")
    if value.get("edition") not in {"fallout4", "skyrimse"}:
        fail("edition")
    data_root = value.get("dataRoot")
    if not isinstance(data_root, str) or not os.path.isabs(data_root):
        fail("Data root")
    if not is_under(Path(data_root).resolve(), WORKSPACE):
        fail("Data root outside K")
    entries = value.get("entries")
    if not isinstance(entries, list):
        fail("entries")
    if args.count is not None and len(entries) != args.count:
        fail("entry count")
    diagnostics = value.get("diagnostics")
    if not isinstance(diagnostics, list):
        fail("diagnostics")

    seen: set[str] = set()
    previous_path: tuple[str, str] | None = None
    for entry in entries:
        if not isinstance(entry, dict) or set(entry) != {"path", "winner", "providers"}:
            fail("entry fields")
        path = entry["path"]
        if not isinstance(path, str) or not path or path.startswith(("/", "\\")) or "\\" in path or ":" in path:
            fail("entry path")
        parts = path.split("/")
        if any(part in {"", ".", ".."} for part in parts):
            fail("entry traversal")
        normalized = path.casefold()
        if normalized in seen:
            fail("duplicate entry path")
        seen.add(normalized)
        sort_key = (path.casefold(), path)
        if previous_path is not None and sort_key < previous_path:
            fail("entry ordering")
        previous_path = sort_key

        providers = entry["providers"]
        winner = entry["winner"]
        if not isinstance(providers, list) or not providers:
            fail("provider list")
        if winner != providers[0]:
            fail("winner is not first provider")
        if providers != sorted(providers, key=provider_key):
            fail("provider precedence ordering")
        for provider in providers:
            if not isinstance(provider, dict) or set(provider) != {"path", "kind", "source", "size", "sha256"}:
                fail("provider fields")
            if provider["path"] != path or provider["kind"] not in {"loose", "archive"}:
                fail("provider identity")
            if not isinstance(provider["source"], str) or not provider["source"] or any(ord(char) < 32 for char in provider["source"]):
                fail("provider source")
            if not isinstance(provider["size"], int) or provider["size"] < 0:
                fail("provider size")
            if not isinstance(provider["sha256"], str) or not SHA256.fullmatch(provider["sha256"]):
                fail("provider hash")
    print(f"RESULT PASS asset-index schema/precedence entries={len(entries)}")


if __name__ == "__main__":
    main()
