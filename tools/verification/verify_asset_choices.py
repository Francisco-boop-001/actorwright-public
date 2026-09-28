"""Independent verifier for the bounded assets search JSON contract."""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path


SHA256 = re.compile(r"^[0-9a-f]{64}$")
FORM_ID = re.compile(r"^0x[0-9A-Fa-f]{8}$")


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
    parser.add_argument("--kind", required=True, choices=("mesh", "headpart"))
    parser.add_argument("--count", type=int)
    args = parser.parse_args()
    value = load(args.json)
    if value.get("schemaVersion") != "1":
        fail("schema version")
    if value.get("edition") not in {"fallout4", "skyrimse"}:
        fail("edition")
    if value.get("kind") != args.kind:
        fail("kind")
    candidates = value.get("candidates")
    if not isinstance(candidates, list):
        fail("candidate array")
    if args.count is not None and len(candidates) != args.count:
        fail("candidate count")
    seen_paths: set[str] = set()
    seen_forms: set[str] = set()
    for candidate in candidates:
        if not isinstance(candidate, dict):
            fail("candidate object")
        required = {"path", "providerStatus", "providers", "plugin", "formId", "signature",
                    "editorId", "name", "provenanceKind", "overrideChain"}
        if required - candidate.keys():
            fail("candidate fields")
        path = candidate["path"]
        if not isinstance(path, str) or not path or "\\" in path or path.startswith(("/", "\\")):
            fail("normalized relative path")
        parts = path.split("/")
        if any(part in {"", ".", ".."} for part in parts) or ":" in path:
            fail("asset traversal")
        if args.kind == "mesh" and not path.lower().endswith(".nif"):
            fail("mesh extension")
        if path.lower() in seen_paths:
            fail("duplicate normalized path")
        seen_paths.add(path.lower())
        status = candidate["providerStatus"]
        if status not in {"resolved", "missing"}:
            fail("provider status")
        providers = candidate["providers"]
        if not isinstance(providers, list) or (status == "resolved" and not providers) or (status == "missing" and providers):
            fail("provider evidence")
        for provider in providers:
            if not isinstance(provider, dict) or set(provider) != {"kind", "source", "size", "sha256"}:
                fail("provider fields")
            if provider["kind"] not in {"loose", "archive"} or not isinstance(provider["source"], str):
                fail("provider scalar")
            if not isinstance(provider["size"], int) or provider["size"] < 0 or not isinstance(provider["sha256"], str) or not SHA256.fullmatch(provider["sha256"]):
                fail("provider hash/size")
        if args.kind == "mesh":
            if any(candidate[field] is not None for field in ("plugin", "formId", "signature", "editorId", "name", "provenanceKind", "overrideChain")):
                fail("mesh metadata")
        else:
            plugin = candidate["plugin"]
            form_id = candidate["formId"]
            if not isinstance(plugin, str) or not FORM_ID.fullmatch(form_id or "") or candidate["signature"] != "HDPT":
                fail("headpart identity")
            if form_id in seen_forms:
                fail("duplicate winning headpart FormID")
            seen_forms.add(form_id)
            if candidate["provenanceKind"] not in {"Base", "Override", "InferredOrder", "Unknown"}:
                fail("headpart provenance")
            chain = candidate["overrideChain"]
            if not isinstance(chain, list) or not chain or chain[-1] != plugin:
                fail("headpart provenance chain")
    print(f"RESULT PASS assets schema/provider/path kind={args.kind} count={len(candidates)}")


if __name__ == "__main__":
    main()
