"""Independent verifier for the bounded forms search JSON contract."""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path


REQUIRED = {
    "plugin",
    "formId",
    "signature",
    "editorId",
    "name",
    "isDeleted",
    "provenanceKind",
    "overrideChain",
}
SIGNATURE = re.compile(r"^[A-Z_]{4}$")


def fail(message: str) -> None:
    raise SystemExit(f"RESULT FAIL {message}")


def load(path: Path) -> dict:
    workspace = Path(r"K:\ExampleWorkspace").resolve()
    resolved = path.resolve()
    if workspace not in resolved.parents:
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


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", required=True, type=Path)
    parser.add_argument("--signatures", required=True)
    parser.add_argument("--count", type=int)
    args = parser.parse_args()
    value = load(args.json)
    if value.get("schemaVersion") != "1":
        fail("schema version")
    if value.get("edition") not in {"fallout4", "skyrimse"}:
        fail("edition")
    if not isinstance(value.get("allowNull"), bool):
        fail("allowNull")
    candidates = value.get("candidates")
    if not isinstance(candidates, list):
        fail("candidate array")
    if args.count is not None and len(candidates) != args.count:
        fail("candidate count")

    allowed = {item.strip().upper() for item in args.signatures.split(",") if item.strip()}
    seen = set()
    for candidate in candidates:
        if not isinstance(candidate, dict) or REQUIRED - candidate.keys():
            fail("candidate fields")
        form_id = candidate["formId"]
        signature = candidate["signature"]
        plugin = candidate["plugin"]
        if not isinstance(plugin, str) or not isinstance(candidate["isDeleted"], bool):
            fail("candidate scalar types")
        if candidate["editorId"] is not None and not isinstance(candidate["editorId"], str):
            fail("candidate EditorID type")
        if candidate["name"] is not None and not isinstance(candidate["name"], str):
            fail("candidate name type")
        if not isinstance(form_id, str) or not re.fullmatch(r"0x[0-9A-Fa-f]{8}", form_id):
            fail("candidate FormID")
        if not isinstance(signature, str) or not SIGNATURE.fullmatch(signature) or signature not in allowed:
            fail("candidate signature")
        identity = int(form_id, 16)
        if identity in seen:
            fail("duplicate winning FormID")
        seen.add(identity)
        chain = candidate["overrideChain"]
        if not isinstance(chain, list) or not chain or chain[-1] != plugin:
            fail("provenance chain")
        if not isinstance(candidate["provenanceKind"], str) or candidate["provenanceKind"] not in {"Base", "Override", "InferredOrder", "Unknown"}:
            fail("provenance kind")
    print(f"RESULT PASS forms schema/signatures/provenance count={len(candidates)}")


if __name__ == "__main__":
    main()
