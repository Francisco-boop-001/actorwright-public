"""Independent oracle for deterministic change-session field diffs."""

import argparse
import json
from pathlib import Path


def load(path: Path):
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--response", required=True, type=Path)
    args = parser.parse_args()
    response = load(args.response)
    assert response["succeeded"] is True
    assert response["edition"] == "fallout4"
    assert response["diagnostics"] == []
    changes = response["changes"]
    assert len(changes) == 2
    assert [item["formId"]["value"] for item in changes] == [2048, 2050]
    assert changes[0]["signature"] == "NPC_"
    assert changes[0]["editorId"] == "Alpha"
    assert changes[0]["fields"] == [{"name": "weight", "baseline": "50", "working": "55"}]
    assert changes[1]["signature"] == "ARMO"
    assert changes[1]["editorId"] == "New"
    assert changes[1]["fields"] == [{"name": "record", "baseline": None, "working": "present"}]
    assert all(item["formId"]["value"] != 2049 for item in changes)
    print(f"CHANGE TRACKING INDEPENDENT PASS response={args.response}")


if __name__ == "__main__":
    main()
