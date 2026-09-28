"""Independently verify copied-plugin VMAD inspection evidence."""

from __future__ import annotations

import argparse
import hashlib
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
        fail("evidence path outside K workspace")
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
    parser.add_argument("--plugin", required=True, type=Path)
    parser.add_argument("--proposal", required=True, type=Path)
    parser.add_argument("--edition", required=True, choices=("fallout4", "skyrimse"))
    args = parser.parse_args()
    value = load(args.json)
    if value.get("resolved") is not True:
        fail("inspection did not resolve")
    artifact = value.get("artifact")
    if not isinstance(artifact, dict) or artifact.get("artifactKind") != "runtime-script-vmad-inspection":
        fail("artifact kind")
    expected_script = "NPCM_Manolov_ApplyFO4" if args.edition == "fallout4" else "NPCM_Manolov_ApplySSE"
    if artifact.get("edition") != args.edition or artifact.get("scriptName") != expected_script:
        fail("game/script identity")
    plugin = args.plugin.resolve()
    if not plugin.is_file() or artifact.get("plugin") != plugin.name:
        fail("plugin identity")
    digest = hashlib.sha256(plugin.read_bytes()).hexdigest().upper()
    if artifact.get("pluginSha256", "").upper() != digest:
        fail("plugin hash")
    proposal = load(args.proposal)
    properties = proposal.get("artifact", proposal).get("properties")
    expected = [item.get("name") for item in properties or [] if isinstance(item, dict)]
    actual = artifact.get("propertyNames")
    if actual != expected or artifact.get("propertyCount") != len(expected):
        fail("property names")
    if artifact.get("noWrite") is not True or artifact.get("runtimeProof") is not False:
        fail("boundary flags")
    diagnostics = value.get("diagnostics")
    if not isinstance(diagnostics, list) or any(item.get("severity") in {"error", "Error"} for item in diagnostics):
        fail("unexpected error diagnostic")
    print(f"RESULT PASS edition={args.edition} properties={len(expected)} hash=verified")


if __name__ == "__main__":
    main()
