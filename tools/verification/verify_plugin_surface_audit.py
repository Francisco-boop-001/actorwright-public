"""Independently verify the read-only plugin surface audit contract."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

WORKSPACE = Path(r"K:\ExampleWorkspace").resolve()


def fail(message: str) -> None:
    raise SystemExit(f"RESULT FAIL {message}")


def load(path: Path) -> dict:
    resolved = path.resolve()
    try:
        resolved.relative_to(WORKSPACE)
    except ValueError:
        fail("response path outside K workspace")
    try:
        value = json.loads(resolved.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as error:
        fail(f"invalid JSON: {error}")
    if not isinstance(value, dict):
        fail("response root must be an object")
    return value


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", required=True, type=Path)
    parser.add_argument("--before", required=True, type=Path)
    parser.add_argument("--after", required=True, type=Path)
    parser.add_argument("--edition", required=True, choices=("fallout4", "skyrimse"))
    parser.add_argument("--mode", required=True, choices=("changed", "unchanged", "providers", "unsafe"))
    parser.add_argument("--expected-winner")
    parser.add_argument("--expected-chain", nargs="+")
    args = parser.parse_args()
    value = load(args.json)
    diagnostics = value.get("diagnostics")
    if not isinstance(diagnostics, list):
        fail("diagnostics are not an array")

    if args.mode == "unsafe":
        if value.get("isValid") is not False or not any(
            item.get("code") == "before-outside-lab" for item in diagnostics if isinstance(item, dict)
        ):
            fail("unsafe input was not refused")
        print("RESULT PASS unsafe-root-refused")
        return

    for key in ("isValid", "schemaVersion", "beforeRecordCount", "afterRecordCount",
                "addedRecordCount", "removedRecordCount", "changedRecordCount", "changes"):
        if key not in value:
            fail(f"missing {key}")
    if value["isValid"] is not True or value["schemaVersion"] != 1:
        fail("validity/schema")
    if value.get("edition") != args.edition:
        fail("edition")
    if any(item.get("severity") in {"error", "Error"} for item in diagnostics if isinstance(item, dict)):
        fail("unexpected error diagnostic")
    before = args.before.resolve()
    after = args.after.resolve()
    if not before.is_file() or not after.is_file():
        fail("plugin input missing")
    if value.get("beforePlugin") != before.name or value.get("afterPlugin") != after.name:
        fail("plugin identity")
    changes = value["changes"]
    if not isinstance(changes, list):
        fail("changes are not an array")
    if args.mode == "providers":
        if not args.expected_winner or not args.expected_chain:
            fail("provider mode requires expected winner and chain")
        resolutions = value.get("providerResolutions")
        if not isinstance(resolutions, list):
            fail("providerResolutions are not an array")
        matching = [item for item in resolutions if isinstance(item, dict) and
                    item.get("winningPlugin") == args.expected_winner and
                    item.get("overrideChain") == args.expected_chain]
        if not matching:
            fail("expected provider winner and override chain were not reported")
        print(f"RESULT PASS mode=providers resolutions={len(resolutions)}")
        return
    if args.mode == "changed":
        if value["changedRecordCount"] < 1 or not any(item.get("changeKind") == "changed" for item in changes):
            fail("changed record was not reported")
    elif any(value[key] != 0 for key in ("addedRecordCount", "removedRecordCount", "changedRecordCount")) or changes:
        fail("unchanged inputs produced a change")
    print(f"RESULT PASS mode={args.mode} records={value['afterRecordCount']} changes={len(changes)}")


if __name__ == "__main__":
    main()
