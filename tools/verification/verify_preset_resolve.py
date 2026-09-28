"""Independent oracle for explicit K-local preset form-identifier resolution."""

import argparse
import json
from pathlib import Path


def load(path: Path):
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--response", required=True, type=Path)
    parser.add_argument("--missing-response", required=True, type=Path)
    parser.add_argument("--invalid-response", required=True, type=Path)
    args = parser.parse_args()

    resolved = load(args.response)
    assert resolved["identifier"] == "ExampleHair.esp|01000010"
    assert resolved["resolvedFormId"] == "0x02000010"
    assert resolved["isResolved"] is True
    assert resolved["diagnostics"] == []

    missing = load(args.missing_response)
    assert missing["isResolved"] is False
    assert missing["resolvedFormId"] is None
    assert any(item["code"] == "preset-plugin-unresolved" for item in missing["diagnostics"])

    invalid = load(args.invalid_response)
    assert invalid["isResolved"] is False
    assert invalid["resolvedFormId"] is None
    assert any(item["code"] == "preset-form-identifier-unresolved" for item in invalid["diagnostics"])
    print(f"PRESET RESOLVE INDEPENDENT PASS response={args.response}")


if __name__ == "__main__":
    main()
