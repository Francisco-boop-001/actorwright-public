"""Independent oracle for typed OTFT browse/list evidence."""

import argparse
import json
from pathlib import Path


def load(path: Path):
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    response = load(args.output)
    assert response["schemaVersion"] == "1"
    assert response["edition"] == "fallout4"
    candidates = response["candidates"]
    assert len(candidates) == 1
    selected = next(item for item in candidates if item["editorId"] == "M3DefaultOutfitFO4")
    assert selected["plugin"] == "Override.esp"
    assert selected["formId"] == "0x00000801"
    assert selected["items"] == ["0x00000803"]
    assert selected["provenanceKind"] == "Override"
    assert selected["overrideChain"] == ["Base.esp", "Override.esp"]
    assert not response["diagnostics"]
    print(f"OUTFIT LIST INDEPENDENT PASS output={args.output}")


if __name__ == "__main__":
    main()
