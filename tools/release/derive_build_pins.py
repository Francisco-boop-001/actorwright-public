#!/usr/bin/env python3
"""Derive release-summary overlap pins from canonical build evidence."""

from __future__ import annotations

import hashlib
import json
import re
import sys
from pathlib import Path

from tools.verification.compatibility_firewall import canonical_json_bytes


def derive(build_log_path: Path, protocol_path: Path) -> dict[str, int | str]:
    build_log = build_log_path.read_text(encoding="utf-8-sig")
    protocol = json.loads(protocol_path.read_text(encoding="utf-8-sig"))
    commands = protocol["result"]["commands"]
    if not isinstance(commands, list) or not all(
        isinstance(row, dict) for row in commands
    ):
        raise ValueError("protocol capabilities have no command inventory")

    registry = re.findall(
        r"(?m)^standalone test registry: PASS runnable=(\d+) fixture-bound=(\d+) "
        r"unverified=(\d+)(?: default-entry=\d+)?$",
        build_log,
    )
    results = [
        {"id": match.group(1), "status": match.group(2)}
        for match in re.finditer(
            r"(?m)^SELECTOR_RESULT id=([^\s]+) status=(PASS|FAIL)$", build_log
        )
    ]
    if len(registry) != 1:
        raise ValueError("canonical build must have exactly one selector registry result")
    runnable, _fixture_bound, unverified = map(int, registry[0])
    if (
        runnable != len(results)
        or unverified != 0
        or any(row["status"] != "PASS" for row in results)
        or len({row["id"] for row in results}) != len(results)
    ):
        raise ValueError("canonical selector results do not match runnable registry")

    pytest_results = re.findall(
        r"(?m)^(\d+) passed, (\d+) skipped, \d+ warnings in ", build_log
    )
    if len(pytest_results) != 1:
        raise ValueError("canonical build must have exactly one executed pytest summary")
    passed, skipped = map(int, pytest_results[0])

    def digest(value: object) -> str:
        return hashlib.sha256(canonical_json_bytes(value)).hexdigest().upper()

    return {
        "standalonePythonCases": passed,
        "standalonePythonSkipped": skipped,
        "orderedHelpSha256": digest(commands),
        "protocolReadinessSha256": digest([
            {"name": row.get("name"), "readiness": row.get("readiness")}
            for row in commands
        ]),
        "selectorInventorySha256": digest([row["id"] for row in results]),
        "selectorResultsSha256": digest(results),
    }


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: derive_build_pins.py BUILD_LOG PROTOCOL_CAPABILITIES", file=sys.stderr)
        return 2
    try:
        pins = derive(Path(sys.argv[1]), Path(sys.argv[2]))
    except (OSError, UnicodeError, ValueError, KeyError, TypeError) as exc:
        print(f"cannot derive release overlap pins: {exc}", file=sys.stderr)
        return 1
    print(json.dumps(pins, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
