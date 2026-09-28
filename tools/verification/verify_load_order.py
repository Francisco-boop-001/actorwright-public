#!/usr/bin/env python3
"""Independent structural verifier for the M2 load-order manifest contract."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path


def fail(message: str) -> int:
    print(f"RESULT FAIL {message}")
    return 1


def load(path: Path) -> dict:
    def pairs(items: list[tuple[str, object]]) -> dict:
        result: dict[str, object] = {}
        for key, value in items:
            if key in result:
                raise ValueError(f"duplicate key: {key}")
            result[key] = value
        return result

    with path.open("r", encoding="utf-8") as handle:
        result = json.load(handle, object_pairs_hook=pairs)
    if not isinstance(result, dict):
        raise ValueError("root must be an object")
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--plugins", required=True, type=Path)
    parser.add_argument("--edition", required=True, choices=("fallout4", "skyrimse"))
    args = parser.parse_args()
    manifest = args.manifest.resolve()
    plugins = args.plugins.resolve()
    if manifest.drive.upper() != "K:" or plugins.drive.upper() != "K:":
        return fail("manifest and plugin root must be on K:")
    if not manifest.is_file() or not plugins.is_dir():
        return fail("manifest or plugin root is missing")
    try:
        data = load(manifest)
        if data.get("schemaVersion") != 1 or data.get("edition") != args.edition:
            return fail("schema or edition mismatch")
        entries = data.get("plugins")
        if not isinstance(entries, list) or not entries:
            return fail("plugins must be a non-empty array")
        names: set[str] = set()
        orders: set[int] = set()
        for entry in entries:
            if not isinstance(entry, dict):
                return fail("plugin entry must be an object")
            name = entry.get("name")
            order = entry.get("order")
            if not isinstance(name, str) or Path(name).name != name or Path(name).suffix.lower() not in {".esp", ".esm", ".esl"}:
                return fail(f"invalid plugin name: {name}")
            if not isinstance(order, int) or order < 0:
                return fail(f"invalid order for {name}")
            key = name.casefold()
            if key in names or order in orders:
                return fail("duplicate plugin name or order")
            names.add(key)
            orders.add(order)
            if entry.get("enabled") and not (plugins / name).is_file():
                return fail(f"enabled plugin missing: {name}")
    except (OSError, ValueError, json.JSONDecodeError) as error:
        return fail(str(error))
    print(f"RESULT PASS entries={len(entries)} edition={args.edition}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
