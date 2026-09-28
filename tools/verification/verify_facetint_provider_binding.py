"""Independently verify copied-plugin FO4 RACE/CLFM FaceTint binding evidence."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


WORKSPACE = Path(r"K:\ExampleWorkspace").resolve()


def fail(message: str) -> None:
    raise SystemExit(f"RESULT FAIL {message}")


def load_json(path: Path) -> dict:
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
    parser.add_argument("--data-root", required=True, type=Path)
    parser.add_argument("--plugin", required=True)
    args = parser.parse_args()

    value = load_json(args.json)
    if value.get("resolved") is not True:
        fail("resolver did not report resolved=true")
    artifact = value.get("artifact")
    if not isinstance(artifact, dict) or artifact.get("artifactKind") != "facegen-provider-resolution":
        fail("artifact kind")
    if artifact.get("schemaVersion") != "3":
        fail("schema version")
    expected_plugin = args.plugin
    if artifact.get("edition") != "fallout4":
        fail("edition")
    if artifact.get("npcFormId") != "0x00000800":
        fail("NPC identity")
    if artifact.get("originatingPlugin") != expected_plugin or artifact.get("winningPlugin") != expected_plugin:
        fail("winning plugin identity")
    context = artifact.get("npcContext")
    if not isinstance(context, dict) or context.get("sex") != "female":
        fail("female NPC context")
    binding = artifact.get("faceTintBinding")
    if not isinstance(binding, dict):
        fail("missing FaceTint binding")
    if binding.get("raceFormId") != "0x00000801" or binding.get("racePlugin") != expected_plugin:
        fail("RACE identity")
    if binding.get("sex") != "female":
        fail("RACE gender binding")
    groups = binding.get("groups")
    if not isinstance(groups, list) or len(groups) != 1:
        fail("RACE tint group count")
    group = groups[0]
    if not isinstance(group, dict) or group.get("categoryIndex") != 12:
        fail("tint group category")
    options = group.get("options")
    if not isinstance(options, list) or len(options) != 1:
        fail("tint option count")
    option = options[0]
    if not isinstance(option, dict) or option.get("index") != 42 or option.get("slot") != "SkinTone":
        fail("tint option identity")
    colors = option.get("templateColors")
    if not isinstance(colors, list) or len(colors) != 1:
        fail("template color count")
    color = colors[0]
    if not isinstance(color, dict) or color.get("templateIndex") != 3:
        fail("template color identity")
    provider = color.get("color")
    if not isinstance(provider, dict):
        fail("CLFM provider")
    if provider.get("formId") != "0x00000802" or provider.get("plugin") != expected_plugin:
        fail("CLFM identity")
    if provider.get("dataKind") != "rgb" or [provider.get(key) for key in ("red", "green", "blue")] != [64, 32, 16]:
        fail("CLFM RGB data")

    data_root = args.data_root.resolve()
    try:
        data_root.relative_to(WORKSPACE)
    except ValueError:
        fail("data root outside K workspace")
    plugin_path = data_root / expected_plugin
    if not plugin_path.is_file():
        fail("copied plugin missing")
    plugin_hash = hashlib.sha256(plugin_path.read_bytes()).hexdigest()
    print(f"RESULT PASS plugin={expected_plugin} sha256={plugin_hash} race=0x00000801 clfm=0x00000802")


if __name__ == "__main__":
    main()
