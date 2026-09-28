"""Independent semantic oracle for the bounded body-section reset contract."""

import argparse
import json
from pathlib import Path


def load(path: Path) -> dict:
    with path.open("r", encoding="utf-8-sig") as handle:
        value = json.load(handle)
    if not isinstance(value, dict):
        raise AssertionError(f"{path} is not a JSON object")
    return value


def find(root: dict, name: str):
    for key, value in root.items():
        if key.casefold() == name.casefold():
            return key, value
    return None, None


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--current", required=True, type=Path)
    parser.add_argument("--baseline", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--section", required=True)
    args = parser.parse_args()

    current = load(args.current)
    baseline = load(args.baseline)
    output = load(args.output)
    for metadata in ("schemaVersion", "game", "npcFormId"):
        assert current.get(metadata) == baseline.get(metadata) == output.get(metadata), metadata

    _, baseline_section = find(baseline, args.section)
    _, output_section = find(output, args.section)
    assert output_section == baseline_section, f"requested section did not match baseline: {args.section}"

    names = {key.casefold() for key in current} | {key.casefold() for key in output}
    for name in names:
        if name == args.section.casefold():
            continue
        _, current_value = find(current, name)
        _, output_value = find(output, name)
        assert current_value == output_value, f"unrelated root value changed: {name}"

    print(f"BODY RESET INDEPENDENT PASS section={args.section} output={args.output}")


if __name__ == "__main__":
    main()
