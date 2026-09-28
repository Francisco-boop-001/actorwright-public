"""Independent verifier for the bounded NPC browse/inspection JSON contract."""

from __future__ import annotations

import argparse
import json
from pathlib import Path


REQUIRED_INSPECT_FIELDS = {
    "plugin",
    "formId",
    "editorId",
    "name",
    "signature",
    "isDeleted",
    "sex",
    "raceFormId",
    "provenanceKind",
    "overrideChain",
    "changeState",
    "categories",
}


def fail(message: str) -> None:
    raise SystemExit(f"RESULT FAIL {message}")


def load(path: Path) -> dict:
    workspace = Path(r"K:\ExampleWorkspace").resolve()
    resolved = path.resolve()
    if workspace not in resolved.parents and resolved != workspace:
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


def verify_inspection(value: dict, args: argparse.Namespace) -> None:
    if value.get("schemaVersion") != "1":
        fail("inspection schema version")
    npc = value.get("npc")
    if not isinstance(npc, dict):
        fail("inspection NPC object missing")
    missing = REQUIRED_INSPECT_FIELDS - npc.keys()
    if missing:
        fail(f"inspection fields missing: {sorted(missing)}")
    if npc["plugin"] != args.plugin or npc["formId"].lower() != args.form_id.lower():
        fail("inspection identity mismatch")
    if npc.get("editorId") != args.editor_id or npc.get("name") != args.name:
        fail("inspection display identity mismatch")
    if args.sex is not None and npc.get("sex") != args.sex:
        fail("inspection sex mismatch")
    if not isinstance(npc["overrideChain"], list) or not npc["overrideChain"]:
        fail("inspection provenance chain missing")
    unsupported = value.get("unsupportedFields")
    if not isinstance(unsupported, list) or len(unsupported) != len(set(unsupported)):
        fail("unsupported field declaration is not explicit and unique")
    print("RESULT PASS inspection schema/provenance/typed fields")


def verify_list(value: dict, args: argparse.Namespace) -> None:
    npcs = value.get("npcs")
    if not isinstance(npcs, list) or len(npcs) != args.count:
        fail("list count mismatch")
    seen = set()
    for npc in npcs:
        if not isinstance(npc, dict):
            fail("list NPC is not an object")
        identity = (npc.get("plugin"), npc.get("formId"))
        if identity in seen:
            fail("duplicate NPC identity")
        seen.add(identity)
        if "categories" not in npc or "changeState" not in npc or "provenanceKind" not in npc:
            fail("list omitted classification/provenance fields")
    print(f"RESULT PASS list identities/classification count={len(npcs)}")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", required=True, type=Path)
    parser.add_argument("--mode", choices=("inspect", "list"), required=True)
    parser.add_argument("--plugin", default="")
    parser.add_argument("--form-id", default="")
    parser.add_argument("--editor-id", default="")
    parser.add_argument("--name", default="")
    parser.add_argument("--sex")
    parser.add_argument("--count", type=int, default=1)
    args = parser.parse_args()
    value = load(args.json)
    if args.mode == "inspect":
        verify_inspection(value, args)
    else:
        verify_list(value, args)


if __name__ == "__main__":
    main()
