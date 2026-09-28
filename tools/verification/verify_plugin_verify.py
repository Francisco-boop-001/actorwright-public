#!/usr/bin/env python3
"""Independent oracle for the proposal-bound plugin verify command."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

WORKSPACE = Path(r"K:\ExampleWorkspace").resolve()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", required=True)
    parser.add_argument("--expect", choices=("allowed", "refused"), required=True)
    args = parser.parse_args()
    path = Path(args.json).resolve()
    try:
        path.relative_to(WORKSPACE)
        document = json.loads(path.read_text(encoding="utf-8-sig"))
        if not isinstance(document, dict):
            raise ValueError("JSON root")
        valid = document.get("isValid") is True
        observed = document.get("observedChanges")
        has_sex = isinstance(observed, list) and any(
            isinstance(item, dict) and item.get("field") == "Sex" and item.get("after") == "female"
            for item in observed
        )
        if args.expect == "allowed" and (not valid or not has_sex):
            raise ValueError("validity or observed Sex evidence missing")
        if args.expect == "refused" and valid:
            raise ValueError("drifted plugin was accepted")
        print(f"RESULT PASS plugin-verify {args.expect}")
        return 0
    except (OSError, UnicodeError, ValueError, json.JSONDecodeError) as error:
        print(f"RESULT FAIL {error}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
