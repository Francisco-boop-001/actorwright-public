"""Independent oracle for explicit reset/delete action proposals."""

import argparse
import json
from pathlib import Path


def load(path: Path):
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--reset", required=True, type=Path)
    parser.add_argument("--delete", required=True, type=Path)
    parser.add_argument("--reset-response", required=True, type=Path)
    parser.add_argument("--delete-response", required=True, type=Path)
    args = parser.parse_args()
    reset = load(args.reset)
    assert reset["artifactKind"] == "change-action-proposal"
    assert reset["action"] == "reset"
    assert reset["outcome"] == "restore-baseline"
    assert reset["formId"] == "0x00000800"
    assert reset["signature"] == "NPC_"
    assert reset["baselinePresent"] is True and reset["workingPresent"] is True
    assert reset["baselineFields"] == {"weight": "50"}
    assert reset["workingFields"] == {"weight": "55"}
    assert load(args.reset_response)["written"] is True
    delete = load(args.delete)
    assert delete["action"] == "delete"
    assert delete["outcome"] == "remove-new-record"
    assert delete["formId"] == "0x00000801"
    assert delete["baselinePresent"] is False and delete["workingPresent"] is True
    assert load(args.delete_response)["written"] is True
    print(f"CHANGE ACTIONS INDEPENDENT PASS reset={args.reset} delete={args.delete}")


if __name__ == "__main__":
    main()
