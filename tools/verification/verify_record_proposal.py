"""Independent oracle for typed new and override record proposals."""

import argparse
import json
from pathlib import Path


def load(path: Path):
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--new", required=True, type=Path)
    parser.add_argument("--override", required=True, type=Path)
    parser.add_argument("--new-response", required=True, type=Path)
    parser.add_argument("--override-response", required=True, type=Path)
    args = parser.parse_args()
    new = load(args.new)
    assert new["artifactKind"] == "record-proposal"
    assert new["edition"] == "fallout4" and new["mode"] == "new"
    assert new["signature"] == "NPC_" and new["formId"] == "0x00001234"
    assert new["sourceFormId"] is None and new["editorId"] == "npcm_Fixture"
    assert new["masterDependencies"] == ["Fallout4.esm"]
    assert new["allocationStrategy"] == "explicit-local-form-id"
    assert load(args.new_response)["written"] is True
    override = load(args.override)
    assert override["edition"] == "skyrimse" and override["mode"] == "override"
    assert override["formId"] == "0x00002345" and override["sourceFormId"] == "0x00002345"
    assert load(args.override_response)["written"] is True
    print(f"RECORD PROPOSAL INDEPENDENT PASS new={args.new} override={args.override}")


if __name__ == "__main__":
    main()
