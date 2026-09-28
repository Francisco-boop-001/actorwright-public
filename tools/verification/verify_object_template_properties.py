"""Independent oracle for typed Fallout 4 OBTS property evidence."""

import argparse
import hashlib
import json
from pathlib import Path


def load(path: Path):
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--proposal", required=True, type=Path)
    parser.add_argument("--response", required=True, type=Path)
    args = parser.parse_args()
    artifact = load(args.proposal)
    source_hash = hashlib.sha256(args.source.read_bytes()).hexdigest().upper()
    assert artifact["artifactKind"] == "object-template-properties-proposal"
    assert artifact["schemaVersion"] == "1"
    assert artifact["edition"] == "fallout4"
    assert artifact["sourcePlugin"].casefold() == str(args.source.resolve()).casefold()
    assert artifact["sourceFormId"] == "0x00000801"
    assert artifact["inputSha256"].casefold() == source_hash.casefold()
    assert artifact["properties"] == [
        {"valueType": "FloatType", "functionType": 1, "propertyIndex": 7, "value1Integer": 0, "value1Float": 1.5, "value1FormId": None, "value2Integer": 0, "value2Float": 2.5, "stepValue": 0.25, "combinationIndex": 0},
        {"valueType": "FormIDInt", "functionType": 0, "propertyIndex": 8, "value1Integer": 0, "value1Float": 0, "value1FormId": "Source.esp|0x00000902", "value2Integer": 0, "value2Float": 0, "stepValue": 1, "combinationIndex": 0},
    ]
    assert artifact["noUnrelatedRecords"] is True
    assert load(args.response)["written"] is True
    print(f"OBJECT TEMPLATE PROPERTIES INDEPENDENT PASS proposal={args.proposal}")


if __name__ == "__main__":
    main()
